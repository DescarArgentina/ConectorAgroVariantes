using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using static Web_Service.SqlToJsonConverter;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;




namespace Web_Service // Note: actual namespace depends on the project name.
{
    internal class Program
    {
        // MUTEX GLOBAL (single instance)
        private const string GlobalMutexName = @"Global\DescarConector_WebService_SingleInstance";
        private static Mutex? _singleInstanceMutex;
        private static bool _mutexHeld;


        // Modelos para mapear los datos de SQL

        static async Task Main(string[] args)
        {
            Mutex? singleInstanceMutex = null;
            bool ownsMutex = false;

            try
            {
                // 0) MUTEX GLOBAL (una sola instancia en todo el SO)
                // Nota: "Global\" puede requerir privilegios para crearse si lo corrés manual como usuario no-admin.
                // En servicio (LocalSystem) normalmente no hay problema.
                try
                {
                    // Crea o abre el mutex (si ya existe, createdNew=false, pero igual tenemos el handle)
                    singleInstanceMutex = new Mutex(false, GlobalMutexName, out _);
                }
                catch (UnauthorizedAccessException)
                {
                    // No pude crearlo (típico si existe con ACL restrictiva o falta privilegio).
                    // Intento abrir el mutex existente:
                    try
                    {
                        singleInstanceMutex = Mutex.OpenExisting(GlobalMutexName);
                    }
                    catch (Exception openEx)
                    {
                        Console.WriteLine($"No se pudo crear/abrir el mutex global '{GlobalMutexName}'. " +
                                          $"Ejecutá como Administrador o corré solo vía servicio. Detalle: {openEx.Message}");
                        Environment.ExitCode = 11;
                        return;
                    }
                }

                // Intento adquirirlo al instante (0 ms). Si no lo puedo adquirir, hay otra instancia corriendo.
                try
                {
                    ownsMutex = singleInstanceMutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    // La instancia anterior murió sin liberar: se considera adquirido.
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    Console.WriteLine("Ya existe otra instancia de Web_Service.exe ejecutándose. Esta instancia finalizará.");
                    Environment.ExitCode = 10;
                    return;
                }

                // 1) Validación de parámetros
                if (args == null || args.Length < 4)
                {
                    Console.WriteLine("Uso:");
                    Console.WriteLine("  Web_Service.exe <MBOM_Input> <MBOM_Procesada> <BOP_Input> <BOP_Procesada>");
                    Console.WriteLine("Ejemplo:");
                    Console.WriteLine(@"  Web_Service.exe ""C:\...\M-BOM_XXXX"" ""C:\...\M-BOM_XXXX\MBOM_Procesada"" ""C:\...\BOP_Pendientes"" ""C:\...\BOP_Procesadas""");
                    Environment.ExitCode = 1;
                    return;
                }

                string mbomInput = Path.GetFullPath(args[0]);
                string mbomProcesada = Path.GetFullPath(args[1]);
                string bopInput = Path.GetFullPath(args[2]);
                string bopProcesada = Path.GetFullPath(args[3]);
                Tabla_SB1.BopInputPath = bopInput;


                Utilidades.InicializarLogPorMbom(mbomInput);
                Utilidades.EscribirEnLog($"Inicio ScriptPrincipal (Mutex OK). MBOM_INPUT={mbomInput} | MBOM_Procesada={mbomProcesada} | BOP_INPUT={bopInput} | BOP_Procesada={bopProcesada}");

                // 2) Validación/creación de carpetas (mínimo)
                if (!Directory.Exists(mbomInput))
                {
                    Console.WriteLine($"No existe MBOM_Input: {mbomInput}");
                    Environment.ExitCode = 2;
                    return;
                }
                if (!Directory.Exists(bopInput))
                {
                    Console.WriteLine($"No existe BOP_Input: {bopInput}");
                    Environment.ExitCode = 3;
                    return;
                }

                Directory.CreateDirectory(mbomProcesada);
                Directory.CreateDirectory(bopProcesada);

                // 3) ConnectionString
                string connectionString = Configuracion.ConnectionString;

                // Limpiar SG1 y ZG1 antes de cada corrida
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand("IF OBJECT_ID('SG1','U') IS NOT NULL TRUNCATE TABLE SG1;", conn))
                        cmd.ExecuteNonQuery();
                    using (var cmd = new SqlCommand("IF OBJECT_ID('ZG1','U') IS NOT NULL TRUNCATE TABLE ZG1;", conn))
                        cmd.ExecuteNonQuery();
                }

                // 4) Procesar MBOM
                await ProcesarMBOM(connectionString, mbomInput, mbomProcesada);

                // BOP desactivado — solo MBOM activo por ahora
                //Console.WriteLine("[DEBUG] MBOM terminado. Presioná ENTER para continuar con BOP...");
                //Console.ReadLine();

                //// 5) Limpiar BD ANTES de BOP
                //using (SqlConnection conn = new SqlConnection(connectionString))
                //{
                //    conn.Open();
                //    BorrarTabla(conn, new Dictionary<string, TableBucket>());
                //}

