using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Web_Service
{
	public class DataModel
	{
		public string ParentName { get; set; }
		public string ParentCodigo { get; set; }
		public string ChildName { get; set; }
		public string ChildCodigo { get; set; }
		public string CantidadHijo { get; set; }
		public string Variante { get; set; }
	}

	public class Tabla_SG1
	{
		private readonly HttpClient _httpClient;

		private const string SG1_POST_URL = "http://119.8.73.193:8076/rest/TCEstructura/Incluir/";
		private const string SG1_PUT_URL = "http://119.8.73.193:8076/rest/TCEstructura/Modificar/";
		private const string SG1_DELETE_URL = "http://119.8.73.193:8076/rest/TCEstructura/Eliminar/";

		private const string ZG1_POST_URL    = "http://119.8.73.193:8076/rest/ZG1Variantes/Incluir/";
		private const string ZG1_PUT_URL     = "http://119.8.73.193:8076/rest/ZG1Variantes/Modificar/";
		private const string ZG1_DELETE_URL  = "http://119.8.73.193:8076/rest/ZG1Variantes/Eliminar/";

		private const string USERNAME = "USERREST";
		private const string PASSWORD = "restagr";

		private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
		private static readonly HttpClient _clientSG1 = CreateHttpClient();

		// Productos que en la query original venían sin Codigo_Padre
		private static readonly HashSet<string> ProductosSinCodigoPadre =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public Tabla_SG1()
		{
			_httpClient = new HttpClient();
		}

		// =========================
		// Helpers de log (usa Utilidades)
		// =========================
		private static void Log(string msg)
		{
			Console.WriteLine(msg);
			Utilidades.EscribirEnLog(msg);
		}

		private static void LogJson(string header, string json)
		{
			// JSON siempre al log JSON, tal como pediste
			Utilidades.EscribirJSONEnLog($"{header}\n{json}");
		}

		private static bool LooksLikeJson(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return false;
			var t = s.TrimStart();
			return t.StartsWith("{") || t.StartsWith("[");
		}

		// =========================

		private static HttpClient CreateHttpClient()
		{
			var client = new HttpClient
			{
				Timeout = TimeSpan.FromMinutes(3)
			};

			var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{USERNAME}:{PASSWORD}"));
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

			return client;
		}

		private static bool IsRetryableStatus(HttpStatusCode code)
			=> (int)code >= 500 && (int)code <= 599;

		private static bool IsRetryableException(Exception ex)
			=> ex is HttpRequestException
			   || ex is TaskCanceledException
			   || ex is IOException;

		private static async Task<bool> EliminarEstructurasBopAsync(IEnumerable<string> codigos)
		{
			bool okGlobal = true;

			foreach (var codigo in codigos.Where(c => !string.IsNullOrWhiteSpace(c)))
			{
				var del = await EliminarEstructuraSG1Async(codigo);

				if (!del.ok)
				{
					okGlobal = false;
					Log($"[SG1-RESET] DELETE falló para {codigo}. status={del.statusCode}. resp={del.responseBody}");
				}
				else
				{
					Log($"[SG1-RESET] DELETE OK para {codigo}. status={del.statusCode}");
				}
			}

			return okGlobal;
		}

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
				var err = JsonConvert.DeserializeObject<WsError>(body);
				if (err?.errorCode != null)
				{
					errorCode = err.errorCode;
					errorMessage = err.errorMessage;
					return true;
				}

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

		private static bool ContainsRecursiv(string? text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return false;

			return text.IndexOf("recursiv", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool IsRecursividadError(string responseBody)
		{
			if (string.IsNullOrWhiteSpace(responseBody))
				return false;

			if (TryParseWsError(responseBody, out var code, out var msg))
			{
				if (code == 6 && ContainsRecursiv(msg))
					return true;
			}

			return ContainsRecursiv(responseBody);
		}

		// =========================
		// POST RAW (ahora loguea estilo SB1)
		// - JSON -> log JSON
		// - Response/eventos -> ScriptPrincipal.log
		// =========================
		private static async Task<(int statusCode, HttpStatusCode httpStatus, string responseBody)>
			PostSG1RawAsync(string codigo, string jsonData)
		{
			int intento = 0;

			Log($"[SG1][POST] Enviando estructura -> producto: {codigo}");
			LogJson($"[SG1][POST] JSON COMPLETO para producto {(string.IsNullOrWhiteSpace(codigo) ? "(sin codigo)" : codigo)}:", jsonData);

			while (true)
			{
				intento++;

				try
				{
					await ProtheusHealth.WaitUntilActiveAsync("SG1-POST", RetryDelay);

					using var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
					using HttpResponseMessage response = await _clientSG1.PostAsync(SG1_POST_URL, content);

					int statusCode = (int)response.StatusCode;
					string responseData = await response.Content.ReadAsStringAsync();

					// Response siempre al log principal (como pediste)
					LogErrorProtheusIfAny("SG1", "POST", codigo, responseData);

					if (IsRetryableStatus(response.StatusCode))
					{
						Log($"[SG1][POST] Protheus respondió {statusCode}. Reintento en 5 minutos. Intento #{intento}");
						Log($"[SG1][POST] Respuesta: {responseData}");
						await Task.Delay(RetryDelay);
						continue;
					}

					Log($"[SG1][POST] Código de estado: {statusCode}");
					Log($"[SG1][POST] Respuesta: {responseData}");

					await ProtheusHealth.PostCheckBestEffortAsync("SG1-POST");
					return (statusCode, response.StatusCode, responseData);
				}
				catch (Exception ex) when (IsRetryableException(ex))
				{
					Log($"[SG1][POST] EXCEPCIÓN transitoria para {codigo}: {ex.Message}. Reintento en 5 minutos. Intento #{intento}");
					await Task.Delay(RetryDelay);
					continue;
				}
			}
		}

		private static async Task<(bool ok, int statusCode, string responseBody)> EliminarEstructuraSG1Async(string codigo)
		{
			int intento = 0;

			// Payload del DELETE (esto ES json request => al log JSON)
			var payload = JsonConvert.SerializeObject(new { producto = codigo }, Formatting.None);
			LogJson($"[SG1][DELETE] JSON payload para producto {codigo}:", payload);

			while (true)
			{
				intento++;

				try
				{
					await ProtheusHealth.WaitUntilActiveAsync("SG1-DELETE", RetryDelay);

					using var req = new HttpRequestMessage(HttpMethod.Delete, SG1_DELETE_URL)
					{
						Content = new StringContent(payload, Encoding.UTF8, "application/json")
					};

					using HttpResponseMessage resp = await _clientSG1.SendAsync(req);

					int status = (int)resp.StatusCode;
					string body = await resp.Content.ReadAsStringAsync();
					LogErrorProtheusIfAny("SG1", "DELETE", codigo, body);

					if (IsRetryableStatus(resp.StatusCode))
					{
						Log($"[SG1][DELETE] {status} para {codigo}. Reintento en 5 minutos. Intento #{intento}");
						Log($"[SG1][DELETE] Respuesta: {body}");
						await Task.Delay(RetryDelay);
						continue;
					}

					if (resp.IsSuccessStatusCode)
					{
						Log($"[SG1][DELETE] OK para {codigo}: {status} - {body}");
						await ProtheusHealth.PostCheckBestEffortAsync("SG1-DELETE");
						return (true, status, body);
					}

					Log($"[SG1][DELETE] ERROR NO-RETRY para {codigo}: {status} {resp.ReasonPhrase}. Contenido: {body}");
					return (false, status, body);
				}
				catch (Exception ex) when (IsRetryableException(ex))
				{
					Log($"[SG1][DELETE] EXCEPCIÓN transitoria para {codigo}: {ex.Message}. Reintento en 5 minutos. Intento #{intento}");
					await Task.Delay(RetryDelay);
					continue;
				}
				catch (Exception ex)
				{
					Log($"[SG1][DELETE] EXCEPCIÓN NO transitoria para {codigo}: {ex}");
					return (false, 0, ex.ToString());
				}
			}
		}

        // ✅ Query original — CON WorkArea (ya está con Qty corregida)
        private static readonly string consultaSG1_BOP_Con1Workarea = @"
        WITH FirstProcessName AS(
    SELECT TOP (1)
        CASE
            WHEN LEN(p.catalogueId) != 8 THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
            ELSE RIGHT(p.catalogueId, 6)
        END AS first_process_name
    FROM Process p
    LEFT JOIN ProcessRevision pr
        ON pr.masterRef = p.id_Table
    LEFT JOIN ProcessOccurrence po
        ON pr.id_Table = po.instancedRef
    WHERE po.parentRef IS NULL
),
ValidChildren AS(
    SELECT
        po.id_Table AS PR_OccId,
        CASE
            WHEN LEFT(prod.productId, 1) IN('M','E')
                THEN RIGHT(prod.productId, LEN(prod.productId) - 1)
            WHEN RIGHT(prod.productId, 3) = '-FV'
                THEN LEFT(prod.productId, LEN(prod.productId) - 3)
            ELSE prod.productId
        END AS Codigo,
        prod_rev.subType AS subType,
        CASE
            WHEN prod.productId IS NULL THEN 0
            WHEN ISNULL(qty_pick.QtyValue, 0) > 0 THEN qty_pick.QtyValue
            ELSE 1
        END AS Qty,
        ISNULL(bl_pick.bl_formula_val, '') AS bl_formula,
        CASE
            WHEN ISNULL(bl_pick.bl_formula_val, '') != ''
             AND CHARINDEX('[''', bl_pick.bl_formula_val) > 0
             AND CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val)) > 0
            THEN SUBSTRING(
                    bl_pick.bl_formula_val,
                    CHARINDEX('[''', bl_pick.bl_formula_val) + 2,
                    CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val))
                      - CHARINDEX('[''', bl_pick.bl_formula_val) - 2)
            ELSE ''
        END AS idContext
    FROM Process p
    LEFT JOIN ProcessRevision pr
        ON pr.masterRef = p.id_Table
    LEFT JOIN ProcessOccurrence po
        ON po.instancedRef = pr.id_Table
    LEFT JOIN ProcessOccurrence po_op
        ON po_op.parentRef = po.id_Table
    LEFT JOIN Occurrence o
        ON (
            o.parentRef = po.id_Table
            OR o.parentRef = po_op.id_Table
        )
        AND ISNULL(o.subType,'') <> 'METool'
    LEFT JOIN ProductRevision prod_rev
        ON prod_rev.id_Table = o.instancedRef
    LEFT JOIN Product prod
        ON prod.id_Table = prod_rev.masterRef
    OUTER APPLY (
        SELECT TOP (1) q.QtyValue
        FROM(
            SELECT
                TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvO.value)), ''), ',', '.')) AS QtyValue,
                1 AS prio
            FROM UserValue_Occurrence uvO
            WHERE uvO.id_Father = o.id_Table
              AND uvO.title IN ('Quantity','quantity')

            UNION ALL

            SELECT
                TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvUD.value)), ''), ',', '.')) AS QtyValue,
                2 AS prio
            FROM UserData ud
            JOIN UserValue_UserData uvUD
                ON uvUD.id_Father = ud.id_Table
               AND uvUD.title IN ('Quantity','quantity')
            WHERE ud.id_Father = o.id_Table

            UNION ALL

            SELECT
                TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvUD2.value)), ''), ',', '.')) AS QtyValue,
                3 AS prio
            FROM UserValue_UserData uvUD2
            WHERE uvUD2.id_Father = o.id_Table + 2
              AND uvUD2.title IN ('Quantity','quantity')

        ) q
        WHERE q.QtyValue IS NOT NULL
        ORDER BY q.prio
    ) qty_pick
    OUTER APPLY (
        SELECT TOP 1 uvo.value AS bl_formula_val
        FROM Occurrence o_formula
        INNER JOIN UserValue_Occurrence uvo
            ON uvo.id_Father = o_formula.id_Table
           AND uvo.title = 'bl_formula'
        WHERE o_formula.instancedRef = o.instancedRef
          AND uvo.value IS NOT NULL
          AND LTRIM(RTRIM(uvo.value)) != ''
    ) bl_pick
    WHERE
        prod.productId IS NOT NULL
        AND NOT(
            prod_rev.subType IN ('Mfg0MEFactoryToolRevision', 'Mfg0MEResourceRevision')
            OR
            (
                prod_rev.subType IN ('Agm4_MatPrimaRevision','Agm4_RepCompradoRevision')
                AND ISNULL(qty_pick.QtyValue, 0) <= 0
            )
        )
)
SELECT
    fpn.first_process_name AS Process_codigo,
    prcodet.PR_Codigo_Protheus AS PR_Codigo,
    vc.Codigo AS Codigo,
    SUM(ISNULL(vc.Qty, 0)) AS Cantidad,
    vc.subType AS subType,
    seq_pick.SeqValue AS num_busqueda,
    wa_pick.WorkArea_CatalogueId,
    wa_pick.WorkArea_Nombre,
    wa_pick.WorkArea_Revision,
    MAX(vc.bl_formula) AS bl_formula,
    MAX(vc.idContext) AS idContext
