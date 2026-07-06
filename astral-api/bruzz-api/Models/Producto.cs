namespace miastral_api.Models
{
    public class Producto
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public decimal Precio { get; set; }
        public int Stock { get; set; }
        public string Tipo { get; set; } = ""; // "producto" | "servicio" | "programa"
        public string ImageUrl { get; set; } = "";
        public string Tag { get; set; } = "";
        public bool Activo { get; set; } = true;
        public int Orden { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}