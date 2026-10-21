using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Constantino.Core;

namespace Constantino.App.Servicios;

/// <summary>
/// Comprueba en GitHub si hay una versión nueva de Calíope y la instala sin preguntas: descarga el instalador,
/// lo lanza en modo silencioso y cierra la aplicación; el instalador la vuelve a abrir al terminar.
/// </summary>
public sealed class ServicioActualizaciones(ServicioAjustes ajustes)
{
    /// <summary>Como mucho una comprobación automática al día.</summary>
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(20);

    private static readonly HttpClient Http = CrearHttp();

    public static Version VersionActual { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Solo una copia instalada con el instalador puede actualizarse sola (una compilada a mano no tiene desinstalador
    /// al lado: actualizarla crearía una segunda instalación).
    /// </summary>
    public static bool PuedeActualizarseSola =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe").Any();

    /// <summary>La versión nueva publicada, o null si no la hay (o no toca comprobar, o no hay conexión).</summary>
    public async Task<VersionPublicada?> ComprobarAsync(bool forzar, CancellationToken ct = default)
    {
        var a = ajustes.Actual;
        if (!forzar && (!a.BuscarActualizaciones || a.UltimaComprobacion is { } u && DateTimeOffset.Now - u < Intervalo))
            return null;
        try
        {
            var json = await Http.GetStringAsync(Actualizaciones.UrlUltimaVersion, ct);
            ajustes.Guardar(ajustes.Actual with { UltimaComprobacion = DateTimeOffset.Now });
            return Actualizaciones.Leer(json) is { } v && Actualizaciones.EsMasNueva(v, VersionActual) ? v : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Sin conexión o sin versiones publicadas: no es un error para el usuario.
            return null;
        }
    }

    /// <summary>Descarga el instalador a una carpeta temporal y devuelve su ruta.</summary>
    public async Task<string> DescargarAsync(VersionPublicada version, IProgress<double> progreso, CancellationToken ct = default)
    {
        var url = version.Instalador ?? throw new InvalidOperationException("Esta versión no trae instalador.");
        var carpeta = Path.Combine(Path.GetTempPath(), "Caliope-actualizacion");
        Directory.CreateDirectory(carpeta);
        var destino = Path.Combine(carpeta, Path.GetFileName(new Uri(url).LocalPath));
        using var respuesta = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        respuesta.EnsureSuccessStatusCode();
        var total = respuesta.Content.Headers.ContentLength ?? 0;
        await using (var origen = await respuesta.Content.ReadAsStreamAsync(ct))
        await using (var archivo = File.Create(destino))
        {
            var bufer = new byte[1 << 16];
            long leidos = 0;
            int n;
            while ((n = await origen.ReadAsync(bufer, ct)) > 0)
            {
                await archivo.WriteAsync(bufer.AsMemory(0, n), ct);
                leidos += n;
                if (total > 0)
                    progreso.Report((double)leidos / total);
            }
        }
        return destino;
    }

    /// <summary>
    /// Lanza el instalador en silencio (sin asistente ni preguntas) en la misma carpeta y cierra Calíope para que pueda
    /// sustituir los archivos. El instalador vuelve a abrir Calíope al terminar.
    /// </summary>
    public static void InstalarYSalir(string instalador)
    {
        Process.Start(new ProcessStartInfo(instalador)
        {
            ArgumentList = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", $"/DIR={AppContext.BaseDirectory.TrimEnd('\\')}" },
            UseShellExecute = false,
        });
        System.Windows.Application.Current.Shutdown();
    }

    private static HttpClient CrearHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Caliope/{VersionActual}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
