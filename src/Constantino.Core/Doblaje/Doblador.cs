using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Constantino.Core.Instalaciones;
using Constantino.Core.Motores;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Doblaje;

/// <summary>Los motores que necesita el doblaje (separador, Whisper y motor castellano).</summary>
public interface IMotoresDoblaje
{
    Task SepararAsync(string audio, string voces, string fondo, CancellationToken ct);
    Task<IReadOnlyList<TramoTranscrito>> TranscribirAsync(string voces, string idioma, CancellationToken ct);

    /// <summary>Genera <paramref name="texto"/> con la pronunciación de una voz y el timbre de otra; devuelve su duración.</summary>
    Task<double> SintetizarAsync(string texto, string pronunciacion, string timbre, string salida, int semilla, CancellationToken ct);
}

public sealed record FraseATraducir(int Id, string Original, int MaxCaracteres);

public interface ITraductorDoblaje
{
    /// <summary>Traduce todas las frases (con el resto del guion como contexto); devuelve id → traducción.</summary>
    Task<IReadOnlyDictionary<int, string>> TraducirAsync(IReadOnlyList<FraseATraducir> frases, CancellationToken ct);

    Task<string> AcortarAsync(string original, string traduccion, int maxCaracteres, CancellationToken ct);
}

/// <summary>Lo que el doblaje hace con FFmpeg.</summary>
public interface IAudioDoblaje
{
    Task ExtraerAudioAsync(string video, string wav, CancellationToken ct);
    Task RecortarAsync(string wav, double inicio, double fin, string salida, CancellationToken ct);
    Task CambiarTempoAsync(string wav, string salida, double tempo, CancellationToken ct);

    /// <summary>Fondo + cada frase en su instante (+ la voz original muy baja, si se indica), de la duración del vídeo.</summary>
    Task MontarAsync(string fondo, string? vozOriginal, IReadOnlyList<(double Inicio, string Archivo)> frases, double duracion,
        string salida, CancellationToken ct);

    /// <summary>El vídeo original con la pista de audio nueva (la imagen se copia sin recodificar).</summary>
    Task UnirAsync(string video, string audio, string salida, CancellationToken ct);
}

public sealed record EventoDoblaje(string Mensaje, double? Fraccion = null);

public sealed record ResultadoExportacion(string Video, string Audio, string Subtitulos);

/// <summary>
/// El doblaje de un vídeo de punta a punta: extraer el audio → separar voz y fondo → transcribir por frases →
/// elegir el tramo de la voz original para su timbre → traducir → generar cada frase con la pronunciación de una
/// voz castellana nativa y el timbre original → encajarla en su hueco → montar y exportar.
/// Todo se guarda al acabar cada paso y lo ya hecho no se repite (las frases, por su huella).
/// </summary>
public sealed class Doblador(AlmacenDoblajes almacen, IMotoresDoblaje motores, ITraductorDoblaje traductor, IAudioDoblaje audio)
{
    /// <summary>Caracteres por segundo del motor castellano (medido: ~13–14): el presupuesto de cada frase.</summary>
    public const double CaracteresPorSegundo = 14;
    /// <summary>Acelerar más se nota artificial.</summary>
    public const double TempoMaximo = 1.25;
    /// <summary>Respiro antes de la frase siguiente.</summary>
    public const double Respiro = 0.1;
    public const int IntentosAcortar = 2;
    public const int Semilla = 1234;

    public string AudioOriginal(ProyectoDoblaje p) => Path.Combine(almacen.Trabajo(p.Id), "audio.wav");
    public string Voces(ProyectoDoblaje p) => Path.Combine(almacen.Trabajo(p.Id), "voces.wav");
    public string Fondo(ProyectoDoblaje p) => Path.Combine(almacen.Trabajo(p.Id), "fondo.wav");
    public string Referencia(ProyectoDoblaje p, int hablante) => Path.Combine(almacen.Trabajo(p.Id), $"referencia_{hablante}.wav");
    public string ArchivoFrase(ProyectoDoblaje p, int id) => Path.Combine(almacen.Frases(p.Id), $"{id:000}.wav");

