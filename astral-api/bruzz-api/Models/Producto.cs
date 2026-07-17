namespace miastral_api.Models
{
    public class Producto
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string? Descripcion { get; set; }                    // breve, para la card
        public string? DescripcionCompleta { get; set; }            // texto largo del modal de detalle
        public decimal? Precio { get; set; }                        // null = "a consultar"
        public decimal? PrecioUSD { get; set; }                     // equivalente en dólares, si aplica
        public decimal? Sena { get; set; }                          // seña/depósito (sesiones), no reembolsable
        public string? Duracion { get; set; }                       // ej. "90 min" (solo sesiones)
        public string? Modalidad { get; set; }                      // ej. "Online por Google Meet"
        public string? Incluye { get; set; }                        // bullets separados por "|"
        public bool RequiereDatosNacimiento { get; set; } = false;   // ej. Informe PDF
        public int? Stock { get; set; }                              // null = sin control de stock
        public string Tipo { get; set; } = ""; // "producto" | "servicio" | "programa"
        public string? ImageUrl { get; set; }
        public string? Tag { get; set; }
        public bool Activo { get; set; } = true;
        public int Orden { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}