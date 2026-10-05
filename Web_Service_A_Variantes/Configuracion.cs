namespace Web_Service
{
    internal static class Configuracion
    {
        // ── Conexión activa ──────────────────────────────────────────────────────
        // Descomentar UNA sola línea según el entorno:

        // Pruebas (local PC-18)
        public static readonly string ConnectionString =
            "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";

        // Producción (SRV-TEAMCENTER)
        //public static readonly string ConnectionString =
        //    "Server=SRV-TEAMCENTER;Database=MBOM-BOP_Agrometal;User Id=infodba;Password=infodba;";
    }
}