    /// <summary>Hasta dónde puede llegar cada frase: el comienzo de la siguiente (o el final del vídeo), menos un respiro.</summary>
    public static double HuecoDisponible(ProyectoDoblaje p, FraseDoblaje f)
    {
        var siguiente = p.ListaFrases.Where(x => x.Inicio > f.Inicio).Select(x => x.Inicio).DefaultIfEmpty(p.Duracion).Min();
        return Math.Max(0.3, siguiente - f.Inicio - Respiro);
    }

    public static int MaxCaracteres(double hueco) => Math.Max(8, (int)(hueco * CaracteresPorSegundo));

    /// <summary>Cuánto hay que acelerar una frase de <paramref name="duracion"/> para que quepa, y cómo encaja.</summary>
    public static (double Tempo, EncajeFrase Encaje) Encajar(double duracion, double hueco)
    {
        if (duracion <= hueco)
            return (1.0, EncajeFrase.Cabe);
        var necesario = duracion / hueco;
        return necesario <= TempoMaximo ? (necesario, EncajeFrase.Justa) : (TempoMaximo, EncajeFrase.NoCabe);
    }

    /// <summary>
    /// Prepara el doblaje hasta tener el guion traducido: lo hecho antes no se repite.
    /// <paramref name="vozPronunciacion"/> es el id de la voz de «Mis voces» que se asigna a los hablantes nuevos.
    /// </summary>
    public async Task<ProyectoDoblaje> PrepararAsync(ProyectoDoblaje p, string? vozPronunciacion, IProgress<EventoDoblaje> progreso,
        CancellationToken ct)
    {
        Directory.CreateDirectory(almacen.Trabajo(p.Id));
        if (p.Fase < FaseDoblaje.Separado || !File.Exists(Voces(p)) || !File.Exists(Fondo(p)))
        {
            progreso.Report(new EventoDoblaje(T("Sacando el audio del vídeo"), 0.02));
            if (!File.Exists(AudioOriginal(p)))
                await audio.ExtraerAudioAsync(p.Video, AudioOriginal(p), ct).ConfigureAwait(false);
            progreso.Report(new EventoDoblaje(T("Separando la voz de la música y los efectos"), 0.1));
            await motores.SepararAsync(AudioOriginal(p), Voces(p), Fondo(p), ct).ConfigureAwait(false);
            p = almacen.Guardar(p with { Fase = FaseDoblaje.Separado });
        }

        if (p.Fase < FaseDoblaje.Transcrito)
        {
            progreso.Report(new EventoDoblaje(T("Escuchando lo que se dice, frase a frase"), 0.3));
            var tramos = await motores.TranscribirAsync(Voces(p), p.IdiomaOrigen, ct).ConfigureAwait(false);
            if (tramos.Count == 0)
                throw new InvalidOperationException(T("No se ha encontrado ninguna voz en el vídeo."));
            var frases = tramos.Select((t, i) => new FraseDoblaje(i + 1, 1, t.Inicio, t.Fin, t.Texto)).ToList();
            var (ini, fin) = ElegirReferencia(frases);
            var hablantes = new List<HablanteDoblaje> { new(1, T("Hablante {0}", 1), vozPronunciacion, ini, fin) };
            p = almacen.Guardar(p with { Fase = FaseDoblaje.Transcrito, Frases = frases, Hablantes = hablantes });
        }

        foreach (var h in p.ListaHablantes)
        {
            if (!File.Exists(Referencia(p, h.Id)))
                await audio.RecortarAsync(Voces(p), h.ReferenciaInicio, h.ReferenciaFin, Referencia(p, h.Id), ct).ConfigureAwait(false);
        }

        if (p.Fase < FaseDoblaje.Traducido || p.ListaFrases.Any(f => string.IsNullOrWhiteSpace(f.Traduccion)))
        {
            progreso.Report(new EventoDoblaje(T("Traduciendo el guion al castellano"), 0.5));
            var pendientes = p.ListaFrases.Where(f => string.IsNullOrWhiteSpace(f.Traduccion))
                .Select(f => new FraseATraducir(f.Id, f.Original, MaxCaracteres(HuecoDisponible(p, f)))).ToList();
            var traducidas = await traductor.TraducirAsync(pendientes, ct).ConfigureAwait(false);
            p = p with
            {
                Fase = FaseDoblaje.Traducido,
                Frases = p.ListaFrases.Select(f => traducidas.TryGetValue(f.Id, out var es) && !string.IsNullOrWhiteSpace(es)
                    ? f with { Traduccion = es.Trim() }
                    : f).ToList(),
            };
            p = almacen.Guardar(p);
        }
        progreso.Report(new EventoDoblaje(T("Guion listo"), 1));
        return p;
    }

