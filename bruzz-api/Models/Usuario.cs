namespace miastral_api.Models
{
    public class Usuario
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string Apellido { get; set; } = "";
        public string Email { get; set; } = "";
        public string? PasswordHash { get; set; }   // null si se registró con Google
        public string? GoogleId { get; set; }        // null si se registró con email/password
        public string? Telefono { get; set; }
        public bool Activo { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? LastLogin { get; set; }
    }
}