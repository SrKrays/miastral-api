using miastral_api.Data;
using Microsoft.EntityFrameworkCore;

namespace miastral_api.Services
{
    // Calcula el costo de envío de un carrito contra MiCorreo. Lo usan dos lugares:
    //   - EnviosController (el checkout lo llama para mostrar el costo en vivo).
    //   - OrdenesController.Create (recalcula SIEMPRE en el servidor al crear la
    //     orden, así el cliente no puede mandar un costo de envío inventado).
    public class EnvioCotizadorService
    {
        private readonly MiastralContext _db;
        private readonly MiCorreoService _micorreo;

        public EnvioCotizadorService(MiastralContext db, MiCorreoService micorreo)
        {
            _db = db;
            _micorreo = micorreo;
        }

        public async Task<ResultadoEnvio> CotizarAsync(
            IEnumerable<(int productoId, int cantidad)> items,
            string? tipoEntrega, string? provinciaCodigo, string? cp, string? sucursalCodigo)
        {
            var lista = items.ToList();
            var ids = lista.Select(i => i.productoId).ToList();
            var productos = await _db.Productos.Where(p => ids.Contains(p.Id)).ToListAsync();

            var (bulto, faltantes) = MiCorreoService.ArmarBulto(
                lista.Where(i => productos.Any(p => p.Id == i.productoId))
                     .Select(i => (productos.First(p => p.Id == i.productoId), i.cantidad)));

            // Carrito sin productos físicos (talleres, programas, digitales): no hay envío.
            if (bulto == null && faltantes == null)
                return ResultadoEnvio.SinEnvio();

            if (faltantes != null)
                return ResultadoEnvio.Error($"Todavía no podemos cotizar el envío de: {faltantes}. Escribinos y lo resolvemos.");

            // Sin credenciales cargadas el sistema funciona como antes (sin costo de envío).
            if (!_micorreo.CredencialesCargadas)
                return ResultadoEnvio.ConEnvioSinCotizar();

            if (tipoEntrega != "domicilio" && tipoEntrega != "sucursal")
                return ResultadoEnvio.ConEnvioSinCotizar(configurado: true);

            string cpDestino;
            string? sucursalNombre = null;

            if (tipoEntrega == "sucursal")
            {
                if (string.IsNullOrWhiteSpace(provinciaCodigo) || string.IsNullOrWhiteSpace(sucursalCodigo))
                    return ResultadoEnvio.ConEnvioSinCotizar(configurado: true);

                var (okSuc, msjSuc, sucursales) = await _micorreo.ObtenerSucursalesAsync(provinciaCodigo);
                if (!okSuc) return ResultadoEnvio.Error($"No pudimos consultar las sucursales: {msjSuc}");

                var suc = sucursales.FirstOrDefault(s => s.Codigo == sucursalCodigo);
                if (suc == null) return ResultadoEnvio.Error("La sucursal elegida no es válida, elegí otra.");

                cpDestino = suc.CP; // el CP sale de la sucursal, no del cliente
                sucursalNombre = $"{suc.Nombre} — {suc.Direccion}, {suc.Localidad}";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(cp)) return ResultadoEnvio.ConEnvioSinCotizar(configurado: true);
                cpDestino = cp;
            }

            var (ok, mensaje, cot) = await _micorreo.CotizarAsync(cpDestino, tipoEntrega == "sucursal" ? "S" : "D", bulto!);
            if (!ok || cot == null) return ResultadoEnvio.Error($"No pudimos cotizar el envío: {mensaje}");

            return new ResultadoEnvio(true, "OK", true, true, Math.Ceiling(cot.Precio), cot.DiasMin, cot.DiasMax, sucursalNombre);
        }
    }

    // requiereEnvio: hay productos físicos. configurado: MiCorreo está cargado.
    // costo == null con requiereEnvio y configurado → falta elegir destino/sucursal.
    public record ResultadoEnvio(bool Ok, string Mensaje, bool RequiereEnvio, bool Configurado, decimal? Costo, string? DiasMin, string? DiasMax, string? SucursalNombre)
    {
        public static ResultadoEnvio SinEnvio() => new(true, "OK", false, true, null, null, null, null);
        public static ResultadoEnvio ConEnvioSinCotizar(bool configurado = false) => new(true, "OK", true, configurado, null, null, null, null);
        public static ResultadoEnvio Error(string mensaje) => new(false, mensaje, true, true, null, null, null, null);
    }
}
