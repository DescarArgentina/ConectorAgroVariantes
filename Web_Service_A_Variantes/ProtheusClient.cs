using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Web_Service
{
    public static class ProtheusClient
    {
        // Endpoints de negocio (los tuyos)
        public static string UrlSb1Incluir = "http://119.8.73.193:8096/rest/TCProductos/Incluir/";
        public static string UrlSb1Modificar = "http://119.8.73.193:8096/rest/TCProductos/Modificar/";

        // Endpoint health nuevo (el que te habilitaron)
        public static string UrlHealth = "http://119.8.73.193:8096/rest/TCEstado/Consultar/";

        // Body requerido por Protheus en el GET
        private const string HealthBodyJson = "{\"consulta\":\"TeamCenter\"}";

        private static readonly string _username = "USERREST";
        private static readonly string _password = "restagr";

        private static readonly HttpClient _http = CreateHttpClient();

        private static DateTime _lastHealthOkUtc = DateTime.MinValue;
        private static readonly object _healthLock = new();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            return client;
        }

        /// <summary>
        /// Valida que Protheus esté "activo" usando el endpoint dedicado.
        /// Usa cache para no golpear el servicio en cada request.
        /// </summary>
        public static async Task EnsureUpAsync(string motivo, CancellationToken ct, bool infinite)
        {
            // Cache para no consultar estado en cada request
            if ((DateTime.UtcNow - _lastHealthOkUtc) < TimeSpan.FromSeconds(15))
                return;

            int intento = 0;
            int delayMs = 3000;       // 3s
            int delayMaxMs = 30000;   // 30s

            // Si NO es infinito, damos pocos intentos y no bloqueamos el flujo
            int maxIntentos = infinite ? int.MaxValue : 2;

            while (intento < maxIntentos)
            {
                ct.ThrowIfCancellationRequested();
                intento++;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5)); // timeout corto para health

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, UrlHealth)
                    {
                        Content = new StringContent(HealthBodyJson, Encoding.UTF8, "application/json")
                    };

                    using var resp = await _http.SendAsync(req, cts.Token);
                    var body = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"Health HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

                    // Esperamos {"estado":"activo"}
                    string estado = null;
                    using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    {
                        if (doc.RootElement.TryGetProperty("estado", out var prop))
                            estado = prop.GetString();
                    }

                    if (!string.Equals(estado, "activo", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Health respondió estado='{estado ?? "null"}'. Body: {body}");

                    lock (_healthLock)
                        _lastHealthOkUtc = DateTime.UtcNow;

                    return; // OK
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // Timeout health
                    Console.WriteLine($"[HEALTH] TIMEOUT ({motivo}). Intento {intento}/{maxIntentos}. Reintentando en {delayMs}ms. Detalle: {ex.Message}");
                    Utilidades.EscribirEnLog($"[HEALTH] TIMEOUT ({motivo}). Intento {intento}/{maxIntentos}. Reintentando en {delayMs}ms. Detalle: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HEALTH] ERROR ({motivo}). Intento {intento}/{maxIntentos}. Reintentando en {delayMs}ms. Detalle: {ex.Message}");
                    Utilidades.EscribirEnLog($"[HEALTH] ERROR ({motivo}). Intento {intento}/{maxIntentos}. Reintentando en {delayMs}ms. Detalle: {ex.Message}");
                }

                // Si es best-effort, reintenta un poco y sigue; si es infinito, queda esperando siempre.
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, delayMaxMs);
            }

            // Best-effort: si no pudo validar, no bloqueamos.
            Console.WriteLine($"[HEALTH] WARN: No se pudo validar estado ({motivo}) luego de {maxIntentos} intentos. Se continúa.");
            Utilidades.EscribirEnLog($"[HEALTH] WARN: No se pudo validar estado ({motivo}) luego de {maxIntentos} intentos. Se continúa.");
        }



        /// <summary>
        /// Envía JSON a un endpoint Protheus con health-check pre/post.
        /// </summary>
        public static async Task<HttpResponseMessage> SendJsonAsync(
    HttpMethod method,
    string url,
    string json,
    TimeSpan requestTimeout,
    CancellationToken ct)
        {
            // PRE: infinito (no avanzamos si Protheus está abajo)
            await EnsureUpAsync("PRE", ct, infinite: true);

            using var req = new HttpRequestMessage(method, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(requestTimeout);

            try
            {
                return await _http.SendAsync(req, cts.Token);
            }
            finally
            {
                // POST: best-effort (no queremos clavar el conector si cae justo después)
                try { await EnsureUpAsync("POST", CancellationToken.None, infinite: false); }
                catch { /* nunca debería romper el flujo */ }
            }
        }

    }
}
