using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
namespace Web_Service
{
    public static class Tabla_SB1
    {
        private const string BaseUrlPost = "http://119.8.73.193:8076/rest/TCProductos/Incluir/";
        private const string BaseUrlPut = "http://119.8.73.193:8076/rest/TCProductos/Modificar/";
        private const string Username = "USERREST";
        private const string Password = "restagr";
        private const string consultaSB1_BOP_Con1Workarea = @"
WITH CTE_Hierarchy AS (
      SELECT
          o.id_Table,
          pr.name,
          CASE
              WHEN LEFT(p.catalogueId, 2) = 'P-'
                  THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
              ELSE p.catalogueId
          END AS catalogueId,
          CAST(o.parentRef AS INT) AS parentRef,
          pr.revision,
          pr.subType,
          o.idXml,
          'PA' AS Tipo,
          'UN' AS unMedida,
          waData.WorkArea_CatalogueId,
          waData.WorkArea_Nombre,
          waData.WorkArea_Revision,
          CAST('' AS NVARCHAR(MAX)) AS bl_formula,
          CAST('' AS NVARCHAR(MAX)) AS idContext
      FROM ProcessOccurrence o
      INNER JOIN ProcessRevision pr ON o.instancedRef = pr.id_Table
      INNER JOIN Process p          ON pr.masterRef   = p.id_Table
      OUTER APPLY (
          SELECT TOP 1
              wa.catalogueId AS WorkArea_CatalogueId,
              wa.name        AS WorkArea_Nombre,
              war.revision   AS WorkArea_Revision
          FROM Occurrence wao
          INNER JOIN WorkAreaRevision war ON war.id_Table = wao.instancedRef
          INNER JOIN WorkArea wa          ON wa.id_Table  = war.masterRef
          WHERE wao.parentRef = o.id_Table
      ) AS waData
  ),
  CTE_Niveles AS (
      SELECT
          h.*,
          0 AS Nivel
      FROM CTE_Hierarchy h
      WHERE h.parentRef IS NULL OR h.parentRef = 0

      UNION ALL

      SELECT
          h.*,
          n.Nivel + 1
      FROM CTE_Hierarchy h
      INNER JOIN CTE_Niveles n ON h.parentRef = n.id_Table
  )
  SELECT *
  FROM (
      SELECT
          COALESCE(Parent.name, '')        AS Nombre_Padre,
          COALESCE(Parent.catalogueId, '') AS Process_codigo,
          CASE
              WHEN LEFT(Child.catalogueId, 2) = 'PR'
                  THEN Child.name + ' - Proceso: ' + COALESCE(Parent.catalogueId, '')
              ELSE Child.name
          END AS Nombre_Hijo,
          Child.catalogueId  AS Codigo_Hijo,
          Child.subType      AS Subtype_Hijo,
          Child.revision     AS Revision,
          1                  AS CantidadHijo_Total,
          nChild.Nivel       AS Nivel,
          CASE
              WHEN LEFT(Child.catalogueId, 2) = 'PR'
               AND RIGHT(Child.catalogueId, 1) = 'T'
                  THEN 'SV'
              WHEN Child.subType IN ('Agm4_RepCompradoRevision','Agm4_MatPrimaRevision')
                  THEN 'MP'
              WHEN LEFT(Child.catalogueId, 2) = 'PR'
                  THEN 'PI'
              ELSE 'PA'
          END AS Tipo,
          Child.unMedida,
          Child.WorkArea_CatalogueId,
          Child.WorkArea_Nombre,
          Child.WorkArea_Revision,
          Child.bl_formula,
          Child.idContext
      FROM CTE_Niveles nChild
      LEFT JOIN CTE_Niveles   nParent ON nChild.parentRef = nParent.id_Table
      LEFT JOIN CTE_Hierarchy Parent  ON nParent.id_Table = Parent.id_Table
      LEFT JOIN CTE_Hierarchy Child   ON nChild.id_Table  = Child.id_Table

      UNION ALL

      SELECT
          Operation.name   AS Nombre_Padre,
          p2.catalogueId   AS Process_codigo,
          CASE
              WHEN LEFT(x.CodigoNorm, 2) = 'PR'
                  THEN p.name + '- Proceso: ' + COALESCE(p2.catalogueId, '')
              ELSE p.name
          END AS Nombre_Hijo,
          x.CodigoNorm AS Codigo_Hijo,
          pr.subType   AS Subtype_Hijo,
          pr.revision  AS Revision,
          COUNT(p.productId) AS CantidadHijo_Total,
          3 AS Nivel,
          CASE
              WHEN LEFT(x.CodigoNorm, 2) = 'PR'
               AND RIGHT(x.CodigoNorm, 1) = 'T'
                  THEN 'SV'
              WHEN pr.subType IN ('Agm4_RepCompradoRevision','Agm4_MatPrimaRevision')
                  THEN 'MP'
              WHEN LEFT(x.CodigoNorm, 2) = 'PR'
                  THEN 'PI'
              ELSE 'PA'
          END AS Tipo,
          COALESCE(
              NULLIF(MAX(uomTagProd.ValueTag), ''),
              MAX(uomUnitProd.UnidadMedida),
              'UN'
          ) AS unMedida,
          CAST(NULL AS varchar(50))  AS WorkArea_CatalogueId,
          CAST(NULL AS varchar(255)) AS WorkArea_Nombre,
          CAST(NULL AS varchar(20))  AS WorkArea_Revision,
          ISNULL(MAX(bl_pick.bl_formula_val), '') AS bl_formula,
          MAX(CASE
              WHEN ISNULL(bl_pick.bl_formula_val, '') != ''
               AND CHARINDEX('[''', bl_pick.bl_formula_val) > 0
               AND CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val)) > 0
              THEN SUBSTRING(
                      bl_pick.bl_formula_val,
                      CHARINDEX('[''', bl_pick.bl_formula_val) + 2,
                      CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val))
                        - CHARINDEX('[''', bl_pick.bl_formula_val) - 2)
              ELSE ''
          END) AS idContext
      FROM Occurrence
      INNER JOIN ProductRevision pr ON pr.id_Table = Occurrence.instancedRef
      INNER JOIN Product p          ON p.id_Table  = pr.masterRef

      LEFT JOIN ProcessOccurrence o   ON Occurrence.parentRef = o.id_Table
      LEFT JOIN OperationRevision op  ON op.id_Table = o.instancedRef
      LEFT JOIN Operation             ON Operation.id_Table = op.masterRef

      LEFT JOIN ProcessOccurrence o2  ON o2.id_Table     = o.parentRef
      LEFT JOIN ProcessRevision pr2   ON o2.instancedRef = pr2.id_Table
      LEFT JOIN Process p2            ON pr2.masterRef   = p2.id_Table

      OUTER APPLY (
          SELECT TOP 1 s.ValueTag, s.DataRef, s.UnitIdTable
          FROM (
              SELECT
                  uvp.value AS ValueTag,
                  uvp.dataRef AS DataRef,
                  TRY_CONVERT(int, REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(uvp.dataRef)),'#',''),'ID',''),'id','')) AS
  UnitIdTable,
                  1 AS prio
              FROM UserValue_Product uvp
              WHERE uvp.idXml     = p.idXml
                AND uvp.title     = 'uom_tag'
                AND uvp.id_Father = p.id_Table

              UNION ALL

              SELECT
                  uvp2.value,
                  uvp2.dataRef,
                  TRY_CONVERT(int, REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(uvp2.dataRef)),'#',''),'ID',''),'id','')),
                  2
              FROM UserData udp
              INNER JOIN UserValue_Product uvp2
                  ON uvp2.id_Father = udp.id_Table
                 AND uvp2.idXml     = udp.idXml
              WHERE udp.idXml     = p.idXml
                AND udp.id_Father  = p.id_Table
                AND uvp2.title     = 'uom_tag'
          ) s
          ORDER BY s.prio
      ) AS uomTagProd

      OUTER APPLY (
          SELECT TOP 1 t.UnidadMedida
          FROM (
              SELECT
                  CASE
                      WHEN uvu.title = 'Agm4_Kilogramos' THEN uvu.value
                      WHEN uvu.title = 'Agm4_Litros'     THEN uvu.value
                      WHEN uvu.title = 'Agm4_Metros'     THEN uvu.value
                      WHEN uvu.title = 'Agm4_Unidad'     THEN uvu.value
                      ELSE NULL
                  END AS UnidadMedida,
                  CASE uvu.title
                      WHEN 'Agm4_Kilogramos' THEN 1
                      WHEN 'Agm4_Litros'     THEN 2
                      WHEN 'Agm4_Metros'     THEN 3
                      WHEN 'Agm4_Unidad'     THEN 4
                      ELSE 99
                  END AS prio
              FROM Unit u
              INNER JOIN UserValue_Unit uvu
                  ON uvu.id_Father = u.id_Table
                 AND uvu.idXml     = u.idXml
              WHERE u.idXml = p.idXml
                AND uomTagProd.UnitIdTable IS NOT NULL
                AND u.id_Table = uomTagProd.UnitIdTable
                AND uvu.title IN ('Agm4_Unidad','Agm4_Kilogramos','Agm4_Litros','Agm4_Metros')

              UNION ALL

              SELECT
                  CASE
                      WHEN uuv.title = 'Agm4_Kilogramos' THEN uuv.value
                      WHEN uuv.title = 'Agm4_Litros'     THEN uuv.value
                      WHEN uuv.title = 'Agm4_Metros'     THEN uuv.value
                      WHEN uuv.title = 'Agm4_Unidad'     THEN uuv.value
                      ELSE NULL
                  END,
                  CASE uuv.title
                      WHEN 'Agm4_Kilogramos' THEN 11
                      WHEN 'Agm4_Litros'     THEN 12
                      WHEN 'Agm4_Metros'     THEN 13
                      WHEN 'Agm4_Unidad'     THEN 14
                      ELSE 199
                  END
              FROM Unit u
              INNER JOIN UserData ud_u
                  ON ud_u.id_Father = u.id_Table
                 AND ud_u.idXml     = u.idXml
              INNER JOIN UserValue_UserData uuv
                  ON uuv.id_Father = ud_u.id_Table
                 AND uuv.idXml     = ud_u.idXml
              WHERE u.idXml = p.idXml
                AND uomTagProd.UnitIdTable IS NOT NULL
                AND u.id_Table = uomTagProd.UnitIdTable
                AND uuv.title IN ('Agm4_Unidad','Agm4_Kilogramos','Agm4_Litros','Agm4_Metros')
          ) t
          WHERE t.UnidadMedida IS NOT NULL AND LTRIM(RTRIM(t.UnidadMedida)) <> ''
          ORDER BY t.prio
      ) AS uomUnitProd

      CROSS APPLY (
          SELECT
              CASE
                  WHEN LEFT(p.productId, 1) = 'E'
                      THEN RIGHT(p.productId, LEN(p.productId) - 1)
                  ELSE p.productId
              END AS CodigoNorm
      ) AS x

      OUTER APPLY (
          SELECT TOP 1 uvo.value AS bl_formula_val
          FROM Occurrence o_formula
          INNER JOIN UserValue_Occurrence uvo
              ON uvo.id_Father = o_formula.id_Table
             AND uvo.title = 'bl_formula'
          WHERE o_formula.instancedRef = Occurrence.instancedRef
            AND uvo.value IS NOT NULL
            AND LTRIM(RTRIM(uvo.value)) != ''
      ) bl_pick

      WHERE
          (
              Occurrence.subType = 'MEConsumed'
              OR (
                  (Occurrence.subType IS NULL OR LTRIM(RTRIM(Occurrence.subType)) = '')
                  AND op.id_Table IS NOT NULL
              )
          )

      GROUP BY
          Operation.name,
          p2.catalogueId,
          p.name,
          x.CodigoNorm,
          pr.subType,
          pr.revision
  ) R
  ORDER BY
      R.Nivel,
      R.Codigo_Hijo DESC;
    ";
        private const string consultaSB1_BOP_ConMasWorkareas = @"
