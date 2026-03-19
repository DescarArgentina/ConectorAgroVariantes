using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Web_Service.Program;
using static Web_Service.SqlToJsonConverter;
using static Web_Service.Tablas_SG2_SH3;

namespace Web_Service
{
    public class Tablas_SG2_SH3
    {
        public sealed class WsError
        {
            public int? errorCode { get; set; }
            public string errorMessage { get; set; }
        }

        // =========================
        // CONFIG PROTHEUS
        // =========================
        private const string UrlPost = "http://119.8.73.193:8096/rest/TCProceso/Incluir/";
        private const string UrlPut = "http://119.8.73.193:8096/rest/TCProceso/Modificar/";

        private const string Username = "USERREST";
        private const string Password = "restagr";
        // Protheus MV_TPHR = N (HH.MM) y restricción 99.59
        private const int MAX_MINUTES_TOTAL = 99 * 60 + 59;          // 5999
        private const int MAX_SECONDS_TOTAL = MAX_MINUTES_TOTAL * 60; // 359940
        private static double ParseDoubleInvariant(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0.0;
            raw = raw.Trim().Replace(',', '.');
            return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a == 0 ? 1 : a;
        }

        private sealed class TiempoLoteCalc
        {
            public string TiempoHHMM { get; init; }   // "HH.MM"
            public int LoteStd { get; init; }
            public bool EsAprox { get; init; }
            public double ErrorPct { get; init; }     // % error por pieza
            public int SegundosPorPieza { get; init; }
            public double MinutosDecimalesOriginal { get; init; }
        }

        private static string MinutesToHHMM(int totalMinutes)
        {
            int hh = totalMinutes / 60;
            int mm = totalMinutes % 60;

            if (totalMinutes <= 0) return "00.01";  // mínimo 1 minuto

            if (hh < 0) hh = 0;
            if (hh > 99) { hh = 99; mm = 59; }
            if (mm < 0) mm = 0;
            if (mm > 59) mm = 59;

            return $"{hh:D2}.{mm:D2}";
        }

        /// <summary>
        /// allocated_time_centesimal: OJO en este proyecto viene en MINUTOS decimales (aunque el alias diga "centesimal").
        /// Devuelve tiempo (HH.MM) y loteStd aplicando:
        /// - exactitud por MCD con 60 segundos
        /// - si excede 99.59, usa Lmax + redondeo al minuto más cercano
        /// </summary>
        /// <summary>
        /// allocated_time_centesimal_raw: OJO: en este proyecto viene en SEGUNDOS por pieza (aunque el alias diga "centesimal").
        /// Devuelve tiempo (HH.MM) y loteStd aplicando:
        /// - exactitud por MCD con 60 segundos
        /// - si excede 99.59, usa Lmax + redondeo al minuto más cercano
        /// </summary>
        private static TiempoLoteCalc CalcularTiempoYLoteDesdeSegundos(string allocated_time_centesimal_raw, string producto, string operacion)
        {
            // 1) Parse segundos por pieza (puede venir con decimales)
            double segOriginal = ParseDoubleInvariant(allocated_time_centesimal_raw);

            // Política mínima: si viene 0 o inválido -> 1 minuto por pieza
            if (segOriginal <= 0.0) segOriginal = 60.0;

            // 2) Para MCD necesitamos entero: redondeo al segundo
            int tSeconds = (int)Math.Round(segOriginal, MidpointRounding.AwayFromZero);
            if (tSeconds <= 0) tSeconds = 60;

            // 3) Intento exacto (forzar minutos enteros por lote)
            int g = Gcd(tSeconds, 60);
            int L0 = 60 / g;        // lote exacto mínimo
            int M0 = tSeconds / g;  // minutos exactos del lote

            if (M0 <= MAX_MINUTES_TOTAL)
            {
                return new TiempoLoteCalc
                {
                    SegundosPorPieza = tSeconds,
                    LoteStd = L0,
                    TiempoHHMM = MinutesToHHMM(M0),
                    EsAprox = false,
                    ErrorPct = 0.0,
                    MinutosDecimalesOriginal = segOriginal / 60.0 // opcional: para log/diagnóstico
                };
            }

            // 4) No entra en 99.59 -> modo restringido (aprox)
            int Lmax = MAX_SECONDS_TOTAL / tSeconds; // floor(359940 / tSeconds)

            if (Lmax < 1)
            {
                Utilidades.EscribirEnLog(
                    $"[SG2SH3][TIEMPO] WARNING: tiempo por pieza excede 99.59 con lote=1 | prod={producto} op={operacion} raw={allocated_time_centesimal_raw} seg={segOriginal}. Se capea a 99.59.");

                return new TiempoLoteCalc
                {
                    SegundosPorPieza = tSeconds,
                    LoteStd = 1,
                    TiempoHHMM = "99.59",
                    EsAprox = true,
                    ErrorPct = 0.0,
                    MinutosDecimalesOriginal = segOriginal / 60.0
                };
            }

            double minutosReal = (tSeconds * (double)Lmax) / 60.0;
            int minutosRedondeados = (int)Math.Round(minutosReal, MidpointRounding.AwayFromZero);

            if (minutosRedondeados < 1) minutosRedondeados = 1;
            if (minutosRedondeados > MAX_MINUTES_TOTAL) minutosRedondeados = MAX_MINUTES_TOTAL;

            // Error por pieza (compará contra segOriginal, no solo tSeconds)
            double segPorPiezaRepresentado = (minutosRedondeados * 60.0) / Lmax;
            double errorPct = (Math.Abs(segPorPiezaRepresentado - segOriginal) / segOriginal) * 100.0;

            return new TiempoLoteCalc
            {
                SegundosPorPieza = tSeconds,
                LoteStd = Lmax,
                TiempoHHMM = MinutesToHHMM(minutosRedondeados),
                EsAprox = true,
                ErrorPct = errorPct,
                MinutosDecimalesOriginal = segOriginal / 60.0
            };
        }


        // Healthcheck dedicado (nuevo)
        private const string UrlHealth = "http://119.8.73.193:8096/rest/TCEstado/Consultar/";
        private const string HealthBodyJson = "{\"consulta\":\"TeamCenter\"}";

        private const string TablaLogErroresProtheus = "SG2SH3";

        // Misma política que tus otras tablas
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        // Timeout “negocio” para POST/PUT
        private static readonly TimeSpan BusinessTimeout = TimeSpan.FromSeconds(100);

        // Cache para no sobrecargar el healthcheck
        private static DateTime _lastHealthOkUtc = DateTime.MinValue;
        private static readonly object _healthLock = new();