    /// <summary>
    /// El tramo de la voz original para su timbre: la frase más larga, alargada con las siguientes si están seguidas,
    /// hasta unos 12 segundos.
    /// </summary>
    public static (double Inicio, double Fin) ElegirReferencia(IReadOnlyList<FraseDoblaje> frases)
    {
        var mejor = (Inicio: 0.0, Fin: 0.0);
        for (var i = 0; i < frases.Count; i++)
        {
            var inicio = frases[i].Inicio;
            var fin = frases[i].Fin;
            for (var j = i + 1; j < frases.Count && frases[j].Inicio - fin < 0.6 && frases[j].Fin - inicio <= 12; j++)
                fin = frases[j].Fin;
            fin = Math.Min(fin, inicio + 12);
            if (fin - inicio > mejor.Fin - mejor.Inicio)
                mejor = (inicio, fin);
        }
        return mejor;
    }

    private static string HuellaDe(FraseDoblaje f, string pronunciacion, string timbre) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{f.Traduccion}\n{pronunciacion}@{Marca(pronunciacion)}\n{timbre}@{Marca(timbre)}\n{Semilla}")))[..16];

    private static string Marca(string archivo) => File.Exists(archivo) ? File.GetLastWriteTimeUtc(archivo).Ticks.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Genera las frases que faltan o han cambiado (o solo <paramref name="solo"/>, aunque no hayan cambiado).
    /// <paramref name="rutaVoz"/> da el archivo de referencia de una voz de «Mis voces» por su id.
    /// </summary>
    public async Task<ProyectoDoblaje> GenerarAsync(ProyectoDoblaje p, Func<string?, string> rutaVoz, IReadOnlyCollection<int>? solo,
        IProgress<EventoDoblaje> progreso, CancellationToken ct)
    {
        Directory.CreateDirectory(almacen.Frases(p.Id));
        var hablantes = p.ListaHablantes.ToDictionary(h => h.Id);
        var todas = p.ListaFrases.Where(f => !string.IsNullOrWhiteSpace(f.Traduccion)).ToList();
        for (var k = 0; k < todas.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var f = p.ListaFrases.First(x => x.Id == todas[k].Id);
            var pronunciacion = rutaVoz(hablantes.GetValueOrDefault(f.Hablante)?.VozPronunciacion);
            var timbre = Referencia(p, f.Hablante);
            var final = ArchivoFrase(p, f.Id);
            var forzada = solo?.Contains(f.Id) == true;
            if (!forzada && (solo is not null || (f.Huella == HuellaDe(f, pronunciacion, timbre) && File.Exists(final))))
                continue;
            progreso.Report(new EventoDoblaje(T("Doblando la frase {0} de {1}", k + 1, todas.Count), (double)k / todas.Count));

            var hueco = HuecoDisponible(p, f);
            var bruto = Path.Combine(almacen.Frases(p.Id), $"{f.Id:000}_bruto.wav");
            var duracion = await motores.SintetizarAsync(f.Traduccion, pronunciacion, timbre, bruto, Semilla, ct).ConfigureAwait(false);
            // Si no cabe ni acelerando al máximo, la IA propone una frase más corta (hasta dos veces).
            for (var intento = 0; intento < IntentosAcortar && duracion / hueco > TempoMaximo; intento++)
            {
                var corta = (await traductor.AcortarAsync(f.Original, f.Traduccion, MaxCaracteres(hueco), ct).ConfigureAwait(false)).Trim();
                if (corta.Length == 0 || corta.Length >= f.Traduccion.Length)
                    break;
                f = f with { Traduccion = corta };
                duracion = await motores.SintetizarAsync(f.Traduccion, pronunciacion, timbre, bruto, Semilla, ct).ConfigureAwait(false);
            }
            var (tempo, encaje) = Encajar(duracion, hueco);
            if (tempo > 1.0)
                await audio.CambiarTempoAsync(bruto, final, tempo, ct).ConfigureAwait(false);
            else
                File.Copy(bruto, final, overwrite: true);
            File.Delete(bruto);
            f = f with
            {
                Encaje = encaje, Tempo = Math.Round(tempo, 3), Duracion = Math.Round(duracion / tempo, 2),
                Huella = HuellaDe(f, pronunciacion, timbre),
            };
            p = almacen.Guardar(p.ConFrase(f));
        }
        progreso.Report(new EventoDoblaje(T("Frases dobladas"), 1));
        return p;
    }

