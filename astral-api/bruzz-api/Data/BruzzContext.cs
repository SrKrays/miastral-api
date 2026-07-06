using bruzz_api.Models;
using miastral_api.Models;
using Microsoft.EntityFrameworkCore;

namespace miastral_api.Data
{
    public class MiastralContext : DbContext
    {
        public MiastralContext(DbContextOptions<MiastralContext> options) : base(options) { }

        public DbSet<Admin> Admins { get; set; }
        public DbSet<Usuario> Usuarios { get; set; }
        public DbSet<Producto> Productos { get; set; }
        public DbSet<Orden> Ordenes { get; set; }
        public DbSet<OrdenItem> OrdenItems { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Admin>(e => {
                e.ToTable("admins");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Email).HasColumnName("email");
                e.Property(x => x.PasswordHash).HasColumnName("password_hash");
                e.Property(x => x.Nombre).HasColumnName("nombre");
                e.Property(x => x.Activo).HasColumnName("activo");
                e.Property(x => x.CreatedAt).HasColumnName("created_at");
                e.Property(x => x.LastLogin).HasColumnName("last_login");
            });

            modelBuilder.Entity<Usuario>(e => {
                e.ToTable("usuarios");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Nombre).HasColumnName("nombre");
                e.Property(x => x.Email).HasColumnName("email");
                e.Property(x => x.PasswordHash).HasColumnName("password_hash");
                e.Property(x => x.Telefono).HasColumnName("telefono");
                e.Property(x => x.Activo).HasColumnName("activo");
                e.Property(x => x.CreatedAt).HasColumnName("created_at");
                e.Property(x => x.LastLogin).HasColumnName("last_login");
            });

            modelBuilder.Entity<Producto>(e => {
                e.ToTable("productos");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Nombre).HasColumnName("nombre");
                e.Property(x => x.Descripcion).HasColumnName("descripcion");
                e.Property(x => x.Precio).HasColumnName("precio");
                e.Property(x => x.Stock).HasColumnName("stock");
                e.Property(x => x.Tipo).HasColumnName("tipo");
                e.Property(x => x.ImageUrl).HasColumnName("image_url");
                e.Property(x => x.Tag).HasColumnName("tag");
                e.Property(x => x.Activo).HasColumnName("activo");
                e.Property(x => x.Orden).HasColumnName("orden");
                e.Property(x => x.CreatedAt).HasColumnName("created_at");
            });

            modelBuilder.Entity<Orden>(e => {
                e.ToTable("ordenes");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
                e.Property(x => x.FechaCreacion).HasColumnName("fecha_creacion");
                e.Property(x => x.Estado).HasColumnName("estado");
                e.Property(x => x.Total).HasColumnName("total");
                e.Property(x => x.MetodoPago).HasColumnName("metodo_pago");
                e.Property(x => x.MpPaymentId).HasColumnName("mp_payment_id");
                e.Property(x => x.EnvioNombre).HasColumnName("envio_nombre");
                e.Property(x => x.EnvioEmail).HasColumnName("envio_email");
                e.Property(x => x.EnvioTelefono).HasColumnName("envio_telefono");
                e.Property(x => x.EnvioCalle).HasColumnName("envio_calle");
                e.Property(x => x.EnvioCiudad).HasColumnName("envio_ciudad");
                e.Property(x => x.EnvioProvincia).HasColumnName("envio_provincia");
                e.Property(x => x.EnvioCP).HasColumnName("envio_cp");
                e.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId);
                e.HasMany(x => x.Items).WithOne(x => x.Orden).HasForeignKey(x => x.OrdenId);
            });

            modelBuilder.Entity<OrdenItem>(e => {
                e.ToTable("orden_items");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.OrdenId).HasColumnName("orden_id");
                e.Property(x => x.ProductoId).HasColumnName("producto_id");
                e.Property(x => x.Cantidad).HasColumnName("cantidad");
                e.Property(x => x.PrecioUnitario).HasColumnName("precio_unitario");
                e.HasOne(x => x.Producto).WithMany().HasForeignKey(x => x.ProductoId);
            });
        }
    }
}