FROM Process p
LEFT JOIN ProcessRevision pr
    ON pr.masterRef = p.id_Table
LEFT JOIN ProcessOccurrence po
    ON po.instancedRef = pr.id_Table
OUTER APPLY (
    SELECT TOP (1)
        wa2.catalogueId AS WorkArea_CatalogueId,
        wa2.name AS WorkArea_Nombre,
        war2.revision AS WorkArea_Revision
    FROM(
        SELECT po.id_Table AS ParentRef
        UNION ALL
        SELECT po_op2.id_Table
        FROM ProcessOccurrence po_op2
        WHERE po_op2.parentRef = po.id_Table
          AND po_op2.idXml = po.idXml
    ) prf
    JOIN Occurrence owa
        ON owa.parentRef = prf.ParentRef
       AND owa.idXml = po.idXml
       AND owa.subType IN ('MEWorkarea','MEWorkArea')
    JOIN WorkAreaRevision war2
        ON war2.id_Table = owa.instancedRef
       AND war2.idXml = owa.idXml
    JOIN WorkArea wa2
        ON wa2.id_Table = war2.masterRef
       AND wa2.idXml = war2.idXml
    ORDER BY
        CASE WHEN wa2.name LIKE '%TERCEROS%' THEN 0 ELSE 1 END,
        owa.id_Table
) wa_pick

OUTER APPLY(
    SELECT TOP (1) s.SeqValue
    FROM(
        SELECT
            NULLIF(LTRIM(RTRIM(uvpo.value)), '') AS SeqValue,
            1 AS prio
        FROM UserValue_ProcessOccurrence uvpo
        WHERE uvpo.id_Father = po.id_Table
          AND uvpo.title IN ('SequenceNumber','sequencenumber','sequenceNumber')

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uvpo2.value)), '') AS SeqValue,
            2 AS prio
        FROM UserValue_ProcessOccurrence uvpo2
        WHERE uvpo2.id_Father = po.id_Table
          AND uvpo2.title LIKE '%Sequence%'

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uv.value)), '') AS SeqValue,
            3 AS prio
        FROM UserData ud
        JOIN UserValue_UserData uv
            ON uv.id_Father = ud.id_Table
           AND uv.title IN ('SequenceNumber','sequencenumber')
        WHERE ud.id_Father = po.id_Table

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uv2.value)), '') AS SeqValue,
            4 AS prio
        FROM UserValue_UserData uv2
        WHERE uv2.id_Father = po.id_Table + 2
          AND uv2.title IN ('SequenceNumber','sequencenumber')

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uv3.value)), '') AS SeqValue,
            5 AS prio
        FROM UserValue_UserData uv3
        WHERE uv3.id_Father = po.id_Table
          AND uv3.title IN ('SequenceNumber','sequencenumber')
    ) s
    WHERE s.SeqValue IS NOT NULL
    ORDER BY s.prio
) seq_pick

CROSS APPLY(
    SELECT
        CASE
            WHEN LEFT(p.catalogueId, 1) IN('M','E')
                THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 1)
            WHEN RIGHT(p.catalogueId, 3) = '-FV'
                THEN LEFT(p.catalogueId, LEN(p.catalogueId) - 3)
            WHEN LEFT(p.catalogueId, 2) = 'P-'
                THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
            ELSE p.catalogueId
        END AS PR_Codigo_Norm
) prcode

CROSS APPLY(
    SELECT
        CASE
            WHEN prcode.PR_Codigo_Norm IS NOT NULL
             AND LEFT(prcode.PR_Codigo_Norm, 2) = 'PR'
             AND RIGHT(prcode.PR_Codigo_Norm, 1) <> 'T'
             AND(
                    wa_pick.WorkArea_CatalogueId = '000465'
                 OR UPPER(LTRIM(RTRIM(wa_pick.WorkArea_Nombre))) = 'TERCEROS'
                 OR UPPER(wa_pick.WorkArea_Nombre) LIKE '%TERCEROS%'
             )
            THEN prcode.PR_Codigo_Norm + 'T'
            ELSE prcode.PR_Codigo_Norm
        END AS PR_Codigo_Protheus
) prcodet

LEFT JOIN ValidChildren vc
    ON vc.PR_OccId = po.id_Table
CROSS JOIN FirstProcessName fpn
WHERE
    fpn.first_process_name<> prcode.PR_Codigo_Norm
GROUP BY
    fpn.first_process_name,
    p.catalogueId,
    prcodet.PR_Codigo_Protheus,
    vc.Codigo,
    vc.subType,
    seq_pick.SeqValue,
    wa_pick.WorkArea_CatalogueId,
    wa_pick.WorkArea_Nombre,
    wa_pick.WorkArea_Revision
ORDER BY
    TRY_CONVERT(INT, seq_pick.SeqValue) DESC,
    TRY_CONVERT(INT, SUBSTRING(p.catalogueId, 4, LEN(p.catalogueId) - 3)) DESC";

        // Placeholder temporal: después reemplazar por la query real para múltiples WorkAreas
        private static readonly string consultaSG1_BOP_ConMasWorkareas = @"
WITH FirstProcessName AS (
    SELECT TOP (1)
        CASE
            WHEN LEN(p.catalogueId) != 8 THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
            ELSE RIGHT(p.catalogueId, 6)
        END AS first_process_name
    FROM Process p
    LEFT JOIN ProcessRevision pr
        ON pr.masterRef = p.id_Table
    LEFT JOIN ProcessOccurrence po
        ON pr.id_Table = po.instancedRef
    WHERE po.parentRef IS NULL
),
ValidChildren AS (
    SELECT
        po.id_Table AS PR_OccId,
        CASE
            WHEN LEFT(prod.productId, 1) IN ('M','E')
                THEN RIGHT(prod.productId, LEN(prod.productId) - 1)
            WHEN RIGHT(prod.productId, 3) = '-FV'
                THEN LEFT(prod.productId, LEN(prod.productId) - 3)
            ELSE prod.productId
        END AS Codigo,
        prod_rev.subType AS subType,
        CASE
            WHEN prod.productId IS NULL THEN 0
            WHEN ISNULL(qty_pick.QtyValue, 0) > 0 THEN qty_pick.QtyValue
            ELSE 1
        END AS Qty,
        ISNULL(bl_pick.bl_formula_val, '') AS bl_formula,
        CASE
            WHEN ISNULL(bl_pick.bl_formula_val, '') != ''
             AND CHARINDEX('[''', bl_pick.bl_formula_val) > 0
             AND CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val)) > 0
            THEN SUBSTRING(
                    bl_pick.bl_formula_val,
                    CHARINDEX('[''', bl_pick.bl_formula_val) + 2,
                    CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val))
                      - CHARINDEX('[''', bl_pick.bl_formula_val) - 2)
            ELSE ''
        END AS idContext
    FROM Process p
    LEFT JOIN ProcessRevision pr
        ON pr.masterRef = p.id_Table
    LEFT JOIN ProcessOccurrence po
        ON po.instancedRef = pr.id_Table
    LEFT JOIN ProcessOccurrence po_op
        ON po_op.parentRef = po.id_Table
    LEFT JOIN Occurrence o
        ON (
            o.parentRef = po.id_Table
            OR o.parentRef = po_op.id_Table
        )
        AND ISNULL(o.subType,'') <> 'METool'
    LEFT JOIN ProductRevision prod_rev
        ON prod_rev.id_Table = o.instancedRef
    LEFT JOIN Product prod
        ON prod.id_Table = prod_rev.masterRef
    OUTER APPLY (
        SELECT TOP (1) q.QtyValue
        FROM (
            SELECT
                TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvO.value)), ''), ',', '.')) AS QtyValue,
                1 AS prio
            FROM UserValue_Occurrence uvO
            WHERE uvO.id_Father = o.id_Table
              AND uvO.title IN ('Quantity','quantity')

            UNION ALL

            SELECT
                TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvUD.value)), ''), ',', '.')) AS QtyValue,
                2 AS prio
            FROM UserData ud
            JOIN UserValue_UserData uvUD
                ON uvUD.id_Father = ud.id_Table
               AND uvUD.title IN ('Quantity','quantity')
            WHERE ud.id_Father = o.id_Table

            UNION ALL

            SELECT
                TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvUD2.value)), ''), ',', '.')) AS QtyValue,
                3 AS prio
            FROM UserValue_UserData uvUD2
            WHERE uvUD2.id_Father = o.id_Table + 2
              AND uvUD2.title IN ('Quantity','quantity')

        ) q
        WHERE q.QtyValue IS NOT NULL
        ORDER BY q.prio
    ) qty_pick
    OUTER APPLY (
        SELECT TOP 1 uvo.value AS bl_formula_val
        FROM Occurrence o_formula
        INNER JOIN UserValue_Occurrence uvo
            ON uvo.id_Father = o_formula.id_Table
           AND uvo.title = 'bl_formula'
        WHERE o_formula.instancedRef = o.instancedRef
          AND uvo.value IS NOT NULL
          AND LTRIM(RTRIM(uvo.value)) != ''
    ) bl_pick
    WHERE
        prod.productId IS NOT NULL
        AND NOT (
            prod_rev.subType IN ('Mfg0MEFactoryToolRevision', 'Mfg0MEResourceRevision')
            OR
            (
                prod_rev.subType IN ('Agm4_MatPrimaRevision','Agm4_RepCompradoRevision')
                AND ISNULL(qty_pick.QtyValue, 0) <= 0
            )
        )
)
SELECT
    fpn.first_process_name AS Process_codigo,
    prcodet.PR_Codigo_Protheus AS PR_Codigo,
    vc.Codigo AS Codigo,
    SUM(ISNULL(vc.Qty, 0)) AS Cantidad,
    vc.subType AS subType,
    seq_pick.SeqValue AS num_busqueda,
    wa_pick.WorkArea_CatalogueId,
    wa_pick.WorkArea_Nombre,
    wa_pick.WorkArea_Revision,
    MAX(vc.bl_formula) AS bl_formula,
    MAX(vc.idContext) AS idContext