WITH CTE_Hierarchy AS (
      SELECT
          o.id_Table,
          pr.name,
          CASE
              WHEN LEFT(p.catalogueId, 2) = 'P-'
                  THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
              ELSE p.catalogueId
          END AS catalogueId,
          CAST(o.parentRef AS INT) AS parentRef,
          pr.revision,
          pr.subType,
          o.idXml,
          'PA' AS Tipo,
          'UN' AS unMedida,
          waData.WorkArea_CatalogueId,
          waData.WorkArea_Nombre,
          waData.WorkArea_Revision,
          CAST('' AS NVARCHAR(MAX)) AS bl_formula,
          CAST('' AS NVARCHAR(MAX)) AS idContext
      FROM ProcessOccurrence o
      INNER JOIN ProcessRevision pr ON o.instancedRef = pr.id_Table
      INNER JOIN Process p          ON pr.masterRef   = p.id_Table
      OUTER APPLY (
          SELECT TOP 1
              wa.catalogueId AS WorkArea_CatalogueId,
              wa.name        AS WorkArea_Nombre,
              war.revision   AS WorkArea_Revision
          FROM WorkAreaOccurrence wao
          INNER JOIN WorkAreaRevision war ON war.id_Table = wao.instancedRef
          INNER JOIN WorkArea wa          ON wa.id_Table  = war.masterRef
          WHERE wao.parentRef = o.id_Table
      ) AS waData
  ),
  CTE_Niveles AS (
      SELECT
          h.*,
          0 AS Nivel
      FROM CTE_Hierarchy h
      WHERE h.parentRef IS NULL OR h.parentRef = 0

      UNION ALL

      SELECT
          h.*,
          n.Nivel + 1
      FROM CTE_Hierarchy h
      INNER JOIN CTE_Niveles n ON h.parentRef = n.id_Table
  )
  SELECT *
  FROM (
      SELECT
          COALESCE(Parent.name, '')        AS Nombre_Padre,
          COALESCE(Parent.catalogueId, '') AS Process_codigo,
          CASE
              WHEN LEFT(Child.catalogueId, 2) = 'PR'
                  THEN Child.name + ' - Proceso: ' + COALESCE(Parent.catalogueId, '')
              ELSE Child.name
          END AS Nombre_Hijo,
          Child.catalogueId  AS Codigo_Hijo,
          Child.subType      AS Subtype_Hijo,
          Child.revision     AS Revision,
          1                  AS CantidadHijo_Total,
          nChild.Nivel       AS Nivel,
          CASE
              WHEN LEFT(Child.catalogueId, 2) = 'PR'
               AND RIGHT(Child.catalogueId, 1) = 'T'
                  THEN 'SV'
              WHEN Child.subType IN ('Agm4_RepCompradoRevision','Agm4_MatPrimaRevision')
                  THEN 'MP'
              WHEN LEFT(Child.catalogueId, 2) = 'PR'
                  THEN 'PI'
              ELSE 'PA'
          END AS Tipo,
          Child.unMedida,
          Child.WorkArea_CatalogueId,
          Child.WorkArea_Nombre,
          Child.WorkArea_Revision,
          Child.bl_formula,
          Child.idContext
      FROM CTE_Niveles nChild
      LEFT JOIN CTE_Niveles   nParent ON nChild.parentRef = nParent.id_Table
      LEFT JOIN CTE_Hierarchy Parent  ON nParent.id_Table = Parent.id_Table
      LEFT JOIN CTE_Hierarchy Child   ON nChild.id_Table  = Child.id_Table

      UNION ALL

      SELECT
          Operation.name   AS Nombre_Padre,
          p2.catalogueId   AS Process_codigo,
          CASE
              WHEN LEFT(x.CodigoNorm, 2) = 'PR'
                  THEN p.name + '- Proceso: ' + COALESCE(p2.catalogueId, '')
              ELSE p.name
          END AS Nombre_Hijo,
          x.CodigoNorm AS Codigo_Hijo,
          pr.subType   AS Subtype_Hijo,
          pr.revision  AS Revision,
          COUNT(p.productId) AS CantidadHijo_Total,
          3 AS Nivel,
          CASE
              WHEN LEFT(x.CodigoNorm, 2) = 'PR'
               AND RIGHT(x.CodigoNorm, 1) = 'T'
                  THEN 'SV'
              WHEN pr.subType IN ('Agm4_RepCompradoRevision','Agm4_MatPrimaRevision')
                  THEN 'MP'
              WHEN LEFT(x.CodigoNorm, 2) = 'PR'
                  THEN 'PI'
              ELSE 'PA'
          END AS Tipo,
          COALESCE(
              NULLIF(MAX(uomTagProd.ValueTag), ''),
              MAX(uomUnitProd.UnidadMedida),
              'UN'
          ) AS unMedida,
          CAST(NULL AS varchar(50))  AS WorkArea_CatalogueId,
          CAST(NULL AS varchar(255)) AS WorkArea_Nombre,
          CAST(NULL AS varchar(20))  AS WorkArea_Revision,
          ISNULL(MAX(bl_pick.bl_formula_val), '') AS bl_formula,
          MAX(CASE
              WHEN ISNULL(bl_pick.bl_formula_val, '') != ''
               AND CHARINDEX('[''', bl_pick.bl_formula_val) > 0
               AND CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val)) > 0
              THEN SUBSTRING(
                      bl_pick.bl_formula_val,
                      CHARINDEX('[''', bl_pick.bl_formula_val) + 2,
                      CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val))
                        - CHARINDEX('[''', bl_pick.bl_formula_val) - 2)
              ELSE ''
          END) AS idContext
      FROM Occurrence
      INNER JOIN ProductRevision pr ON pr.id_Table = Occurrence.instancedRef
      INNER JOIN Product p          ON p.id_Table  = pr.masterRef

      LEFT JOIN ProcessOccurrence o   ON Occurrence.parentRef = o.id_Table
      LEFT JOIN OperationRevision op  ON op.id_Table = o.instancedRef
      LEFT JOIN Operation             ON Operation.id_Table = op.masterRef

      LEFT JOIN ProcessOccurrence o2  ON o2.id_Table     = o.parentRef
      LEFT JOIN ProcessRevision pr2   ON o2.instancedRef = pr2.id_Table
      LEFT JOIN Process p2            ON pr2.masterRef   = p2.id_Table

      OUTER APPLY (
          SELECT TOP 1 s.ValueTag, s.DataRef, s.UnitIdTable
          FROM (
              SELECT
                  uvp.value AS ValueTag,
                  uvp.dataRef AS DataRef,
                  TRY_CONVERT(int, REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(uvp.dataRef)),'#',''),'ID',''),'id','')) AS
  UnitIdTable,
                  1 AS prio
              FROM UserValue_Product uvp
              WHERE uvp.idXml     = p.idXml
                AND uvp.title     = 'uom_tag'
                AND uvp.id_Father = p.id_Table

              UNION ALL

              SELECT
                  uvp2.value,
                  uvp2.dataRef,
                  TRY_CONVERT(int, REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(uvp2.dataRef)),'#',''),'ID',''),'id','')),
                  2
              FROM UserData udp
              INNER JOIN UserValue_Product uvp2
                  ON uvp2.id_Father = udp.id_Table
                 AND uvp2.idXml     = udp.idXml
              WHERE udp.idXml     = p.idXml
                AND udp.id_Father  = p.id_Table
                AND uvp2.title     = 'uom_tag'
          ) s
          ORDER BY s.prio
      ) AS uomTagProd

      OUTER APPLY (
          SELECT TOP 1 t.UnidadMedida
          FROM (
              SELECT
                  CASE
                      WHEN uvu.title = 'Agm4_Kilogramos' THEN uvu.value
                      WHEN uvu.title = 'Agm4_Litros'     THEN uvu.value
                      WHEN uvu.title = 'Agm4_Metros'     THEN uvu.value
                      WHEN uvu.title = 'Agm4_Unidad'     THEN uvu.value
                      ELSE NULL
                  END AS UnidadMedida,
                  CASE uvu.title
                      WHEN 'Agm4_Kilogramos' THEN 1
                      WHEN 'Agm4_Litros'     THEN 2
                      WHEN 'Agm4_Metros'     THEN 3
                      WHEN 'Agm4_Unidad'     THEN 4
                      ELSE 99
                  END AS prio
              FROM Unit u
              INNER JOIN UserValue_Unit uvu
                  ON uvu.id_Father = u.id_Table
                 AND uvu.idXml     = u.idXml
              WHERE u.idXml = p.idXml
                AND uomTagProd.UnitIdTable IS NOT NULL
                AND u.id_Table = uomTagProd.UnitIdTable
                AND uvu.title IN ('Agm4_Unidad','Agm4_Kilogramos','Agm4_Litros','Agm4_Metros')

              UNION ALL

              SELECT
                  CASE
                      WHEN uuv.title = 'Agm4_Kilogramos' THEN uuv.value
                      WHEN uuv.title = 'Agm4_Litros'     THEN uuv.value
                      WHEN uuv.title = 'Agm4_Metros'     THEN uuv.value
                      WHEN uuv.title = 'Agm4_Unidad'     THEN uuv.value
                      ELSE NULL
                  END,
                  CASE uuv.title
                      WHEN 'Agm4_Kilogramos' THEN 11
                      WHEN 'Agm4_Litros'     THEN 12
                      WHEN 'Agm4_Metros'     THEN 13
                      WHEN 'Agm4_Unidad'     THEN 14
                      ELSE 199
                  END
              FROM Unit u
              INNER JOIN UserData ud_u
                  ON ud_u.id_Father = u.id_Table
                 AND ud_u.idXml     = u.idXml
              INNER JOIN UserValue_UserData uuv
                  ON uuv.id_Father = ud_u.id_Table
                 AND uuv.idXml     = ud_u.idXml
              WHERE u.idXml = p.idXml
                AND uomTagProd.UnitIdTable IS NOT NULL
                AND u.id_Table = uomTagProd.UnitIdTable
                AND uuv.title IN ('Agm4_Unidad','Agm4_Kilogramos','Agm4_Litros','Agm4_Metros')
          ) t
          WHERE t.UnidadMedida IS NOT NULL AND LTRIM(RTRIM(t.UnidadMedida)) <> ''
          ORDER BY t.prio
      ) AS uomUnitProd

      CROSS APPLY (
          SELECT
              CASE
                  WHEN LEFT(p.productId, 1) = 'E'
                      THEN RIGHT(p.productId, LEN(p.productId) - 1)
                  ELSE p.productId
              END AS CodigoNorm
      ) AS x

      OUTER APPLY (
          SELECT TOP 1 uvo.value AS bl_formula_val
          FROM Occurrence o_formula
          INNER JOIN UserValue_Occurrence uvo
              ON uvo.id_Father = o_formula.id_Table
             AND uvo.title = 'bl_formula'
          WHERE o_formula.instancedRef = Occurrence.instancedRef
            AND uvo.value IS NOT NULL
            AND LTRIM(RTRIM(uvo.value)) != ''
      ) bl_pick

      WHERE
          (
              Occurrence.subType = 'MEConsumed'
              OR (
                  (Occurrence.subType IS NULL OR LTRIM(RTRIM(Occurrence.subType)) = '')
                  AND op.id_Table IS NOT NULL
              )
          )

      GROUP BY
          Operation.name,
          p2.catalogueId,
          p.name,
          x.CodigoNorm,
          pr.subType,
          pr.revision
  ) R
  ORDER BY
      R.Nivel,
      R.Codigo_Hijo DESC;
    ";
        private const string consultaSB1_BOP_SinWorkArea = @"
