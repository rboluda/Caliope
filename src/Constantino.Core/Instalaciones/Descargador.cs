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
/// Descargas HTTP reanudables y en paralelo.
/// <list type="bullet">
/// <item>Si el servidor admite rangos y el archivo es grande, se baja en tramos con varias conexiones a la vez
/// (cada tramo en «.parteIdeN», reanudable por su tamaño) y al final se unen: aprovecha el ancho de banda cuando
/// el servidor limita la velocidad de cada conexión.</item>
/// <item>Si no, con una sola conexión: lo descargado a medias queda en «.part» y se continúa con Range.</item>
/// <item>Si una conexión se queda <paramref name="sinDatos"/> sin recibir nada, se corta y se reanuda: una conexión
/// muerta no deja la descarga parada para siempre.</item>
/// </list>
/// Cada conexión reintenta sola (<paramref name="intentos"/> veces, con esperas crecientes).
/// </summary>
public sealed class Descargador(HttpClient http, int intentos = 8, TimeSpan? esperaBase = null, TimeSpan? sinDatos = null,
    int conexiones = 8, long tramoMinimo = 8 * 1024 * 1024) : IDescargador
{
    private readonly TimeSpan _esperaBase = esperaBase ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _sinDatos = sinDatos ?? TimeSpan.FromSeconds(30);

    public async Task DescargarAsync(string url, string destino, string sha256, IProgress<(long Recibidos, long? Total)> progreso, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        if (File.Exists(destino) && await HashAsync(destino, ct).ConfigureAwait(false) == sha256.ToLowerInvariant())
            return;

        var parcial = destino + ".part";
        // Una descarga a medias de una sola conexión (p. ej. de una versión anterior) se continúa tal cual.
        var enTramos = !File.Exists(parcial) && conexiones > 1
                       && await ConsultarAsync(url, ct).ConfigureAwait(false) is { } total && total >= 2 * tramoMinimo
            ? await EnTramosAsync(url, destino, total, progreso, ct).ConfigureAwait(false)
            : false;
        if (!enTramos)
            await ConReintentosAsync(() => UnaConexionAsync(url, parcial, progreso, ct), ct).ConfigureAwait(false);

        var hash = await HashAsync(parcial, ct).ConfigureAwait(false);
        if (hash != sha256.ToLowerInvariant())
        {
            File.Delete(parcial);
            throw new DescargaCorruptaException(
                T("El archivo descargado de {0} no coincide con el esperado (SHA-256 {1}). Se ha borrado; vuelve a intentarlo.", url, hash));
        }
        File.Move(parcial, destino, overwrite: true);
        foreach (var resto in Tramos(destino))
            File.Delete(resto);
    }

    /// <summary>El tamaño del archivo si el servidor admite descargarlo por rangos; si no (o falla la consulta), null.</summary>
    private async Task<long?> ConsultarAsync(string url, CancellationToken ct)
    {
        try
        {
            using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
            plazo.CancelAfter(_sinDatos);
            using var peticion = new HttpRequestMessage(HttpMethod.Head, url);
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, plazo.Token).ConfigureAwait(false);
            return respuesta.IsSuccessStatusCode && respuesta.Headers.AcceptRanges.Contains("bytes")
                ? respuesta.Content.Headers.ContentLength
                : null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Baja el archivo en tramos a la vez y los une en «.part». False si el servidor no respeta los rangos.</summary>
    private async Task<bool> EnTramosAsync(string url, string destino, long total, IProgress<(long Recibidos, long? Total)> progreso,
        CancellationToken ct)
    {
        var n = (int)Math.Clamp(total / tramoMinimo, 2, conexiones);
        var tamano = (total + n - 1) / n;
        var tramos = Enumerable.Range(0, n)
            .Select(i => (Ruta: $"{destino}.parte{i + 1}de{n}", Inicio: i * tamano, Fin: Math.Min(total, (i + 1) * tamano) - 1))
            .ToList();
        // Tramos de una división distinta (otro tamaño de archivo u otro número de conexiones) no sirven.
        foreach (var viejo in Tramos(destino).Except(tramos.Select(t => t.Ruta)))
            File.Delete(viejo);

        long recibidos = tramos.Sum(t => File.Exists(t.Ruta) ? new FileInfo(t.Ruta).Length : 0);
        progreso.Report((recibidos, total));
        void Sumar(int bytes) => progreso.Report((Interlocked.Add(ref recibidos, bytes), total));

        // Si un tramo se rinde, los demás paran (lo bajado se conserva para el siguiente intento).
        using var todos = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tareas = tramos.Select(async t =>
        {
            try
            {
                await ConReintentosAsync(() => TramoAsync(url, t.Ruta, t.Inicio, t.Fin, Sumar, todos.Token), todos.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                await todos.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tareas).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // El error de verdad, no la cancelación que provocó en los demás tramos.
            var error = tareas.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (error is RangoNoAdmitidoException)
            {
                foreach (var t in tramos)
                    File.Delete(t.Ruta);
                return false;
            }
            if (error is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            throw;
        }

        var parcial = destino + ".part";
        await using (var salida = new FileStream(parcial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            foreach (var t in tramos)
            {
                await using var entrada = new FileStream(t.Ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
                await entrada.CopyToAsync(salida, ct).ConfigureAwait(false);
            }
        }
        foreach (var t in tramos)
            File.Delete(t.Ruta);
        return true;
    }

    private static IEnumerable<string> Tramos(string destino) =>
        Directory.EnumerateFiles(Path.GetDirectoryName(destino)!, Path.GetFileName(destino) + ".parte*");

    private sealed class RangoNoAdmitidoException() : Exception("El servidor no respeta los rangos.");

    /// <summary>Completa un tramo: pide lo que le falta desde su tamaño actual hasta <paramref name="fin"/>.</summary>
    private async Task TramoAsync(string url, string ruta, long inicio, long fin, Action<int> sumar, CancellationToken ct)
    {
        var yaTengo = File.Exists(ruta) ? new FileInfo(ruta).Length : 0;
        if (inicio + yaTengo > fin)
            return;
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        plazo.CancelAfter(_sinDatos);
        using var peticion = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(inicio + yaTengo, fin) } };
        using var respuesta = await Vigilar(() => http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, plazo.Token), ct)
            .ConfigureAwait(false);
        if (respuesta.StatusCode != HttpStatusCode.PartialContent)
        {
            respuesta.EnsureSuccessStatusCode();
            throw new RangoNoAdmitidoException();
        }
        await using var entrada = await respuesta.Content.ReadAsStreamAsync(plazo.Token).ConfigureAwait(false);
        await using var salida = new FileStream(ruta, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await CopiarAsync(entrada, salida, plazo, sumar, ct).ConfigureAwait(false);
        if (inicio + new FileInfo(ruta).Length <= fin)
            throw new IOException(T("La conexión se cerró antes de tiempo."));
    }

    /// <summary>Descarga con una sola conexión en <paramref name="parcial"/>, continuando lo que ya hubiera.</summary>
    private async Task UnaConexionAsync(string url, string parcial, IProgress<(long Recibidos, long? Total)> progreso, CancellationToken ct)
    {
        var yaTengo = File.Exists(parcial) ? new FileInfo(parcial).Length : 0;
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        plazo.CancelAfter(_sinDatos);
        using var peticion = new HttpRequestMessage(HttpMethod.Get, url);
        if (yaTengo > 0)
            peticion.Headers.Range = new RangeHeaderValue(yaTengo, null);
        using var respuesta = await Vigilar(() => http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, plazo.Token), ct)
            .ConfigureAwait(false);

        // Si el servidor no admite continuar, se empieza de cero.
        var continua = yaTengo > 0 && respuesta.StatusCode == HttpStatusCode.PartialContent;
        if (respuesta.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return; // ya estaba entero
        respuesta.EnsureSuccessStatusCode();
        if (!continua)
            yaTengo = 0;

        long? total = respuesta.Content.Headers.ContentLength is { } largo ? largo + yaTengo : null;
        var recibidos = yaTengo;
        await using var entrada = await respuesta.Content.ReadAsStreamAsync(plazo.Token).ConfigureAwait(false);
        await using var salida = new FileStream(parcial, continua ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await CopiarAsync(entrada, salida, plazo, n => progreso.Report((recibidos += n, total)), ct).ConfigureAwait(false);
        progreso.Report((recibidos, total));
        if (total is { } t && recibidos < t)
            throw new IOException(T("La conexión se cerró antes de tiempo."));
    }

    /// <summary>
    /// Copia con vigilancia: cada lectura reinicia el plazo; si pasa <see cref="_sinDatos"/> sin llegar nada, la
    /// conexión se da por muerta (IOException, que se reintenta reanudando).
    /// </summary>
    private async Task CopiarAsync(Stream entrada, Stream salida, CancellationTokenSource plazo, Action<int> avisar, CancellationToken ct)
    {
        var bufer = new byte[1 << 16];
        var pendiente = 0;
        while (true)
        {
            plazo.CancelAfter(_sinDatos);
            var leidos = await Vigilar(() => entrada.ReadAsync(bufer, plazo.Token).AsTask(), ct).ConfigureAwait(false);
            if (leidos == 0)
                break;
            await salida.WriteAsync(bufer.AsMemory(0, leidos), ct).ConfigureAwait(false);
            pendiente += leidos;
            if (pendiente >= 512 * 1024)
            {
                avisar(pendiente);
                pendiente = 0;
            }
        }
        if (pendiente > 0)
            avisar(pendiente);
    }

    /// <summary>Convierte el corte por falta de datos en una IOException reintentable (la cancelación del usuario sigue siéndolo).</summary>
    private async Task<T> Vigilar<T>(Func<Task<T>> operacion, CancellationToken ct)
    {
        try
        {
            return await operacion().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(Idioma.T("La descarga lleva {0:0} s sin recibir datos: se reanuda.", _sinDatos.TotalSeconds));
        }
    }

    private async Task ConReintentosAsync(Func<Task> intento, CancellationToken ct)
    {
        for (var n = 1; ; n++)
        {
            try
            {
                await intento().ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (n < intentos && !ct.IsCancellationRequested && e is HttpRequestException or IOException)
            {
                await Task.Delay(_esperaBase * Math.Pow(2, Math.Min(n - 1, 4)), ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task<string> HashAsync(string ruta, CancellationToken ct)
    {
        await using var f = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
    }
}