                // 6) Procesar BOP
                await ProcesarBOP(connectionString, bopInput, bopProcesada);

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                // Si ya inicializaste log por MBOM, esto quedará registrado
                try { Utilidades.EscribirEnLog($"[FATAL] Excepción no controlada en Main: {ex}"); } catch { }
                Console.WriteLine(ex.ToString());
                Environment.ExitCode = 99;
            }
            finally
            {
                // Liberar mutex SOLO si lo adquirimos
                if (singleInstanceMutex != null)
                {
                    if (ownsMutex)
                    {
                        try { singleInstanceMutex.ReleaseMutex(); } catch { /* noop */ }
                    }
                    singleInstanceMutex.Dispose();
                }
            }
        }



        static string? NormalizarRef(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;

            // Casos típicos en PLMXML
            if (v.StartsWith("#id", StringComparison.OrdinalIgnoreCase)) return v.Substring(3);
            if (v.StartsWith("id", StringComparison.OrdinalIgnoreCase)) return v.Substring(2);
            if (v.StartsWith("#", StringComparison.OrdinalIgnoreCase)) return v.Substring(1);

            return v;
        }
        public class TableBucket
        {
            public string TableName { get; set; } = string.Empty;

            // Conjunto de nombres de columnas encontradas
            public HashSet<string> Attributes { get; } = new HashSet<string>();

            // True si al menos un nodo de esta tabla tiene atributo "id"
            public bool HasIdAttribute { get; set; }

            // Todos los nodos XML de esta tabla (cada ocurrencia)
            public List<XmlNode> Nodes { get; } = new List<XmlNode>();
        }
        private static async Task ProcesarBOP(string connectionString, string carpetaInput, string carpetaProcesados)
        {
            Console.WriteLine("=== INICIANDO PROCESAMIENTO BOP ===");

            string sqlQuery = @"WITH CTE_Hierarchy AS (
    SELECT 
        o.id_Table,
        pr.name,
        CASE WHEN LEFT(p.catalogueId, 2) = 'P-' 
             THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2) 
             ELSE p.catalogueId 
        END AS catalogueId,
        CAST(o.parentRef AS INT) AS parentRef,
        pr.revision,
        pr.subType,
        o.idXml,

        -- 👇 columnas alineadas con SB1
        'PA' AS Tipo,
        '01' AS Deposito,
        CASE 
            WHEN uudUnidad.title = 'Agm4_Unidad'     THEN uudUnidad.value
            WHEN uudUnidad.title = 'Agm4_Kilogramos' THEN uudUnidad.value
            WHEN uudUnidad.title = 'Agm4_Litros'     THEN uudUnidad.value
            WHEN uudUnidad.title = 'Agm4_Metros'     THEN uudUnidad.value
            ELSE 'UN'
        END AS unMedida

    FROM ProcessOccurrence o
    INNER JOIN ProcessRevision pr ON o.instancedRef = pr.id_Table
    INNER JOIN Process p          ON pr.masterRef   = p.id_Table
    LEFT JOIN Form fUnidad
           ON p.catalogueId = CASE
                                WHEN CHARINDEX('/', fUnidad.name) > 0 
                                     THEN LEFT(fUnidad.name, CHARINDEX('/', fUnidad.name) - 1)
                                ELSE fUnidad.name
                             END
    LEFT JOIN UserValue_UserData uudUnidad
           ON fUnidad.id_Table + 9 = uudUnidad.id_Father
          AND p.idXml              = uudUnidad.idXml
),

CTE_Niveles AS (
    -- Nivel 0 → nodos raíz
    SELECT 
        h.*,
        0 AS Nivel
    FROM CTE_Hierarchy h
    WHERE h.parentRef IS NULL OR h.parentRef = 0

    UNION ALL

    -- Niveles siguientes (hijos)
    SELECT 
        h.*,
        n.Nivel + 1
    FROM CTE_Hierarchy h
    INNER JOIN CTE_Niveles n ON h.parentRef = n.id_Table
)

-- 🟢 PRIMERA PARTE: jerarquía
SELECT
    COALESCE(Parent.name, '')        AS Nombre_Padre,
    COALESCE(Parent.catalogueId, '') AS Process_codigo,
    Child.name                       AS Nombre_Hijo,
    Child.catalogueId                AS PR_Codigo,
    Child.subType                    AS Subtype_Hijo,
    Child.revision                   AS Revision,
    1                                AS CantidadHijo_Total,
    nChild.Nivel                     AS Nivel,
    Child.Tipo,
    Child.Deposito,
    Child.unMedida
FROM CTE_Niveles nChild
LEFT JOIN CTE_Niveles   nParent ON nChild.parentRef = nParent.id_Table
LEFT JOIN CTE_Hierarchy Parent  ON nParent.id_Table = Parent.id_Table
LEFT JOIN CTE_Hierarchy Child   ON nChild.id_Table  = Child.id_Table

UNION ALL

-- 🟠 SEGUNDA PARTE: MEConsumed (hojas) con la MISMA lógica de unidad
SELECT 
    Operation.name                      AS Nombre_Padre,
    p2.catalogueId                      AS Process_codigo,
    p.name                              AS Nombre_Hijo,
    CASE WHEN LEFT(productId, 1) = 'E' 
         THEN RIGHT(productId, LEN(productId) - 1) 
         ELSE productId 
    END                                 AS PR_Codigo,
    pr.subType                          AS Subtype_Hijo,
    pr.revision                         AS Revision,
    COUNT(productId)                    AS CantidadHijo_Total,
    3                                   AS Nivel,
    'PA'                                AS Tipo,
    '01'                                AS Deposito,
    -- 👇 lógica de unidad de medida aplicada también acá
    MAX(
        CASE 
            WHEN uudUnidad2.title = 'Agm4_Unidad'     THEN uudUnidad2.value
            WHEN uudUnidad2.title = 'Agm4_Kilogramos' THEN uudUnidad2.value
            WHEN uudUnidad2.title = 'Agm4_Litros'     THEN uudUnidad2.value
            WHEN uudUnidad2.title = 'Agm4_Metros'     THEN uudUnidad2.value
            ELSE 'UN'
        END
    )                                   AS unMedida