FROM Process p
LEFT JOIN ProcessRevision pr
    ON pr.masterRef = p.id_Table
LEFT JOIN ProcessOccurrence po
    ON po.instancedRef = pr.id_Table
OUTER APPLY (
    SELECT TOP (1)
        wa2.catalogueId AS WorkArea_CatalogueId,
        wa2.name AS WorkArea_Nombre,
        war2.revision AS WorkArea_Revision
    FROM (
        SELECT po.id_Table AS ParentRef
        UNION ALL
        SELECT po_op2.id_Table
        FROM ProcessOccurrence po_op2
        WHERE po_op2.parentRef = po.id_Table
          AND po_op2.idXml = po.idXml
    ) prf
    JOIN WorkareaOccurrence owa
        ON owa.parentRef = prf.ParentRef
       AND owa.idXml = po.idXml
       AND owa.subType IN ('MEWorkarea','MEWorkArea')
    JOIN WorkAreaRevision war2
        ON war2.id_Table = owa.instancedRef
       AND war2.idXml = owa.idXml
    JOIN WorkArea wa2
        ON wa2.id_Table = war2.masterRef
       AND wa2.idXml = war2.idXml
    ORDER BY
        CASE WHEN wa2.name LIKE '%TERCEROS%' THEN 0 ELSE 1 END,
        owa.id_Table
) wa_pick

OUTER APPLY (
    SELECT TOP (1) s.SeqValue
    FROM (
        SELECT
            NULLIF(LTRIM(RTRIM(uvpo.value)), '') AS SeqValue,
            1 AS prio
        FROM UserValue_ProcessOccurrence uvpo
        WHERE uvpo.id_Father = po.id_Table
          AND uvpo.title IN ('SequenceNumber','sequencenumber','sequenceNumber')

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uvpo2.value)), '') AS SeqValue,
            2 AS prio
        FROM UserValue_ProcessOccurrence uvpo2
        WHERE uvpo2.id_Father = po.id_Table
          AND uvpo2.title LIKE '%Sequence%'

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uv.value)), '') AS SeqValue,
            3 AS prio
        FROM UserData ud
        JOIN UserValue_UserData uv
            ON uv.id_Father = ud.id_Table
           AND uv.title IN ('SequenceNumber','sequencenumber')
        WHERE ud.id_Father = po.id_Table

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uv2.value)), '') AS SeqValue,
            4 AS prio
        FROM UserValue_UserData uv2
        WHERE uv2.id_Father = po.id_Table + 2
          AND uv2.title IN ('SequenceNumber','sequencenumber')

        UNION ALL

        SELECT
            NULLIF(LTRIM(RTRIM(uv3.value)), '') AS SeqValue,
            5 AS prio
        FROM UserValue_UserData uv3
        WHERE uv3.id_Father = po.id_Table
          AND uv3.title IN ('SequenceNumber','sequencenumber')
    ) s
    WHERE s.SeqValue IS NOT NULL
    ORDER BY s.prio
) seq_pick

CROSS APPLY (
    SELECT
        CASE
            WHEN LEFT(p.catalogueId, 1) IN ('M','E')
                THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 1)
            WHEN RIGHT(p.catalogueId, 3) = '-FV'
                THEN LEFT(p.catalogueId, LEN(p.catalogueId) - 3)
            WHEN LEFT(p.catalogueId, 2) = 'P-'
                THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
            ELSE p.catalogueId
        END AS PR_Codigo_Norm
) prcode

CROSS APPLY (
    SELECT
        CASE
            WHEN prcode.PR_Codigo_Norm IS NOT NULL
             AND LEFT(prcode.PR_Codigo_Norm, 2) = 'PR'
             AND RIGHT(prcode.PR_Codigo_Norm, 1) <> 'T'
             AND (
                    wa_pick.WorkArea_CatalogueId = '000465'
                 OR UPPER(LTRIM(RTRIM(wa_pick.WorkArea_Nombre))) = 'TERCEROS'
                 OR UPPER(wa_pick.WorkArea_Nombre) LIKE '%TERCEROS%'
             )
            THEN prcode.PR_Codigo_Norm + 'T'
            ELSE prcode.PR_Codigo_Norm
        END AS PR_Codigo_Protheus
) prcodet

LEFT JOIN ValidChildren vc
    ON vc.PR_OccId = po.id_Table
CROSS JOIN FirstProcessName fpn
WHERE
    fpn.first_process_name <> prcode.PR_Codigo_Norm
GROUP BY
    fpn.first_process_name,
    p.catalogueId,
    prcodet.PR_Codigo_Protheus,
    vc.Codigo,
    vc.subType,
    seq_pick.SeqValue,
    wa_pick.WorkArea_CatalogueId,
    wa_pick.WorkArea_Nombre,
    wa_pick.WorkArea_Revision
ORDER BY
    TRY_CONVERT(INT, seq_pick.SeqValue) DESC,
    TRY_CONVERT(INT, SUBSTRING(p.catalogueId, 4, LEN(p.catalogueId) - 3)) DESC
";
        // ✅ Query alternativa: SIN WorkArea (ya está con Qty corregida)
        private const string consultaSG1_BOP_SinWorkArea = @"
