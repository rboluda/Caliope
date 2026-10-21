using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Instalaciones;

public interface IDescargador
{
    /// <summary>Descarga <paramref name="url"/> en <paramref name="destino"/> y verifica su SHA-256.</summary>
    /// <param name="progreso">Bytes recibidos y total, si se conoce.</param>
    Task DescargarAsync(string url, string destino, string sha256, IProgress<(long Recibidos, long? Total)> progreso, CancellationToken ct);
}

public sealed class DescargaCorruptaException(string mensaje) : Exception(mensaje);

/// <summary>
/// Descargas HTTP reanudables: lo descargado a medias queda en «.part» y se continúa con Range.
/// Si se corta la conexión, reintenta solo (<paramref name="intentos"/> veces, con esperas crecientes).
/// </summary>
public sealed class Descargador(HttpClient http, int intentos = 5, TimeSpan? esperaBase = null) : IDescargador
{
    private readonly TimeSpan _esperaBase = esperaBase ?? TimeSpan.FromSeconds(2);

    public async Task DescargarAsync(string url, string destino, string sha256, IProgress<(long Recibidos, long? Total)> progreso, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        if (File.Exists(destino) && await HashAsync(destino, ct).ConfigureAwait(false) == sha256.ToLowerInvariant())
            return;

        for (var intento = 1; ; intento++)
        {
            try
            {
                await IntentarAsync(url, destino, sha256, progreso, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (intento < intentos && !ct.IsCancellationRequested
                                      && e is HttpRequestException or IOException && e is not DescargaCorruptaException)
            {
                await Task.Delay(_esperaBase * Math.Pow(2, intento - 1), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task IntentarAsync(string url, string destino, string sha256, IProgress<(long Recibidos, long? Total)> progreso, CancellationToken ct)
    {
        var parcial = destino + ".part";
        var yaTengo = File.Exists(parcial) ? new FileInfo(parcial).Length : 0;

        using var peticion = new HttpRequestMessage(HttpMethod.Get, url);
        if (yaTengo > 0)
            peticion.Headers.Range = new RangeHeaderValue(yaTengo, null);
        using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Si el servidor no admite continuar, se empieza de cero.
        var continua = yaTengo > 0 && respuesta.StatusCode == HttpStatusCode.PartialContent;
        if (respuesta.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            continua = true;
        else
            respuesta.EnsureSuccessStatusCode();
        if (!continua)
            yaTengo = 0;

        long? total = respuesta.Content.Headers.ContentLength is { } largo ? largo + yaTengo : null;
        if (respuesta.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            await using var entrada = await respuesta.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var salida = new FileStream(parcial, continua ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var bufer = new byte[1 << 16];
            var recibidos = yaTengo;
            var ultimoAviso = 0L;
            int leidos;
            while ((leidos = await entrada.ReadAsync(bufer, ct).ConfigureAwait(false)) > 0)
            {
                await salida.WriteAsync(bufer.AsMemory(0, leidos), ct).ConfigureAwait(false);
                recibidos += leidos;
                if (recibidos - ultimoAviso > 512 * 1024)
                {
                    progreso.Report((recibidos, total));
                    ultimoAviso = recibidos;
                }
            }
            progreso.Report((recibidos, total));
        }

        var hash = await HashAsync(parcial, ct).ConfigureAwait(false);
        if (hash != sha256.ToLowerInvariant())
        {
            File.Delete(parcial);
            throw new DescargaCorruptaException(
                T("El archivo descargado de {0} no coincide con el esperado (SHA-256 {1}). Se ha borrado; vuelve a intentarlo.", url, hash));
        }
        File.Move(parcial, destino, overwrite: true);
    }

    public static async Task<string> HashAsync(string ruta, CancellationToken ct)
    {
        await using var f = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
    }
}
