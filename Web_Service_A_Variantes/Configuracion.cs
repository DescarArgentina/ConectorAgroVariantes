namespace Web_Service
{
    internal static class Configuracion
    {
        // ── Conexión activa ──────────────────────────────────────────────────────
        // Descomentar UNA sola línea según el entorno:

        // Pruebas (local PC-18)
        public static readonly string ConnectionString =
            "Server=PC-18;Database=AgroVariantes;Integrated Security=true;";

        // Producción (servidor 10.0.0.82)
        //public static readonly string ConnectionString =
        //    "Server=10.0.0.82;Database=AgrometalBOP;User Id=sa;Password=Descar_2020;";
    }
}