FROM Occurrence
INNER JOIN ProductRevision pr ON pr.id_Table = Occurrence.instancedRef
INNER JOIN Product         p  ON p.id_Table  = pr.masterRef
LEFT JOIN ProcessOccurrence o   ON Occurrence.parentRef = o.id_Table
LEFT JOIN OperationRevision op  ON op.id_Table         = o.instancedRef
LEFT JOIN Operation             ON Operation.id_Table  = op.masterRef
LEFT JOIN ProcessOccurrence o2  ON o2.id_Table         = o.parentRef
LEFT JOIN ProcessRevision pr2   ON o2.instancedRef     = pr2.id_Table
LEFT JOIN Process p2            ON pr2.masterRef       = p2.id_Table

-- 👇 joins para unidad de medida en la rama MEConsumed
LEFT JOIN Form fUnidad2
       ON p2.catalogueId = CASE
                             WHEN CHARINDEX('/', fUnidad2.name) > 0 
                                  THEN LEFT(fUnidad2.name, CHARINDEX('/', fUnidad2.name) - 1)
                             ELSE fUnidad2.name
                          END
LEFT JOIN UserValue_UserData uudUnidad2
       ON fUnidad2.id_Table + 9 = uudUnidad2.id_Father
      AND p2.idXml              = uudUnidad2.idXml

WHERE Occurrence.subType = 'MEConsumed'
GROUP BY 
    Operation.name,
    p2.catalogueId,
    p.name,
    productId,
    pr.subType,
    pr.revision
ORDER BY 
    Nivel,
    PR_Codigo DESC;
