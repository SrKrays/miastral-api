namespace miastral_api.Models
{
    public class Orden
    {
        public int Id { get; set; }
        public int UsuarioId { get; set; }
        public DateTime FechaCreacion { get; set; } = DateTime.Now;
        public string Estado { get; set; } = "pendiente"; // pendiente | pagado | enviado | cancelado
        public decimal Total { get; set; }
        public string? MetodoPago { get; set; }
        public string? MpPaymentId { get; set; } // ID de MercadoPago

        // Datos de envío (embebidos en la orden) — nullable, se completan recién en el checkout
        public string? EnvioNombre { get; set; }
        public string? EnvioEmail { get; set; }
        public string? EnvioTelefono { get; set; }
        public string? EnvioCalle { get; set; }
        public string? EnvioCiudad { get; set; }
        public string? EnvioProvincia { get; set; }
        public string? EnvioCP { get; set; }

        // Envío por Andreani — se completan al apretar "Generar etiqueta" en el
        // panel admin (ver AndreaniShippingService). Antes de eso quedan null.
        public string? EnvioTransportista { get; set; }   // "andreani" (por si a futuro sumamos otro)
        public string? EnvioNumeroAndreani { get; set; }  // número de la orden de envío / tracking
        public decimal? EnvioCosto { get; set; }          // lo que cotizó Andreani por el despacho
        public string? EnvioEtiquetaUrl { get; set; }      // URL del PDF de la etiqueta para imprimir

        // Navegación
        public Usuario? Usuario { get; set; }
        public List<OrdenItem> Items { get; set; } = new();
    }
}