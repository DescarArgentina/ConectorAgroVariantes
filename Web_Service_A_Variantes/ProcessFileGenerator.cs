using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Web_Service
{
    public class ProcessFileGenerator
    {
        private string connectionString;
        private string outputDirectory;

        public ProcessFileGenerator(string connectionString, string outputDirectory)
        {
            this.connectionString = connectionString;
            this.outputDirectory = outputDirectory;

            // Crear directorio si no existe
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
        }


        public void GenerateProcessFiles()
        {
            string query = @"
            -- CONSULTA SQL MODIFICADA PARA GENERAR LOS DATOS NECESARIOS
WITH ProcessHierarchy AS ( 
    SELECT 
        CAST(dbo.ProcessOccurrence.id_Table AS BIGINT) AS idTable, 
        COALESCE(dbo.Process.catalogueId, dbo.Operation.catalogueId) AS catalogueId, 
        COALESCE(dbo.Process.name, dbo.Operation.name) AS name, 
        CAST(dbo.ProcessOccurrence.parentRef AS BIGINT) AS ParentRef, 
        COALESCE(dbo.Process.Subtype, dbo.Operation.Subtype) AS subtype, 
        dbo.ProcessOccurrence.idXml ,
		dbo.ProcessRevision.revision AS revision
    FROM dbo.ProcessOccurrence 
    LEFT JOIN dbo.ProcessRevision ON ProcessOccurrence.instancedRef = ProcessRevision.id_Table  
        AND ProcessOccurrence.idXml = ProcessRevision.idXml 
    LEFT JOIN dbo.Process ON Process.id_Table = ProcessRevision.masterRef  
        AND ProcessRevision.idXml = Process.idXml 
    LEFT JOIN dbo.OperationRevision ON OperationRevision.id_Table = ProcessOccurrence.instancedRef  
        AND ProcessOccurrence.idXml = OperationRevision.idXml 
    LEFT JOIN dbo.Operation ON Operation.id_Table = OperationRevision.masterRef  
        AND OperationRevision.idXml = Operation.idXml 
) 
SELECT DISTINCT 
    -- Datos del Padre (Nivel 0)
    padre.catalogueId AS Padre_CatalogueId,
    padre.name AS Padre_Name,
    padre.subtype AS Padre_Subtype,
    
    -- Datos del Hijo (Nivel 1) 
    hijo.catalogueId AS Hijo_CatalogueId,
    hijo.name AS Hijo_Name,
    hijo.subtype AS Hijo_Subtype,
    
    -- Datos adicionales
    workarea.RECURSO_DESCAR,
    nombreWorkarea.name AS Workarea_Name,
    padre.idXml,
    
    -- Generar las líneas de texto directamente en SQL
    CONCAT(
        '0#', padre.catalogueId, '#', padre.revision, '#', padre.name, '#-#', padre.subtype, 
        '#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-'
    ) AS Linea_Padre,
    
    CONCAT(
        '1#', workarea.RECURSO_DESCAR, '#', nombreWorkarea.revision, '#', COALESCE(nombreWorkarea.name, hijo.name), 
        '#-#',nombreWorkarea.subType,'#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-#-'
    ) AS Linea_Hijo,
    
    -- Nombre del archivo
    CONCAT(padre.catalogueId, '_', hijo.catalogueId, '.pim') AS NombreArchivo

FROM ProcessHierarchy abuelo 
INNER JOIN ProcessHierarchy padre ON padre.ParentRef = abuelo.idTable  
    AND padre.idXml = abuelo.idXml 
INNER JOIN ProcessHierarchy hijo ON hijo.ParentRef = padre.idTable  
    AND hijo.idXml = padre.idXml 
INNER JOIN [Workarea_Mega].[dbo].[Workarea] workarea ON workarea.CODIGO = TRY_CAST(RIGHT(abuelo.catalogueId,6) AS INT) 
    AND workarea.DESCOP_SIN_PUNTO = hijo.name 
LEFT JOIN (
SELECT Product.id_Table, Product.idXml, Product.name, ProductRevision.revision, Product.productId, Product.subType
  FROM [BOEAgrometal].[dbo].[Product]
  INNER JOIN [BOEAgrometal].[dbo].[ProductRevision] ON ProductRevision.masterRef = Product.id_Table
  --WHERE productId = '451296'
  UNION ALL
SELECT Workarea.id_Table, Workarea.idXml, Workarea.name, WorkAreaRevision.revision, Workarea.catalogueId, WorkArea.subType
FROM [BOEAgrometal].[dbo].[Workarea]
INNER JOIN [BOEAgrometal].[dbo].[WorkAreaRevision] ON WorkAreaRevision.masterRef = WorkArea.id_Table
) nombreWorkarea ON CAST(workarea.RECURSO_DESCAR AS nvarchar) = nombreWorkarea.productId 
WHERE padre.subtype = 'MEProcess' AND hijo.subtype = 'MEOP'
ORDER BY padre.catalogueId, hijo.catalogueId;";
            string inicial = @"# ==============================================================================
#  File          : C:\Users\infodba\Desktop\pruebas cantidades-1.pim
#  Description   : Teamcenter Manufacturing process import file
#                  This file was converted from PIM Excel file:
#                  C:\Users\infodba\Desktop\pruebas cantidades.txt
#  Creation Date : ju. nov. 07 13:01:17 -0200 2024
# ==============================================================================
#
#COL level item rev name descr type link_root plant_root consumed resource required workarea attributes owner group predecessor duration act_name act_desc frequency category unittime occ_note occ_eff abs_occ qty uom seq matrix status occs activities loadif filePath
#DELIMITER #
#ALT_DELIMITER ;
#DEFAULT_REV A
#";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 300;
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            int fileCount = 0;

                            while (reader.Read())
                            {
                                string fileName = reader["NombreArchivo"].ToString();
                                string lineaPadre = reader["Linea_Padre"].ToString();
                                string lineaHijo = reader["Linea_Hijo"].ToString();

                                // Crear el contenido del archivo
                                string fileContent = inicial + Environment.NewLine + lineaPadre + Environment.NewLine + lineaHijo;

                                // Ruta completa del archivo
                                string filePath = Path.Combine(outputDirectory, fileName);

                                // Escribir el archivo
                                File.WriteAllText(filePath, fileContent);

                                fileCount++;
                                Console.WriteLine($"Archivo creado: {fileName}");
                            }

                            Console.WriteLine($"\nTotal de archivos generados: {fileCount}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }
    }
}