";

            Sg1Exclusions.Clear();
            var converter = new SqlToJsonConverter(connectionString);

            if (!Directory.Exists(carpetaInput))
            {
                Console.WriteLine("No existe carpeta BOP_input");
                return;
            }
            Directory.CreateDirectory(carpetaProcesados);

            var archivos = Directory.EnumerateFiles(carpetaInput)
    .Where(f =>
    {
        var ext = Path.GetExtension(f);
        return ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".plmxml", StringComparison.OrdinalIgnoreCase);
    })
    .ToArray();

            int contadorXmls = 1;

            // --- helper local: carga XML en SQL sin cargar todo en RAM ---
            static void CargarXmlEnSqlStreaming(SqlConnection connection, string xmlPath, int idXml)
            {
                var ignorados = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ApplicationRef","AttributeContext","RevisionRule","Site","Transform","View"
        };

                var createdTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var knownCols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                void EnsureTable(string tableName, bool hasIdAttr)
                {
                    if (string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (createdTables.Contains(tableName))
                        return;

                    string sql =
                        $"IF OBJECT_ID(N'[{tableName}]', 'U') IS NULL " +
                        $"BEGIN " +
                        $"CREATE TABLE [{tableName}] (" +
                        $"id INT IDENTITY(1,1) PRIMARY KEY, " +
                        $"contenido NVARCHAR(MAX), " +
                        $"id_Table NVARCHAR(MAX) NULL, " +
                        $"id_Father NVARCHAR(MAX) NULL, " +
                        $"idXml INT" +
                        $"); " +
                        $"END;";

                    using (var cmd = new SqlCommand(sql, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    createdTables.Add(tableName);

                    var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "contenido", "id_Table", "id_Father", "idXml"
};
                    knownCols[tableName] = cols;
                }

                void EnsureColumn(string tableName, string columnName)
                {
                    if (string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!knownCols.TryGetValue(tableName, out var cols))
                    {
                        cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        knownCols[tableName] = cols;
                    }

                    if (cols.Contains(columnName))
                        return;

                    string sql =
                        $"IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{tableName}' AND COLUMN_NAME = '{columnName}') " +
                        $"ALTER TABLE [{tableName}] ADD [{columnName}] NVARCHAR(MAX);";

                    using (var cmd = new SqlCommand(sql, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    cols.Add(columnName);
                }

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    IgnoreWhitespace = true
                };

                var nameStack = new Stack<string>();
                var idStack = new Stack<string>();

                nameStack.Push(string.Empty);
                idStack.Push(""); // raíz => sin id (NULL en DB)

                using var fs = File.OpenRead(xmlPath);
                using var reader = XmlReader.Create(fs, settings);

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        string nodeName = reader.Name;

                        // ignorados: en tu ParseNode no recursaba, así que acá skipeamos el sub-árbol
                        if (ignorados.Contains(nodeName))
                        {
                            reader.Skip();
                            continue;
                        }

                        string parentName = nameStack.Count > 0 ? nameStack.Peek() : string.Empty;

                        // atributos
                        var attrList = new List<(string Name, string Value)>();
                        bool hasIdAttr = false;
                        string idValueForStack = ""; // si el nodo no tiene id => NULL

                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                var an = reader.Name;
                                var av = reader.Value ?? string.Empty;
                                attrList.Add((an, av));

                                if (string.Equals(an, "id", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasIdAttr = true;
                                    if (!string.IsNullOrEmpty(av) && av.Length > 2)
                                        idValueForStack = av.Substring(2);
                                }
                            }
                            reader.MoveToElement();
                        }

                        // tabla (misma lógica que GetTableName)
                        string tableName = nodeName;
                        if (!hasIdAttr && !string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                            tableName = $"{nodeName}_{parentName}";

                        bool isEmpty = reader.IsEmptyElement;

                        // PLMXML: no insertás fila, pero sí afecta parentNodeName para hijos (como antes)
                        if (!string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                        {
                            EnsureTable(tableName, hasIdAttr);

                            // columnas base para el insert
                            var colNames = new List<string>();
                            var paramNames = new List<string>();
                            var sqlParams = new List<SqlParameter>();
                            int p = 0;

                            // contenido (vacío para ahorrar RAM)
                            colNames.Add("[contenido]");
                            paramNames.Add($"@p{p}");
                            sqlParams.Add(new SqlParameter($"@p{p}", ""));
                            p++;

                            // id_Table / id_Father
                            // id_Table (NULL si el nodo no tiene id)
                            EnsureColumn(tableName, "id_Table");
                            colNames.Add("[id_Table]");
                            paramNames.Add($"@p{p}");
                            sqlParams.Add(new SqlParameter($"@p{p}", string.IsNullOrEmpty(idValueForStack) ? (object)DBNull.Value : idValueForStack));
                            p++;

                            // id_Father (NULL si no hay id del padre)
                            EnsureColumn(tableName, "id_Father");
                            colNames.Add("[id_Father]");
                            paramNames.Add($"@p{p}");
                            string parentId = idStack.Count > 0 ? idStack.Peek() : "";
                            sqlParams.Add(new SqlParameter($"@p{p}", string.IsNullOrEmpty(parentId) ? (object)DBNull.Value : parentId));
                            p++;


                            // atributos => columnas (respetando tu lógica de recortes)
                            foreach (var (an, avRaw) in attrList)
                            {
                                if (string.Equals(an, "id", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string? normalized = null;

                                bool esRef =
                                        an.Equals("instancedRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("masterRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("parentRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("instanceRefs", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("revisionRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("partRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("unitRef", StringComparison.OrdinalIgnoreCase);

                                normalized = esRef ? (NormalizarRef(avRaw) ?? "") : avRaw;

                                EnsureColumn(tableName, an);
                                colNames.Add($"[{an}]");
                                paramNames.Add($"@p{p}");
                                sqlParams.Add(new SqlParameter($"@p{p}", normalized ?? ""));
                                p++;
                            }

                            // idXml
                            colNames.Add("[idXml]");
                            paramNames.Add($"@p{p}");
                            sqlParams.Add(new SqlParameter($"@p{p}", idXml));
                            p++;

                            string insertSql =
                                $"INSERT INTO [{tableName}] ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", paramNames)});";

                            using (var cmd = new SqlCommand(insertSql, connection))
                            {
                                cmd.Parameters.AddRange(sqlParams.ToArray());
                                cmd.ExecuteNonQuery();
                            }
                        }

                        // stack push/pop para mantener parentNodeName e id_Father igual que antes
                        if (!isEmpty)
                        {
                            nameStack.Push(nodeName);
                            idStack.Push(idValueForStack);
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        if (nameStack.Count > 1) nameStack.Pop();
                        if (idStack.Count > 1) idStack.Pop();
                    }
                }
            }

            foreach (string archivo in archivos)
            {
                Sg1Exclusions.Clear();

                try
                {
                    Console.WriteLine($"[INFO] Procesando BOP: {Path.GetFileName(archivo)}");

                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();
                        Tabla_SB1.EnsureTablasOpcionalesSB1(connection);

                        // Mantener tu comportamiento: borrar y recrear por XML
                        BorrarTabla(connection, new Dictionary<string, TableBucket>());

                        // Cargar XML a SQL sin cargar todo en memoria
                        CargarXmlEnSqlStreaming(connection, archivo, contadorXmls);
                        CrearIndices(connection);
                    }
                    Tabla_SB1.BopInputPath = carpetaInput;
                    Tabla_SB1.BopProcesadaPath = carpetaProcesados;
                    Tabla_SB1.CurrentBopCode = Path.GetFileNameWithoutExtension(archivo);

                    // Mover procesado (igual que antes)
                    string destino = Path.Combine(carpetaProcesados, Path.GetFileName(archivo));
                    if (File.Exists(destino))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(destino);
                        string ext = Path.GetExtension(destino);
                        destino = Path.Combine(carpetaProcesados, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    }
                    File.Move(archivo, destino);
                    contadorXmls++;

                    // SB1 (productos de la BOP)
                    Console.WriteLine("[BOP] Generando SB1 (productos) desde estructura BOP...");
                    var listaSB1_BOP = Tabla_SB1.jsonSB1_BOP();
                    Console.WriteLine($"[BOP] jsonSB1_BOP() devolvió {listaSB1_BOP.Count} productos.");
                    foreach (string s in listaSB1_BOP)
                    {
                        await Tabla_SB1.postSB1(s);
                    }

                    // ZG1 BOP (estructuras con variantes)
                    Console.WriteLine("[BOP] Generando ZG1 (estructuras con variantes) desde BOP...");
                    var estructurasZG1_BOP = Tabla_SG1.jsonZG1_BOP();
                    Console.WriteLine($"[BOP] jsonZG1_BOP() devolvió {estructurasZG1_BOP.Count} estructuras.");
                    await Tabla_SG1.postZG1(estructurasZG1_BOP);

                    // SG2/SH3 (procesos)
                    Console.WriteLine("[BOP] Generando SG2/SH3 (Procesos Productivos) desde BOP...");
                    var listaSG2 = Tablas_SG2_SH3.jsonSG2_SH3();
                    Console.WriteLine($"[BOP] jsonSG2_SH3() devolvió {listaSG2.Count} JSONs.");
                    foreach (string s in listaSG2)
                    {
                        await Tablas_SG2_SH3.EnviarSG2_SH3(s);
                    }

                    // JSONs jerárquicos (igual que antes)
                    Console.WriteLine("\n============================");
                    try
                    {
                        //var jsonStrings = converter.ConvertToHierarchicalJsonStrings(sqlQuery);
                    }
                    catch (Exception exConv)
                    {
                        //Utilidades.EscribirEnLog($"[BOP][WARN] Falló ConvertToHierarchicalJsonStrings: {exConv.Message}\n{exConv}");
                    }

                    Utilidades.EscribirEnLog($"BOP Procesado: {Path.GetFileName(destino)}");
                }
                catch (Exception ea)
                {
                    Utilidades.EscribirEnLog($"Error BOP: {Path.GetFileName(archivo)}\n{ea}");
                }
            }
        }

        private static async Task ProcesarMBOM(string connectionString, string carpetaInput, string carpetaProcesados)
        {
            Console.WriteLine("=== INICIANDO PROCESAMIENTO MBOM ===");

            if (!Directory.Exists(carpetaInput))
            {
                Console.WriteLine("No existe carpeta MBOM_input");
                return;
            }
            Directory.CreateDirectory(carpetaProcesados);

            var archivos = Directory.EnumerateFiles(carpetaInput)
    .Where(f =>
    {
        var ext = Path.GetExtension(f);
        return ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".plmxml", StringComparison.OrdinalIgnoreCase);
    })
    .ToArray();

            int contadorXmls = 1;

            // --- helper local: carga XML en SQL sin cargar todo en RAM ---
            static void CargarXmlEnSqlStreaming(SqlConnection connection, string xmlPath, int idXml)
            {
                var ignorados = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ApplicationRef","AttributeContext","RevisionRule","Site","Transform","View"
        };

                var createdTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var knownCols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                void EnsureTable(string tableName, bool hasIdAttr)
                {
                    if (string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (createdTables.Contains(tableName))
                        return;

                    string sql =
                        $"IF OBJECT_ID(N'[{tableName}]', 'U') IS NULL " +
                        $"BEGIN " +
                        $"CREATE TABLE [{tableName}] (" +
                        $"id INT IDENTITY(1,1) PRIMARY KEY, " +
                        $"contenido NVARCHAR(MAX), " +
                        $"id_Table NVARCHAR(MAX) NULL, " +
                        $"id_Father NVARCHAR(MAX) NULL, " +
                        $"idXml INT" +
                        $"); " +
                        $"END;";

                    using (var cmd = new SqlCommand(sql, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    createdTables.Add(tableName);

                    var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "contenido", "id_Table", "id_Father", "idXml"
};
                    knownCols[tableName] = cols;
                }

                void EnsureColumn(string tableName, string columnName)
                {
                    if (string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!knownCols.TryGetValue(tableName, out var cols))
                    {
                        cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        knownCols[tableName] = cols;
                    }

                    if (cols.Contains(columnName))
                        return;

                    string sql =
                        $"IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{tableName}' AND COLUMN_NAME = '{columnName}') " +
                        $"ALTER TABLE [{tableName}] ADD [{columnName}] NVARCHAR(MAX);";

                    using (var cmd = new SqlCommand(sql, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    cols.Add(columnName);
                }

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    IgnoreWhitespace = true
                };

                var nameStack = new Stack<string>();
                var idStack = new Stack<string>();
                nameStack.Push(string.Empty);
                idStack.Push("");

                using var fs = File.OpenRead(xmlPath);
                using var reader = XmlReader.Create(fs, settings);

                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        string nodeName = reader.Name;

                        if (ignorados.Contains(nodeName))
                        {
                            reader.Skip();
                            continue;
                        }

                        string parentName = nameStack.Count > 0 ? nameStack.Peek() : string.Empty;

                        var attrList = new List<(string Name, string Value)>();
                        bool hasIdAttr = false;
                        string idValueForStack = "";

                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                var an = reader.Name;
                                var av = reader.Value ?? string.Empty;
                                attrList.Add((an, av));

                                if (string.Equals(an, "id", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasIdAttr = true;
                                    if (!string.IsNullOrEmpty(av) && av.Length > 2)
                                        idValueForStack = av.Substring(2);
                                }
                            }
                            reader.MoveToElement();
                        }

                        string tableName = nodeName;
                        if (!hasIdAttr && !string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                            tableName = $"{nodeName}_{parentName}";

                        bool isEmpty = reader.IsEmptyElement;

                        if (!string.Equals(tableName, "PLMXML", StringComparison.OrdinalIgnoreCase))
                        {
                            EnsureTable(tableName, hasIdAttr);

                            var colNames = new List<string>();
                            var paramNames = new List<string>();
                            var sqlParams = new List<SqlParameter>();
                            int p = 0;

                            colNames.Add("[contenido]");
                            paramNames.Add($"@p{p}");
                            sqlParams.Add(new SqlParameter($"@p{p}", ""));
                            p++;

                            // id_Table (NULL si el nodo no tiene id)
                            EnsureColumn(tableName, "id_Table");
                            colNames.Add("[id_Table]");
                            paramNames.Add($"@p{p}");
                            sqlParams.Add(new SqlParameter($"@p{p}", string.IsNullOrEmpty(idValueForStack) ? (object)DBNull.Value : idValueForStack));
                            p++;

                            // id_Father (NULL si no hay id del padre)
                            EnsureColumn(tableName, "id_Father");
                            colNames.Add("[id_Father]");
                            paramNames.Add($"@p{p}");
                            string parentId = idStack.Count > 0 ? idStack.Peek() : "";
                            sqlParams.Add(new SqlParameter($"@p{p}", string.IsNullOrEmpty(parentId) ? (object)DBNull.Value : parentId));
                            p++;


                            foreach (var (an, avRaw) in attrList)
                            {
                                if (string.Equals(an, "id", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string? normalized = null;

                                bool esRef =
                                        an.Equals("instancedRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("masterRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("parentRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("instanceRefs", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("revisionRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("partRef", StringComparison.OrdinalIgnoreCase) ||
                                        an.Equals("unitRef", StringComparison.OrdinalIgnoreCase);

                                normalized = esRef ? (NormalizarRef(avRaw) ?? "") : avRaw;

                                EnsureColumn(tableName, an);
                                colNames.Add($"[{an}]");
                                paramNames.Add($"@p{p}");
                                sqlParams.Add(new SqlParameter($"@p{p}", normalized ?? ""));
                                p++;
                            }

                            colNames.Add("[idXml]");
                            paramNames.Add($"@p{p}");
                            sqlParams.Add(new SqlParameter($"@p{p}", idXml));
                            p++;

                            string insertSql =
                                $"INSERT INTO [{tableName}] ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", paramNames)});";

                            using (var cmd = new SqlCommand(insertSql, connection))
                            {
                                cmd.Parameters.AddRange(sqlParams.ToArray());
                                cmd.ExecuteNonQuery();
                            }
                        }

                        if (!isEmpty)
                        {
                            nameStack.Push(nodeName);
                            idStack.Push(idValueForStack);
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        if (nameStack.Count > 1) nameStack.Pop();
                        if (idStack.Count > 1) idStack.Pop();
                    }
                }
            }

            foreach (string archivo in archivos)
            {
                try
                {
                    Console.WriteLine($"[INFO] Procesando MBOM: {Path.GetFileName(archivo)}");

                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();
                        Tabla_SB1.EnsureTablasOpcionalesSB1(connection);
                        EnsureTablaZG1(connection);
                        // Mantener tu comportamiento: borrar y recrear por XML
                        BorrarTabla(connection, new Dictionary<string, TableBucket>());

                        // Cargar XML a SQL sin cargar todo en memoria
                        CargarXmlEnSqlStreaming(connection, archivo, contadorXmls);
                        CrearIndices(connection);

                        Console.WriteLine("[MBOM] Generando SB1...");
                        HashSet<string> codigosFantasma;
                        var listaSB1_MBOM = Tabla_SB1.jsonSB1_MBOM(out codigosFantasma);
                        Console.WriteLine($"[MBOM] jsonSB1() devolvió {listaSB1_MBOM.Count} productos. Fantasmas={codigosFantasma.Count}");
                        foreach (string s in listaSB1_MBOM)
                        {
                            await Tabla_SB1.postSB1(s);
                        }

                        Console.WriteLine("[MBOM] Generando ZG1 (estructuras con variantes)...");
                        var listaZG1 = Tabla_SG1.jsonZG1_MBOM();
                        Console.WriteLine($"[MBOM] jsonZG1() generó {listaZG1.Count} JSONs de estructura con variantes.");
                        await Tabla_SG1.postZG1(listaZG1);
                    }

                    // Mover procesado (igual que antes)
                    string destino = Path.Combine(carpetaProcesados, Path.GetFileName(archivo));
                    if (File.Exists(destino))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(destino);
                        string ext = Path.GetExtension(destino);
                        destino = Path.Combine(carpetaProcesados, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    }
                    File.Move(archivo, destino);
                    contadorXmls++;

                    Utilidades.EscribirEnLog($"MBOM Procesado: {Path.GetFileName(destino)}");
                }
                catch (Exception ea)
                {
                    Utilidades.EscribirEnLog($"Error MBOM: {Path.GetFileName(archivo)}\nError: {ea.Message}");
                }
            }
        }




        public static string ConvertImageToBase64(string imagePath)
        {
            try
            {
                byte[] imageBytes = File.ReadAllBytes(imagePath);
                return Convert.ToBase64String(imageBytes);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error al convertir imagen a Base64: {ex.Message}");
            }
        }

        public static bool SaveBase64ToImageFile(string base64String, string outputPath)
        {
            try
            {
                byte[] imageBytes = Convert.FromBase64String(base64String);
                File.WriteAllBytes(outputPath, imageBytes);
                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error al guardar imagen desde Base64: {ex.Message}");
            }
        }

        static void EnsureTablaZG1(SqlConnection connection)
        {
            try
            {
                string sql = @"
IF OBJECT_ID('ZG1','U') IS NULL
CREATE TABLE ZG1 (
    Codigo_Padre  NVARCHAR(100),
    Nombre_Padre  NVARCHAR(500),
    Codigo_Hijo   NVARCHAR(100),
    Nombre_Hijo   NVARCHAR(500),
    Cantidad      NVARCHAR(50),
    bl_formula    NVARCHAR(MAX),
    idContext     NVARCHAR(200)
)";
                using (var cmd = new SqlCommand(sql, connection))
                {
                    cmd.ExecuteNonQuery();
                }
                Utilidades.EscribirEnLog("[ZG1] Tabla ZG1 verificada/creada correctamente.");
            }
            catch (Exception ex)
            {
                Utilidades.EscribirEnLog($"[ZG1] ERROR al crear tabla ZG1: {ex.Message}");
                Console.WriteLine($"[ZG1] ERROR al crear tabla ZG1: {ex.Message}");
            }
        }

        static void BorrarTabla(SqlConnection connection, Dictionary<string, TableBucket> tables)
        {
            try
            {
                string sql = @"
        DECLARE @sql NVARCHAR(MAX) = N'';

        SELECT @sql = STRING_AGG(
                'DROP TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name),
                '; '
            )
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'dbo'
          AND t.name NOT IN ('SG1', 'ZG1');  -- ⬅️ NO borrar la tabla SG1 ni ZG1

        IF @sql IS NOT NULL AND @sql <> ''
            EXEC (@sql);";

                using (var cmd = new SqlCommand(sql, connection))
                {
                    cmd.ExecuteNonQuery();
                }

                Utilidades.EscribirEnLog("Todas las tablas del esquema dbo fueron eliminadas correctamente (excepto SG1).");
                Console.WriteLine("Todas las tablas del esquema dbo fueron eliminadas correctamente (excepto SG1).");
            }
            catch (Exception ex)
            {
                Utilidades.EscribirEnLog($"Error al intentar eliminar tablas: {ex.Message}");
                Console.WriteLine($"Error al intentar eliminar tablas: {ex.Message}");
            }
        }

        private static void CrearIndices(SqlConnection connection)
        {
            // Primero convertir columnas clave de NVARCHAR(MAX) a NVARCHAR(450) para poder indexarlas
            string[] conversiones = new[]
            {
                "IF OBJECT_ID('Occurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Occurrence') AND name='instancedRef') = -1 ALTER TABLE Occurrence ALTER COLUMN instancedRef NVARCHAR(450) END",
                "IF OBJECT_ID('Occurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Occurrence') AND name='parentRef') = -1 ALTER TABLE Occurrence ALTER COLUMN parentRef NVARCHAR(450) END",
                "IF OBJECT_ID('Occurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Occurrence') AND name='idXml') = -1 ALTER TABLE Occurrence ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('Occurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Occurrence') AND name='id_Table') = -1 ALTER TABLE Occurrence ALTER COLUMN id_Table NVARCHAR(450) END",
                "IF OBJECT_ID('ProductRevision','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductRevision') AND name='masterRef') = -1 ALTER TABLE ProductRevision ALTER COLUMN masterRef NVARCHAR(450) END",
                "IF OBJECT_ID('ProductRevision','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductRevision') AND name='id_Table') = -1 ALTER TABLE ProductRevision ALTER COLUMN id_Table NVARCHAR(450) END",
                "IF OBJECT_ID('ProductRevision','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductRevision') AND name='idXml') = -1 ALTER TABLE ProductRevision ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('Product','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Product') AND name='id_Table') = -1 ALTER TABLE Product ALTER COLUMN id_Table NVARCHAR(450) END",
                "IF OBJECT_ID('Product','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Product') AND name='idXml') = -1 ALTER TABLE Product ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('UserData','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserData') AND name='id_Father') = -1 ALTER TABLE UserData ALTER COLUMN id_Father NVARCHAR(450) END",
                "IF OBJECT_ID('UserData','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserData') AND name='idXml') = -1 ALTER TABLE UserData ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('UserValue_UserData','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserValue_UserData') AND name='id_Father') = -1 ALTER TABLE UserValue_UserData ALTER COLUMN id_Father NVARCHAR(450) END",
                "IF OBJECT_ID('UserValue_UserData','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserValue_UserData') AND name='idXml') = -1 ALTER TABLE UserValue_UserData ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('UserValue_UserData','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserValue_UserData') AND name='title') = -1 ALTER TABLE UserValue_UserData ALTER COLUMN title NVARCHAR(450) END",
                "IF OBJECT_ID('UserValue_Occurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserValue_Occurrence') AND name='id_Father') = -1 ALTER TABLE UserValue_Occurrence ALTER COLUMN id_Father NVARCHAR(450) END",
                "IF OBJECT_ID('UserValue_Occurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserValue_Occurrence') AND name='idXml') = -1 ALTER TABLE UserValue_Occurrence ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('UserValue_Occurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('UserValue_Occurrence') AND name='title') = -1 ALTER TABLE UserValue_Occurrence ALTER COLUMN title NVARCHAR(450) END",
                "IF OBJECT_ID('ProcessOccurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProcessOccurrence') AND name='instancedRef') = -1 ALTER TABLE ProcessOccurrence ALTER COLUMN instancedRef NVARCHAR(450) END",
                "IF OBJECT_ID('ProcessOccurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProcessOccurrence') AND name='parentRef') = -1 ALTER TABLE ProcessOccurrence ALTER COLUMN parentRef NVARCHAR(450) END",
                "IF OBJECT_ID('ProcessOccurrence','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProcessOccurrence') AND name='idXml') = -1 ALTER TABLE ProcessOccurrence ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('ProcessRevision','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProcessRevision') AND name='masterRef') = -1 ALTER TABLE ProcessRevision ALTER COLUMN masterRef NVARCHAR(450) END",
                "IF OBJECT_ID('ProcessRevision','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProcessRevision') AND name='id_Table') = -1 ALTER TABLE ProcessRevision ALTER COLUMN id_Table NVARCHAR(450) END",
                "IF OBJECT_ID('ProcessRevision','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProcessRevision') AND name='idXml') = -1 ALTER TABLE ProcessRevision ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('Process','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Process') AND name='id_Table') = -1 ALTER TABLE Process ALTER COLUMN id_Table NVARCHAR(450) END",
                "IF OBJECT_ID('Process','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('Process') AND name='idXml') = -1 ALTER TABLE Process ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('ProductRevisionView','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductRevisionView') AND name='revisionRef') = -1 ALTER TABLE ProductRevisionView ALTER COLUMN revisionRef NVARCHAR(450) END",
                "IF OBJECT_ID('ProductRevisionView','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductRevisionView') AND name='idXml') = -1 ALTER TABLE ProductRevisionView ALTER COLUMN idXml NVARCHAR(450) END",
                "IF OBJECT_ID('ProductRevisionView','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductRevisionView') AND name='id_Table') = -1 ALTER TABLE ProductRevisionView ALTER COLUMN id_Table NVARCHAR(450) END",
                "IF OBJECT_ID('ProductInstance','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductInstance') AND name='partRef') = -1 ALTER TABLE ProductInstance ALTER COLUMN partRef NVARCHAR(450) END",
                "IF OBJECT_ID('ProductInstance','U') IS NOT NULL BEGIN IF (SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('ProductInstance') AND name='idXml') = -1 ALTER TABLE ProductInstance ALTER COLUMN idXml NVARCHAR(450) END",
            };

            foreach (var sql in conversiones)
            {
                try
                {
                    using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[INDICES] Advertencia al convertir columna: {ex.Message}");
                }
            }

            // Crear índices
            string[] indices = new[]
            {
                "IF OBJECT_ID('Occurrence','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Occurrence_instancedRef') CREATE INDEX IX_Occurrence_instancedRef ON Occurrence(instancedRef, idXml)",
                "IF OBJECT_ID('Occurrence','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Occurrence_parentRef') CREATE INDEX IX_Occurrence_parentRef ON Occurrence(parentRef, idXml)",
                "IF OBJECT_ID('ProductRevision','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProductRevision_master') CREATE INDEX IX_ProductRevision_master ON ProductRevision(masterRef, idXml)",
                "IF OBJECT_ID('ProductRevision','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProductRevision_idTable') CREATE INDEX IX_ProductRevision_idTable ON ProductRevision(id_Table, idXml)",
                "IF OBJECT_ID('Product','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Product_idTable') CREATE INDEX IX_Product_idTable ON Product(id_Table, idXml)",
                "IF OBJECT_ID('UserData','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_UserData_idFather') CREATE INDEX IX_UserData_idFather ON UserData(id_Father, idXml)",
                "IF OBJECT_ID('UserValue_UserData','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_UserValue_UD_idFather') CREATE INDEX IX_UserValue_UD_idFather ON UserValue_UserData(id_Father, idXml, title)",
                "IF OBJECT_ID('UserValue_Occurrence','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_UserValue_Occ_idFather') CREATE INDEX IX_UserValue_Occ_idFather ON UserValue_Occurrence(id_Father, idXml, title)",
                "IF OBJECT_ID('ProcessOccurrence','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProcessOccurrence_instancedRef') CREATE INDEX IX_ProcessOccurrence_instancedRef ON ProcessOccurrence(instancedRef, idXml)",
                "IF OBJECT_ID('ProcessOccurrence','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProcessOccurrence_parentRef') CREATE INDEX IX_ProcessOccurrence_parentRef ON ProcessOccurrence(parentRef, idXml)",
                "IF OBJECT_ID('ProcessRevision','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProcessRevision_master') CREATE INDEX IX_ProcessRevision_master ON ProcessRevision(masterRef, idXml)",
                "IF OBJECT_ID('Process','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Process_idTable') CREATE INDEX IX_Process_idTable ON Process(id_Table, idXml)",
                "IF OBJECT_ID('ProductRevisionView','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProductRevisionView_revisionRef') CREATE INDEX IX_ProductRevisionView_revisionRef ON ProductRevisionView(revisionRef, idXml)",
                "IF OBJECT_ID('ProductRevisionView','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProductRevisionView_idTable') CREATE INDEX IX_ProductRevisionView_idTable ON ProductRevisionView(id_Table, idXml)",
                "IF OBJECT_ID('ProductInstance','U') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ProductInstance_partRef') CREATE INDEX IX_ProductInstance_partRef ON ProductInstance(partRef, idXml)",
            };

            foreach (var sql in indices)
            {
                try
                {
                    using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[INDICES] Advertencia al crear índice: {ex.Message}");
                }
            }
            Console.WriteLine("[INDICES] Índices creados.");

            string[] statsTablas = new[]
            {
                "Occurrence", "UserValue_UserData", "UserValue_Occurrence",
                "UserData", "ProductRevision", "Product",
                "ProductRevisionView", "ProductInstance"
            };
            foreach (var tabla in statsTablas)
            {
                try
                {
                    using var cmd = new SqlCommand($"IF OBJECT_ID('{tabla}','U') IS NOT NULL UPDATE STATISTICS [{tabla}] WITH FULLSCAN", connection) { CommandTimeout = 300 };
                    cmd.ExecuteNonQuery();
                    Console.WriteLine($"[STATS] {tabla} OK");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[STATS] Advertencia {tabla}: {ex.Message}");
                }
            }
            Console.WriteLine("[STATS] Estadísticas actualizadas.");
        }

        private static readonly HashSet<string> NodosIgnorados = new(StringComparer.OrdinalIgnoreCase)
{
    "ApplicationRef", "AttributeContext",
    "RevisionRule", "Site", "Transform", "View"
};

        

    }
}