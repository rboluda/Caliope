using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Constantino.Core;
using static Constantino.Core.Idioma;

namespace Constantino.App.Servicios;

/// <summary>Lo que ha salido de comprobar si hay versión nueva.</summary>
/// <param name="Nueva">La versión nueva, si la hay.</param>
/// <param name="Fallo">Por qué no se ha podido comprobar (null si se ha podido).</param>
/// <param name="Publicada">La última versión publicada, si se ha podido saber.</param>
public sealed record ResultadoComprobacion(VersionPublicada? Nueva, string? Fallo, Version? Publicada = null);

/// <summary>
/// Comprueba en GitHub si hay una versión nueva de Calíope y la instala sin preguntas: descarga el instalador,
/// lo lanza en modo silencioso y cierra la aplicación; el instalador la vuelve a abrir al terminar.
/// </summary>
public sealed class ServicioActualizaciones(ServicioAjustes ajustes)
{
    /// <summary>
    /// Con Calíope abierta, se vuelve a mirar cada 3 horas. Al abrirla se mira siempre: antes solo si habían pasado
    /// 20 horas, y una versión publicada por la mañana no se veía hasta el día siguiente.
    /// </summary>
    public static readonly TimeSpan Intervalo = TimeSpan.FromHours(3);

    // Antes que Http: los campos estáticos se inicializan en orden y CrearHttp necesita la versión.
    public static Version VersionActual { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    private static readonly HttpClient Http = CrearHttp();

    /// <summary>
    /// Solo una copia instalada con el instalador puede actualizarse sola (una compilada a mano no tiene desinstalador
    /// al lado: actualizarla crearía una segunda instalación).
    /// </summary>
    public static bool PuedeActualizarseSola =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe").Any();

    /// <summary>La versión nueva publicada, o null si no la hay (o están desactivadas, o no se ha podido comprobar).</summary>
    public async Task<VersionPublicada?> ComprobarAsync(bool forzar, CancellationToken ct = default)
    {
        if (!forzar && !ajustes.Actual.BuscarActualizaciones)
            return null;
        // Al arrancar, un fallo no se avisa (sin conexión no es un problema); en «Buscar ahora» sí: ver ComprobarAhoraAsync.
        return (await ComprobarAhoraAsync(ct)).Nueva;
    }

    /// <summary>
    /// Comprueba ya y dice qué ha pasado: hay una nueva, estás al día, o no se ha podido saber (y por qué). Antes un fallo
    /// se mostraba como «Tienes la última versión», y con una versión antigua eso era falso.
    /// </summary>
    public async Task<ResultadoComprobacion> ComprobarAhoraAsync(CancellationToken ct = default)
    {
        try
        {
            using var respuesta = await Http.GetAsync(Actualizaciones.UrlUltimaVersion, ct);
            if (!respuesta.IsSuccessStatusCode)
                return new ResultadoComprobacion(null, Motivo(respuesta));
            var json = await respuesta.Content.ReadAsStringAsync(ct);
            ajustes.Guardar(ajustes.Actual with { UltimaComprobacion = DateTimeOffset.Now });
            if (Actualizaciones.Leer(json) is not { } v)
                return new ResultadoComprobacion(null, T("GitHub no ha devuelto ninguna versión publicada"));
            return new ResultadoComprobacion(Actualizaciones.EsMasNueva(v, VersionActual) ? v : null, null, v.Version);
        }
        catch (HttpRequestException e)
        {
            return new ResultadoComprobacion(null, T("no hay conexión con GitHub ({0})", e.InnerException?.Message ?? e.Message));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ResultadoComprobacion(null, T("GitHub no ha respondido a tiempo"));
        }
        catch (System.Text.Json.JsonException)
        {
            return new ResultadoComprobacion(null, T("la respuesta de GitHub no se entiende"));
        }
    }

    private static string Motivo(HttpResponseMessage r)
    {
        // Sin cuenta, GitHub deja 60 consultas por hora a cada red: en una oficina se gastan entre todos.
        if ((int)r.StatusCode is 403 or 429 && r.Headers.TryGetValues("X-RateLimit-Remaining", out var quedan) && quedan.FirstOrDefault() == "0")
        {
            var hora = r.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime().ToString("HH:mm")
                : null;
            return hora is null
                ? T("GitHub ha limitado las consultas desde tu red por un rato")
                : T("GitHub ha limitado las consultas desde tu red hasta las {0}", hora);
        }
        return T("GitHub ha respondido con un error ({0} {1})", (int)r.StatusCode, r.ReasonPhrase ?? "");
    }

    /// <summary>Descarga el instalador a una carpeta temporal y devuelve su ruta.</summary>
    public async Task<string> DescargarAsync(VersionPublicada version, IProgress<double> progreso, CancellationToken ct = default)
    {
        var url = version.Instalador ?? throw new InvalidOperationException(T("Esta versión no trae instalador."));
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
