using System;
using System.IO;
using System.Text;

namespace Web_Service
{
    public static class Utilidades
    {
        private static readonly object _lock = new();

        // Fallbacks si no se inicializa
        private static string _rutaLog = @"C:\Temp\ScriptPrincipal.log";
        private static string _rutaLogJson = @"C:\Temp\JSON_ScriptPrincipal.log";

        // NUEVO: log de errores Protheus
        private static string _rutaErroresProtheus = @"C:\Temp\ErroresProtheus.log";

        /// <summary>
        /// Inicializa el log para esta ejecución (1 set de logs por MBOM).
        /// Debe llamarse una vez al inicio del Main con la carpeta de la MBOM de origen.
        /// </summary>
        public static void InicializarLogPorMbom(string mbomFolderPath)
        {
            if (string.IsNullOrWhiteSpace(mbomFolderPath))
                return;

            Directory.CreateDirectory(mbomFolderPath);

            _rutaLog = Path.Combine(mbomFolderPath, "ScriptPrincipal.log");
            _rutaLogJson = Path.Combine(mbomFolderPath, "JSON_ScriptPrincipal.log");

            // NUEVO: mismo nivel dentro de la carpeta MBOM
            _rutaErroresProtheus = Path.Combine(mbomFolderPath, "ErroresProtheus.log");
        }

        public static void EscribirEnLog(string mensaje)
            => AppendLineSafeConTimestamp(_rutaLog, mensaje);

        public static void EscribirJSONEnLog(string mensaje)
            => AppendLineSafeConTimestamp(_rutaLogJson, mensaje);

        /// <summary>
        /// NUEVO: Log dedicado para cualquier errorCode de Protheus.
        /// Formato (1 línea):
        /// {hora} | {tabla} | {metodo} | {producto} | {descripcion}
        /// donde descripcion = "errorCode=XX | errorMessage=..."
        /// </summary>
        public static void EscribirErrorProtheus(string tabla, string metodo, string producto, int? errorCode, string errorMessage)
        {
            try
            {
                // ✅ Purga en origen: no registrar "error inútil" (POST + errorCode=3)
                // Nota: normalizo método para evitar "post", "POST ", etc.
                var metodoNorm = (metodo ?? "").Trim();
                if (errorCode == 3 && metodoNorm.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    return;

                var hora = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                string desc;
                if (errorCode.HasValue)
                    desc = $"errorCode={errorCode.Value} | {errorMessage ?? ""}";
                else
                    desc = errorMessage ?? "";

                // Sanitizar separadores y saltos de línea para mantener 1 línea por evento
                tabla = (tabla ?? "").Replace("|", "/").Trim();
                metodo = metodoNorm.Replace("|", "/").Trim();
                producto = (producto ?? "").Replace("|", "/").Trim();
                desc = (desc ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

                var line = $"{hora} | {tabla} | {metodo} | {producto} | {desc}{Environment.NewLine}";

                lock (_lock)
                {
                    File.AppendAllText(_rutaErroresProtheus, line, Encoding.UTF8);
                }
            }
            catch
            {
                // No abortar la ejecución por un fallo de log.
            }
        }


        // Wrapper legacy para no romper código existente (SG2SH3 todavía llama esto)
        public static void EscribirOrdenesDeProduccion(string productoCodigo)
        {
            // Sin tocar lógica de los callers: lo redireccionamos al nuevo log
            EscribirErrorProtheus("SG2SH3", "LEGACY", productoCodigo, null, "Orden de producción asociada (migrar a logging completo)");
        }

        private static void AppendLineSafeConTimestamp(string ruta, string mensaje)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {mensaje}{Environment.NewLine}";
                lock (_lock)
                {
                    File.AppendAllText(ruta, line, Encoding.UTF8);
                }
            }
            catch
            {
                // No abortar la ejecución por un fallo de log.
            }
        }
    }
}