WITH CTE_Hierarchy AS (
SELECT
o.id_Table,
pr.name,
CASE
WHEN LEFT(p.catalogueId, 2) = 'P-'
THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
ELSE p.catalogueId
END AS catalogueId,
CAST(o.parentRef AS INT) AS parentRef,
pr.revision,
pr.subType,
o.idXml,
'PA' AS Tipo,
'UN' AS unMedida,
CAST(NULL AS varchar(50)) AS WorkArea_CatalogueId,
CAST(NULL AS varchar(255)) AS WorkArea_Nombre,
CAST(NULL AS varchar(20)) AS WorkArea_Revision,
CAST('' AS NVARCHAR(MAX)) AS bl_formula,
CAST('' AS NVARCHAR(MAX)) AS idContext
FROM ProcessOccurrence o
INNER JOIN ProcessRevision pr ON o.instancedRef = pr.id_Table
INNER JOIN Process p ON pr.masterRef = p.id_Table
),
CTE_Niveles AS (
SELECT
h.*,
0 AS Nivel
FROM CTE_Hierarchy h
WHERE h.parentRef IS NULL OR h.parentRef = 0

UNION ALL

SELECT
h.*,
n.Nivel + 1
FROM CTE_Hierarchy h
INNER JOIN CTE_Niveles n ON h.parentRef = n.id_Table
)
SELECT *
FROM (
SELECT
COALESCE(Parent.name, '') AS Nombre_Padre,
COALESCE(Parent.catalogueId, '') AS Process_codigo,
CASE
WHEN LEFT(Child.catalogueId, 2) = 'PR'
THEN Child.name + ' - Proceso: ' + COALESCE(Parent.catalogueId, '')
ELSE Child.name
END AS Nombre_Hijo,
Child.catalogueId AS Codigo_Hijo,
Child.subType AS Subtype_Hijo,
Child.revision AS Revision,
1 AS CantidadHijo_Total,
nChild.Nivel AS Nivel,
CASE
WHEN LEFT(Child.catalogueId, 2) = 'PR'
AND RIGHT(Child.catalogueId, 1) = 'T'
THEN 'SV'
WHEN Child.subType IN ('Agm4_RepCompradoRevision','Agm4_MatPrimaRevision')
THEN 'MP'
WHEN LEFT(Child.catalogueId, 2) = 'PR'
THEN 'PI'
ELSE 'PA'
END AS Tipo,
Child.unMedida,
Child.WorkArea_CatalogueId,
Child.WorkArea_Nombre,
Child.WorkArea_Revision,
Child.bl_formula,
Child.idContext
FROM CTE_Niveles nChild
LEFT JOIN CTE_Niveles nParent ON nChild.parentRef = nParent.id_Table
LEFT JOIN CTE_Hierarchy Parent ON nParent.id_Table = Parent.id_Table
LEFT JOIN CTE_Hierarchy Child ON nChild.id_Table = Child.id_Table

UNION ALL

SELECT
Operation.name AS Nombre_Padre,
p2.catalogueId AS Process_codigo,
CASE
WHEN LEFT(x.CodigoNorm, 2) = 'PR'
THEN p.name + '- Proceso: ' + COALESCE(p2.catalogueId, '')
ELSE p.name
END AS Nombre_Hijo,
x.CodigoNorm AS Codigo_Hijo,
pr.subType AS Subtype_Hijo,
pr.revision AS Revision,
COUNT(p.productId) AS CantidadHijo_Total,
3 AS Nivel,
CASE
WHEN LEFT(x.CodigoNorm, 2) = 'PR'
AND RIGHT(x.CodigoNorm, 1) = 'T'
THEN 'SV'
WHEN pr.subType IN ('Agm4_RepCompradoRevision','Agm4_MatPrimaRevision')
THEN 'MP'
WHEN LEFT(x.CodigoNorm, 2) = 'PR'
THEN 'PI'
ELSE 'PA'
END AS Tipo,
COALESCE(
NULLIF(MAX(uomTagProd.ValueTag), ''),
MAX(uomUnitProd.UnidadMedida),
'UN'
) AS unMedida,
CAST(NULL AS varchar(50)) AS WorkArea_CatalogueId,
CAST(NULL AS varchar(255)) AS WorkArea_Nombre,
CAST(NULL AS varchar(20)) AS WorkArea_Revision,
ISNULL(MAX(bl_pick.bl_formula_val), '') AS bl_formula,
MAX(CASE
    WHEN ISNULL(bl_pick.bl_formula_val, '') != ''
     AND CHARINDEX('[''', bl_pick.bl_formula_val) > 0
     AND CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val)) > 0
    THEN SUBSTRING(
            bl_pick.bl_formula_val,
            CHARINDEX('[''', bl_pick.bl_formula_val) + 2,
            CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val))
              - CHARINDEX('[''', bl_pick.bl_formula_val) - 2)
    ELSE ''
