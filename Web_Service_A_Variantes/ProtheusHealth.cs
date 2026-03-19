using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web_Service;
using static Web_Service.Utilidades;
public static class ProtheusHealth
{
    private static readonly string UrlHealth = "http://119.8.73.193:8096/rest/TCEstado/Consultar/";
    private static readonly string HealthBodyJson = "{\"consulta\":\"TeamCenter\"}";

    private static readonly string _username = "USERREST";
    private static readonly string _password = "restagr";

    // HttpClient dedicado a health (timeout corto)
    private static readonly HttpClient _healthClient = CreateHealthClient();

    // Cache anti-sobrecarga
    private static DateTime _lastOkUtc = DateTime.MinValue;
    private static readonly object _lock = new();

    private static HttpClient CreateHealthClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5) // clave: el timeout del health
        };

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return c;
    }

    /// <summary>
    /// Espera INFINITO hasta que Protheus responda estado=activo.
    /// Si el endpoint da timeout, reintenta infinito (como tu 500).
    /// </summary>
    public static async Task WaitUntilActiveAsync(string tag, TimeSpan retryDelay)
    {
        // cache 15s para no pegarle todo el tiempo al health
        if ((DateTime.UtcNow - _lastOkUtc) < TimeSpan.FromSeconds(15))
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

                using var resp = await _healthClient.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[{tag}][HEALTH] HTTP {(int)resp.StatusCode}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}. Body: {body}");
                    Utilidades.EscribirEnLog($"[{tag}][HEALTH] HTTP {(int)resp.StatusCode}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}. Body: {body}");
                    await Task.Delay(retryDelay);
                    continue;
                }

                // Esperado: { "estado": "activo" }
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
                    Console.WriteLine($"[{tag}][HEALTH] Estado='{estado ?? "null"}'. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}. Body: {body}");
                    Utilidades.EscribirEnLog($"[{tag}][HEALTH] HTTP {(int)resp.StatusCode}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}. Body: {body}");
                    await Task.Delay(retryDelay);
                    continue;
                }

                lock (_lock)
                    _lastOkUtc = DateTime.UtcNow;

                return; // OK
            }
            catch (TaskCanceledException ex)
            {
                // Este es el caso “timeout” que te describió Protheus
                Console.WriteLine($"[{tag}][HEALTH] TIMEOUT: {ex.Message}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}");
                Utilidades.EscribirEnLog($"[{tag}][HEALTH] TIMEOUT: {ex.Message}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}");
                await Task.Delay(retryDelay);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"[{tag}][HEALTH] ERROR red/http: {ex.Message}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}");
                Utilidades.EscribirEnLog($"[{tag}][HEALTH] ERROR red/http: {ex.Message}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}");
                await Task.Delay(retryDelay);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{tag}][HEALTH] ERROR inesperado: {ex.Message}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}");
                Utilidades.EscribirEnLog($"[{tag}][HEALTH] ERROR inesperado: {ex.Message}. Reintentando en {retryDelay.TotalMinutes} min. Intento #{intento}");
                await Task.Delay(retryDelay);
            }
        }
    }

    /// <summary>
    /// POST-check: best effort (no bloqueante). Si querés infinito, decímelo y lo ajustamos.
    /// </summary>
    public static async Task PostCheckBestEffortAsync(string tag)
    {
        try
        {
            await WaitUntilActiveAsync(tag + "-POSTCHECK", TimeSpan.FromSeconds(10)); // corto para no clavar
        }
        catch
        {
            // nunca debe romper el flujo
        }
    }
}
