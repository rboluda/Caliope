using System.IO;
using Constantino.Core.Doblaje;
using Constantino.Core.Instalaciones;
using Constantino.Core.Motores;
using static Constantino.Core.Idioma;

namespace Constantino.App.Servicios;

/// <summary>
/// El doblaje con los componentes reales: separador, Whisper y motor castellano (por el servicio de motores), el
/// traductor local (un llama-server que se arranca para traducir y se cierra al acabar) y FFmpeg.
/// </summary>
public sealed class ServicioDoblaje(ServicioInstalacion instalacion, ServicioMotor motor, ServicioVoces voces)
{
    /// <summary>Lo que hay que tener instalado para doblar, con su nombre para pedirlo.</summary>
    public static readonly string[] Necesarios = ["nucleo", ServicioMotor.ModeloWhisper, "chatterbox", ServicioMotor.ComponenteSeparador, "traductor"];

    public const string Modelo = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf";

    public async Task<AlmacenDoblajes> AlmacenAsync() => new((await instalacion.InstaladorAsync()).Rutas.Doblajes);

    /// <summary>Los componentes que faltan para doblar (sus nombres), o ninguno.</summary>
    public async Task<IReadOnlyList<string>> FaltanAsync()
    {
        var instalador = await instalacion.InstaladorAsync();
        return Necesarios.Where(id => !instalador.EstaInstalado(id)).Select(id => instalacion.Catalogo[id].Nombre).ToList();
    }

    public async Task<double> DuracionAsync(string video, CancellationToken ct = default)
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        var ffprobe = Path.Combine(Path.GetDirectoryName(rutas.Ffmpeg)!, "ffprobe.exe");
        var r = await new EjecutorProcesos().EjecutarAsync(ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", video],
            new Dictionary<string, string>(), _ => { }, ct);
        return double.TryParse(r.Salida.Trim().Split('\n')[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? s
            : throw new InvalidOperationException(T("No se ha podido leer el vídeo: {0}", Path.GetFileName(video)));
    }

    /// <summary>La grabación de una voz de «Mis voces» (la predeterminada si no se indica o ya no existe).</summary>
    public async Task<string> RutaVozAsync(string? id)
    {
        var biblioteca = await voces.BibliotecaAsync();
        var todas = biblioteca.Listar();
        var elegida = todas.FirstOrDefault(v => v.Id == id) ?? todas.FirstOrDefault(v => v.Id == voces.Predeterminada)
                      ?? todas.FirstOrDefault(v => v.Idioma == "Spanish") ?? todas.FirstOrDefault()
                      ?? throw new InvalidOperationException(T("Para doblar hace falta una voz en castellano en «Voces»: su pronunciación es la que se usa."));
        return biblioteca.ReferenciaDe(elegida.Id);
    }

    /// <summary>Un doblador listo para usar; desecharlo cierra el traductor.</summary>
    public async Task<Sesion> AbrirAsync()
    {
        var instalador = await instalacion.InstaladorAsync();
        var rutas = instalador.Rutas;
        var traductor = new TraductorLocal(Path.Combine(rutas.Herramientas, "llama", "llama-server.exe"),
            Path.Combine(rutas.Modelos, "traductor", Modelo), rutas.UsaGpu);
        var doblador = new Doblador(new AlmacenDoblajes(rutas.Doblajes), new Motores(motor),
            new TraductorSinMotor(traductor, motor), new AudioDoblajeFfmpeg(rutas.Ffmpeg, new EjecutorProcesos()));
        return new Sesion(doblador, traductor);
    }

    public sealed class Sesion(Doblador doblador, TraductorLocal traductor) : IAsyncDisposable
    {
        public Doblador Doblador { get; } = doblador;

        public ValueTask DisposeAsync() => traductor.DisposeAsync();
    }

    private sealed class Motores(ServicioMotor motor) : IMotoresDoblaje
    {
        public Task SepararAsync(string audio, string voces, string fondo, CancellationToken ct) =>
            motor.SepararAsync(audio, voces, fondo, ct);

        public async Task<IReadOnlyList<TramoTranscrito>> TranscribirAsync(string voces, string idioma, CancellationToken ct) =>
            (await motor.TranscribirTramosAsync(voces, idioma, ct)).Frases;

        public async Task<double> SintetizarAsync(string texto, string pronunciacion, string timbre, string salida, int semilla,
            CancellationToken ct) =>
            (await motor.SintetizarDoblajeAsync(texto, pronunciacion, timbre, salida, semilla, ct)).Duracion;
    }

    /// <summary>El traductor necesita la tarjeta: antes de usarlo se apaga el motor de voz que hubiera.</summary>
    private sealed class TraductorSinMotor(ITraductorDoblaje traductor, ServicioMotor motor) : ITraductorDoblaje
    {
        public async Task<IReadOnlyDictionary<int, string>> TraducirAsync(IReadOnlyList<FraseATraducir> frases, CancellationToken ct)
        {
            await motor.ApagarAsync();
            return await traductor.TraducirAsync(frases, ct);
        }

        public Task<string> AcortarAsync(string original, string traduccion, int maxCaracteres, CancellationToken ct) =>
            traductor.AcortarAsync(original, traduccion, maxCaracteres, ct);
    }
}