WITH FirstProcessName AS (
    SELECT TOP (1)
        CASE
            WHEN LEN(p.catalogueId) != 8 THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
            ELSE RIGHT(p.catalogueId, 6)
        END AS first_process_name
    FROM Process p
    LEFT JOIN ProcessRevision pr
        ON pr.masterRef = p.id_Table
    LEFT JOIN ProcessOccurrence po
        ON pr.id_Table = po.instancedRef
    WHERE po.parentRef IS NULL
),
OccKey AS (
    SELECT
        o.id_Table AS OccIdStr,
        o.idXml,
        TRY_CONVERT(BIGINT, o.id_Table) AS OccIdNum
    FROM Occurrence o
),
PoKey AS (
    SELECT
        po.id_Table AS PoIdStr,
        po.idXml,
        TRY_CONVERT(BIGINT, po.id_Table) AS PoIdNum
    FROM ProcessOccurrence po
),
QtyCandidates AS (
    SELECT
        TRY_CONVERT(BIGINT, uvO.id_Father) AS OccIdNum,
        uvO.idXml,
        TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvO.value)), ''), ',', '.')) AS QtyValue,
        1 AS prio
    FROM UserValue_Occurrence uvO
    WHERE uvO.title IN ('Quantity','quantity')

    UNION ALL

    SELECT
        TRY_CONVERT(BIGINT, ud.id_Father) AS OccIdNum,
        uvUD.idXml,
        TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvUD.value)), ''), ',', '.')) AS QtyValue,
        2 AS prio
    FROM UserData ud
    JOIN UserValue_UserData uvUD
        ON uvUD.id_Father = ud.id_Table
       AND uvUD.title IN ('Quantity','quantity')
    WHERE ud.id_Father IS NOT NULL

    UNION ALL

    SELECT
        TRY_CONVERT(BIGINT, uvUD2.id_Father) - 2 AS OccIdNum,
        uvUD2.idXml,
        TRY_CONVERT(FLOAT, REPLACE(NULLIF(LTRIM(RTRIM(uvUD2.value)), ''), ',', '.')) AS QtyValue,
        3 AS prio
    FROM UserValue_UserData uvUD2
    WHERE uvUD2.title IN ('Quantity','quantity')
),
QtyFinal AS (
    SELECT OccIdNum, idXml, QtyValue
    FROM (
        SELECT
            ok.OccIdNum,
            ok.idXml,
            qc.QtyValue,
            ROW_NUMBER() OVER (
                PARTITION BY ok.OccIdNum, ok.idXml
                ORDER BY
                    CASE WHEN qc.idXml = ok.idXml THEN 0 ELSE 1 END,
                    qc.prio
            ) AS rn
        FROM OccKey ok
        JOIN QtyCandidates qc
            ON qc.OccIdNum = ok.OccIdNum
           AND (qc.idXml = ok.idXml OR qc.idXml IS NULL)
        WHERE ok.OccIdNum IS NOT NULL
          AND qc.QtyValue IS NOT NULL
    ) x
    WHERE rn = 1
),
SeqCandidates AS (
    SELECT
        TRY_CONVERT(BIGINT, uvpo.id_Father) AS PoIdNum,
        uvpo.idXml,
        NULLIF(LTRIM(RTRIM(uvpo.value)), '') AS SeqValue,
        1 AS prio
    FROM UserValue_ProcessOccurrence uvpo
    WHERE uvpo.title IN ('SequenceNumber','sequencenumber','sequenceNumber')

    UNION ALL

    SELECT
        TRY_CONVERT(BIGINT, uvpo2.id_Father) AS PoIdNum,
        uvpo2.idXml,
        NULLIF(LTRIM(RTRIM(uvpo2.value)), '') AS SeqValue,
        2 AS prio
    FROM UserValue_ProcessOccurrence uvpo2
    WHERE uvpo2.title LIKE '%Sequence%'

    UNION ALL

    SELECT
        TRY_CONVERT(BIGINT, ud.id_Father) AS PoIdNum,
        uv.idXml,
        NULLIF(LTRIM(RTRIM(uv.value)), '') AS SeqValue,
        3 AS prio
    FROM UserData ud
    JOIN UserValue_UserData uv
        ON uv.id_Father = ud.id_Table
       AND uv.title IN ('SequenceNumber','sequencenumber','sequenceNumber')
    WHERE ud.id_Father IS NOT NULL

    UNION ALL

    SELECT
        TRY_CONVERT(BIGINT, uv2.id_Father) - 2 AS PoIdNum,
        uv2.idXml,
        NULLIF(LTRIM(RTRIM(uv2.value)), '') AS SeqValue,
        4 AS prio
    FROM UserValue_UserData uv2
    WHERE uv2.title IN ('SequenceNumber','sequencenumber','sequenceNumber')

    UNION ALL

    SELECT
        TRY_CONVERT(BIGINT, uv3.id_Father) AS PoIdNum,
        uv3.idXml,
        NULLIF(LTRIM(RTRIM(uv3.value)), '') AS SeqValue,
        5 AS prio
    FROM UserValue_UserData uv3
    WHERE uv3.title IN ('SequenceNumber','sequencenumber','sequenceNumber')
),
SeqFinal AS (
    SELECT PoIdNum, idXml, SeqValue
    FROM (
        SELECT
            pk.PoIdNum,
            pk.idXml,
            sc.SeqValue,
            ROW_NUMBER() OVER (
                PARTITION BY pk.PoIdNum, pk.idXml
                ORDER BY
                    CASE WHEN sc.idXml = pk.idXml THEN 0 ELSE 1 END,
                    sc.prio
            ) AS rn
        FROM PoKey pk
        JOIN SeqCandidates sc
            ON sc.PoIdNum = pk.PoIdNum
           AND (sc.idXml = pk.idXml OR sc.idXml IS NULL)
        WHERE pk.PoIdNum IS NOT NULL
          AND sc.SeqValue IS NOT NULL
    ) y
    WHERE rn = 1
),
ValidChildren AS (
    SELECT
        po.id_Table AS PR_OccId,
        CASE
            WHEN LEFT(prod.productId, 1) IN ('M','E')
                THEN RIGHT(prod.productId, LEN(prod.productId) - 1)
            WHEN RIGHT(prod.productId, 3) = '-FV'
                THEN LEFT(prod.productId, LEN(prod.productId) - 3)
            ELSE prod.productId
        END AS Codigo,
        prod_rev.subType AS subType,
        CASE
            WHEN prod.productId IS NULL THEN 0
            WHEN ISNULL(qf.QtyValue, 0) > 0 THEN qf.QtyValue
            ELSE 1
        END AS Qty,
        ISNULL(bl_pick.bl_formula_val, '') AS bl_formula,
        CASE
            WHEN ISNULL(bl_pick.bl_formula_val, '') != ''
             AND CHARINDEX('[''', bl_pick.bl_formula_val) > 0
             AND CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val)) > 0
            THEN SUBSTRING(
                    bl_pick.bl_formula_val,
                    CHARINDEX('[''', bl_pick.bl_formula_val) + 2,
                    CHARINDEX(''']', bl_pick.bl_formula_val, CHARINDEX('[''', bl_pick.bl_formula_val))
                      - CHARINDEX('[''', bl_pick.bl_formula_val) - 2)
            ELSE ''
        END AS idContext
    FROM Process p
    LEFT JOIN ProcessRevision pr
        ON pr.masterRef = p.id_Table
    LEFT JOIN ProcessOccurrence po
        ON po.instancedRef = pr.id_Table
    LEFT JOIN ProcessOccurrence po_op
        ON po_op.parentRef = po.id_Table
       AND po_op.idXml = po.idXml
    LEFT JOIN Occurrence o
        ON (
            o.parentRef = po.id_Table
            OR o.parentRef = po_op.id_Table
        )
       AND o.idXml = po.idXml
       AND ISNULL(o.subType,'') <> 'METool'
    LEFT JOIN OccKey ok
        ON ok.OccIdStr = o.id_Table
       AND ok.idXml = o.idXml
    LEFT JOIN QtyFinal qf
        ON qf.OccIdNum = ok.OccIdNum
       AND qf.idXml = ok.idXml
    LEFT JOIN ProductRevision prod_rev
        ON prod_rev.id_Table = o.instancedRef
       AND prod_rev.idXml = o.idXml
    LEFT JOIN Product prod
        ON prod.id_Table = prod_rev.masterRef
       AND prod.idXml = prod_rev.idXml
    OUTER APPLY (
        SELECT TOP 1 uvo.value AS bl_formula_val
        FROM Occurrence o_formula
        INNER JOIN UserValue_Occurrence uvo
            ON uvo.id_Father = o_formula.id_Table
           AND uvo.title = 'bl_formula'
        WHERE o_formula.instancedRef = o.instancedRef
          AND uvo.value IS NOT NULL
          AND LTRIM(RTRIM(uvo.value)) != ''
    ) bl_pick
    WHERE
        prod.productId IS NOT NULL
        AND prod_rev.subType NOT IN ('Mfg0MEResourceRevision', 'Mfg0MEFactoryToolRevision')
        AND NOT (
            prod_rev.subType IN ('Agm4_MatPrimaRevision','Agm4_RepCompradoRevision')
            AND ISNULL(qf.QtyValue, 0) <= 0
        )
)
SELECT
    fpn.first_process_name AS Process_codigo,
    CASE
        WHEN LEFT(p.catalogueId, 1) IN ('M','E')
            THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 1)
        WHEN RIGHT(p.catalogueId, 3) = '-FV'
            THEN LEFT(p.catalogueId, LEN(p.catalogueId) - 3)
        WHEN LEFT(p.catalogueId, 2) = 'P-'
            THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
        ELSE p.catalogueId
    END AS PR_Codigo,
    vc.Codigo AS Codigo,
    SUM(ISNULL(vc.Qty, 0)) AS Cantidad,
    vc.subType AS subType,
    sf.SeqValue AS num_busqueda,
    NULL AS WorkArea_CatalogueId,
    NULL AS WorkArea_Nombre,
    NULL AS WorkArea_Revision,
    MAX(vc.bl_formula) AS bl_formula,
    MAX(vc.idContext) AS idContext
FROM Process p
LEFT JOIN ProcessRevision pr
    ON pr.masterRef = p.id_Table
LEFT JOIN ProcessOccurrence po
    ON po.instancedRef = pr.id_Table
LEFT JOIN PoKey pk
    ON pk.PoIdStr = po.id_Table
   AND pk.idXml = po.idXml
LEFT JOIN SeqFinal sf
    ON sf.PoIdNum = pk.PoIdNum
   AND sf.idXml = pk.idXml
LEFT JOIN ValidChildren vc
    ON vc.PR_OccId = po.id_Table
CROSS JOIN FirstProcessName fpn
WHERE
    fpn.first_process_name <>
    CASE
        WHEN LEFT(p.catalogueId, 1) IN ('M','E')
            THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 1)
        WHEN RIGHT(p.catalogueId, 3) = '-FV'
            THEN LEFT(p.catalogueId, LEN(p.catalogueId) - 3)
        WHEN LEFT(p.catalogueId, 2) = 'P-'
            THEN RIGHT(p.catalogueId, LEN(p.catalogueId) - 2)
        ELSE p.catalogueId
    END
GROUP BY
    fpn.first_process_name,
    p.catalogueId,
    vc.Codigo,
    vc.subType,
    sf.SeqValue
ORDER BY
    TRY_CONVERT(INT, sf.SeqValue) DESC,
    TRY_CONVERT(INT, SUBSTRING(p.catalogueId, 4, LEN(p.catalogueId) - 3)) DESC;
