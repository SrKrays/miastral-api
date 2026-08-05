using miastral_api.Data;
using miastral_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace miastral_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductosController : ControllerBase
    {
        private readonly MiastralContext _db;

        public ProductosController(MiastralContext db)
        {
            _db = db;
        }

        // GET api/productos?tipo=producto — público, solo activos.
        // tipo: "producto" | "servicio" | "programa" (opcional, sin filtro trae todo)
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? tipo)
        {
            var query = _db.Productos.Where(p => p.Activo);

            if (!string.IsNullOrEmpty(tipo))
                query = query.Where(p => p.Tipo == tipo);

            var productos = await query.OrderBy(p => p.Orden).ToListAsync();
            return Ok(productos);
        }

        // GET api/productos/admin — solo admin. Trae TODO (activos e inactivos),
        // para que Vale pueda reactivar algo que se dio de baja por error.
        [HttpGet("admin")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> GetAllAdmin()
        {
            var productos = await _db.Productos.OrderBy(p => p.Orden).ToListAsync();
            return Ok(productos);
        }

        // GET api/productos/5 — público
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var producto = await _db.Productos.FirstOrDefaultAsync(p => p.Id == id && p.Activo);
            if (producto == null) return NotFound(new { message = "Producto no encontrado" });
            return Ok(producto);
        }

        // POST api/productos — solo admin (Valentina)
        [HttpPost]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Create([FromBody] Producto producto)
        {
            producto.Id = 0; // por si viene seteado, que lo genere la BD
            producto.CreatedAt = DateTime.Now;

            _db.Productos.Add(producto);
            await _db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = producto.Id }, producto);
        }

        // PUT api/productos/5 — solo admin
        [HttpPut("{id}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Update(int id, [FromBody] Producto producto)
        {
            var existente = await _db.Productos.FindAsync(id);
            if (existente == null) return NotFound(new { message = "Producto no encontrado" });

            existente.Nombre = producto.Nombre;
            existente.Descripcion = producto.Descripcion;
            existente.DescripcionCompleta = producto.DescripcionCompleta;
            existente.Precio = producto.Precio;
            existente.PrecioUSD = producto.PrecioUSD;
            existente.Sena = producto.Sena;
            existente.Duracion = producto.Duracion;
            existente.Modalidad = producto.Modalidad;
            existente.Incluye = producto.Incluye;
            existente.RequiereDatosNacimiento = producto.RequiereDatosNacimiento;
            existente.Stock = producto.Stock;
            existente.PesoGramos = producto.PesoGramos;
            existente.AltoCm = producto.AltoCm;
            existente.AnchoCm = producto.AnchoCm;
            existente.LargoCm = producto.LargoCm;
            existente.Tipo = producto.Tipo;
            existente.ImageUrl = producto.ImageUrl;
            existente.Tag = producto.Tag;
            existente.Activo = producto.Activo;
            existente.Orden = producto.Orden;

            await _db.SaveChangesAsync();
            return Ok(existente);
        }

        // DELETE api/productos/5 — solo admin.
        // Soft delete (Activo = false): nunca se borra físicamente porque puede haber
        // orden_items de compras pasadas apuntando a este producto.
        [HttpDelete("{id}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Delete(int id)
        {
            var producto = await _db.Productos.FindAsync(id);
            if (producto == null) return NotFound(new { message = "Producto no encontrado" });

            producto.Activo = false;
            await _db.SaveChangesAsync();

            return NoContent();
        }

        // DELETE api/productos/5/permanente — solo admin. Borrado real, para sacar
        // de encima productos de prueba (ej. "Prueba", "Producto de prueba") que
        // nunca tuvieron una compra real asociada.
        // Si el producto ya tiene orden_items (compras reales), la base rechaza el
        // borrado por la relación protegida (Restrict) — en ese caso devolvemos un
        // mensaje claro en vez de un 500 pelado, y hay que desactivarlo en cambio.
        [HttpDelete("{id}/permanente")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> DeletePermanente(int id)
        {
            var producto = await _db.Productos.FindAsync(id);
            if (producto == null) return NotFound(new { message = "Producto no encontrado" });

            _db.Productos.Remove(producto);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Conflict(new { message = "Este producto ya tiene pedidos asociados y no se puede eliminar del todo. Desactivalo en cambio." });
            }

            return NoContent();
        }
    }
}