END) AS idContext
FROM Occurrence
INNER JOIN ProductRevision pr ON pr.id_Table = Occurrence.instancedRef
INNER JOIN Product p ON p.id_Table = pr.masterRef

LEFT JOIN ProcessOccurrence o ON Occurrence.parentRef = o.id_Table
LEFT JOIN OperationRevision op ON op.id_Table = o.instancedRef
LEFT JOIN Operation ON Operation.id_Table = op.masterRef

LEFT JOIN ProcessOccurrence o2 ON o2.id_Table = o.parentRef
LEFT JOIN ProcessRevision pr2 ON o2.instancedRef = pr2.id_Table
LEFT JOIN Process p2 ON pr2.masterRef = p2.id_Table

OUTER APPLY (
SELECT TOP 1 s.ValueTag, s.DataRef, s.UnitIdTable
FROM (
SELECT
uvp.value AS ValueTag,
uvp.dataRef AS DataRef,
TRY_CONVERT(int, REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(uvp.dataRef)),'#',''),'ID',''),'id','')) AS UnitIdTable,
1 AS prio
FROM UserValue_Product uvp
WHERE uvp.idXml = p.idXml
AND uvp.title = 'uom_tag'
AND uvp.id_Father = p.id_Table

UNION ALL

SELECT
uvp2.value,
uvp2.dataRef,
TRY_CONVERT(int, REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(uvp2.dataRef)),'#',''),'ID',''),'id','')),
2
FROM UserData udp
INNER JOIN UserValue_Product uvp2
ON uvp2.id_Father = udp.id_Table
AND uvp2.idXml = udp.idXml
WHERE udp.idXml = p.idXml
AND udp.id_Father = p.id_Table
AND uvp2.title = 'uom_tag'
) s
ORDER BY s.prio
) AS uomTagProd

OUTER APPLY (
SELECT TOP 1 t.UnidadMedida
FROM (
SELECT
CASE
WHEN uvu.title = 'Agm4_Kilogramos' THEN uvu.value
WHEN uvu.title = 'Agm4_Litros' THEN uvu.value
WHEN uvu.title = 'Agm4_Metros' THEN uvu.value
WHEN uvu.title = 'Agm4_Unidad' THEN uvu.value
ELSE NULL
END AS UnidadMedida,
CASE uvu.title
WHEN 'Agm4_Kilogramos' THEN 1
WHEN 'Agm4_Litros' THEN 2
WHEN 'Agm4_Metros' THEN 3
WHEN 'Agm4_Unidad' THEN 4
ELSE 99
END AS prio
FROM Unit u
INNER JOIN UserValue_Unit uvu
ON uvu.id_Father = u.id_Table
AND uvu.idXml = u.idXml
WHERE u.idXml = p.idXml
AND uomTagProd.UnitIdTable IS NOT NULL
AND u.id_Table = uomTagProd.UnitIdTable
AND uvu.title IN ('Agm4_Unidad','Agm4_Kilogramos','Agm4_Litros','Agm4_Metros')

UNION ALL

SELECT
CASE
WHEN uuv.title = 'Agm4_Kilogramos' THEN uuv.value
WHEN uuv.title = 'Agm4_Litros' THEN uuv.value
WHEN uuv.title = 'Agm4_Metros' THEN uuv.value
WHEN uuv.title = 'Agm4_Unidad' THEN uuv.value
ELSE NULL
END,
CASE uuv.title
WHEN 'Agm4_Kilogramos' THEN 11
WHEN 'Agm4_Litros' THEN 12
WHEN 'Agm4_Metros' THEN 13
WHEN 'Agm4_Unidad' THEN 14
ELSE 199
END
FROM Unit u
INNER JOIN UserData ud_u
ON ud_u.id_Father = u.id_Table
AND ud_u.idXml = u.idXml
INNER JOIN UserValue_UserData uuv
ON uuv.id_Father = ud_u.id_Table
AND uuv.idXml = ud_u.idXml
WHERE u.idXml = p.idXml
AND uomTagProd.UnitIdTable IS NOT NULL
AND u.id_Table = uomTagProd.UnitIdTable
AND uuv.title IN ('Agm4_Unidad','Agm4_Kilogramos','Agm4_Litros','Agm4_Metros')
) t
WHERE t.UnidadMedida IS NOT NULL AND LTRIM(RTRIM(t.UnidadMedida)) <> ''
ORDER BY t.prio
) AS uomUnitProd

CROSS APPLY (
SELECT
CASE
WHEN LEFT(p.productId, 1) = 'E'
THEN RIGHT(p.productId, LEN(p.productId) - 1)
ELSE p.productId
END AS CodigoNorm
) AS x

OUTER APPLY (
    SELECT TOP 1 uvo.value AS bl_formula_val
    FROM Occurrence o_formula
    INNER JOIN UserValue_Occurrence uvo
        ON uvo.id_Father = o_formula.id_Table
       AND uvo.title = 'bl_formula'
    WHERE o_formula.instancedRef = Occurrence.instancedRef
      AND uvo.value IS NOT NULL
      AND LTRIM(RTRIM(uvo.value)) != ''
) bl_pick

WHERE
(
Occurrence.subType = 'MEConsumed'
OR (
(Occurrence.subType IS NULL OR LTRIM(RTRIM(Occurrence.subType)) = '')
AND op.id_Table IS NOT NULL
)
)

