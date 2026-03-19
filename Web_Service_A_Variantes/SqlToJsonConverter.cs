namespace Web_Service
{
    using System;
    using System.Collections.Generic;
    using System.Data.SqlClient;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Newtonsoft.Json;

    public class ProductStructure
    {
        public string producto { get; set; }
        public string qtdBase { get; set; }
        public List<List<Campo>> estructura { get; set; } = new List<List<Campo>>();
        public string fecha { get; set; }
    }

    public class Campo
    {
        public string campo { get; set; }
        public string valor { get; set; }
    }

    public class SqlRecord
    {
        public string Process_codigo { get; set; }
        public string PR_Codigo { get; set; }
        public string Codigo { get; set; }
        public double Cantidad { get; set; }
        public string Subtype { get; set; }

        public string WorkAreaId { get; set; }
        public string Nombre_WA { get; set; }

        public int NumBusqueda { get; set; }
    }

    public class SqlToJsonConverter
    {
        private readonly string connectionString;

        // Ajustá estos 2 si querés otro nombre/valor de fecha fin
        private const string CAMPO_FECHA_FIN = "fechaFin";
        private const string VALOR_FECHA_FIN_PRT = "20250101";

        public SqlToJsonConverter(string connectionString)
        {
            this.connectionString = connectionString;
        }

        public List<ProductStructure> ConvertSqlToHierarchicalJsons(string sqlQuery)
        {
            var records = new List<SqlRecord>();

            bool EsMatPrimaORepComprado(string subType)
            {
                if (string.IsNullOrWhiteSpace(subType)) return false;

                return string.Equals(subType.Trim(), "Agm4_MatPrimaRevision", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(subType.Trim(), "Agm4_RepCompradoRevision", StringComparison.OrdinalIgnoreCase);
            }

            string GetString(SqlDataReader r, string col) =>
                r.IsDBNull(r.GetOrdinal(col)) ? null : r[col].ToString();

            double GetDouble(SqlDataReader r, string col) =>
                r.IsDBNull(r.GetOrdinal(col)) ? 0 : Convert.ToDouble(r[col]);

            int GetInt(SqlDataReader r, string col) =>
                r.IsDBNull(r.GetOrdinal(col)) ? 0 : (int.TryParse(r[col].ToString(), out var v) ? v : 0);

            int ExtraerNumero(string s)
            {
                var m = Regex.Match(s ?? string.Empty, @"\d+");
                return (m.Success && int.TryParse(m.Value, out var n)) ? n : 0;
            }

            string Left3(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                s = s.Trim();
                return s.Length >= 3 ? s.Substring(0, 3) : s;
            }

            List<Campo> CrearRelacion(string codigoHijo, string cantidad, bool agregarFechaFinSoloSiEsPRTGenerado)
            {
                var rel = new List<Campo>
                {
                    new Campo { campo = "codigo", valor = codigoHijo },
                    new Campo { campo = "cantidad", valor = cantidad }
                };

                if (agregarFechaFinSoloSiEsPRTGenerado)
                {
                    rel.Add(new Campo { campo = CAMPO_FECHA_FIN, valor = VALOR_FECHA_FIN_PRT });
                }

                return rel;
            }

            // 1) Leer la consulta SQL
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                using (var cmd = new SqlCommand(sqlQuery, conn))
                {
                    cmd.CommandTimeout = 300;

                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            records.Add(new SqlRecord
                            {
                                Process_codigo = GetString(rdr, "Process_codigo"),
                                PR_Codigo = GetString(rdr, "PR_Codigo"),
                                Codigo = GetString(rdr, "Codigo"),
                                Cantidad = GetDouble(rdr, "Cantidad"),
                                Subtype = GetString(rdr, "subType"),
                                NumBusqueda = GetInt(rdr, "num_busqueda"),
                                WorkAreaId = GetString(rdr, "WorkArea_CatalogueId"),
                                Nombre_WA = GetString(rdr, "WorkArea_Nombre")
                            });
                        }
                    }
                }
            }

            if (!records.Any())
                return new List<ProductStructure>();

            bool EsWorkAreaTerceros(SqlRecord r)
            {
                var id = (r.WorkAreaId ?? string.Empty).Trim();
                var nom = (r.Nombre_WA ?? string.Empty).Trim();

                return id == "000465" ||
                       nom.IndexOf("TERCEROS", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // 2) Root desde Process_codigo
            string proc = (records[0].Process_codigo ?? string.Empty).Trim();

            string rootParent;
            if (proc.StartsWith("P-", StringComparison.OrdinalIgnoreCase))
                rootParent = proc.Substring(2).Trim();
            else
                rootParent = proc;

            // 3) Agrupar PRs
            var prGroups = records
                .Where(r => !string.IsNullOrWhiteSpace(r.PR_Codigo))
                .GroupBy(r => r.PR_Codigo.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    PR = g.Key,
                    NumBusqueda = g.Max(r => r.NumBusqueda),
                    Prefix3 = Left3(g.Key),
                    PrNum = ExtraerNumero(g.Key),
                    EsTerceros = g.Any(EsWorkAreaTerceros)
                })
                .ToList();

            var prMetaOrdenado = prGroups
                .OrderByDescending(x => x.NumBusqueda)
                .ThenByDescending(x => x.Prefix3, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(x => x.PrNum)
                .ThenBy(x => x.PR, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string prARemover = null;

            if (prMetaOrdenado.Count == 1 && !prMetaOrdenado[0].EsTerceros)
            {
                prARemover = prMetaOrdenado[0].PR;
            }
            else if (prMetaOrdenado.Count > 1 && !prMetaOrdenado[0].EsTerceros)
            {
                prARemover = prMetaOrdenado[0].PR;
            }

            if (!string.IsNullOrWhiteSpace(prARemover))
            {
                string procesoP = $"P-{rootParent}";
                Sg1Exclusions.SetExcluded(procesoP, prARemover);
                Utilidades.EscribirEnLog($"SG1 -> Excluido PR={prARemover} y se mapeará en SG2 al proceso {procesoP}");
            }

            var prMetaOperativa = prMetaOrdenado
                .Where(x => !string.Equals(x.PR, prARemover, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var prList = prMetaOperativa.Select(x => x.PR).ToList();

            var esTercerosPorPR = prMetaOrdenado.ToDictionary(
                x => x.PR,
                x => x.EsTerceros,
                StringComparer.OrdinalIgnoreCase);

            // Padre donde colgar PRT + consumibles del PR removido
            string padrePRRemovido = rootParent;

            // 4) Diccionario estructuras
            var map = new Dictionary<string, ProductStructure>(StringComparer.OrdinalIgnoreCase);

            ProductStructure GetOrCreate(string codigo)
            {
                if (string.IsNullOrWhiteSpace(codigo))
                    throw new ArgumentException("codigo no puede ser nulo o vacío", nameof(codigo));

                if (!map.TryGetValue(codigo, out var ps))
                {
                    ps = new ProductStructure
                    {
                        producto = codigo,
                        qtdBase = "1",
                        estructura = new List<List<Campo>>()
                    };
                    map[codigo] = ps;
                }
                return ps;
            }

            // 5) Calcular padre de cada PR
            var parentByPr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string lastNonTerceros = null;

            foreach (var meta in prMetaOperativa)
            {
                string pr = meta.PR;
                bool esTerceros = meta.EsTerceros;
                string parent;

                if (!esTerceros)
                {
                    parent = lastNonTerceros ?? rootParent;
                    lastNonTerceros = pr;
                }
                else
                {
                    if (lastNonTerceros != null && parentByPr.TryGetValue(lastNonTerceros, out var parentLast))
                        parent = parentLast;
                    else
                        parent = rootParent;
                }

                parentByPr[pr] = parent;
            }

            if (prMetaOperativa.Count > 0)
            {
                var firstPr = prMetaOperativa[0].PR;
                if (parentByPr.TryGetValue(firstPr, out var p))
                    padrePRRemovido = p;
            }

            // 6) hijos por padre
            var childrenByParent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var meta in prMetaOperativa)
            {
                string pr = meta.PR;
                string parent = parentByPr[pr];

                if (!childrenByParent.TryGetValue(parent, out var list))
                {
                    list = new List<string>();
                    childrenByParent[parent] = list;
                }
                list.Add(pr);
            }

            // root
            GetOrCreate(rootParent);

            // 7) Esqueleto PRs
            foreach (var kvp in childrenByParent)
            {
                string parent = kvp.Key;
                var hijos = kvp.Value;

                var psParent = GetOrCreate(parent);

                var hijosNormales = hijos.Where(h => !esTercerosPorPR[h]).ToList();
                var hijosTerceros = hijos.Where(h => esTercerosPorPR[h]).ToList();

                foreach (var child in hijosNormales)
                {
                    psParent.estructura.Add(CrearRelacion(child, "1", agregarFechaFinSoloSiEsPRTGenerado: false));
                    GetOrCreate(child);
                }

                foreach (var child in hijosTerceros)
                {
                    psParent.estructura.Add(CrearRelacion(child, "1", agregarFechaFinSoloSiEsPRTGenerado: false));
                    GetOrCreate(child);
                }
            }

            // 8) Agregar PRT generado por código: PRxxxxxxT como hijo del PR NO terceros (y SOLO a estos les agregamos fechaFin)
            foreach (var pr in prList)
            {
                if (esTercerosPorPR.TryGetValue(pr, out var esTer) && esTer)
                    continue;

                var psParent = GetOrCreate(pr);
                string codigoServ = pr + "T";

                bool yaExiste = psParent.estructura.Any(rel =>
                {
                    var c = rel.FirstOrDefault(x => x.campo == "codigo");
                    return c != null && string.Equals(c.valor, codigoServ, StringComparison.OrdinalIgnoreCase);
                });

                if (!yaExiste)
                {
                    psParent.estructura.Add(CrearRelacion(codigoServ, "1", agregarFechaFinSoloSiEsPRTGenerado: true));
                }
            }

            // 8.b) Si hubo PR removido, agregar su PRT (generado por código) como hijo del padre correspondiente, también con fechaFin
            if (!string.IsNullOrWhiteSpace(prARemover))
            {
                var psPadre = GetOrCreate(padrePRRemovido);
                string codigoServRemovido = prARemover + "T";

                bool yaExiste = psPadre.estructura.Any(rel =>
                {
                    var c = rel.FirstOrDefault(x => x.campo == "codigo");
                    return c != null && string.Equals(c.valor, codigoServRemovido, StringComparison.OrdinalIgnoreCase);
                });

                if (!yaExiste)
                {
                    int insertAt = psPadre.estructura.Count;

                    if (prList.Count > 0)
                    {
                        string prSiguiente = prList[0];
                        int idx = psPadre.estructura.FindIndex(rel =>
                        {
                            var c = rel.FirstOrDefault(x => x.campo == "codigo");
                            return c != null && string.Equals(c.valor, prSiguiente, StringComparison.OrdinalIgnoreCase);
                        });
                        if (idx >= 0)
                            insertAt = idx;
                    }

                    psPadre.estructura.Insert(insertAt, CrearRelacion(codigoServRemovido, "1", agregarFechaFinSoloSiEsPRTGenerado: true));
                }
            }

            // 9) Consumibles
            foreach (var rec in records)
            {
                var stLower = (rec.Subtype ?? string.Empty).ToLowerInvariant();

                if (stLower.Contains("operation") || stLower.Contains("meoperation") ||
                    stLower.Contains("fixture") || stLower.Contains("tool"))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rec.PR_Codigo))
                    continue;

                var prRec = rec.PR_Codigo.Trim();

                var codigoHijo = (rec.Codigo ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(codigoHijo))
                    continue;

                if (rec.Cantidad <= 0)
                    continue;

                bool prEsRemovido = !string.IsNullOrWhiteSpace(prARemover) &&
                                   string.Equals(prRec, prARemover, StringComparison.OrdinalIgnoreCase);

                bool prEsTerceros = esTercerosPorPR.TryGetValue(prRec, out var esTer) && esTer;

                bool esMpORep = EsMatPrimaORepComprado(rec.Subtype);

                string padreDestino;

                if (prEsRemovido)
                {
                    padreDestino = padrePRRemovido;
                }
                else if (prEsTerceros)
                {
                    if (!esMpORep)
                        continue;

                    padreDestino = parentByPr.TryGetValue(prRec, out var p) ? p : rootParent;

                    Utilidades.EscribirEnLog($"SG1 -> Recolgando consumible '{codigoHijo}' ({rec.Subtype}) desde PR Terceros '{prRec}' hacia '{padreDestino}'");
                }
                else
                {
                    padreDestino = prRec;
                }

                var psParent = GetOrCreate(padreDestino);

                psParent.estructura.Add(
                    CrearRelacion(
                        codigoHijo,
                        rec.Cantidad.ToString(CultureInfo.InvariantCulture),
                        agregarFechaFinSoloSiEsPRTGenerado: false
                    )
                );
            }

            // 10) Lista final
            var result = new List<ProductStructure>();

            if (map.ContainsKey(rootParent))
                result.Add(map[rootParent]);

            foreach (var pr in prList)
            {
                if (map.TryGetValue(pr, out var ps))
                    result.Add(ps);
            }

            return result;
        }

        public List<string> ConvertToHierarchicalJsonStrings(string sqlQuery)
        {
            var products = ConvertSqlToHierarchicalJsons(sqlQuery);
            return products
                .Select(p => JsonConvert.SerializeObject(p, Formatting.Indented))
                .ToList();
        }

        public void SaveHierarchicalJsonFiles(string sqlQuery, string basePath = "")
        {
            var products = ConvertSqlToHierarchicalJsons(sqlQuery);

            for (int i = 0; i < products.Count; i++)
            {
                var product = products[i];

                var hijos = product.estructura?
                    .Select(rel => rel.First(c => c.campo == "codigo").valor)
                    .Take(3)
                    .ToList() ?? new List<string>();

                string hijosStr = hijos.Count == 0 ? "sin_hijos" : string.Join("_", hijos);

                string json = JsonConvert.SerializeObject(product, Formatting.Indented);

                string fileName =
                    $"{basePath}relacion_{i + 1:D3}_padre_{product.producto}_hijos_{hijosStr}.json";

                System.IO.File.WriteAllText(fileName, json);

                Console.WriteLine($"Guardado: {fileName}");
            }
        }

        public void ProcessHierarchicalJsons(string sqlQuery)
        {
            var products = ConvertSqlToHierarchicalJsons(sqlQuery);

            Console.WriteLine($"Total de JSONs jerárquicos generados: {products.Count}");
            Console.WriteLine(new string('=', 50));

            for (int i = 0; i < products.Count; i++)
            {
                var product = products[i];
                string json = JsonConvert.SerializeObject(product, Formatting.Indented);

                Console.WriteLine($"JSON #{i + 1}:");
                Console.WriteLine(json);
                Console.WriteLine(new string('-', 30));
            }
        }
    }
}