";

        private static string ObtenerConsultaZG1_BOP(SqlConnection connection)
        {
            bool occurrenceTieneSubTypeMEWorkarea = false;
            bool workAreaOccurrenceTieneSubType = false;

            using (var cmd = new SqlCommand(@"
                IF COL_LENGTH('Occurrence','subType') IS NOT NULL
                AND EXISTS (
                    SELECT 1 FROM Occurrence WHERE subType = 'MEWorkarea'
                )
                SELECT 1;", connection))
            {
                occurrenceTieneSubTypeMEWorkarea = cmd.ExecuteScalar() is not null and not DBNull;
            }

            if (occurrenceTieneSubTypeMEWorkarea)
            {
                Log("ZG1 -> ObtenerConsultaZG1_BOP: Occurrence.subType='MEWorkarea'. Se elige CON 1 WorkArea.");
                return consultaSG1_BOP_Con1Workarea;
            }

            using (var cmd = new SqlCommand(@"
                    SELECT 1
                    WHERE COL_LENGTH('WorkAreaOccurrence','subType') IS NOT NULL;", connection))
            {
                workAreaOccurrenceTieneSubType = cmd.ExecuteScalar() is not null and not DBNull;
            }

            if (workAreaOccurrenceTieneSubType)
            {
                Log("ZG1 -> ObtenerConsultaZG1_BOP: WorkAreaOccurrence tiene subType. Se elige CON MÁS WorkAreas.");
                return consultaSG1_BOP_ConMasWorkareas;
            }

            Log("ZG1 -> ObtenerConsultaZG1_BOP: No se detecta subType en Occurrence ni en WorkAreaOccurrence. Se elige SIN WorkArea.");
            return consultaSG1_BOP_SinWorkArea;
        }

        public static async Task postSG1(
			Dictionary<string, List<List<Dictionary<string, string>>>> estructurasBopCompleta,
			bool yaHizoResetGlobal = false)
		{
			var putFallBack = new Dictionary<string, List<List<Dictionary<string, string>>>>();

			Log($"[SG1-POST] Iniciando envío masivo. Productos: {estructurasBopCompleta.Count}");

			foreach (var parent in estructurasBopCompleta)
			{
				string producto = parent.Key;

				var jsonBody = new
				{
					producto = producto,
					qtdBase = "1",
					estructura = parent.Value
				};

				string jsonData = JsonConvert.SerializeObject(jsonBody, Formatting.Indented);

				// JSON request -> log JSON
				LogJson($"[SG1][POST] JSON COMPLETO para producto {producto}:", jsonData);

				var (statusCode, httpStatus, responseData) = await PostSG1RawAsync(producto, jsonData);

				if (httpStatus == HttpStatusCode.Conflict)
				{
					Log($"[SG1][POST] 409 Conflict para {producto}. Se acumula para PUT.");
					putFallBack[producto] = parent.Value;
					ActualizarBase(statusCode, responseData, producto);
					continue;
				}

				ActualizarBase(statusCode, responseData, producto);
			}

			if (putFallBack.Count > 0)
			{
				Log($"[SG1-POST] POST finalizado. 409 acumulados={putFallBack.Count}. Disparando PUT...");
				await putSG1(putFallBack, estructurasBopCompleta, yaHizoResetGlobal);
			}
			else
			{
				Log("[SG1-POST] POST finalizado. Sin 409.");
			}
		}

		private static (string codigo, List<List<Dictionary<string, string>>> estructura)
			ConvertirJsonAEstructura(string jsonString)
		{
			var root = JObject.Parse(jsonString);

			string codigo = root["producto"]?.ToString()
				?? throw new InvalidOperationException("JSON sin 'producto'.");

			var estructuraToken = root["estructura"]
				?? throw new InvalidOperationException("JSON sin 'estructura'.");

			var estructura = estructuraToken.ToObject<List<List<Dictionary<string, string>>>>()
				?? throw new InvalidOperationException("No se pudo mapear 'estructura'.");

			return (codigo, estructura);
		}

		public static List<string> jsonZG1_BOP()
		{
			//string connectionString = "Server=10.0.0.82;Database=AgrometalBOP;User Id=sa;Password=Descar_2020;";
			string connectionString = "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";

			Log("[ZG1-JSON][BOP] Iniciando generación de estructuras ZG1 desde BOP...");

			var listaJsons = new List<string>();

			string queryElegida;
			using (var conn = new SqlConnection(connectionString))
			{
				conn.Open();
				queryElegida = ObtenerConsultaZG1_BOP(conn);
			}

			try
			{
				using (SqlConnection connection = new SqlConnection(connectionString))
				{
					connection.Open();

					using (SqlCommand command = new SqlCommand(queryElegida, connection))
					{
						command.CommandTimeout = 1200;

						using (SqlDataReader reader = command.ExecuteReader())
						{
							// Agrupa filas por Process_codigo (padre)
							var dataByParent = new Dictionary<string, List<(string Codigo, string Cantidad, string BlFormula, string IdContext)>>(StringComparer.OrdinalIgnoreCase);

							int filasLeidas = 0;

							while (reader.Read())
							{
								filasLeidas++;

								string procesoCodigo = reader["Process_codigo"]?.ToString()?.Trim() ?? string.Empty;
								string codigo        = reader["Codigo"]?.ToString()?.Trim()         ?? string.Empty;
								string cantidad      = (reader["Cantidad"]?.ToString() ?? "1").Replace(',', '.');
								string blFormula     = reader["bl_formula"]?.ToString()             ?? string.Empty;
								string idContext     = reader["idContext"]?.ToString()               ?? string.Empty;

								if (string.IsNullOrWhiteSpace(procesoCodigo) && string.IsNullOrWhiteSpace(codigo))
								{
									Log("[ZG1-JSON][BOP] WARNING: fila sin Process_codigo ni Codigo. Se omite.");
									continue;
								}

								string padre = string.IsNullOrWhiteSpace(procesoCodigo) ? codigo : procesoCodigo;

								if (!dataByParent.ContainsKey(padre))
									dataByParent[padre] = new List<(string, string, string, string)>();

								dataByParent[padre].Add((codigo, cantidad, blFormula, idContext));
							}

							Log($"[ZG1-JSON][BOP] SQL procesado. Filas leídas={filasLeidas} | Padres={dataByParent.Count}");

							foreach (var parentGroup in dataByParent)
							{
								string codigoPadre = parentGroup.Key;
								var hijos = parentGroup.Value;

								// idContext se toma del primer hijo que lo tenga (misma lógica que MBOM ZG1)
								string idContextFinal = hijos
									.Select(h => h.IdContext)
									.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;

								var estructuraJson = new System.Text.StringBuilder();
								estructuraJson.Append("[");

								bool primerHijo = true;
								foreach (var hijo in hijos)
								{
									if (string.IsNullOrWhiteSpace(hijo.Codigo)) continue;

									if (!primerHijo) estructuraJson.Append(",");
									primerHijo = false;

									string cantidadFmt = decimal.TryParse(hijo.Cantidad, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal cantDec)
										? cantDec.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)
										: (hijo.Cantidad ?? "1");

									estructuraJson.Append("[");
									estructuraJson.Append($"{{\"campo\":\"codigo\",\"valor\":\"{EscapeJson(hijo.Codigo)}\"}}");
									estructuraJson.Append($",{{\"campo\":\"cantidad\",\"valor\":\"{EscapeJson(cantidadFmt)}\"}}");

									if (!string.IsNullOrWhiteSpace(hijo.BlFormula))
										estructuraJson.Append($",{{\"campo\":\"formula\",\"valor\":\"{EscapeJson(hijo.BlFormula)}\"}}");

									estructuraJson.Append("]");
								}

								estructuraJson.Append("]");

								var estructura = JsonConvert.DeserializeObject<JArray>(estructuraJson.ToString());
								var jsonPayload = new JObject
								{
									["producto"]   = codigoPadre,
									["idContext"]  = idContextFinal,
									["qtdBase"]    = "1",
									["estructura"] = estructura
								};
								string jsonFinal = jsonPayload.ToString(Formatting.Indented);

								listaJsons.Add(jsonFinal);
								Log($"[ZG1-JSON][BOP] Padre {codigoPadre}: hijos={hijos.Count} | idContext={idContextFinal}");
								LogJson($"[ZG1][BOP][JSON] Producto {codigoPadre}:", jsonFinal);
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log($"[ZG1-JSON][BOP] ERROR al consultar la base: {ex.Message}");
				Log($"[ZG1-JSON][BOP] Stack trace: {ex.StackTrace}");
			}

			Log($"[ZG1-JSON][BOP] Generación finalizada. JSONs generados: {listaJsons.Count}");
			return listaJsons;
		}

		public static async Task putSG1(
			Dictionary<string, List<List<Dictionary<string, string>>>> estructuras409,
			Dictionary<string, List<List<Dictionary<string, string>>>> estructurasBopCompleta,
			bool yaHizoResetGlobal)
		{
			Log($"[SG1-PUT] Iniciando PUT masivo. Cantidad de productos: {estructuras409.Count}");

			bool recursividadDetectada = false;
			string? codigoQueDisparo = null;
			string? bodyQueDisparo = null;

			foreach (var parent in estructuras409)
			{
				string producto = parent.Key;

				var jsonBody = new
				{
					producto = parent.Key,
					qtdBase = "1",
					estructura = parent.Value
				};

				string jsonData = JsonConvert.SerializeObject(jsonBody, Formatting.Indented);

				// JSON request -> log JSON
				LogJson($"[SG1][PUT] JSON COMPLETO para producto {producto}:", jsonData);

				JObject obj = JObject.Parse(jsonData);
				string? codigo = obj["producto"]?.ToString();
				if (string.IsNullOrEmpty(codigo))
					continue;

				int intento = 0;

				while (true)
				{
					intento++;

					int statusCode = 0;
					string responseData = string.Empty;

					try
					{
						await ProtheusHealth.WaitUntilActiveAsync("SG1-PUT", RetryDelay);
						using var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
						using HttpResponseMessage response = await _clientSG1.PutAsync(SG1_PUT_URL, content);

						statusCode = (int)response.StatusCode;
						responseData = await response.Content.ReadAsStringAsync();
						LogErrorProtheusIfAny("SG1", "PUT", codigo, responseData);

						if (!response.IsSuccessStatusCode &&
							!IsRetryableStatus(response.StatusCode) &&
							IsRecursividadError(responseData))
						{
							recursividadDetectada = true;
							codigoQueDisparo = codigo;
							bodyQueDisparo = responseData;

							Log($"[SG1][PUT] Recursividad detectada en {codigo}. Se solicita RESET global de la BOP.");
							break;
						}

						if (IsRetryableStatus(response.StatusCode))
						{
							Log($"[SG1][PUT] Protheus respondió {statusCode}. Reintento en 5 minutos. Intento #{intento}");
							Log($"[SG1][PUT] Respuesta: {responseData}");
							await Task.Delay(RetryDelay);
							continue;
						}

						Log($"[SG1][PUT] Código de estado: {statusCode}");
						Log($"[SG1][PUT] Respuesta: {responseData}");

						await ProtheusHealth.PostCheckBestEffortAsync("SG1-PUT");
						ActualizarBase(statusCode, responseData, codigo);
						break;
					}
					catch (Exception ex) when (IsRetryableException(ex))
					{
						Log($"[SG1][PUT] EXCEPCIÓN transitoria para {codigo}: {ex.Message}. Reintento en 5 minutos. Intento #{intento}");
						await Task.Delay(RetryDelay);
						continue;
					}
					catch (Exception ex)
					{
						Log($"[SG1][PUT] EXCEPCIÓN NO transitoria para {codigo}: {ex}");
						ActualizarBase(0, ex.ToString(), codigo);
						break;
					}
				}

				if (recursividadDetectada)
					break;
			}

			if (!recursividadDetectada)
			{
				Log("[SG1-PUT] PUT masivo finalizado sin recursividad.");
				return;
			}

			if (yaHizoResetGlobal)
			{
				Log($"[SG1-RESET] Recursividad volvió a ocurrir (disparó {codigoQueDisparo}). Ya se hizo reset global antes, se aborta reproceso. Body: {bodyQueDisparo}");
				if (!string.IsNullOrWhiteSpace(codigoQueDisparo))
					ActualizarBase(0, $"Recursividad persistente. Body={bodyQueDisparo}", codigoQueDisparo);
				return;
			}

			Log($"[SG1-RESET] Iniciando RESET global por recursividad. Disparó={codigoQueDisparo}. Se eliminarán {estructurasBopCompleta.Count} estructuras.");

			var okDelete = await EliminarEstructurasBopAsync(estructurasBopCompleta.Keys);

			Log($"[SG1-RESET] RESET global finalizado. okDelete={okDelete}. Se reprocesa BOP completa.");

			await postSG1(estructurasBopCompleta, yaHizoResetGlobal: true);
		}


		public static Dictionary<string, List<List<Dictionary<string, string>>>> jsonSG1_MBOM(HashSet<string> codigosFantasma)
		{
			if (codigosFantasma == null || codigosFantasma.Count == 0)
			{
				Log("[SG1-JSON][MBOM] Set de fantasmas vacío o null. No se generarán JSON de SG1.");
				return new Dictionary<string, List<List<Dictionary<string, string>>>>(StringComparer.OrdinalIgnoreCase);
			}

			//string connectionString = "Server=10.0.0.82;Database=AgrometalBOP;User Id=sa;Password=Descar_2020;";
			string connectionString = "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";
			Console.WriteLine("[SG1-JSON] Iniciando generación de estructuras SG1 desde SQL...");
			Utilidades.EscribirEnLog("[SG1-JSON] Iniciando generación de estructuras SG1 desde SQL...");

			string query = @"
WITH CTE_Hierarchy AS (
    SELECT DISTINCT
        Occurrence.id_table,
        ProductRevision.name,
        Product.productId AS codigo,
        CAST(Occurrence.parentRef AS INT) AS parentRef,
        ProductRevision.revision,
        ProductRevision.subType,
        Occurrence.idXml
    FROM Occurrence
    LEFT JOIN ProductRevision
        ON Occurrence.instancedRef = ProductRevision.id_Table
       AND Occurrence.idXml = ProductRevision.idXml
    LEFT JOIN Product
        ON ProductRevision.masterRef = Product.id_Table
       AND ProductRevision.idXml = Product.idXml
    GROUP BY
        Occurrence.id_table, ProductRevision.name, Product.productId,
        Occurrence.parentRef, ProductRevision.revision,
        ProductRevision.subType, Occurrence.idXml
),
Base AS (
    SELECT DISTINCT
        COALESCE(Parent.name, '') AS Nombre_Padre,
        COALESCE(CodFmt.CodigoPadre_Final, '') AS Process_codigo,
        Child.name AS Nombre_Hijo,
        CodFmt.CodigoHijo_Final AS Codigo_Hijo,
        Child.subType AS Subtype_Hijo,
        Qty.CantidadFinal AS CantidadHijo_Total,
        Child.revision AS Revision,

        MIN('PA') AS Tipo,
        MIN('01') AS Deposito,
        MAX(
            CASE
                WHEN uudUnidad.title = 'Agm4_Unidad' THEN uudUnidad.value
                WHEN uudUnidad.title = 'Agm4_Kilogramos' THEN uudUnidad.value
                WHEN uudUnidad.title = 'Agm4_Litros' THEN uudUnidad.value
                WHEN uudUnidad.title = 'Agm4_Metros' THEN uudUnidad.value
                ELSE 'UN'
            END
        ) AS unMedida

    FROM CTE_Hierarchy Child
    LEFT JOIN CTE_Hierarchy Parent
        ON Child.parentRef = Parent.id_table

    LEFT JOIN Form fUnidad
        ON Child.codigo = CASE
            WHEN CHARINDEX('/', fUnidad.name) > 0
                THEN LEFT(fUnidad.name, CHARINDEX('/', fUnidad.name) - 1)
            ELSE fUnidad.name
        END
    LEFT JOIN UserValue_UserData uudUnidad
        ON fUnidad.id_Table + 9 = uudUnidad.id_Father
       AND Child.idXml = uudUnidad.idXml

    LEFT JOIN (
        SELECT
            oPadre.id_Table AS ParentOccurrenceId,
            pHijo.productId AS ChildCodigo,
            CASE
                WHEN prHijo.subType = 'Agm4_MatPrimaRevision'
                    THEN SUM(TRY_CAST(uvud.value AS DECIMAL(18,6)))
                ELSE COUNT(DISTINCT oHijo.id_Table)
            END AS Cantidad
        FROM Product pHijo
        INNER JOIN ProductRevision prHijo
            ON pHijo.id_Table = prHijo.masterRef
        LEFT JOIN Occurrence oHijo
            ON oHijo.instancedRef = prHijo.id_Table
        LEFT JOIN UserValue_UserData uvud
            ON uvud.id_Father = oHijo.id_Table + 2
           AND uvud.title = 'Quantity'
        LEFT JOIN Occurrence oPadre
            ON oHijo.parentRef = oPadre.id_Table
        GROUP BY oPadre.id_Table, pHijo.productId, prHijo.subType
    ) sq3
        ON sq3.ParentOccurrenceId = Parent.id_table
       AND sq3.ChildCodigo = Child.codigo

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
                        WHEN LEFT(Parent.codigo, 1) = 'M' THEN RIGHT(Parent.codigo, LEN(Parent.codigo) - 1)
                        WHEN LEFT(Parent.codigo, 1) = 'E' THEN RIGHT(Parent.codigo, LEN(Parent.codigo) - 1)
                        WHEN RIGHT(Parent.codigo, 3) = '-FV' THEN LEFT(Parent.codigo, LEN(Parent.codigo) - 3)
                        ELSE Parent.codigo
                    END
            END AS CodigoPadre_SB1,

            CASE
                WHEN Child.codigo IS NULL THEN NULL
                ELSE
                    CASE
                        WHEN LEFT(Child.codigo, 2) = 'M-' THEN RIGHT(Child.codigo, LEN(Child.codigo) - 2)
                        WHEN LEFT(Child.codigo, 1) = 'M' THEN RIGHT(Child.codigo, LEN(Child.codigo) - 1)
                        WHEN LEFT(Child.codigo, 1) = 'E' THEN RIGHT(Child.codigo, LEN(Child.codigo) - 1)
                        WHEN RIGHT(Child.codigo, 3) = '-FV' THEN LEFT(Child.codigo, LEN(Child.codigo) - 3)
                        ELSE Child.codigo
                    END
            END AS CodigoHijo_SB1
    ) CodSB1

    OUTER APPLY (
        SELECT
            CASE
                WHEN Parent.codigo IS NULL THEN NULL
                ELSE
                    CASE
                        WHEN LEFT(CodSB1.CodigoPadre_SB1, 1) = 'E'
                            THEN SUBSTRING(CodSB1.CodigoPadre_SB1, 2, LEN(CodSB1.CodigoPadre_SB1) - 1)
                        ELSE CodSB1.CodigoPadre_SB1
                    END
            END AS CodigoPadre_SinE,

            CASE
                WHEN Parent.codigo IS NULL THEN CodSB1.CodigoHijo_SB1
                ELSE
                    CASE
                        WHEN LEFT(CodSB1.CodigoHijo_SB1, 1) = 'E'
                            THEN SUBSTRING(CodSB1.CodigoHijo_SB1, 2, LEN(CodSB1.CodigoHijo_SB1) - 1)
                        ELSE CodSB1.CodigoHijo_SB1
                    END
            END AS CodigoHijo_SinE
    ) CodSinE

    OUTER APPLY (
        SELECT
            CASE
                WHEN CodSinE.CodigoPadre_SinE IS NULL THEN NULL
                ELSE
                    CASE
                        WHEN RIGHT(CodSinE.CodigoPadre_SinE, 2) LIKE '-[0-9]'
                            THEN LEFT(CodSinE.CodigoPadre_SinE, LEN(CodSinE.CodigoPadre_SinE) - 2)
                        ELSE CodSinE.CodigoPadre_SinE
                    END
            END AS CodigoPadre_Final,

            CASE
                WHEN Parent.codigo IS NULL THEN CodSinE.CodigoHijo_SinE
                ELSE
                    CASE
                        WHEN RIGHT(CodSinE.CodigoHijo_SinE, 2) LIKE '-[0-9]'
                            THEN LEFT(CodSinE.CodigoHijo_SinE, LEN(CodSinE.CodigoHijo_SinE) - 2)
                        ELSE CodSinE.CodigoHijo_SinE
                    END
            END AS CodigoHijo_Final
    ) CodFmt

    WHERE
        Child.subType IN (
            'Agm4_ConGeneralRevision',
            'Agm4_MatPrimaRevision',
            'Agm4_PiezaRevision',
            'Agm4_RepCompradoRevision',
            'Agm4_SubConRevision',
            'Agm4_sub_mBOM_ERevision'
        )
    GROUP BY
        Parent.name,
        CodFmt.CodigoPadre_Final,
        Child.name,
        CodFmt.CodigoHijo_Final,
        Qty.CantidadFinal,
        Child.revision,
        Child.subType
)
SELECT
    b.*,
    CASE
        WHEN b.Subtype_Hijo = 'Agm4_SubConRevision'
         AND b.Nombre_Hijo NOT LIKE '%INT.%'
            THEN 'S'
        WHEN b.Subtype_Hijo = 'Agm4_sub_mBOM_ERevision'
         AND LEN(ISNULL(b.Codigo_Hijo,'')) = 6
            THEN 'S'
        WHEN b.Subtype_Hijo = 'Agm4_ConGeneralRevision'
         AND b.Nombre_Hijo NOT LIKE '%INT.%'
            THEN 'S'
        ELSE 'N'
    END AS Es_Fantasma
FROM Base b
ORDER BY b.Process_codigo;
";

			var estructuras = new Dictionary<string, List<List<Dictionary<string, string>>>>(StringComparer.OrdinalIgnoreCase);

			try
			{
				using (SqlConnection connection = new SqlConnection(connectionString))
				{
					connection.Open();
					Log("[SG1-JSON] Conexión a SQL abierta.");

					using (SqlCommand command = new SqlCommand(query, connection))
					{
						command.CommandTimeout = 300;

						using (SqlDataReader reader = command.ExecuteReader())
						{
							var dataByParent = new Dictionary<string, List<DataModel>>(StringComparer.OrdinalIgnoreCase);

							int filasLeidas = 0;
							int filasOmitidasPorNoFantasma = 0;

							while (reader.Read())
							{
								filasLeidas++;

								string parentName = reader["Nombre_Padre"]?.ToString() ?? string.Empty;
								string rawParentCod = reader["Process_codigo"]?.ToString();
								string childName = reader["Nombre_Hijo"]?.ToString() ?? string.Empty;
								string childCodigo = reader["Codigo_Hijo"]?.ToString() ?? string.Empty;
								string cantidadHijo = (reader["CantidadHijo_Total"]?.ToString() ?? string.Empty).Replace(',', '.');

								string parentCodigo = rawParentCod;
								bool esFilaRaiz = false;

								if (string.IsNullOrWhiteSpace(parentCodigo))
								{
									if (!string.IsNullOrWhiteSpace(childCodigo))
									{
										parentCodigo = childCodigo;
										esFilaRaiz = true;
										Log($"[SG1-JSON] Fila raíz detectada -> ParentCodigo={parentCodigo}, ChildCodigo={childCodigo}");
									}
									else
									{
										Log("[SG1-JSON] WARNING: fila sin Process_codigo ni Codigo_Hijo. Se omite.");
										continue;
									}
								}

								parentCodigo = parentCodigo.Trim();

								if (!codigosFantasma.Contains(parentCodigo))
								{
									filasOmitidasPorNoFantasma++;
									continue;
								}

								var model = new DataModel
								{
									ParentName = parentName,
									ParentCodigo = parentCodigo,
									ChildName = childName,
									ChildCodigo = childCodigo,
									CantidadHijo = cantidadHijo,
								};

								poblarBaseSG1(parentName, parentCodigo, childName, childCodigo, cantidadHijo);

								if (!dataByParent.ContainsKey(model.ParentCodigo))
									dataByParent[model.ParentCodigo] = new List<DataModel>();

								dataByParent[model.ParentCodigo].Add(model);
							}

							Log($"[SG1-JSON][MBOM] SQL procesado. Filas leídas={filasLeidas} | Filas omitidas(no fantasma)={filasOmitidasPorNoFantasma} | Padres fantasma={dataByParent.Count}");

							foreach (var parentGroup in dataByParent)
							{
								string parentCodigo = parentGroup.Key;
								List<DataModel> children = parentGroup.Value;

								if (!estructuras.ContainsKey(parentCodigo))
									estructuras[parentCodigo] = new List<List<Dictionary<string, string>>>();

								var allConditions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

								foreach (var child in children)
								{
									if (!string.IsNullOrEmpty(child.Variante))
									{
										try
										{
											var conditions = ExtractAllConditions(child.Variante);
											foreach (var condition in conditions)
											{
												if (condition.Key == null)
													continue;

												if (!allConditions.ContainsKey(condition.Key))
													allConditions[condition.Key] = new List<string>();

												if (!allConditions[condition.Key].Contains(condition.Value))
													allConditions[condition.Key].Add(condition.Value);
											}
										}
										catch
										{
										}
									}
								}

								var configCounter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

								foreach (var child in children)
								{
									if (!string.IsNullOrEmpty(child.ChildCodigo) &&
										child.ChildCodigo == parentCodigo)
									{
										Log($"[SG1-JSON] INFO: se omite línea autoreferenciada {child.ChildCodigo} en estructura de {parentCodigo}");
										continue;
									}

									var childStructure = new List<Dictionary<string, string>>
							{
								new() { { "campo", "codigo" }, { "valor", child.ChildCodigo } },
								new() { { "campo", "cantidad" }, { "valor", child.CantidadHijo } }
							};

									if (string.IsNullOrEmpty(child.Variante))
									{
										estructuras[parentCodigo].Add(childStructure);
										continue;
									}

									Dictionary<string, string> conditions;
									try
									{
										conditions = ExtractAllConditions(child.Variante);
									}
									catch
									{
										estructuras[parentCodigo].Add(childStructure);
										continue;
									}

									string grupoOpc = "001";
									string prefijoOpcional = null;

									if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SOLO SEMILLA") &&
										ContainsCondition(conditions, "SEMILLA-", "ELECTRICA"))
									{
										prefijoOpcional = "SSE";
									}
									else if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SOLO SEMILLA") &&
											 ContainsCondition(conditions, "SEMILLA-", "HIDRAULICA"))
									{
										prefijoOpcional = "SSH";
									}
									else if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SOLO SEMILLA") &&
											 ContainsCondition(conditions, "SEMILLA-", "MECANICA"))
									{
										prefijoOpcional = "SSM";
									}
									else if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SEMILLA + SIMPLE FERTILIZACION") &&
											 ContainsCondition(conditions, "FERTILIZACION-SIMPLE", "ELECTRICA"))
									{
										prefijoOpcional = "FSE";
									}
									else if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SEMILLA + SIMPLE FERTILIZACION") &&
											 ContainsCondition(conditions, "FERTILIZACION-SIMPLE", "HIDRAULICA"))
									{
										prefijoOpcional = "FSH";
									}
									else if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SEMILLA + SIMPLE FERTILIZACION") &&
											 ContainsCondition(conditions, "FERTILIZACION-SIMPLE", "MECANICA"))
									{
										prefijoOpcional = "FSM";
									}
									else if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SEMILLA + DOBLE FERTILIZACION") &&
											 ContainsCondition(conditions, "FERTILIZACION-DOBLE", "HIDRAULICA"))
									{
										prefijoOpcional = "FDH";
									}
									else if (ContainsCondition(conditions, "CONFIGURACION DE USO", "SEMILLA + DOBLE FERTILIZACION") &&
											 ContainsCondition(conditions, "FERTILIZACION-DOBLE", "MECANICA"))
									{
										prefijoOpcional = "FDM";
									}
									else
									{
										prefijoOpcional = "";
									}

									if (grupoOpc != null &&
										!string.IsNullOrEmpty(prefijoOpcional) &&
										prefijoOpcional.Length > 1)
									{
										if (!configCounter.ContainsKey(prefijoOpcional))
											configCounter[prefijoOpcional] = 1;

										childStructure.Add(new Dictionary<string, string>
								{
									{ "campo", "grupo_opc" },
									{ "valor", grupoOpc }
								});

										childStructure.Add(new Dictionary<string, string>
								{
									{ "campo", "opcional" },
									{ "valor", prefijoOpcional }
								});
									}

									estructuras[parentCodigo].Add(childStructure);
								}

								Log($"[SG1-JSON][MBOM] Padre {parentCodigo}: hijos en estructura={estructuras[parentCodigo].Count}");
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log($"[SG1-JSON] ERROR al consultar la base: {ex.Message}");
				Log($"[SG1-JSON] Stack trace: {ex.StackTrace}");
			}

			Log($"[SG1-JSON][MBOM] Estructuras generadas para {estructuras.Count} productos (solo fantasmas).");
			return estructuras;
		}


		public static void poblarBaseSG1(string Nombre_Padre, string Codigo_Padre, string Nombre_Hijo, string Codigo_Hijo, string CantidadHijo)
		{
			//string connectionString = "Server=10.0.0.82;Database=AgrometalBOP;User Id=sa;Password=Descar_2020;";
			string connectionString = "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";
			string query = "INSERT INTO SG1 VALUES (@Nombre_Padre, @Codigo_Padre, @Nombre_Hijo, @Codigo_Hijo, @CantidadHijo, NULL, NULL)";

			try
			{
				using (SqlConnection connection = new SqlConnection(connectionString))
				{
					connection.Open();

					using (SqlCommand command = new SqlCommand(query, connection))
					{
						command.Parameters.AddWithValue("@Nombre_Padre", Nombre_Padre);
						command.Parameters.AddWithValue("@Codigo_Padre", Codigo_Padre);
						command.Parameters.AddWithValue("@Nombre_Hijo", Nombre_Hijo);
						command.Parameters.AddWithValue("@Codigo_Hijo", Codigo_Hijo);
						command.Parameters.AddWithValue("@CantidadHijo", CantidadHijo);
						command.ExecuteNonQuery();
					}
				}
			}
			catch (Exception ex)
			{
				Log($"[SG1-DB] ERROR en poblarBaseSG1 para padre {Codigo_Padre}, hijo {Codigo_Hijo}: {ex.Message}");
			}
		}

		public static void ActualizarBase(int estado, string mensaje, string codigo)
		{
			//string connectionString = "Server=10.0.0.82;Database=AgrometalBOP;User Id=sa;Password=Descar_2020;";
			string connectionString = "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";
			string query = @"UPDATE SG1
SET estado = @estado, mensaje = @mensaje
WHERE Codigo_Padre = @codigo";

			try
			{
				using (SqlConnection connection = new SqlConnection(connectionString))
				{
					connection.Open();

					using (SqlCommand command = new SqlCommand(query, connection))
					{
						command.Parameters.AddWithValue("@estado", estado);
						command.Parameters.AddWithValue("@mensaje", mensaje ?? string.Empty);
						command.Parameters.AddWithValue("@codigo", codigo);
						int rows = command.ExecuteNonQuery();
						Log($"[SG1-DB] ActualizarBase: estado={estado} codigo={codigo} filas_afectadas={rows}");
					}
				}
			}
			catch (Exception ex)
			{
				Log($"[SG1-DB] ERROR en ActualizarBase para codigo {codigo}: {ex.Message}");
			}
		}

		private static bool ContainsCondition(Dictionary<string, string> conditions, string key, string value)
		{
			return conditions.ContainsKey(key) && conditions[key] == value;
		}

		public static List<string> jsonZG1_MBOM()
	{
		string connectionString = "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";
		Log("[ZG1-JSON] Iniciando generación de estructuras ZG1 desde SQL...");

		var listaJsons = new List<string>();

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
                WHEN Child.bl_formula != '' AND CHARINDEX('[''', Child.bl_formula) > 0 AND CHARINDEX(''']', Child.bl_formula, CHARINDEX('[''', Child.bl_formula)) > 0
                THEN SUBSTRING(Child.bl_formula,
                        CHARINDEX('[''', Child.bl_formula) + 2,
                        CHARINDEX(''']', Child.bl_formula, CHARINDEX('[''', Child.bl_formula)) - CHARINDEX('[''', Child.bl_formula) - 2)
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

		try
		{
			using (SqlConnection connection = new SqlConnection(connectionString))
			{
				connection.Open();
				Log("[ZG1-JSON] Conexión a SQL abierta.");

				using (SqlCommand command = new SqlCommand(query, connection))
				{
					command.CommandTimeout = 0; // sin límite — solo para pruebas locales

					using (SqlDataReader reader = command.ExecuteReader())
					{
						var dataByParent = new Dictionary<string, List<(string NombrePadre, string CodigoHijo, string NombreHijo, string Cantidad, string BlFormula, string IdContext)>>(StringComparer.OrdinalIgnoreCase);

						int filasLeidas = 0;

						while (reader.Read())
						{
							filasLeidas++;

							string codigoPadre = reader["Codigo_Padre"]?.ToString() ?? string.Empty;
							string nombrePadre = reader["Nombre_Padre"]?.ToString() ?? string.Empty;
							string codigoHijo  = reader["Codigo_Hijo"]?.ToString() ?? string.Empty;
							string nombreHijo  = reader["Nombre_Hijo"]?.ToString() ?? string.Empty;
							string cantidad    = (reader["CantidadHijo_Total"]?.ToString() ?? "1").Replace(',', '.');
							string blFormula   = reader["bl_formula"]?.ToString() ?? string.Empty;
							string idContext   = reader["idContext"]?.ToString() ?? string.Empty;

							if (string.IsNullOrWhiteSpace(codigoPadre) && string.IsNullOrWhiteSpace(codigoHijo))
							{
								Log("[ZG1-JSON] WARNING: fila sin Codigo_Padre ni Codigo_Hijo. Se omite.");
								continue;
							}

							if (string.IsNullOrWhiteSpace(codigoPadre))
								codigoPadre = codigoHijo;

							codigoPadre = codigoPadre.Trim();

							if (!dataByParent.ContainsKey(codigoPadre))
								dataByParent[codigoPadre] = new List<(string, string, string, string, string, string)>();

							dataByParent[codigoPadre].Add((nombrePadre, codigoHijo, nombreHijo, cantidad, blFormula, idContext));
						}

						Log($"[ZG1-JSON] SQL procesado. Filas leídas={filasLeidas} | Padres={dataByParent.Count}");

						foreach (var parentGroup in dataByParent)
						{
							string codigoPadre = parentGroup.Key;
							var hijos = parentGroup.Value;

							string idContextFinal = hijos
								.Select(h => h.IdContext)
								.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;

							string nombrePadreFinal = hijos.Count > 0 ? hijos[0].NombrePadre : string.Empty;

							var estructuraJson = new System.Text.StringBuilder();
							estructuraJson.Append("[");

							bool primerHijo = true;
							foreach (var hijo in hijos)
							{
								if (!primerHijo) estructuraJson.Append(",");
								primerHijo = false;

								estructuraJson.Append("[");
								estructuraJson.Append($"{{\"campo\":\"codigo\",\"valor\":\"{EscapeJson(hijo.CodigoHijo)}\"}}");
								string cantidadFmt = decimal.TryParse(hijo.Cantidad, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal cantDec)
								? cantDec.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)
								: (hijo.Cantidad ?? "");
							estructuraJson.Append($",{{\"campo\":\"cantidad\",\"valor\":\"{EscapeJson(cantidadFmt)}\"}}");

								if (!string.IsNullOrWhiteSpace(hijo.BlFormula))
									estructuraJson.Append($",{{\"campo\":\"formula\",\"valor\":\"{EscapeJson(hijo.BlFormula)}\"}}");

								estructuraJson.Append("]");

								try
								{
									using (SqlConnection connInsert = new SqlConnection(connectionString))
									{
										connInsert.Open();
										string insertSql = @"INSERT INTO ZG1 (Codigo_Padre, Nombre_Padre, Codigo_Hijo, Nombre_Hijo, Cantidad, bl_formula, idContext)
VALUES (@CodigoPadre, @NombrePadre, @CodigoHijo, @NombreHijo, @Cantidad, @BlFormula, @IdContext)";
										using (SqlCommand cmdInsert = new SqlCommand(insertSql, connInsert))
										{
											cmdInsert.Parameters.AddWithValue("@CodigoPadre", codigoPadre);
											cmdInsert.Parameters.AddWithValue("@NombrePadre", nombrePadreFinal);
											cmdInsert.Parameters.AddWithValue("@CodigoHijo", hijo.CodigoHijo ?? string.Empty);
											cmdInsert.Parameters.AddWithValue("@NombreHijo", hijo.NombreHijo ?? string.Empty);
											cmdInsert.Parameters.AddWithValue("@Cantidad", hijo.Cantidad ?? string.Empty);
											cmdInsert.Parameters.AddWithValue("@BlFormula", hijo.BlFormula ?? string.Empty);
											cmdInsert.Parameters.AddWithValue("@IdContext", idContextFinal ?? string.Empty);
											cmdInsert.ExecuteNonQuery();
										}
									}
								}
								catch (Exception exInsert)
								{
									Log($"[ZG1-DB] ERROR INSERT para padre={codigoPadre}, hijo={hijo.CodigoHijo}: {exInsert.Message}");
								}
							}

							estructuraJson.Append("]");

							var estructura = JsonConvert.DeserializeObject<JArray>(estructuraJson.ToString());
							var jsonPayload = new JObject
							{
								["producto"]  = codigoPadre,
								["idContext"] = idContextFinal,
								["qtdBase"]   = "1",
								["estructura"] = estructura
							};
							string jsonFinal = jsonPayload.ToString(Formatting.Indented);

							listaJsons.Add(jsonFinal);
							Log($"[ZG1-JSON] Padre {codigoPadre}: hijos={hijos.Count} | idContext={idContextFinal}");
							LogJson($"[ZG1][JSON] Producto {codigoPadre}:", jsonFinal);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log($"[ZG1-JSON] ERROR al consultar la base: {ex.Message}");
			Log($"[ZG1-JSON] Stack trace: {ex.StackTrace}");
		}

		Log($"[ZG1-JSON] Generación finalizada. JSONs generados: {listaJsons.Count}");
		return listaJsons;
	}

	// =========================
	// POST / PUT ZG1 (variantes MBOM)
	// =========================

	public static async Task postZG1(List<string> listaJsons)
	{
		var putFallBack = new List<string>();

		Log($"[ZG1-POST] Iniciando envío masivo. Productos: {listaJsons.Count}");

		foreach (var jsonData in listaJsons)
		{
			string producto = "(sin codigo)";
			try { producto = JObject.Parse(jsonData)["producto"]?.ToString() ?? "(sin codigo)"; } catch { }

			Log($"[ZG1][POST] Enviando estructura -> producto: {producto}");
			LogJson($"[ZG1][POST] JSON COMPLETO para producto {producto}:", jsonData);

			int intento = 0;
			bool acumularPut = false;

			while (true)
			{
				intento++;
				try
				{
					await ProtheusHealth.WaitUntilActiveAsync("ZG1-POST", RetryDelay);

					using var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
					using HttpResponseMessage response = await _clientSG1.PostAsync(ZG1_POST_URL, content);

					int statusCode = (int)response.StatusCode;
					string responseData = await response.Content.ReadAsStringAsync();
					LogErrorProtheusIfAny("ZG1", "POST", producto, responseData);

					if (IsRetryableStatus(response.StatusCode))
					{
						Log($"[ZG1][POST] Protheus respondió {statusCode}. Reintento en 5 minutos. Intento #{intento}");
						Log($"[ZG1][POST] Respuesta: {responseData}");
						await Task.Delay(RetryDelay);
						continue;
					}

					if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
					{
						Log($"[ZG1][POST] 409 Conflict para {producto}. Se acumula para PUT.");
						acumularPut = true;
					}
					else
					{
						Log($"[ZG1][POST] Código de estado: {statusCode}");
						Log($"[ZG1][POST] Respuesta: {responseData}");
					}

					await ProtheusHealth.PostCheckBestEffortAsync("ZG1-POST");
					break;
				}
				catch (Exception ex) when (IsRetryableException(ex))
				{
					Log($"[ZG1][POST] EXCEPCIÓN transitoria para {producto}: {ex.Message}. Reintento en 5 minutos. Intento #{intento}");
					await Task.Delay(RetryDelay);
					continue;
				}
			}

			if (acumularPut)
				putFallBack.Add(jsonData);
		}

		if (putFallBack.Count > 0)
		{
			Log($"[ZG1-POST] POST finalizado. 409 acumulados={putFallBack.Count}. Disparando PUT...");
			await putZG1(putFallBack);
		}
		else
		{
			Log("[ZG1-POST] POST finalizado. Sin 409.");
		}
	}

	public static async Task putZG1(List<string> listaJsons409)
	{
		Log($"[ZG1-PUT] Iniciando PUT masivo. Cantidad de productos: {listaJsons409.Count}");

		foreach (var jsonData in listaJsons409)
		{
			string producto = "(sin codigo)";
			try { producto = JObject.Parse(jsonData)["producto"]?.ToString() ?? "(sin codigo)"; } catch { }

			Log($"[ZG1][PUT] Modificando estructura -> producto: {producto}");
			LogJson($"[ZG1][PUT] JSON COMPLETO para producto {producto}:", jsonData);

			int intento = 0;

			while (true)
			{
				intento++;
				try
				{
					await ProtheusHealth.WaitUntilActiveAsync("ZG1-PUT", RetryDelay);

					using var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
					using HttpResponseMessage response = await _clientSG1.PutAsync(ZG1_PUT_URL, content);

					int statusCode = (int)response.StatusCode;
					string responseData = await response.Content.ReadAsStringAsync();
					LogErrorProtheusIfAny("ZG1", "PUT", producto, responseData);

					if (IsRetryableStatus(response.StatusCode))
					{
						Log($"[ZG1][PUT] Protheus respondió {statusCode}. Reintento en 5 minutos. Intento #{intento}");
						Log($"[ZG1][PUT] Respuesta: {responseData}");
						await Task.Delay(RetryDelay);
						continue;
					}

					Log($"[ZG1][PUT] Código de estado: {statusCode}");
					Log($"[ZG1][PUT] Respuesta: {responseData}");

					await ProtheusHealth.PostCheckBestEffortAsync("ZG1-PUT");
					break;
				}
				catch (Exception ex) when (IsRetryableException(ex))
				{
					Log($"[ZG1][PUT] EXCEPCIÓN transitoria para {producto}: {ex.Message}. Reintento en 5 minutos. Intento #{intento}");
					await Task.Delay(RetryDelay);
					continue;
				}
				catch (Exception ex)
				{
					Log($"[ZG1][PUT] EXCEPCIÓN NO transitoria para {producto}: {ex}");
					break;
				}
			}
		}

		Log("[ZG1-PUT] PUT masivo finalizado.");
	}

	private static string EscapeJson(string value)
	{
		if (string.IsNullOrEmpty(value)) return string.Empty;
		return value
			.Replace("\\", "\\\\")
			.Replace("\"", "\\\"")
			.Replace("\n", "\\n")
			.Replace("\r", "\\r")
			.Replace("\t", "\\t");
	}

	private static Dictionary<string, string> ExtractAllConditions(string varianteText)
		{
			var conditions = new Dictionary<string, string>();

			if (string.IsNullOrEmpty(varianteText))
				return conditions;

			string[] parts = varianteText.Split(new[] { " & " }, StringSplitOptions.RemoveEmptyEntries);
			foreach (var part in parts)
			{
				try
				{
					string pattern = part.Trim();

					int teamcenterPos = pattern.IndexOf("[Teamcenter]");
					if (teamcenterPos >= 0)
					{
						string keyValuePart = pattern.Substring(teamcenterPos + "[Teamcenter]".Length).Trim();

						if (keyValuePart.Contains("''"))
						{
							string[] keyValueSplit = keyValuePart.Split('=');
							if (keyValueSplit.Length == 2)
							{
								string keyWithQuotes = keyValueSplit[0].Trim();
								string valueWithQuotes = keyValueSplit[1].Trim();

								string key = keyWithQuotes.Replace("''", "").Trim();
								string value = valueWithQuotes.Replace("''", "").Trim();

								if (!string.IsNullOrEmpty(key))
									conditions[key] = value ?? string.Empty;
								else
									Console.WriteLine($"Warning: Null or empty key found in '{keyValuePart}'");
							}
						}
						else
						{
							string[] keyValue = keyValuePart.Split('=');
							if (keyValue.Length == 2)
							{
								string key = keyValue[0].Trim();
								string value = keyValue[1].Trim();

								if (!string.IsNullOrEmpty(key))
									conditions[key] = value ?? string.Empty;
								else
									Console.WriteLine($"Warning: Null or empty key found in '{keyValuePart}'");
							}
						}
					}
					else
					{
						Match match = Regex.Match(pattern, @"\[([^\]]+)\]([^=]+)=(.+)");
						if (match.Success)
						{
							string key = match.Groups[2].Value.Trim();
							string value = match.Groups[3].Value.Trim();

							if (!string.IsNullOrEmpty(key))
								conditions[key] = value ?? string.Empty;
							else
								Console.WriteLine($"Warning: Null or empty key found in '{pattern}'");
						}
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error processing condition part '{part}': {ex.Message}");
				}
			}
			return conditions;
		}
	}
}