GROUP BY
Operation.name,
p2.catalogueId,
p.name,
x.CodigoNorm,
pr.subType,
pr.revision
) R
ORDER BY
R.Nivel,
R.Codigo_Hijo DESC;
    ";
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        // Reutilizar HttpClient (MUY importante en procesos/servicios)
        private static readonly HttpClient _client = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2) // ajustá si Protheus tarda más
            };

            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Username}:{Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            return client;
        }

        private static bool IsRetryableStatus(HttpStatusCode code)
            => (int)code >= 500 && (int)code <= 599;

        private static bool IsRetryableException(Exception ex)
            => ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is IOException;

        private sealed class WsError
        {
            public int? errorCode { get; set; }
            public string errorMessage { get; set; }
        }

        private static bool TryParseWsError(string body, out int? errorCode, out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(body))
                return false;

            try
            {
                // Caso típico: {"errorCode":10,"errorMessage":"..."}
                var err = JsonConvert.DeserializeObject<WsError>(body);
                if (err?.errorCode != null)
                {
                    errorCode = err.errorCode;
                    errorMessage = err.errorMessage;
                    return true;
                }

                // Si a veces viene con estructura distinta, intento por JObject
                var j = JObject.Parse(body);
                var codeToken = j["errorCode"];
                if (codeToken != null && int.TryParse(codeToken.ToString(), out var code))
                {
                    errorCode = code;
                    errorMessage = j["errorMessage"]?.ToString();
                    return true;
                }
            }
            catch
            {
                // Body no JSON o estructura inesperada
            }

            return false;
        }

        private static void LogErrorProtheusIfAny(string tabla, string metodo, string producto, string responseBody)
        {
            if (TryParseWsError(responseBody, out var code, out var msg))
            {
                Utilidades.EscribirErrorProtheus(tabla, metodo, producto, code, msg);
            }
        }

        public static void EnsureTablasOpcionalesSB1(SqlConnection conn)
        {
            const string sql = @"
IF OBJECT_ID('dbo.UserValue_Occurrence','U') IS NULL
    CREATE TABLE dbo.UserValue_Occurrence (
        id_Table BIGINT NULL,
        id_Father BIGINT NULL,
        idXml INT NULL,
        title NVARCHAR(255) NULL,
        value NVARCHAR(MAX) NULL
    );

IF OBJECT_ID('dbo.UserData','U') IS NULL
    CREATE TABLE dbo.UserData (
        id_Table BIGINT NULL,
        id_Father BIGINT NULL,
        idXml INT NULL
    );

IF OBJECT_ID('dbo.UserValue_UserData','U') IS NULL
    CREATE TABLE dbo.UserValue_UserData (
        id_Table BIGINT NULL,
        id_Father BIGINT NULL,
        idXml INT NULL,
        title NVARCHAR(255) NULL,
        value NVARCHAR(MAX) NULL
    );

IF OBJECT_ID('dbo.Unit','U') IS NULL
    CREATE TABLE dbo.Unit (
        id_Table BIGINT NULL,
        idXml INT NULL
    );

IF OBJECT_ID('dbo.UserValue_Unit','U') IS NULL
    CREATE TABLE dbo.UserValue_Unit (
        id_Table BIGINT NULL,
        id_Father BIGINT NULL,
        idXml INT NULL,
        title NVARCHAR(255) NULL,
        value NVARCHAR(MAX) NULL
    );

IF OBJECT_ID('dbo.Form','U') IS NULL
    CREATE TABLE dbo.Form (
        id_Table BIGINT NULL,
        idXml INT NULL,
        name NVARCHAR(255) NULL
    );

IF OBJECT_ID('dbo.ProductRevisionView','U') IS NULL
    CREATE TABLE dbo.ProductRevisionView (
        id_Table BIGINT NULL,
        idXml INT NULL,
        revisionRef BIGINT NULL
    );

IF OBJECT_ID('dbo.ProductInstance','U') IS NULL
    CREATE TABLE dbo.ProductInstance (
        id_Table BIGINT NULL,
        idXml INT NULL,
        partRef BIGINT NULL,
        unitRef NVARCHAR(255) NULL
    );

IF OBJECT_ID('dbo.WorkAreaOccurrence','U') IS NULL
    CREATE TABLE dbo.WorkAreaOccurrence (
        id_Table BIGINT NULL,
        idXml INT NULL,
        parentRef NVARCHAR(255) NULL,
        instancedRef BIGINT NULL
    );
";
            using var cmd = new SqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        public static async Task postSB1(string jsonData)
        {
            void Log(string msg)
            {
                Console.WriteLine(msg);
                Utilidades.EscribirEnLog(msg);
            }

            void LogJson(string header, string json)
            {
                Utilidades.EscribirJSONEnLog($"{header}\n{json}");
            }

            bool LooksLikeJson(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return false;
                var t = s.TrimStart();
                return t.StartsWith("{") || t.StartsWith("[");
            }

            JObject obj;
            try
            {
                obj = JObject.Parse(jsonData);
            }
            catch (JsonException ex)
            {
                Log($"[SB1][POST] Error al parsear JSON: {ex.Message}");
                LogJson("[SB1][POST] JSON recibido (inválido):", jsonData);
                return;
            }

            var productos = obj["producto"];

            string codigo = null;
            string descripcion = null;
            string revision = null;

            if (productos != null)
            {
                foreach (var item in productos)
                {
                    var campo = item["campo"]?.ToString();
                    var valor = item["valor"]?.ToString();

                    if (campo == "codigo") codigo = valor;
                    if (campo == "descripcion") descripcion = valor;
                    if (campo == "revision") revision = valor; // opcional, si existe en el JSON
                }
            }

            Log($"[SB1][POST] Enviando producto -> codigo: {codigo}, descripcion: {descripcion}");
            LogJson($"[SB1][POST] JSON COMPLETO para producto {(string.IsNullOrEmpty(codigo) ? "(sin codigo)" : codigo)}:", jsonData);

            int intento = 0;

            while (true)
            {
                intento++;

                try
                {
                    await ProtheusHealth.WaitUntilActiveAsync("SB1-POST", RetryDelay);

                    using var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await _client.PostAsync(BaseUrlPost, content);

                    int statusCode = (int)response.StatusCode;
                    string responseData = await response.Content.ReadAsStringAsync(); // SIEMPRE leer

                    // Si la respuesta es JSON, mandarla al log JSON (además de log normal)
                    if (LooksLikeJson(responseData))
                        LogJson($"[SB1][POST] RESPUESTA JSON para {codigo} (HTTP {statusCode}):", responseData);

                    // Si viene errorCode, loguear
                    LogErrorProtheusIfAny("SB1", "POST", codigo, responseData);

                    // 409 => hacer PUT (el PUT también reintenta)
                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        Log($"[SB1][POST] 409 Conflict. Intentando PUT /Modificar... (intento POST #{intento})");
                        await putSB1(jsonData);
                        return;
                    }

                    // Reintento infinito ante 5xx
                    if (IsRetryableStatus(response.StatusCode))
                    {
                        Log($"[SB1][POST] Protheus respondió {statusCode}. Reintentando en 5 minutos. Intento #{intento}");
                        Log($"[SB1][POST] Respuesta: {responseData}");
                        await Task.Delay(RetryDelay);
                        continue;
                    }

                    // No reintentar: registramos resultado final
                    Log($"[SB1][POST] Código de estado: {statusCode}");
                    Log($"[SB1][POST] Respuesta: {responseData}");

                    poblarBase(codigo, descripcion, "PA", "01", "UN", revision, statusCode, responseData);
                    await ProtheusHealth.PostCheckBestEffortAsync("SB1-POST");
                    return;
                }
                catch (Exception ex) when (IsRetryableException(ex))
                {
                    Log($"[SB1][POST] Error transitorio: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    await Task.Delay(RetryDelay);
                    continue;
                }
                catch (Exception ex)
                {
                    Log($"[SB1][POST] Error NO transitorio: {ex.Message}. Abortando envío para {codigo}.");
                    return;
                }
            }
        }

        public static async Task putSB1(string jsonData)
        {
            void Log(string msg)
            {
                Console.WriteLine(msg);
                Utilidades.EscribirEnLog(msg);
            }

            void LogJson(string header, string json)
            {
                Utilidades.EscribirJSONEnLog($"{header}\n{json}");
            }

            bool LooksLikeJson(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return false;
                var t = s.TrimStart();
                return t.StartsWith("{") || t.StartsWith("[");
            }

            JObject obj;
            try
            {
                obj = JObject.Parse(jsonData);
            }
            catch (JsonException ex)
            {
                Log($"[SB1][PUT] Error al parsear JSON: {ex.Message}");
                LogJson("[SB1][PUT] JSON recibido (inválido):", jsonData);
                return;
            }

            var productos = obj["producto"];

            string codigo = null;
            string descripcion = null;
            string revision = null;

            if (productos != null)
            {
                foreach (var item in productos)
                {
                    var campo = item["campo"]?.ToString();
                    var valor = item["valor"]?.ToString();

                    if (campo == "codigo") codigo = valor;
                    if (campo == "descripcion") descripcion = valor;
                    if (campo == "revision") revision = valor; // opcional, si existe en el JSON
                }
            }

            Log($"[SB1][PUT] Modificando producto -> codigo: {codigo}, descripcion: {descripcion}");
            LogJson($"[SB1][PUT] JSON COMPLETO para producto {(string.IsNullOrEmpty(codigo) ? "(sin codigo)" : codigo)}:", jsonData);

            int intento = 0;

            while (true)
            {
                intento++;

                try
                {
                    await ProtheusHealth.WaitUntilActiveAsync("SB1-PUT", RetryDelay);

                    using var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await _client.PutAsync(BaseUrlPut, content);

                    int statusCode = (int)response.StatusCode;
                    string responseData = await response.Content.ReadAsStringAsync();

                    // Si la respuesta es JSON, mandarla al log JSON (además de log normal)
                    if (LooksLikeJson(responseData))
                        LogJson($"[SB1][PUT] RESPUESTA JSON para {codigo} (HTTP {statusCode}):", responseData);

                    // Si viene errorCode, loguear
                    LogErrorProtheusIfAny("SB1", "PUT", codigo, responseData);

                    // Reintento infinito ante 5xx
                    if (IsRetryableStatus(response.StatusCode))
                    {
                        Log($"[SB1][PUT] Protheus respondió {statusCode}. Reintentando en 5 minutos. Intento #{intento}");
                        Log($"[SB1][PUT] Respuesta: {responseData}");
                        await Task.Delay(RetryDelay);
                        continue;
                    }

                    // No reintentar: registramos resultado final
                    Log($"[SB1][PUT] Código de estado: {statusCode}");
                    Log($"[SB1][PUT] Respuesta: {responseData}");

                    await ProtheusHealth.PostCheckBestEffortAsync("SB1-PUT");
                    ActualizarBase(statusCode, responseData, codigo, descripcion);
                    return;
                }
                catch (Exception ex) when (IsRetryableException(ex))
                {
                    Log($"[SB1][PUT] Error transitorio: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    await Task.Delay(RetryDelay);
                    continue;
                }
                catch (Exception ex)
                {
                    Log($"[SB1][PUT] Error NO transitorio: {ex.Message}. Abortando envío para {codigo}.");
                    return;
                }
            }
        }

		public static string BopProcesadaPath { get; set; } = string.Empty;
		public static string CurrentBopCode { get; set; } = string.Empty;

		private static string NormalizarNombreBop(string? name)
		{
			if (string.IsNullOrWhiteSpace(name)) return string.Empty;
			name = name.Trim();

			// Si el archivo es "P-037801.xml" pero el código es "037801", matchea igual
			if (name.StartsWith("P-", StringComparison.OrdinalIgnoreCase))
				name = name.Substring(2).Trim();

			return name;
		}

		private static HashSet<string> LeerNombresBopsDisponibles()
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void AddFromFolder(string folder)
			{
				if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
					return;

				foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
				{
					var ext = Path.GetExtension(file);
					if (!ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
						!ext.Equals(".plmxml", StringComparison.OrdinalIgnoreCase))
						continue;

					var name = NormalizarNombreBop(Path.GetFileNameWithoutExtension(file));
					if (!string.IsNullOrWhiteSpace(name))
						set.Add(name);
				}
			}

			// Importante para BOP: considerar Input + Procesada
			AddFromFolder(BopInputPath);
			AddFromFolder(BopProcesadaPath);

			// Y también la BOP actual (por si ya se movió / renombró)
			var cur = NormalizarNombreBop(CurrentBopCode);
			if (!string.IsNullOrWhiteSpace(cur))
				set.Add(cur);

			Utilidades.EscribirEnLog($"[SB1][BOP] BOPs disponibles={set.Count} | Input={BopInputPath} | Procesada={BopProcesadaPath} | Actual={CurrentBopCode}");
			return set;
		}


        private static string ObtenerConsultaSB1_BOP(SqlConnection connection, out bool usaWorkArea)
        {
            static bool ScalarTrue(object? o) => o != null && o != DBNull.Value;

            bool tieneSubTypeEnOccurrence;

            using (var cmd = new SqlCommand(@"
        IF COL_LENGTH('Occurrence', 'subType') IS NOT NULL
        AND EXISTS (
            SELECT 1
            FROM Occurrence
            WHERE subType = 'MEWorkarea'
        )
        SELECT 1;", connection))
            {
                tieneSubTypeEnOccurrence = ScalarTrue(cmd.ExecuteScalar());
            }

            if (tieneSubTypeEnOccurrence)
            {
                usaWorkArea = true;

                Utilidades.EscribirEnLog(
                    "SB1 -> ObtenerConsultaSB1_BOP: Occurrence.subType presente → 1 WorkArea. Query=Con1Workarea.");

                return consultaSB1_BOP_Con1Workarea;
            }

            bool tieneWorkAreaOccurrence;

            using (var cmd = new SqlCommand(@"
        IF COL_LENGTH('WorkAreaOccurrence', 'subType') IS NOT NULL
            EXEC('IF EXISTS (SELECT 1 FROM WorkAreaOccurrence WHERE subType = ''MEWorkarea'') SELECT 1');", connection))
            {
                tieneWorkAreaOccurrence = ScalarTrue(cmd.ExecuteScalar());
            }

            if (tieneWorkAreaOccurrence)
            {
                usaWorkArea = true;

                Utilidades.EscribirEnLog(
                    "SB1 -> ObtenerConsultaSB1_BOP: WorkAreaOccurrence con subType=MEWorkarea → múltiples WorkAreas. Query=ConMasWorkareas.");

                return consultaSB1_BOP_ConMasWorkareas;
            }
            else
            {
                usaWorkArea = false;

                Utilidades.EscribirEnLog(
                    "SB1 -> ObtenerConsultaSB1_BOP: Sin WorkArea → Query=SinWorkArea.");

                return consultaSB1_BOP_SinWorkArea;
            }
        }

        public static List<string> jsonSB1_BOP()
        {
            string connectionString = Configuracion.ConnectionString;

            // ✅ Elegir query según compatibilidad del XML/BD (misma lógica que SG1)
            string queryElegida;
            bool usaWorkArea;

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                queryElegida = ObtenerConsultaSB1_BOP(conn, out usaWorkArea);
                //Utilidades.EscribirEnLog("SB1 -> Query elegida:\n" + queryElegida);
            }

            // Diccionario para deduplicar por código
            var productosDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // codigo -> jsonData
            var metaProductos = new Dictionary<string, (string desc, string tipo,  string um, string rev)>(StringComparer.OrdinalIgnoreCase);

            int totalFilasSql = 0;
			var bopDisponibles = LeerNombresBopsDisponibles();

			void EjecutarSB1Query(string query)
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 1200;

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                totalFilasSql++;

                                string codigo = reader["Codigo_Hijo"]?.ToString() ?? "";
                                string descripcion = reader["Nombre_Hijo"]?.ToString() ?? "";
                                string tipo = reader["Tipo"]?.ToString() ?? "";
                                //string deposito = reader["Deposito"]?.ToString() ?? "";
                                string unMedida = reader["unMedida"]?.ToString() ?? "";
                                string revision = reader["Revision"]?.ToString() ?? "";
								string subtype = reader["Subtype_Hijo"]?.ToString() ?? "";
								string blFormula = reader["bl_formula"]?.ToString() ?? "";
								string fantasma = CalcularFantasmaDesdeBopPendiente(codigo, subtype, bopDisponibles);


								Console.WriteLine(
                                    $"[SB1 SQL] codigo_hijo={codigo} | desc={descripcion} | tipo={tipo} | UM={unMedida} | rev={revision} | fantasma={fantasma}"
                                );
                                Utilidades.EscribirEnLog(
                                    $"[SB1 SQL] codigo_hijo={codigo} | desc={descripcion} | tipo={tipo} | UM={unMedida} | rev={revision} | fantasma ={fantasma}"
                                );

                                metaProductos[codigo] = (descripcion, tipo, unMedida, revision);

                                var camposProducto = new List<Dictionary<string, string>>
                            {
                                new() { { "campo", "codigo"      }, { "valor", codigo      } },
                                new() { { "campo", "descripcion" }, { "valor", descripcion } },
                                new() { { "campo", "tipo"        }, { "valor", tipo        } },
                                //new() { { "campo", "deposito"    }, { "valor", deposito    } },
                                new() { { "campo", "unMedida"    }, { "valor", unMedida    } },
                                new() { { "campo", "revEstruct"  }, { "valor", revision    } },
								new() { { "campo", "fantasma"    }, { "valor", fantasma     } },
							};

                                if (!string.IsNullOrWhiteSpace(blFormula))
                                    camposProducto.Add(new() { { "campo", "formula" }, { "valor", blFormula } });

                                var producto = new
                                {
                                    producto = camposProducto
								};

                                string jsonData = JsonConvert.SerializeObject(producto, Formatting.Indented);

                                if (productosDict.ContainsKey(codigo))
                                    Console.WriteLine($"[SB1] Código {codigo} ya existía, se reemplaza el JSON anterior por el nuevo.");
                                else
                                    Console.WriteLine($"[SB1] Nuevo código agregado al diccionario: {codigo}");

                                productosDict[codigo] = jsonData;
                            }
                        }
                    }
                }
            }

            try
            {
                // 1) Ejecutar la query elegida (CON o SIN WorkArea según detección)
                EjecutarSB1Query(queryElegida);
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[SB1] Error SQL al consultar la base: {ex.Message}");
                Utilidades.EscribirEnLog($"[SB1] Error SQL al consultar la base: {ex.Message}");

                // 2) Fallback defensivo: si se eligió CON WorkArea pero faltan objetos, reintentar SIN WorkArea
                bool esFaltaDeWorkArea =
                    ex.Message.Contains("Invalid object name 'WorkArea'", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Invalid object name 'WorkAreaRevision'", StringComparison.OrdinalIgnoreCase);

                if (usaWorkArea && esFaltaDeWorkArea)
                {
                    Utilidades.EscribirEnLog("[SB1] FALLBACK: falló query CON WorkArea por objetos faltantes. Se reintenta SIN WorkArea.");
                    Console.WriteLine("[SB1] FALLBACK: falló query CON WorkArea por objetos faltantes. Se reintenta SIN WorkArea.");

                    try
                    {
                        // Nota: no limpiamos diccionarios; si llegó a leer algo antes de fallar, el SIN WorkArea puede completar/overridear.
                        EjecutarSB1Query(consultaSB1_BOP_SinWorkArea);
                        usaWorkArea = false;
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"[SB1] FALLBACK también falló: {ex2.Message}");
                        Utilidades.EscribirEnLog($"[SB1] FALLBACK también falló: {ex2}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SB1] Error al consultar la base de datos: {ex.Message}");
                Utilidades.EscribirEnLog($"[SB1] Error al consultar la base de datos: {ex}");
            }

            // ===== NUEVO: generar productos PRxxxxxxT (SV) para PR que NO sean de terceros =====
            // (Se mantiene tal cual tu lógica actual)
            var prsParaServicio = metaProductos
                .Where(kvp =>
                    kvp.Key.StartsWith("PR", StringComparison.OrdinalIgnoreCase) &&   // solo PR...
                    !kvp.Key.EndsWith("T", StringComparison.OrdinalIgnoreCase) &&     // ...que NO sean ya PRxxxxxxT
                    !string.Equals(kvp.Value.tipo, "SV", StringComparison.OrdinalIgnoreCase)) // ...y que no sean de terceros
                .Select(kvp => new { codigoBase = kvp.Key, meta = kvp.Value })
                .ToList();

            foreach (var item in prsParaServicio)
            {
                string codigoBase = item.codigoBase;
                var meta = item.meta;

                string codigoT = codigoBase + "T";

                if (productosDict.ContainsKey(codigoT))
                    continue; // por seguridad

                var productoT = new
                {
                    producto = new List<Dictionary<string, string>>
            {
                new() { { "campo", "codigo"      }, { "valor", codigoT   } },
                new() { { "campo", "descripcion" }, { "valor", meta.desc } },
                new() { { "campo", "tipo"        }, { "valor", "SV"      } },
                //new() { { "campo", "deposito"    }, { "valor", meta.depo } },
                new() { { "campo", "unMedida"    }, { "valor", meta.um   } },
                new() { { "campo", "revEstruct"  }, { "valor", meta.rev  } },
				new() { { "campo", "fantasma" }, { "valor", "N" } },
			}
				};

                string jsonT = JsonConvert.SerializeObject(productoT, Formatting.Indented);

                Console.WriteLine($"[SB1] Generado producto servicio para PR no Terceros: {codigoT} (origen {codigoBase})");
                Utilidades.EscribirEnLog($"[SB1] Generado producto servicio para PR no Terceros: {codigoT} (origen {codigoBase})");

                productosDict[codigoT] = jsonT;
                // NO tocamos metaProductos dentro del foreach
            }
            // =====================================================================

            // Salida final
            var jsonProductos = new List<string>();
            foreach (var kvp in productosDict)
            {
                Utilidades.EscribirJSONEnLog($"[SB1 JSON FINAL] codigo={kvp.Key}");
                Utilidades.EscribirJSONEnLog(kvp.Value);
                jsonProductos.Add(kvp.Value);
            }

            Console.WriteLine($"SB1 -> filas SQL totales: {totalFilasSql}, códigos únicos (incluyendo PRxxxxxxT): {productosDict.Count}");
            Utilidades.EscribirEnLog($"SB1 -> filas SQL totales: {totalFilasSql}, códigos únicos (incluyendo PRxxxxxxT): {productosDict.Count}");

            return jsonProductos;
        }

		public static string BopInputPath { get; set; } = string.Empty;

		private static readonly HashSet<string> SubtypesFantasmaSinBop =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
		"Agm4_ConGeneralRevision",
        "Agm4_SubConRevision",
        "Agm4_conj_mBOM_FRevision",
        "Agm4_sub_mBOM_ERevision",
        "Agm4_sub_mBOM_SRevision"
            };

		private static HashSet<string> LeerNombresBopsPendientesDesdeBopInput()
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var carpeta = (BopInputPath ?? "").Trim();

			if (string.IsNullOrWhiteSpace(carpeta) || !Directory.Exists(carpeta))
			{
				Utilidades.EscribirEnLog($"[SB1][MBOM] BOP_Input inválido o inexistente: {carpeta}. Se asume 0 BOPs pendientes.");
				return set;
			}

			var archivos = Directory.EnumerateFiles(carpeta, "*", SearchOption.TopDirectoryOnly)
				.Where(f =>
				{
					var ext = Path.GetExtension(f);
					return ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
						|| ext.Equals(".plmxml", StringComparison.OrdinalIgnoreCase);
				});

			foreach (var file in archivos)
			{
				var name = Path.GetFileNameWithoutExtension(file)?.Trim();
				if (!string.IsNullOrWhiteSpace(name))
					set.Add(name);
			}

			Utilidades.EscribirEnLog($"[SB1][MBOM] BOPs detectadas en BOP_Input ({carpeta}): {set.Count}");
			return set;
		}

		private static string CalcularFantasmaDesdeBopPendiente(string codigoHijoFinal, string subtypeHijo, HashSet<string> bopDisponibles)
		{
			if (string.IsNullOrWhiteSpace(codigoHijoFinal))
				return "N";

			var codigo = NormalizarNombreBop(codigoHijoFinal);
			var st = (subtypeHijo ?? "").Trim();

			if (bopDisponibles != null && bopDisponibles.Contains(codigo))
				return "N";

			if (!string.IsNullOrWhiteSpace(st) && SubtypesFantasmaSinBop.Contains(st))
				return "S";

			return "N";
		}



		public static List<string> jsonSB1_MBOM()
		{
			return jsonSB1_MBOM(out _);
		}

		public static List<string> jsonSB1_MBOM(out HashSet<string> codigosFantasma)
		{
			codigosFantasma = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			string connectionString = Configuracion.ConnectionString;
			string query = @"
WITH UNIT_VALUES AS (
    SELECT
        TRY_CONVERT(BIGINT, u.id_Table) AS UnitId,
        uv.title,
        uv.value
    FROM Unit u
    LEFT JOIN UserValue_Unit uv
        ON uv.id_Father = u.id_Table
       AND uv.idXml     = u.idXml
    UNION ALL
    SELECT
        TRY_CONVERT(BIGINT, u.id_Table) AS UnitId,
        uv2.title,
        uv2.value
    FROM Unit u
    INNER JOIN UserData ud
        ON ud.id_Father = u.id_Table
       AND ud.idXml     = u.idXml
    INNER JOIN UserValue_UserData uv2
        ON uv2.id_Father = ud.id_Table
       AND uv2.idXml     = u.idXml
),
UNIT_MAP AS (
    SELECT
        UnitId,
        COALESCE(
            NULLIF(MAX(CASE WHEN title = 'Agm4_Kilogramos' THEN value END), ''),
            NULLIF(MAX(CASE WHEN title = 'Agm4_Litros'     THEN value END), ''),
            NULLIF(MAX(CASE WHEN title = 'Agm4_Metros'     THEN value END), ''),
            NULLIF(MAX(CASE WHEN title = 'Agm4_Unidad'     THEN value END), ''),
            NULLIF(MAX(CASE WHEN title = 'uom_tag'         THEN value END), '')
        ) AS UnidadMedida
    FROM UNIT_VALUES
    GROUP BY UnitId
),
QTY_VALUES AS (
    SELECT
        o.id_Table AS OccurrenceId,
        o.idXml,
        COALESCE(
            MAX(CASE WHEN uvo.title = 'Quantity' THEN TRY_CAST(uvo.value AS DECIMAL(18,6)) END),
            MAX(CASE WHEN uvud.title = 'Quantity' THEN TRY_CAST(uvud.value AS DECIMAL(18,6)) END)
        ) AS QtyValue
    FROM Occurrence o
    LEFT JOIN UserValue_Occurrence uvo
        ON uvo.id_Father = o.id_Table
       AND uvo.idXml     = o.idXml
    LEFT JOIN UserData ud
        ON ud.id_Father = o.id_Table
       AND ud.idXml     = o.idXml
    LEFT JOIN UserValue_UserData uvud
        ON uvud.id_Father = ud.id_Table
       AND uvud.idXml     = o.idXml
    GROUP BY o.id_Table, o.idXml
),
CTE_Hierarchy AS (
    SELECT DISTINCT
        o.id_Table AS id_table,
        pr.id_Table AS ProductRevisionId,
        pr.name,
        p.productId AS codigo,
        TRY_CONVERT(BIGINT, o.parentRef) AS parentRef,
        pr.revision,
        pr.subType,
        o.idXml,
        ISNULL(uvo_formula.value, '') AS bl_formula
    FROM Occurrence o
    LEFT JOIN ProductRevision pr
        ON o.instancedRef = pr.id_Table
       AND o.idXml        = pr.idXml
    LEFT JOIN Product p
        ON pr.masterRef = p.id_Table
       AND pr.idXml     = p.idXml
    LEFT JOIN UserValue_Occurrence uvo_formula
        ON uvo_formula.id_Father = o.id_Table
       AND uvo_formula.title     = 'bl_formula'
    GROUP BY
        o.id_Table, pr.id_Table, pr.name, p.productId, o.parentRef,
        pr.revision, pr.subType, o.idXml, uvo_formula.value
),
Base AS (
    SELECT DISTINCT
        COALESCE(Parent.name, '') AS Nombre_Padre,
        COALESCE(CodFmt.CodigoPadre_Final, '') AS Codigo_Padre,
        Child.name AS Nombre_Hijo,
        CodFmt.CodigoHijo_Final AS Codigo_Hijo,
        Child.subType AS Subtype_Hijo,
        Qty.CantidadFinal AS CantidadHijo_Total,
        Child.revision AS Revision,
        MIN('PA') AS Tipo,
        MIN('YY') AS Deposito,
        COALESCE(NULLIF(MAX(um.UnidadMedida), ''), 'UN') AS unMedida,
        Child.bl_formula AS bl_formula,
        MAX(
            CASE
                WHEN Child.bl_formula != '' AND CHARINDEX('[''', Child.bl_formula) > 0 THEN
                    CASE
                        WHEN CHARINDEX(' & ', Child.bl_formula, CHARINDEX('[''', Child.bl_formula)) > 0
                        THEN SUBSTRING(Child.bl_formula,
                                CHARINDEX('[''', Child.bl_formula),
                                CHARINDEX(' & ', Child.bl_formula, CHARINDEX('[''', Child.bl_formula)) - CHARINDEX('[''', Child.bl_formula))
                        ELSE SUBSTRING(Child.bl_formula,
                                CHARINDEX('[''', Child.bl_formula),
                                LEN(Child.bl_formula))
                    END
                ELSE ''
            END
        ) AS idContext
    FROM CTE_Hierarchy Child
    LEFT JOIN CTE_Hierarchy Parent
        ON Child.parentRef = TRY_CONVERT(BIGINT, Parent.id_table)

    LEFT JOIN ProductRevisionView prv
        ON prv.revisionRef = Child.ProductRevisionId
       AND prv.idXml       = Child.idXml

    LEFT JOIN ProductInstance pi
        ON pi.partRef = prv.id_Table
       AND pi.idXml   = Child.idXml

    OUTER APPLY (
        SELECT TRY_CONVERT(BIGINT, pi.unitRef) AS UnitId
    ) unitPick

    LEFT JOIN UNIT_MAP um
        ON um.UnitId = unitPick.UnitId

    LEFT JOIN (
        SELECT
            oPadre.id_Table AS ParentOccurrenceId,
            pHijo.productId AS ChildCodigo,
            CASE
                WHEN prHijo.subType = 'Agm4_MatPrimaRevision'
                    THEN SUM(COALESCE(qv.QtyValue, 1))
                ELSE COUNT(DISTINCT oHijo.id_Table)
            END AS Cantidad
        FROM Product pHijo
        INNER JOIN ProductRevision prHijo
            ON pHijo.id_Table = prHijo.masterRef
           AND pHijo.idXml    = prHijo.idXml
        LEFT JOIN Occurrence oHijo
            ON oHijo.instancedRef = prHijo.id_Table
           AND oHijo.idXml        = prHijo.idXml
        LEFT JOIN QTY_VALUES qv
            ON qv.OccurrenceId = oHijo.id_Table
           AND qv.idXml        = oHijo.idXml
        LEFT JOIN Occurrence oPadre
            ON oHijo.parentRef = oPadre.id_Table
           AND oHijo.idXml     = oPadre.idXml
        GROUP BY oPadre.id_Table, pHijo.productId, prHijo.subType
    ) sq3
        ON sq3.ParentOccurrenceId = TRY_CONVERT(BIGINT, Parent.id_table)
       AND sq3.ChildCodigo        = Child.codigo

    OUTER APPLY (
        SELECT CAST(
            CASE
                WHEN Parent.id_table IS NULL THEN 1
                ELSE ISNULL(sq3.Cantidad, 1)
            END
        AS DECIMAL(18,2)) AS CantidadFinal
    ) AS Qty

    OUTER APPLY (
        SELECT
            CASE
                WHEN Parent.codigo IS NULL THEN NULL
                ELSE
                    CASE
                        WHEN LEFT(Parent.codigo, 2) = 'M-' THEN RIGHT(Parent.codigo, LEN(Parent.codigo) - 2)
                        WHEN LEFT(Parent.codigo, 1) = 'M'  THEN RIGHT(Parent.codigo, LEN(Parent.codigo) - 1)
                        WHEN LEFT(Parent.codigo, 1) = 'E'  THEN RIGHT(Parent.codigo, LEN(Parent.codigo) - 1)
                        WHEN RIGHT(Parent.codigo, 3) = '-FV' THEN LEFT(Parent.codigo, LEN(Parent.codigo) - 3)
                        ELSE Parent.codigo
                    END
            END AS CodigoPadre_SB1,
            CASE
                WHEN Child.codigo IS NULL THEN NULL
                ELSE
                    CASE
                        WHEN LEFT(Child.codigo, 2) = 'M-' THEN RIGHT(Child.codigo, LEN(Child.codigo) - 2)
                        WHEN LEFT(Child.codigo, 1) = 'M'  THEN RIGHT(Child.codigo, LEN(Child.codigo) - 1)
                        WHEN LEFT(Child.codigo, 1) = 'E'  THEN RIGHT(Child.codigo, LEN(Child.codigo) - 1)
                        WHEN RIGHT(Child.codigo, 3) = '-FV' THEN LEFT(Child.codigo, LEN(Child.codigo) - 3)
                        ELSE Child.codigo
                    END
            END AS CodigoHijo_SB1
    ) CodSB1

    OUTER APPLY (
        SELECT
            CASE
                WHEN CodSB1.CodigoPadre_SB1 IS NULL THEN NULL
                ELSE CASE WHEN LEFT(CodSB1.CodigoPadre_SB1, 1) = 'E'
                    THEN SUBSTRING(CodSB1.CodigoPadre_SB1, 2, LEN(CodSB1.CodigoPadre_SB1) - 1)
                    ELSE CodSB1.CodigoPadre_SB1 END
            END AS CodigoPadre_SinE,
            CASE
                WHEN Parent.codigo IS NULL THEN CodSB1.CodigoHijo_SB1
                ELSE CASE WHEN LEFT(CodSB1.CodigoHijo_SB1, 1) = 'E'
                    THEN SUBSTRING(CodSB1.CodigoHijo_SB1, 2, LEN(CodSB1.CodigoHijo_SB1) - 1)
                    ELSE CodSB1.CodigoHijo_SB1 END
            END AS CodigoHijo_SinE
    ) CodSinE

    OUTER APPLY (
        SELECT
            CASE
                WHEN CodSinE.CodigoPadre_SinE IS NULL THEN NULL
                ELSE CASE WHEN RIGHT(CodSinE.CodigoPadre_SinE, 2) LIKE '-[0-9]'
                    THEN LEFT(CodSinE.CodigoPadre_SinE, LEN(CodSinE.CodigoPadre_SinE) - 2)
                    ELSE CodSinE.CodigoPadre_SinE END
            END AS CodigoPadre_Final,
            CASE
                WHEN Parent.codigo IS NULL THEN CodSinE.CodigoHijo_SinE
                ELSE CASE WHEN RIGHT(CodSinE.CodigoHijo_SinE, 2) LIKE '-[0-9]'
                    THEN LEFT(CodSinE.CodigoHijo_SinE, LEN(CodSinE.CodigoHijo_SinE) - 2)
                    ELSE CodSinE.CodigoHijo_SinE END
            END AS CodigoHijo_Final
    ) CodFmt

    WHERE Child.subType IN (
        'Agm4_ConGeneralRevision',
        'Agm4_MatPrimaRevision',
        'Agm4_PiezaRevision',
        'Agm4_RepCompradoRevision',
        'Agm4_SubConRevision',
        'Agm4_sub_mBOM_ERevision'
    )
    GROUP BY
        Parent.name, CodFmt.CodigoPadre_Final, Child.name, CodFmt.CodigoHijo_Final,
        Qty.CantidadFinal, Child.revision, Child.subType, Child.bl_formula
)
SELECT b.* FROM Base b ORDER BY b.Codigo_Padre;
    ";

			var bopPendientes = LeerNombresBopsPendientesDesdeBopInput();

			var productosDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var subtypePorCodigo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			int totalFilasSql = 0;
			int cantFantasmaS = 0;
			int cantFantasmaN = 0;

			try
			{
				using (SqlConnection connection = new SqlConnection(connectionString))
				{
					connection.Open();

					using (SqlCommand command = new SqlCommand(query, connection))
					{
						command.CommandTimeout = 0; // sin límite — solo para pruebas locales

						using (SqlDataReader reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								totalFilasSql++;

								string codigo = reader["Codigo_Hijo"]?.ToString()?.Trim() ?? "";
								string descripcion = reader["Nombre_Hijo"]?.ToString() ?? "";
								string tipo = reader["Tipo"]?.ToString() ?? "";
								string unMedida = reader["unMedida"]?.ToString() ?? "";
								string revision = reader["Revision"]?.ToString() ?? "";
								string subtype = reader["Subtype_Hijo"]?.ToString() ?? "";

								if (!string.IsNullOrWhiteSpace(codigo))
								{
									if (!subtypePorCodigo.ContainsKey(codigo))
										subtypePorCodigo[codigo] = subtype;
								}

								var subtypeCanon = (!string.IsNullOrWhiteSpace(codigo) && subtypePorCodigo.TryGetValue(codigo, out var st)) ? st : subtype;

								string fantasma = CalcularFantasmaDesdeBopPendiente(codigo, subtypeCanon, bopPendientes);

								if (fantasma.Equals("S", StringComparison.OrdinalIgnoreCase))
								{
									if (!string.IsNullOrWhiteSpace(codigo))
										codigosFantasma.Add(codigo);
									cantFantasmaS++;
								}
								else
								{
									cantFantasmaN++;
								}

								Utilidades.EscribirEnLog(
									$"[SB1 SQL][MBOM] codigo_hijo={codigo} | desc={descripcion} | tipo={tipo} | UM={unMedida} | rev={revision} | subtype={subtypeCanon} | fantasma={fantasma}"
								);

								var producto = new
								{
									producto = new List<Dictionary<string, string>>
							{
								new() { { "campo", "codigo"      }, { "valor", codigo      } },
								new() { { "campo", "descripcion" }, { "valor", descripcion } },
								new() { { "campo", "tipo"        }, { "valor", tipo        } },
								new() { { "campo", "unMedida"    }, { "valor", unMedida    } },
								new() { { "campo", "revEstruct"  }, { "valor", revision    } },
								new() { { "campo", "fantasma"    }, { "valor", fantasma    } },
							}
								};

								string jsonData = JsonConvert.SerializeObject(producto, Formatting.Indented);
								productosDict[codigo] = jsonData;
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error al consultar la base de datos: {ex.Message}");
				Utilidades.EscribirEnLog($"Error al consultar la base de datos: {ex.Message}");
			}

			Utilidades.EscribirEnLog($"[SB1][MBOM] Resumen fantasma: S={cantFantasmaS} | N={cantFantasmaN} | SetFantasmas={codigosFantasma.Count} | BOP_Input={BopInputPath}");

			var jsonProductos = new List<string>();
			foreach (var kvp in productosDict)
			{
				jsonProductos.Add(kvp.Value);
				Utilidades.EscribirJSONEnLog($"[SB1][JSON] Producto {kvp.Key}:\n{kvp.Value}");
			}

			Utilidades.EscribirEnLog($"SB1[MBOM] -> filas SQL totales: {totalFilasSql}, códigos únicos: {productosDict.Count}");
			return jsonProductos;
		}






		public static void poblarBase(string codigo, string descripcion, string tipo, string deposito, string unMedida, string revision, int estado, string mensaje)
        {
            string connectionString = Configuracion.ConnectionString;
            string query = "  INSERT INTO SB1 (codigo, descripcion, tipo, deposito, unMedida, revision, estado, mensaje)\r\nSELECT @codigo, @descripcion, @tipo, @deposito, @unMedida, @revision, @estado, @mensaje\r\nWHERE NOT EXISTS (SELECT 1 FROM SB1 WHERE codigo = @codigo)";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@codigo", codigo);
                        command.Parameters.AddWithValue("@descripcion", descripcion);
                        command.Parameters.AddWithValue("@tipo", tipo);
                        command.Parameters.AddWithValue("@deposito", deposito);
                        command.Parameters.AddWithValue("@unMedida", unMedida);
                        command.Parameters.AddWithValue("@revision", revision);
                        command.Parameters.AddWithValue("@estado", estado);
                        command.Parameters.AddWithValue("@mensaje", mensaje);
                        command.ExecuteNonQuery();
                    }
                        
                }
            }
            catch (Exception ex)
            {

            }
        }

        public static void ActualizarBase(int estado, string mensaje, string codigo, string descripcion)
        {

            string connectionString = Configuracion.ConnectionString;
            string query = @"UPDATE SB1
                          SET estado = @estado, mensaje = @mensaje
                          WHERE codigo = @codigo AND descripcion = @descripcion 
--AND estado BETWEEN 400 AND 409";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@estado", estado);
                        command.Parameters.AddWithValue("@mensaje", mensaje);
                        command.Parameters.AddWithValue("@codigo", codigo);
                        command.Parameters.AddWithValue("@descripcion", descripcion);
                        command.ExecuteNonQuery();
                    }

                }
            }
            catch (Exception ex)
            {

            }
        }
    }
}