    /// <summary>Monta el audio doblado y exporta el vídeo, el audio y los subtítulos en castellano.</summary>
    public async Task<ResultadoExportacion> ExportarAsync(ProyectoDoblaje p, IProgress<EventoDoblaje> progreso, CancellationToken ct)
    {
        var listas = p.ListaFrases.Where(f => f.Encaje != EncajeFrase.SinGenerar && File.Exists(ArchivoFrase(p, f.Id))).ToList();
        if (listas.Count == 0)
            throw new InvalidOperationException(T("Todavía no hay ninguna frase doblada."));
        var salida = almacen.Salida(p.Id);
        Directory.CreateDirectory(salida);
        var nombre = Textos.NombreDeArchivo(p.Nombre, "doblaje");
        var wav = Path.Combine(salida, $"{nombre} (castellano).wav");
        var mp4 = Path.Combine(salida, $"{nombre} (castellano).mp4");
        var srt = Path.Combine(salida, $"{nombre} (castellano).srt");
        progreso.Report(new EventoDoblaje(T("Mezclando las voces con el fondo"), 0.2));
        await audio.MontarAsync(Fondo(p), p.VozOriginalDeFondo ? Voces(p) : null,
            listas.Select(f => (f.Inicio, ArchivoFrase(p, f.Id))).ToList(), p.Duracion, wav, ct).ConfigureAwait(false);
        progreso.Report(new EventoDoblaje(T("Creando el vídeo doblado"), 0.6));
        await audio.UnirAsync(p.Video, wav, mp4, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(srt, Subtitulos(listas), ct).ConfigureAwait(false);
        progreso.Report(new EventoDoblaje(T("Doblaje exportado"), 1));
        return new ResultadoExportacion(mp4, wav, srt);
    }

    /// <summary>Subtítulos SRT con la traducción, en los tiempos de cada frase doblada.</summary>
    public static string Subtitulos(IEnumerable<FraseDoblaje> frases)
    {
        static string Tiempo(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        var n = 1;
        foreach (var f in frases.OrderBy(f => f.Inicio))
        {
            var fin = f.Inicio + Math.Max(f.Duracion ?? f.Hueco, 0.5);
            sb.Append(n++).Append("\r\n").Append(Tiempo(f.Inicio)).Append(" --> ").Append(Tiempo(fin)).Append("\r\n")
              .Append(f.Traduccion).Append("\r\n\r\n");
        }
        return sb.ToString();
    }
}

/// <summary>El doblaje con FFmpeg (y su filtro rubberband para acelerar sin cambiar el tono).</summary>
public sealed class AudioDoblajeFfmpeg(string ffmpeg, IEjecutorProcesos ejecutor) : IAudioDoblaje
{
    /// <summary>Volumen de la voz original cuando se deja de fondo.</summary>
    public const double VolumenOriginal = 0.12;

    public Task ExtraerAudioAsync(string video, string wav, CancellationToken ct) =>
        Ejecutar(["-y", "-i", video, "-vn", "-ac", "2", "-ar", "44100", wav], ct);

    public Task RecortarAsync(string wav, double inicio, double fin, string salida, CancellationToken ct) =>
        Ejecutar(["-y", "-ss", Num(inicio), "-to", Num(fin), "-i", wav, "-ac", "1", "-ar", "24000", "-af", "loudnorm=I=-20:TP=-2", salida], ct);

    public Task CambiarTempoAsync(string wav, string salida, double tempo, CancellationToken ct) =>
        Ejecutar(["-y", "-i", wav, "-af", $"rubberband=tempo={Num(tempo)}:formant=preserved", salida], ct);

    public async Task MontarAsync(string fondo, string? vozOriginal, IReadOnlyList<(double Inicio, string Archivo)> frases, double duracion,
        string salida, CancellationToken ct)
    {
        // Muchas frases: se mezclan por tandas en una pista de voz y luego con el fondo (FFmpeg admite pocas entradas).
        var voz = salida + ".voz.wav";
        var tandas = frases.Chunk(48).ToList();
        var parciales = new List<string>();
        for (var t = 0; t < tandas.Count; t++)
        {
            var parcial = $"{salida}.voz{t}.wav";
            var argumentos = new List<string> { "-y" };
            var filtros = new List<string>();
            var etiquetas = new StringBuilder();
            for (var i = 0; i < tandas[t].Length; i++)
            {
                argumentos.AddRange(["-i", tandas[t][i].Archivo]);
                filtros.Add($"[{i}:a]aresample=44100,aformat=channel_layouts=stereo,adelay={(int)Math.Round(tandas[t][i].Inicio * 1000)}:all=1[v{i}]");
                etiquetas.Append($"[v{i}]");
            }
            filtros.Add($"{etiquetas}amix=inputs={tandas[t].Length}:normalize=0,apad=whole_dur={Num(duracion)}[m]");
            argumentos.AddRange(["-filter_complex", string.Join(";", filtros), "-map", "[m]", "-t", Num(duracion), parcial]);
            await Ejecutar([.. argumentos], ct).ConfigureAwait(false);
            parciales.Add(parcial);
        }
        try
        {
            var entradas = new List<string> { "-y", "-t", Num(duracion), "-i", fondo };
            var etiquetas = new StringBuilder("[0:a]");
            var filtros = new List<string>();
            var n = 1;
            if (vozOriginal is not null)
            {
                entradas.AddRange(["-t", Num(duracion), "-i", vozOriginal]);
                filtros.Add($"[{n}:a]volume={Num(VolumenOriginal)}[o]");
                etiquetas.Append("[o]");
                n++;
            }
            foreach (var parcial in parciales)
            {
                entradas.AddRange(["-i", parcial]);
                etiquetas.Append($"[{n++}:a]");
            }
            filtros.Add($"{etiquetas}amix=inputs={n}:normalize=0:duration=first,alimiter=limit=0.95[mezcla]");
            entradas.AddRange(["-filter_complex", string.Join(";", filtros), "-map", "[mezcla]", salida]);
            await Ejecutar([.. entradas], ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var parcial in parciales)
                File.Delete(parcial);
            File.Delete(voz);
        }
    }

    public Task UnirAsync(string video, string audio, string salida, CancellationToken ct) =>
        Ejecutar(["-y", "-i", video, "-i", audio, "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-shortest", salida], ct);

    private static string Num(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);

    private async Task Ejecutar(string[] argumentos, CancellationToken ct)
    {
        var r = await ejecutor.EjecutarAsync(ffmpeg, ["-hide_banner", "-nostdin", .. argumentos],
            new Dictionary<string, string>(), _ => { }, ct).ConfigureAwait(false);
        if (r.CodigoSalida != 0)
            throw new InvalidOperationException(T("ffmpeg ha fallado: {0}", r.Salida.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()));
    }
}
