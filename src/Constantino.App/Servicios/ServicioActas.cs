using System.Globalization;
using System.IO;
using Constantino.Core.Actas;
using Constantino.Core.IA;
using Constantino.Core.Instalaciones;
using Constantino.Core.Motores;
using static Constantino.Core.Idioma;

namespace Constantino.App.Servicios;

/// <summary>
/// Las actas con los componentes reales: FFmpeg, Whisper y la huella de voz del motor principal (por el servicio de
/// motores) y la IA local (un llama-server que se arranca para redactar y se cierra al acabar).
/// </summary>
public sealed class ServicioActas(ServicioInstalacion instalacion, ServicioMotor motor)
{
    /// <summary>Lo que hay que tener instalado (además de una voz del motor principal, por su huella de voz).</summary>
    public static readonly string[] Necesarios = ["nucleo", "motor-qwen", ServicioMotor.ModeloWhisper, "traductor"];

    /// <summary>Tokens de contexto de la IA: una reunión de hora y media cabe de una vez.</summary>
    public const int Contexto = 32768;

    public async Task<AlmacenActas> AlmacenAsync() => new((await instalacion.InstaladorAsync()).Rutas.Actas);

    /// <summary>Los componentes que faltan (sus nombres), o ninguno.</summary>
    public async Task<IReadOnlyList<string>> FaltanAsync()
    {
        var instalador = await instalacion.InstaladorAsync();
        var faltan = Necesarios.Where(id => !instalador.EstaInstalado(id)).Select(id => instalacion.Catalogo[id].Nombre).ToList();
        if (await motor.ModeloBaseAsync() is null)
            faltan.Add(instalacion.Catalogo["modelo-qwen-base-1.7b"].Nombre);
        return faltan;
    }

    public async Task<double> DuracionAsync(string grabacion, CancellationToken ct = default)
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        var ffprobe = Path.Combine(Path.GetDirectoryName(rutas.Ffmpeg)!, "ffprobe.exe");
        var r = await new EjecutorProcesos().EjecutarAsync(ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", grabacion],
            new Dictionary<string, string>(), _ => { }, ct);
        return double.TryParse(r.Salida.Trim().Split('\n')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? s
            : throw new InvalidOperationException(T("No se ha podido leer la grabación: {0}", Path.GetFileName(grabacion)));
    }

    /// <summary>Un trozo del audio de la reunión, para escucharlo (se guarda en «trabajo» y se reutiliza).</summary>
    public async Task<string?> TrozoAsync(AlmacenActas almacen, ProyectoActa p, double inicio, double fin, CancellationToken ct = default)
    {
        var audio = almacen.Audio(p.Id);
        if (!File.Exists(audio))
            return null;
        var salida = Path.Combine(almacen.Trabajo(p.Id), $"trozo_{inicio.ToString("0.00", CultureInfo.InvariantCulture)}_{fin.ToString("0.00", CultureInfo.InvariantCulture)}.wav");
        if (!File.Exists(salida))
        {
            var rutas = (await instalacion.InstaladorAsync()).Rutas;
            await new EjecutorProcesos().EjecutarAsync(rutas.Ffmpeg, ["-hide_banner", "-nostdin", "-y", "-ss", Num(inicio), "-to", Num(fin), "-i", audio, salida],
                new Dictionary<string, string>(), _ => { }, ct);
        }
        return File.Exists(salida) ? salida : null;
    }

    private static string Num(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Un secretario listo para usar; desecharlo cierra la IA.</summary>
    public async Task<Sesion> AbrirAsync()
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        var ia = new ServidorIA(Path.Combine(rutas.Herramientas, "llama", "llama-server.exe"),
            Path.Combine(rutas.Modelos, "traductor", ServicioDoblaje.Modelo), rutas.UsaGpu, Contexto);
        var secretario = new Secretario(new AlmacenActas(rutas.Actas), new Motores(motor, rutas.Ffmpeg), new IASinMotor(ia, motor));
        return new Sesion(secretario, ia);
    }

    public sealed class Sesion(Secretario secretario, ServidorIA ia) : IAsyncDisposable
    {
        public Secretario Secretario { get; } = secretario;

        public ValueTask DisposeAsync() => ia.DisposeAsync();
    }

    private sealed class Motores(ServicioMotor motor, string ffmpeg) : IMotoresActa
    {
        public async Task ExtraerAudioAsync(string grabacion, string wav, CancellationToken ct)
        {
            var r = await new EjecutorProcesos().EjecutarAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-i", grabacion, "-vn", "-ac", "1", "-ar", "16000", wav], new Dictionary<string, string>(), _ => { }, ct);
            if (r.CodigoSalida != 0)
                throw new InvalidOperationException(T("ffmpeg ha fallado: {0}", r.Salida.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()));
        }

        public async Task<IReadOnlyList<TramoTranscrito>> TranscribirAsync(string wav, CancellationToken ct) =>
            (await motor.TranscribirReunionAsync(wav, ct)).Frases;

        public async Task<IReadOnlyList<int>> HablantesAsync(string wav, IReadOnlyList<TramoTranscrito> tramos, int? personas, CancellationToken ct) =>
            (await motor.HablantesReunionAsync(wav, tramos, personas, ct)).Hablantes;
    }

    /// <summary>La IA necesita la tarjeta: antes de preguntarle se apaga el motor de voz que hubiera.</summary>
    private sealed class IASinMotor(IModeloLenguaje ia, ServicioMotor motor) : IModeloLenguaje
    {
        public async Task<string> PreguntarAsync(string sistema, string pedido, int maxTokens, bool json, CancellationToken ct,
            double temperatura = 0.3)
        {
            await motor.ApagarAsync();
            return await ia.PreguntarAsync(sistema, pedido, maxTokens, json, ct, temperatura);
        }
    }
}