        // =========================
        // HTTP CLIENTS (estáticos)
        // =========================
        private static readonly HttpClient _clientSG2SH3 = CrearClienteBusiness();
        private static readonly HttpClient _clientHealth = CrearClienteHealth();

        private static HttpClient CrearClienteBusiness()
        {
            var client = new HttpClient
            {
                Timeout = BusinessTimeout
            };

            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Username}:{Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            return client;
        }

        private static HttpClient CrearClienteHealth()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5) // health corto
            };

            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Username}:{Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            return client;
        }

        // =========================
        // LOGGING (ErroresProtheus.log)
        // =========================
        private static WsError TryParseWsError(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                return JsonConvert.DeserializeObject<WsError>(body);
            }
            catch
            {
                return null;
            }
        }

        private static string Truncar(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }

        private static void LogErrorProtheus(string metodo, string producto, HttpStatusCode status, string body)
        {
            var err = TryParseWsError(body);
            int? errorCode = err?.errorCode;

            // ✅ Purga: POST + errorCode=3 (registro existente) NO se registra (ruido)
            if (string.Equals(metodo, "POST", StringComparison.OrdinalIgnoreCase) && errorCode == 3)
                return;

            string msgBase = !string.IsNullOrWhiteSpace(err?.errorMessage)
                ? err.errorMessage
                : Truncar(body ?? "", 600);

            string msg = $"HTTP {(int)status} {status} | {msgBase}";

            // ✅ Evitar "LEGACY": si es OP asociada (7/10), lo dejamos explícito dentro del mismo log normal
            if (errorCode == 7 || errorCode == 10)
                msg = $"Orden de producción asociada | {msg}";

            Utilidades.EscribirErrorProtheus(
                tabla: TablaLogErroresProtheus,
                metodo: metodo,
                producto: producto ?? "",
                errorCode: errorCode,
                errorMessage: msg
            );
        }

        private static void LogExcepcionProtheus(string metodo, string producto, Exception ex)
        {
            Utilidades.EscribirErrorProtheus(
                tabla: TablaLogErroresProtheus,
                metodo: metodo,
                producto: producto ?? "",
                errorCode: null,
                errorMessage: $"EXCEPTION | {ex.GetType().Name}: {ex.Message}"
            );
        }

        // =========================
        // ENVÍO SG2/SH3 (alineado a SB1/SG1)
        // =========================
        public static async Task EnviarSG2_SH3(string jsonData)
        {
            // Intentamos tomar algún identificador del JSON (si existe)
            string codigo = TryGetProducto(jsonData);

            Console.WriteLine($"[SG2SH3][POST] Enviando TCProceso -> producto: {codigo ?? "(sin producto)"}");
            Utilidades.EscribirEnLog($"[SG2SH3][POST] Enviando TCProceso -> producto: {codigo ?? "(sin producto)"}");

            int intento = 0;

            while (true)
            {
                intento++;

                try
                {
                    // 1) Healthcheck PRE: si da timeout => reintento infinito
                    await WaitUntilProtheusActiveAsync($"SG2SH3-POST {codigo ?? ""}");

                    using var contentPost = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    using HttpResponseMessage responsePost = await _clientSG2SH3.PostAsync(UrlPost, contentPost);
                    string bodyPost = await responsePost.Content.ReadAsStringAsync();

                    int statusCode = (int)responsePost.StatusCode;

                    Console.WriteLine($"[SG2SH3][POST] -> {statusCode} {responsePost.ReasonPhrase} | producto={codigo}");
                    Utilidades.EscribirEnLog($"[SG2SH3][POST] -> {statusCode} {responsePost.ReasonPhrase} | producto={codigo}");
                    Utilidades.EscribirEnLog($"[SG2SH3][POST] Body: {bodyPost}");

                    // OK
                    if (responsePost.IsSuccessStatusCode)
                    {
                        // 2) Healthcheck POST: best-effort (no bloquea)
                        _ = PostCheckBestEffortAsync($"SG2SH3-POSTCHECK {codigo ?? ""}");
                        return;
                    }

                    // Registro existente => PUT con retry infinito
                    if (EsRegistroExistente(responsePost.StatusCode, bodyPost))
                    {
                        // ✅ No registrar acá como “error”, porque se resuelve con PUT.
                        // (y si viniera errorCode=3, LogErrorProtheus ya lo filtraría)

                        Console.WriteLine($"[SG2SH3][POST] Registro existente detectado. Disparando PUT. producto={codigo}");
                        Utilidades.EscribirEnLog($"[SG2SH3][POST] Registro existente detectado. Disparando PUT. producto={codigo}");

                        await PutSG2_SH3(jsonData, codigo);
                        _ = PostCheckBestEffortAsync($"SG2SH3-PUTCHECK {codigo ?? ""}");
                        return;
                    }

                    // 5xx => retry infinito (NO ensuciar ErroresProtheus en cada reintento)
                    if (IsRetryableStatus(responsePost.StatusCode))
                    {
                        Console.WriteLine($"[SG2SH3][POST] Protheus respondió {statusCode}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                        Utilidades.EscribirEnLog($"[SG2SH3][POST] Protheus respondió {statusCode}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                        await Task.Delay(RetryDelay);
                        continue;
                    }

                    // 4xx (distinto a registro existente) => NO retry + ✅ log ErroresProtheus
                    LogErrorProtheus("POST", codigo, responsePost.StatusCode, bodyPost);

                    Console.WriteLine($"[SG2SH3][POST] ERROR NO-RETRY {statusCode}. Abortando envío. producto={codigo}");
                    Utilidades.EscribirEnLog($"[SG2SH3][POST] ERROR NO-RETRY {statusCode}. Abortando envío. producto={codigo}");
                    return;
                }
                catch (Exception ex) when (IsRetryableException(ex))
                {
                    Console.WriteLine($"[SG2SH3][POST] Error transitorio: {ex.Message}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                    Utilidades.EscribirEnLog($"[SG2SH3][POST] Error transitorio: {ex.Message}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                    await Task.Delay(RetryDelay);
                }
                catch (Exception ex)
                {
                    // ✅ log ErroresProtheus (una vez) y cortar
                    LogExcepcionProtheus("POST", codigo, ex);

                    Console.WriteLine($"[SG2SH3][POST] Error NO transitorio. Abortando. producto={codigo}. Detalle: {ex}");
                    Utilidades.EscribirEnLog($"[SG2SH3][POST] Error NO transitorio. Abortando. producto={codigo}. Detalle: {ex}");
                    return;
                }
            }
        }

        private static async Task PutSG2_SH3(string jsonData, string codigo)
        {
            Console.WriteLine($"[SG2SH3][PUT] Modificando TCProceso -> producto: {codigo ?? "(sin producto)"}");
            Utilidades.EscribirEnLog($"[SG2SH3][PUT] Modificando TCProceso -> producto: {codigo ?? "(sin producto)"}");

            int intento = 0;

            while (true)
            {
                intento++;

                try
                {
                    // Healthcheck PRE
                    await WaitUntilProtheusActiveAsync($"SG2SH3-PUT {codigo ?? ""}");

                    using var contentPut = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    using HttpResponseMessage responsePut = await _clientSG2SH3.PutAsync(UrlPut, contentPut);
                    string bodyPut = await responsePut.Content.ReadAsStringAsync();

                    int statusCode = (int)responsePut.StatusCode;

                    Console.WriteLine($"[SG2SH3][PUT] -> {statusCode} {responsePut.ReasonPhrase} | producto={codigo}");
                    Utilidades.EscribirEnLog($"[SG2SH3][PUT] -> {statusCode} {responsePut.ReasonPhrase} | producto={codigo}");
                    Utilidades.EscribirEnLog($"[SG2SH3][PUT] Body: {bodyPut}");

                    // 5xx => retry infinito (NO ensuciar ErroresProtheus en cada reintento)
                    if (IsRetryableStatus(responsePut.StatusCode))
                    {
                        Console.WriteLine($"[SG2SH3][PUT] Protheus respondió {statusCode}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                        Utilidades.EscribirEnLog($"[SG2SH3][PUT] Protheus respondió {statusCode}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                        await Task.Delay(RetryDelay);
                        continue;
                    }

                    // ✅ 4xx (o cualquier NO OK no-retry) => registrar en ErroresProtheus
                    if (!responsePut.IsSuccessStatusCode)
                    {
                        LogErrorProtheus("PUT", codigo, responsePut.StatusCode, bodyPut);

                        Console.WriteLine($"[SG2SH3][PUT] ERROR NO-RETRY {statusCode}. producto={codigo}");
                        Utilidades.EscribirEnLog($"[SG2SH3][PUT] ERROR NO-RETRY {statusCode}. producto={codigo}");
                    }

                    return; // éxito o error no-retry
                }
                catch (Exception ex) when (IsRetryableException(ex))
                {
                    Console.WriteLine($"[SG2SH3][PUT] Error transitorio: {ex.Message}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                    Utilidades.EscribirEnLog($"[SG2SH3][PUT] Error transitorio: {ex.Message}. Reintentando en 5 minutos. Intento #{intento} | producto={codigo}");
                    await Task.Delay(RetryDelay);
                }
                catch (Exception ex)
                {
                    // ✅ log ErroresProtheus (una vez) y cortar
                    LogExcepcionProtheus("PUT", codigo, ex);

                    Console.WriteLine($"[SG2SH3][PUT] Error NO transitorio. Abortando. producto={codigo}. Detalle: {ex}");
                    Utilidades.EscribirEnLog($"[SG2SH3][PUT] Error NO transitorio. Abortando. producto={codigo}. Detalle: {ex}");
                    return;
                }
            }
        }

        // =========================
        // HEALTHCHECK (timeout => retry infinito)
        // =========================
        private static async Task WaitUntilProtheusActiveAsync(string tag)
        {
            // cache 15s para no sobrecargar
            if ((DateTime.UtcNow - _lastHealthOkUtc) < TimeSpan.FromSeconds(15))
                return;

            int intento = 0;

            while (true)
            {
                intento++;

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, UrlHealth)
                    {
                        Content = new StringContent(HealthBodyJson, Encoding.UTF8, "application/json")
                    };

                    using HttpResponseMessage resp = await _clientHealth.SendAsync(req);
                    string body = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[{tag}][HEALTH] HTTP {(int)resp.StatusCode}. Reintentando en 5 minutos. Intento #{intento}. Body: {body}");
                        Utilidades.EscribirEnLog($"[{tag}][HEALTH] HTTP {(int)resp.StatusCode}. Reintentando en 5 minutos. Intento #{intento}. Body: {body}");
                        await Task.Delay(RetryDelay);
                        continue;
                    }

                    string estado = null;
                    try
                    {
                        var j = JObject.Parse(body);
                        estado = j["estado"]?.ToString();
                    }
                    catch
                    {
                        estado = null;
                    }

                    if (!string.Equals(estado, "activo", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[{tag}][HEALTH] Estado='{estado ?? "null"}'. Reintentando en 5 minutos. Intento #{intento}. Body: {body}");
                        Utilidades.EscribirEnLog($"[{tag}][HEALTH] Estado='{estado ?? "null"}'. Reintentando en 5 minutos. Intento #{intento}. Body: {body}");
                        await Task.Delay(RetryDelay);
                        continue;
                    }

                    lock (_healthLock)
                        _lastHealthOkUtc = DateTime.UtcNow;

                    return; // OK
                }
                catch (TaskCanceledException ex)
                {
                    // TIMEOUT del health (caso crítico)
                    Console.WriteLine($"[{tag}][HEALTH] TIMEOUT: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    Utilidades.EscribirEnLog($"[{tag}][HEALTH] TIMEOUT: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    await Task.Delay(RetryDelay);
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"[{tag}][HEALTH] ERROR HTTP/red: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    Utilidades.EscribirEnLog($"[{tag}][HEALTH] ERROR HTTP/red: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    await Task.Delay(RetryDelay);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{tag}][HEALTH] ERROR inesperado: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    Utilidades.EscribirEnLog($"[{tag}][HEALTH] ERROR inesperado: {ex.Message}. Reintentando en 5 minutos. Intento #{intento}");
                    await Task.Delay(RetryDelay);
                }
            }
        }

        // Best-effort: no bloquea el pipeline
        private static async Task PostCheckBestEffortAsync(string tag)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, UrlHealth)
                {
                    Content = new StringContent(HealthBodyJson, Encoding.UTF8, "application/json")
                };

                using HttpResponseMessage resp = await _clientHealth.SendAsync(req);
                _ = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{tag}][HEALTH-POSTCHECK] WARN: {ex.Message}");
                Utilidades.EscribirEnLog($"[{tag}][HEALTH-POSTCHECK] WARN: {ex.Message}");
            }
        }

        // =========================
        // HELPERS envío
        // =========================

        private static bool EsErrorOrdenesProduccion(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;

            try
            {
                var err = JsonConvert.DeserializeObject<WsError>(body);
                return err?.errorCode == 7 || err?.errorCode == 10;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRetryableStatus(HttpStatusCode status)
        {
            int code = (int)status;
            return code >= 500 && code <= 599;
        }

        private static bool IsRetryableException(Exception ex)
        {
            return ex is TaskCanceledException || ex is HttpRequestException;
        }

        private static bool EsRegistroExistente(HttpStatusCode status, string body)
        {
            if (status == HttpStatusCode.Conflict) return true;
            if (string.IsNullOrWhiteSpace(body)) return false;

            return body.Contains("ya existe", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("Registro duplicado", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryGetProducto(string jsonData)
        {
            try
            {
                var obj = JObject.Parse(jsonData);
                // tu JSON SG2/SH3 tiene "producto"
                return obj["producto"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        // =========================
        // TU CÓDIGO EXISTENTE (sin cambios)
        // =========================

        static string NormalizarCodigo(string codigo)
        {
            if (string.IsNullOrWhiteSpace(codigo)) return codigo;
            codigo = codigo.Trim();

            // Casos especiales (no es “poner 0”, pero suelen ir juntos)
            if (codigo == "000485") return "481708";
            if (codigo == "000402") return "482918";

            // Códigos que llevan un 0 insertado después del 3er dígito
            if (codigo == "45356" || codigo == "45357" || codigo == "45545" || codigo == "45459" || codigo == "49877" || codigo == "16486" || codigo == "52142" ||
                codigo == "45730" || codigo == "45796" || codigo == "45547" || codigo == "45553" || codigo == "34884" || codigo == "48079" || codigo == "52414")
            {
                return codigo.Insert(3, "0");
            }

            return codigo;
        }

        public static string NormalizarCentroTrabajo(string centroTrabajo, bool devolverVacioSiNull)
        {
            if (string.IsNullOrWhiteSpace(centroTrabajo))
                return devolverVacioSiNull ? "" : "000";

            // Dejar solo dígitos (por si viniera con espacios o caracteres)
            centroTrabajo = new string(centroTrabajo.Where(char.IsDigit).ToArray());

            // Si luego de filtrar queda vacío, respetar la misma política
            if (string.IsNullOrWhiteSpace(centroTrabajo))
                return devolverVacioSiNull ? "" : "000";

            if (centroTrabajo.Length <= 3)
                return centroTrabajo.PadLeft(3, '0');   // completa a 3 dígitos

            return centroTrabajo.Substring(centroTrabajo.Length - 3); // últimos 3
        }

        //Consulta con Workarea y recurso hijo
        private const string consultaD_workarea_recurso = @"SELECT
    p.catalogueId   AS Padre,
    p.name          AS descripcion,
    wa.catalogueId  AS centroTrabajo,
    prod.productId  AS recurso, 
    op.catalogueId  AS Operacion,
	ta.allocated_time_centesimal AS allocated_time_centesimal,
    ROW_NUMBER() OVER (
        PARTITION BY p.catalogueId, op.catalogueId, wa.catalogueId
        ORDER BY prod.productId
    ) AS nro_recurso
FROM ProcessRevision        AS pr
INNER JOIN Process                AS p   ON p.id_Table  = pr.masterRef
INNER JOIN ProcessOccurrence      AS po  ON po.instancedRef = pr.id_Table

-- WorkArea
INNER JOIN Occurrence             AS occ1 
        ON occ1.parentRef = po.id_Table
       AND occ1.subType   IN ('MEWorkArea','MEWorkarea')
INNER JOIN WorkAreaRevision       AS war ON war.id_Table    = occ1.instancedRef
INNER JOIN WorkArea               AS wa  ON wa.id_Table     = war.masterRef

-- Recursos hijos de la WorkArea (vía WorkAreaOccurrence)
INNER JOIN WorkAreaOccurrence wao ON wao.instancedRef = war.id_Table
INNER JOIN Occurrence occ2        ON occ2.parentRef   = wao.id_Table
INNER JOIN ProductRevision prod_rev ON prod_rev.id_Table = occ2.instancedRef
INNER JOIN Product prod             ON prod.id_Table     = prod_rev.masterRef

-- Operaciones
LEFT JOIN ProcessOccurrence po_op 
        ON po_op.parentRef = po.id_Table
LEFT JOIN OperationRevision op_rev 
        ON op_rev.id_Table = po_op.instancedRef
LEFT JOIN Operation op 
        ON op.id_Table = op_rev.masterRef

OUTER APPLY (
    SELECT TOP 1
        uvud_time.value AS allocated_time_centesimal
    FROM STRING_SPLIT(po_op.associatedAttachmentRefs, ' ') s
    JOIN AssociatedAttachment aa
       ON aa.id_Table = RIGHT(s.value, LEN(s.value) - 3)
    JOIN Form f_time
       ON f_time.id_Table = RIGHT(aa.attachmentRef, LEN(aa.attachmentRef) - 3)
      AND f_time.subType = 'MEOpTimeAnalysis'
    JOIN UserValue_UserData uvud_time
       ON uvud_time.id_Father = f_time.id_Table + 1
      AND uvud_time.title = 'allocated_time'
) AS ta


ORDER BY RIGHT(p.catalogueId, LEN(p.catalogueId) - 3) DESC;";

        //Consulta para los xml que vienen sin WorkAreaOccurrence
        private const string ConsultaA_ConWorkArea_SinWAO = @"
SELECT
p.catalogueId AS Padre,
p.name AS descripcion,
wa.catalogueId AS centroTrabajo,
prod.productId AS recurso,
op.catalogueId AS Operacion,
ta.allocated_time_centesimal AS allocated_time_centesimal,
ROW_NUMBER() OVER (PARTITION BY p.catalogueId, op.catalogueId, wa.catalogueId ORDER BY prod.productId) AS nro_recurso
FROM ProcessRevision AS pr
INNER JOIN Process AS p ON p.id_Table = pr.masterRef
INNER JOIN ProcessOccurrence AS po ON po.instancedRef = pr.id_Table
INNER JOIN Occurrence AS occ1 ON occ1.parentRef = po.id_Table AND occ1.subType IN ('MEWorkArea','MEWorkarea')
INNER JOIN WorkAreaRevision AS war ON war.id_Table = occ1.instancedRef
INNER JOIN WorkArea AS wa ON wa.id_Table = war.masterRef
INNER JOIN Occurrence occ2 ON occ2.parentRef = occ1.id_Table
INNER JOIN ProductRevision prod_rev ON prod_rev.id_Table = occ2.instancedRef
INNER JOIN Product prod ON prod.id_Table = prod_rev.masterRef
LEFT JOIN ProcessOccurrence po_op ON po_op.parentRef = po.id_Table
LEFT JOIN OperationRevision op_rev ON op_rev.id_Table = po_op.instancedRef
LEFT JOIN Operation op ON op.id_Table = op_rev.masterRef
OUTER APPLY (
    SELECT TOP 1
        uvf.value AS allocated_time_centesimal
    FROM STRING_SPLIT(COALESCE(po_op.associatedAttachmentRefs, ''), ' ') s
    CROSS APPLY (
        SELECT TRY_CONVERT(BIGINT, REPLACE(REPLACE(LTRIM(RTRIM(s.value)),'#',''),'id','')) AS AttId
    ) s2
    INNER JOIN AssociatedAttachment aa
        ON aa.id_Table = s2.AttId
    CROSS APPLY (
        SELECT TRY_CONVERT(BIGINT, REPLACE(REPLACE(CONCAT(aa.attachmentRef,''),'#',''),'id','')) AS FormId
    ) a2
    INNER JOIN Form f_time
        ON f_time.id_Table = a2.FormId
       AND f_time.subType = 'MEOpTimeAnalysis'
    INNER JOIN UserValue_Form uvf
        ON uvf.title = 'allocated_time'
       AND uvf.idXml = f_time.idXml
       AND (
            TRY_CONVERT(BIGINT, CONCAT(uvf.id_Father,'')) = f_time.id_Table
            OR TRY_CONVERT(BIGINT, CONCAT(uvf.id_Father,'')) = f_time.id_Table + 1
            OR TRY_CONVERT(BIGINT, CONCAT(uvf.id_Table,'')) = f_time.id_Table
       )
) AS ta
GROUP BY p.catalogueId, p.name, wa.catalogueId, prod.productId, op.catalogueId, ta.allocated_time_centesimal
ORDER BY 
TRY_CONVERT(INT, SUBSTRING(p.catalogueId, 4, LEN(p.catalogueId) - 3)) DESC
";

        //Consulta para los xml que vienen sin WorkArea
        private const string consultaB_sin_workarea = @"
SELECT
    p.catalogueId   AS Padre,
    p.name          AS descripcion,
    NULL            AS centroTrabajo,          -- no hay WA
    prod.productId  AS recurso,                -- recurso real

    uud.value AS allocated_time_centesimal,
    op.catalogueId          AS Operacion,
    ROW_NUMBER() OVER (
    PARTITION BY p.catalogueId, op.catalogueId
    ORDER BY prod.productId
) AS nro_recurso

FROM ProcessRevision        AS pr
JOIN Process                AS p
      ON p.id_Table = pr.masterRef

-- Proceso / subproceso
JOIN ProcessOccurrence      AS po
      ON po.instancedRef = pr.id_Table

-- Recursos: hijos directos del PR (sin WorkArea)
JOIN Occurrence             AS occ2
      ON occ2.parentRef = po.id_Table

JOIN ProductRevision        AS prod_rev
      ON prod_rev.id_Table = occ2.instancedRef

JOIN Product                AS prod
      ON prod.id_Table = prod_rev.masterRef

-- Form maestro de proceso + TimeAnalysis
LEFT JOIN Form AS f_proc
       ON f_proc.name = CONCAT(p.catalogueId, '/', pr.revision)

LEFT JOIN Form AS f_time
       ON f_time.id_Table = f_proc.id_Table + 3

LEFT JOIN UserValue_UserData AS uud
       ON uud.id_Father = f_time.id_Table + 1
      AND uud.title = 'allocated_time'

-- Vista e instancia de proceso (para nro_busqueda)
INNER JOIN ProcessRevisionView pr_view 
        ON pr_view.revisionRef = pr.id_Table

INNER JOIN ProcessInstance pr_ins 
        ON pr_ins.partRef = pr_view.id_Table

-- Operaciones
INNER JOIN ProcessOccurrence po_op 
        ON po_op.parentRef = po.id_Table

INNER JOIN OperationRevision op_rev 
        ON op_rev.id_Table = po_op.instancedRef

INNER JOIN Operation op 
        ON op.id_Table = op_rev.masterRef

-- Cálculo de tiempo

ORDER BY
	TRY_CONVERT(INT, SUBSTRING(p.catalogueId, 4, LEN(p.catalogueId) - 3)) DESC
";

        //Consulta para los xml que vienen con WorkArea que apunta directo a ProductRevision
        private const string ConsultaC_WorkAreaEspecial = @"
-- MEWorkArea que apunta directo a ProductRevision

SELECT
    p.catalogueId   AS Padre,
    p.name          AS descripcion,
    NULL            AS centroTrabajo,
    wa_inst.productId  AS recurso,     -- la máquina/estación como recurso
    op.catalogueId  AS Operacion,
	ta.allocated_time_centesimal AS allocated_time_centesimal,
	ROW_NUMBER() OVER (
    PARTITION BY p.catalogueId, op.catalogueId
    ORDER BY wa_inst.productId
) AS nro_recurso


FROM ProcessRevision pr
JOIN Process p 
       ON p.id_Table = pr.masterRef
JOIN ProcessOccurrence po
       ON po.instancedRef = pr.id_Table

-- MEWorkArea -> ProductRevision
JOIN Occurrence occ_wa
       ON occ_wa.parentRef = po.id_Table
      AND occ_wa.subType   IN ('MEWorkArea','MEWorkarea')
JOIN ProductRevision prod_rev
       ON prod_rev.id_Table = occ_wa.instancedRef
JOIN Product wa_inst
       ON wa_inst.id_Table = prod_rev.masterRef

-- Operaciones
LEFT JOIN ProcessOccurrence po_op 
       ON po_op.parentRef = po.id_Table
LEFT JOIN OperationRevision op_rev 
       ON op_rev.id_Table = po_op.instancedRef
LEFT JOIN Operation op 
       ON op.id_Table = op_rev.masterRef

-- Tiempo por operación
OUTER APPLY (
    SELECT TOP 1
        uvud_time.value AS allocated_time_centesimal
    FROM STRING_SPLIT(po_op.associatedAttachmentRefs, ' ') s
    JOIN AssociatedAttachment aa
       ON aa.id_Table = RIGHT(s.value, LEN(s.value) - 3)
    JOIN Form f_time
       ON f_time.id_Table = RIGHT(aa.attachmentRef, LEN(aa.attachmentRef) - 3)
      AND f_time.subType = 'MEOpTimeAnalysis'
    JOIN UserValue_UserData uvud_time
       ON uvud_time.id_Father = f_time.id_Table + 1
      AND uvud_time.title = 'allocated_time'
) AS ta


ORDER BY RIGHT(p.catalogueId, LEN(p.catalogueId) - 3) DESC;
";

        public static string ObtenerConsultaRecursos(SqlConnection connection)
        {
            bool hayWorkArea = false;
            bool esWorkAreaNormal = false;
            bool esWorkAreaEspecial = false;
            bool existeTablaWAO = false;
            bool usaWAO = false;

            // ¿Existe físicamente la tabla WorkArea?
            using (var cmd = new SqlCommand("SELECT OBJECT_ID('WorkArea', 'U');", connection))
            {
                var res = cmd.ExecuteScalar();
                hayWorkArea = (res != null && res != DBNull.Value);
            }

            if (hayWorkArea)
            {
                // Caso A/D: MEWorkArea -> WorkAreaRevision
                using (var cmd = new SqlCommand(@"
            SELECT TOP 1 1
            FROM Occurrence o
            JOIN WorkAreaRevision war ON war.id_Table = o.instancedRef
            WHERE o.subType IN ('MEWorkArea','MEWorkarea');", connection))
                {
                    var res = cmd.ExecuteScalar();
                    esWorkAreaNormal = (res != null && res != DBNull.Value);
                }

                // Caso C: MEWorkArea -> ProductRevision directo
                using (var cmd = new SqlCommand(@"
            SELECT TOP 1 1
            FROM Occurrence o
            JOIN ProductRevision pr ON pr.id_Table = o.instancedRef
            WHERE o.subType IN ('MEWorkArea','MEWorkarea');", connection))
                {
                    var res = cmd.ExecuteScalar();
                    esWorkAreaEspecial = (res != null && res != DBNull.Value);
                }

                // ✅ Primero verificamos si existe la tabla WorkAreaOccurrence
                using (var cmd = new SqlCommand("SELECT OBJECT_ID('WorkAreaOccurrence', 'U');", connection))
                {
                    var res = cmd.ExecuteScalar();
                    existeTablaWAO = (res != null && res != DBNull.Value);
                }

                // ✅ Solo si existe, preguntamos si hay datos reales (este XML/BD usa WAO)
                if (existeTablaWAO)
                {
                    using (var cmd = new SqlCommand(@"
                SELECT TOP 1 1
                FROM WorkAreaOccurrence wao
                JOIN Occurrence occ2 ON occ2.parentRef = wao.id_Table
                JOIN ProductRevision pr2 ON pr2.id_Table = occ2.instancedRef;", connection))
                    {
                        var res = cmd.ExecuteScalar();
                        usaWAO = (res != null && res != DBNull.Value);
                    }
                }
                else
                {
                    usaWAO = false;
                }
            }

            Utilidades.EscribirEnLog(
                "ObtenerQueryRecursos -> hayWorkArea=" + hayWorkArea +
                ", normal=" + esWorkAreaNormal +
                ", especial=" + esWorkAreaEspecial +
                ", existeWAO=" + existeTablaWAO +
                ", usaWAO=" + usaWAO);

            Utilidades.EscribirEnLog("Query elegida: " +
                (!hayWorkArea ? "B" :
                 esWorkAreaNormal && usaWAO ? "D_WAO" :
                 esWorkAreaNormal && !usaWAO ? "A_SinWAO" :
                 esWorkAreaEspecial ? "C" : "NINGUNA"));

            // 🔀 Selección de consulta según combinación detectada
            if (!hayWorkArea)
                return consultaB_sin_workarea;              // sin WorkArea

            if (esWorkAreaNormal)
            {
                if (usaWAO)
                    return consultaD_workarea_recurso;      // usa WorkAreaOccurrence
                else
                    return ConsultaA_ConWorkArea_SinWAO;    // sin WorkAreaOccurrence
            }

            if (esWorkAreaEspecial)
                return ConsultaC_WorkAreaEspecial;          // MEWorkArea -> ProductRevision

            // Fallback: WorkArea y WAO existen pero sin MEWorkArea en Occurrence (ej: BOP 150%)
            if (usaWAO)
            {
                Utilidades.EscribirEnLog("ObtenerQueryRecursos -> Fallback: WAO con datos pero sin MEWorkArea. Usando consultaD.");
                return consultaD_workarea_recurso;
            }

            throw new Exception("No se pudo determinar el tipo de WorkArea para este XML.");
        }

        public static List<string> jsonSG2_SH3()
        {
            string connectionString = "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";
            //string connectionString = "Server=10.0.0.82;Database=AgrometalBOP;User Id=sa;Password=Descar_2020;";

            List<string> jsonProductos = new List<string>();
            Utilidades.EscribirEnLog("jsonSG2_SH3 -> entrando al método");

            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    Utilidades.EscribirEnLog("jsonSG2_SH3 -> conexión abierta");

                    string query = ObtenerConsultaRecursos(connection);
                    SqlCommand command = new SqlCommand(query, connection);
                    SqlDataReader reader = command.ExecuteReader();

                    int filas = 0;
                    Dictionary<string, dynamic> productosDict = new Dictionary<string, dynamic>();
                    var procPorProductoOperacion = new Dictionary<string, Procedimiento>();
                    var ultimoPasoPorProducto = new Dictionary<string, int>();

                    while (reader.Read())
                    {
                        filas++;

                        // Campos base
                        string padrePr = reader["Padre"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(padrePr))
                            continue;

                        string operacion = reader["Operacion"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(operacion))
                            continue; // evita keyProc inválida

                        string producto = padrePr;

                        if (padrePr.Length > 2 && padrePr.StartsWith("P-", StringComparison.OrdinalIgnoreCase))
                            producto = padrePr.Substring(2);

                        if (Sg1Exclusions.TryGetProcessForExcludedPr(padrePr, out var procesoP) && !string.IsNullOrWhiteSpace(procesoP))
                        {
                            producto = (procesoP.Length > 2 && procesoP.StartsWith("P-", StringComparison.OrdinalIgnoreCase))
                                ? procesoP.Substring(2)
                                : procesoP;

                            Utilidades.EscribirEnLog($"SG2_SH3 -> PR excluido detectado: {padrePr} => producto={producto}");
                        }

                        string recurso = reader["recurso"]?.ToString();
                        recurso = NormalizarCodigo(recurso);

                        string nombreOperacion = reader["descripcion"]?.ToString();

                        string centroTrabajo = reader["centroTrabajo"]?.ToString();
                        centroTrabajo = NormalizarCentroTrabajo(centroTrabajo, devolverVacioSiNull: true);

                        int nroRecurso = 1;
                        try
                        {
                            nroRecurso = Convert.ToInt32(reader["nro_recurso"]);
                        }
                        catch
                        {
                            nroRecurso = 1;
                        }

                        // OJO: aunque el alias diga "centesimal", acá viene en SEGUNDOS por pieza (XML).
                        string rawSeconds = reader["allocated_time_centesimal"]?.ToString();
                        var calc = CalcularTiempoYLoteDesdeSegundos(rawSeconds, producto, operacion);

                        string tiempo = calc.TiempoHHMM;                  // "HH.MM"
                        string lote = calc.LoteStd.ToString(CultureInfo.InvariantCulture);

                        if (filas <= 3)
                        {
                            Utilidades.EscribirEnLog(
                                $"jsonSG2_SH3 -> fila {filas}: Padre={padrePr}, recurso={recurso}, op={operacion}, rawSeconds={rawSeconds}, tiempo={tiempo}, lote={lote}, aprox={calc.EsAprox}");
                        }

                        if (calc.EsAprox)
                        {
                            Utilidades.EscribirEnLog(
                                $"[SG2SH3][TIEMPO] Aprox por límite 99.59 | prod={producto} op={operacion} rawSeconds={rawSeconds} minDec={calc.MinutosDecimalesOriginal} seg={calc.SegundosPorPieza} lote={lote} tiempo={tiempo} err%={calc.ErrorPct:0.0000}");
                        }

                        string codigo = "01";
                        string keyProc = producto + "_" + operacion;

                        if (!procPorProductoOperacion.ContainsKey(keyProc))
                        {
                            int operacionActual = 10;
                            if (ultimoPasoPorProducto.TryGetValue(producto, out var ult))
                                operacionActual = ult + 10;

                            ultimoPasoPorProducto[producto] = operacionActual;
                            string operacionPaso = operacionActual.ToString("D2");

                            var nuevoProc = new Procedimiento
                            {
                                detalle = new List<CampoValor>
                        {
                            new CampoValor { campo = "operacion",     valor = operacionPaso },
                            new CampoValor { campo = "recurso",       valor = "" },
                            new CampoValor { campo = "tiempo",        valor = tiempo },
                            new CampoValor { campo = "centroTrabajo", valor = centroTrabajo },
                            new CampoValor { campo = "descripcion",   valor = nombreOperacion },
                            new CampoValor { campo = "loteStd",       valor = lote }
                        },
                                alternativos = new List<List<CampoValor>>()
                            };

                            procPorProductoOperacion[keyProc] = nuevoProc;

                            if (productosDict.ContainsKey(producto))
                            {
                                productosDict[producto].procedimiento.Add(nuevoProc);
                            }
                            else
                            {
                                productosDict[producto] = new
                                {
                                    codigo = codigo,
                                    producto = producto,
                                    procedimiento = new List<Procedimiento> { nuevoProc }
                                };
                            }
                        }

                        Procedimiento proc = procPorProductoOperacion[keyProc];

                        if (nroRecurso == 1)
                        {
                            proc.detalle.First(x => x.campo == "recurso").valor = recurso;

                            // por si la primera fila vino sin tiempo, nos aseguramos de setearlo con el cálculo actual
                            proc.detalle.First(x => x.campo == "tiempo").valor = tiempo;
                            proc.detalle.First(x => x.campo == "loteStd").valor = lote;
                        }
                        else
                        {
                            var alternativo = new List<CampoValor>
                    {
                        new CampoValor { campo = "recursoAlt", valor = recurso },
                        new CampoValor { campo = "tipoAlt",    valor = "A" }
                    };

                            proc.alternativos.Add(alternativo);
                        }
                    }

                    Utilidades.EscribirEnLog($"jsonSG2_SH3 -> filas leídas de la query: {filas}");
                    Utilidades.EscribirEnLog($"jsonSG2_SH3 -> productosDict.Count = {productosDict.Count}");

                    foreach (var item in productosDict.Values)
                    {
                        string json = JsonConvert.SerializeObject(item, Formatting.Indented);
                        Console.WriteLine(json);
                        Utilidades.EscribirJSONEnLog("jsonSG2_SH3 -> JSON generado:\n" + json);
                        jsonProductos.Add(json);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Utilidades.EscribirEnLog("Error en jsonSG2_SH3: " + ex.Message);
            }

            return jsonProductos;
        }


        public static List<string> jsonSB1_BOP()
        {
            string connectionString = "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";
            //string connectionString = "Server=10.0.0.82;Database=AgrometalBOP;User Id=sa;Password=Descar_2020;";
            const string schemaPatch = @"
                                        SET NOCOUNT ON;
                                        SET XACT_ABORT ON;
                                        BEGIN TRAN;

                                        DECLARE @schema sysname, @sql nvarchar(max);

                                        -- ===== Occurrence =====
                                        SELECT TOP(1) @schema = s.name
                                        FROM sys.tables t
                                        JOIN sys.schemas s ON s.schema_id = t.schema_id
                                        WHERE t.name = N'Occurrence';

                                        IF @schema IS NOT NULL
                                        BEGIN
                                            IF NOT EXISTS (
                                                SELECT 1
                                                FROM sys.columns c
                                                WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema)+N'.'+QUOTENAME(N'Occurrence'))
                                                  AND LOWER(c.name) = N'subtype'
                                            )
                                            BEGIN
                                                SET @sql = N'ALTER TABLE ' + QUOTENAME(@schema) + N'.' + QUOTENAME(N'Occurrence')
                                                         + N' ADD ' + QUOTENAME(N'subType') + N' NVARCHAR(100) NULL;';
                                                EXEC sp_executesql @sql;
                                            END
                                        END

                                        -- ===== ProcessRevision =====
                                        SET @schema = NULL;
                                        SELECT TOP(1) @schema = s.name
                                        FROM sys.tables t
                                        JOIN sys.schemas s ON s.schema_id = t.schema_id
                                        WHERE t.name = N'ProcessRevision';

                                        IF @schema IS NOT NULL
                                        BEGIN
                                            IF NOT EXISTS (
                                                SELECT 1
                                                FROM sys.columns c
                                                WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema)+N'.'+QUOTENAME(N'ProcessRevision'))
                                                  AND LOWER(c.name) = N'subtype'
                                            )
                                            BEGIN
                                                SET @sql = N'ALTER TABLE ' + QUOTENAME(@schema) + N'.' + QUOTENAME(N'ProcessRevision')
                                                         + N' ADD ' + QUOTENAME(N'subType') + N' NVARCHAR(100) NULL;';
                                                EXEC sp_executesql @sql;
                                            END
                                        END

                                        -- ===== ProductRevision =====
                                        SET @schema = NULL;
                                        SELECT TOP(1) @schema = s.name
                                        FROM sys.tables t
                                        JOIN sys.schemas s ON s.schema_id = t.schema_id
                                        WHERE t.name = N'ProductRevision';

                                        IF @schema IS NOT NULL
                                        BEGIN
                                            IF NOT EXISTS (
                                                SELECT 1
                                                FROM sys.columns c
                                                WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema)+N'.'+QUOTENAME(N'ProductRevision'))
                                                  AND LOWER(c.name) = N'subtype'
                                            )
                                            BEGIN
                                                SET @sql = N'ALTER TABLE ' + QUOTENAME(@schema) + N'.' + QUOTENAME(N'ProductRevision')
                                                         + N' ADD ' + QUOTENAME(N'subType') + N' NVARCHAR(100) NULL;';
                                                EXEC sp_executesql @sql;
                                            END
                                        END

                                        COMMIT;
                                        ";

            string query = @"WITH FirstProcess_codigo AS(
                            SELECT RIGHT(catalogueId,6) first_process_name
                          FROM Process p
                          INNER JOIN ProcessRevision pr ON pr.masterRef = p.id_Table
                          INNER JOIN ProcessOccurrence po ON pr.id_Table = po.instancedRef
                          WHERE po.parentRef IS NULL
                        )

            SELECT p.catalogueId AS codigo, uud2.value,
            pr.revision AS revEstruct,
            CONCAT('Proceso: ', p.catalogueId, ' - ', fpn.first_process_name) AS descripcion,
            1 AS Cantidad,
            'PA' AS tipo,
            '01' AS deposito,
            'UN' AS 'Unidad de Medida',
            fpn.first_process_name AS Process_codigo,
            pr.subType
            FROM Operation O
            CROSS JOIN FirstProcess_codigo fpn
            INNER JOIN OperationRevision OpR ON OpR.masterRef = o.id_Table
            INNER JOIN ProcessOccurrence po ON po.instancedRef = OpR.id_Table
            CROSS APPLY (
              SELECT CASE
                       WHEN RIGHT(o.catalogueId, 3) = '-OP'
                            THEN LEFT(o.catalogueId, LEN(o.catalogueId) - 3)
                       WHEN CHARINDEX('-OP', o.catalogueId) > 0
                            THEN REPLACE(o.catalogueId, '-OP', '')
                       ELSE o.catalogueId
                     END AS op_code_base
            ) AS x
			LEFT JOIN Form f ON f.name = CONCAT(x.op_code_base, '/', OpR.revision)
            INNER JOIN Form f2 ON f2.id_Table = f.id_Table + 3
            INNER JOIN UserValue_UserData uud ON uud.id_Father = f2.id_Table + 1 AND uud.title = 'allocated_time'
            INNER JOIN ProcessOccurrence po2 ON po2.id_Table = po.parentRef
			LEFT JOIN UserValue_UserData uud2 ON uud2.id_Father = po2.id_Table + 2 AND uud2.title = 'SequenceNumber'
            INNER JOIN ProcessRevision pr ON pr.id_Table = po2.instancedRef
            INNER JOIN Process p ON p.id_Table = pr.masterRef
            LEFT JOIN(SELECT p.catalogueId, sq1.productId FROM Process p
            INNER JOIN ProcessRevision pr ON pr.masterRef = p.id_Table
            INNER JOIN ProcessOccurrence po ON po.instancedRef = pr.id_Table
            INNER JOIN Occurrence o ON po.id_Table = o.parentRef
            INNER JOIN (SELECT p.productId, o.parentRef FROM Product p
            INNER JOIN ProductRevision pr ON pr.masterRef = p.id_Table
            INNER JOIN Occurrence o ON o.instancedRef = pr.id_Table
            WHERE o.subType IS NULL
			

            UNION ALL

            SELECT wa.catalogueId, wao.parentRef FROM WorkArea wa
            INNER JOIN WorkAreaRevision war ON war.masterRef = wa.id_Table
            INNER JOIN Occurrence wao ON wao.instancedRef = war.id_Table) sq1 ON sq1.parentRef = o.parentRef) sq2 ON sq2.catalogueId = p.catalogueId


            UNION ALL

            SELECT productId, 0 as value, pr.revision, pr.name, COUNT(productId) AS Cantidad,
            'PA' AS tipo,
            '01' AS deposito,
            'UN' AS unMedida,
            fpn.first_process_name AS Process_codigo,
            pr.subType
            FROM Product p
            INNER JOIN ProductRevision pr ON pr.masterRef = p.id_Table
            INNER JOIN Occurrence o ON o.instancedRef = pr.id_Table
            CROSS JOIN FirstProcess_codigo fpn
            WHERE o.subType = 'MEConsumed' OR pr.subType LIKE '%MatPrima%'
            GROUP BY productId, pr.revision, pr.name, fpn.first_process_name, pr.subType
			ORDER BY uud2.value DESC, p.catalogueId DESC";

            var productosDict = new Dictionary<string, string>();

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (SqlCommand command = new SqlCommand(query, connection))
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string codigo = reader["codigo"].ToString();
                        string descripcion = reader["Descripcion"].ToString();
                        string tipo = reader["tipo"].ToString();
                        string deposito = reader["deposito"].ToString();
                        string unMedida = reader["unMedida"].ToString();
                        string revision = reader["Revision"].ToString();

                        var producto = new
                        {
                            producto = new List<Dictionary<string, string>>
                            {
                                new() { { "campo", "codigo"      }, { "valor", codigo      } },
                                new() { { "campo", "descripcion" }, { "valor", descripcion } },
                                new() { { "campo", "tipo"        }, { "valor", tipo        } },
                                new() { { "campo", "deposito"    }, { "valor", deposito    } },
                                new() { { "campo", "unMedida"    }, { "valor", unMedida    } },
                                new() { { "campo", "revEstruct"  }, { "valor", revision    } },
                            }
                        };

                        string jsonData = JsonConvert.SerializeObject(producto, Formatting.Indented);
                        productosDict[codigo] = jsonData;
                    }
                }
            }

            var lista = new List<string>(productosDict.Values);
            Console.WriteLine($"SB1_BOP -> códigos únicos: {lista.Count}");
            Utilidades.EscribirEnLog($"SB1_BOP -> códigos únicos: {lista.Count}");
            return lista;
        }

        public class Procedimiento
        {
            public List<CampoValor> detalle { get; set; }
            public List<List<CampoValor>> alternativos { get; set; }
        }

        public class CampoValor
        {
            public string campo { get; set; }
            public string valor { get; set; }
        }
    }
}
