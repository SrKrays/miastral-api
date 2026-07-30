namespace miastral_api.Models
{
    public class OrdenItem
    {
        public int Id { get; set; }
        public int OrdenId { get; set; }
        public int ProductoId { get; set; }
        public int Cantidad { get; set; }
        public decimal PrecioUnitario { get; set; } // precio al momento de compra

        // Navegación
        public Orden? Orden { get; set; }
        public Producto? Producto { get; set; }
    }
}