using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Constantino.Core.Audio;
using Constantino.Core.Instalaciones;

namespace Constantino.Core.Narracion;

/// <summary>Quien convierte texto en voz para el narrador (en la app, el motor Qwen con la voz del proyecto).</summary>
public interface ISintetizador
{
    /// <summary>Cambia si cambia la voz: entra en la huella de los bloques.</summary>
    string FirmaVoz { get; }

    /// <summary>Modelo con el que se genera: también entra en la huella.</summary>
    string Modelo { get; }

    /// <summary>
    /// Ids de las variantes de la voz (susurro, tenso…). Sus etiquetas se reconocen en el guion; si existe «susurro»,
    /// el susurro no se simula bajando el volumen.
    /// </summary>
    IReadOnlyCollection<string> Variantes { get; }

    /// <summary>Lee <paramref name="texto"/> con la voz (o su <paramref name="variante"/>) y devuelve la ruta de un WAV.</summary>
    Task<string> SintetizarAsync(string texto, string? variante, AjustesNarracion ajustes, string salida, CancellationToken ct);
}

/// <summary>Volumen y formato final (en la app, ffmpeg).</summary>
public interface IProcesadorAudio
{
    /// <summary>
    /// Ajusta la sonoridad a <see cref="AjustesNarracion.Lufs"/> midiendo sobre <paramref name="medida"/>
    /// (la versión sin atenuar los susurros) y escribe un WAV a la frecuencia de salida.
    /// </summary>
    Task NormalizarAsync(string entrada, string medida, string salidaWav, AjustesNarracion ajustes, CancellationToken ct);

    /// <summary>Convierte un WAV al formato de salida (MP3 o WAV).</summary>
    Task CodificarAsync(string wav, string destino, AjustesNarracion ajustes, CancellationToken ct);
}

public enum TipoEventoNarracion
{
    Bloque,
    Frase,
    BloqueListo,
    BloqueSinCambios,
    Montando,
}

/// <param name="Fraccion">Avance global, de 0 a 1.</param>
public sealed record EventoNarracion(TipoEventoNarracion Tipo, int Bloque, int TotalBloques, string Mensaje, double Fraccion);

public sealed record ResultadoNarracion(Proyecto Proyecto, int Generados, int SinCambios, string? Completo, string Manifiesto);

/// <summary>
/// Genera los bloques de un proyecto: frase a frase con el sintetizador, recorta silencios, monta pausas y
/// susurros, normaliza y codifica. Los bloques cuya huella no ha cambiado se conservan. Al final une todo en un
/// archivo completo y escribe un manifiesto con la duración y el inicio de cada bloque para el montaje de vídeo.
/// </summary>
public sealed partial class Narrador(AlmacenProyectos almacen, ISintetizador sintetizador, IProcesadorAudio procesador)
{
    /// <summary>Letras por segundo habituales: para detectar frases que el modelo ha cortado o alargado.</summary>
    private const double LetrasPorSegundo = 13.5;
    private const string CarpetaNormalizados = ".normalizados", CarpetaTemporal = ".tmp";
    private const int FrecuenciaPorDefecto = 24000;

    public const string ArchivoManifiesto = "manifiesto.json";

    public static string Extension(AjustesNarracion a) => a.Formato == FormatoSalida.Wav ? "wav" : "mp3";

    public static string ArchivoCompleto(Proyecto p) => $"{p.Id}_completo.{Extension(p.Ajustes)}";

    public async Task<ResultadoNarracion> NarrarAsync(Proyecto proyecto, string guion, IReadOnlySet<int>? solo, bool forzar,
        IProgress<EventoNarracion> progreso, CancellationToken ct)
    {
        var a = proyecto.Ajustes;
        var bloques = Guion.Leer(guion, a.Pausa, a.PausaLarga, sintetizador.Variantes);
        var salida = almacen.CarpetaSalida(proyecto.Id);
        var normalizados = Path.Combine(salida, CarpetaNormalizados);
        var temporal = Path.Combine(salida, CarpetaTemporal);
        Directory.CreateDirectory(normalizados);
        Directory.CreateDirectory(temporal);

        var previos = proyecto.Bloques.ToDictionary(b => b.Indice);
        var estados = new SortedDictionary<int, EstadoBloque>();
        int generados = 0, sinCambios = 0;
        // Para repartir la barra: cada bloque pesa según el texto que hay que leer.
        var pesos = bloques.Select(b => Math.Max(1, b.TextoLeido.Length)).ToList();
        var pesoTotal = (double)pesos.Sum();
        double hecho = 0;

        try
        {
            for (var i = 0; i < bloques.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var b = bloques[i];
                var huella = Huellas.DeBloque(b, a, sintetizador.FirmaVoz, sintetizador.Modelo);
                var final = Path.Combine(salida, $"{b.Archivo}.{Extension(a)}");
                var normalizado = Path.Combine(normalizados, $"{b.Archivo}.wav");
                previos.TryGetValue(b.Indice, out var previo);

                var noPedido = solo is not null && !solo.Contains(b.Indice);
                var vigente = previo is not null && previo.Huella == huella && File.Exists(final) && File.Exists(normalizado);
                if ((noPedido || !forzar) && vigente)
                {
                    estados[b.Indice] = previo!;
                    sinCambios++;
                    hecho += pesos[i];
                    progreso.Report(new EventoNarracion(TipoEventoNarracion.BloqueSinCambios, b.Indice, bloques.Count,
                        $"Bloque {b.Indice} sin cambios", hecho / pesoTotal));
                    continue;
                }
                if (noPedido)
                {
                    // No se pidió regenerarlo: se mantiene lo que hubiera (aunque esté desactualizado).
                    if (previo is not null)
                        estados[b.Indice] = previo;
                    hecho += pesos[i];
                    continue;
                }

                var inicioBloque = hecho;
                progreso.Report(new EventoNarracion(TipoEventoNarracion.Bloque, b.Indice, bloques.Count,
                    $"Generando el bloque {b.Indice} de {bloques.Count}", hecho / pesoTotal));
                var (montaje, medida, avisos) = await GenerarBloqueAsync(b, a, temporal, (fraccion, texto) =>
                    progreso.Report(new EventoNarracion(TipoEventoNarracion.Frase, b.Indice, bloques.Count, texto,
                        (inicioBloque + pesos[i] * fraccion) / pesoTotal)), ct).ConfigureAwait(false);

                var wav = Path.Combine(temporal, $"{b.Archivo}.wav");
                var wavMedida = Path.Combine(temporal, $"{b.Archivo}_medida.wav");
                montaje.EscribirWav(wav);
                medida.EscribirWav(wavMedida);
                await procesador.NormalizarAsync(wav, wavMedida, normalizado, a, ct).ConfigureAwait(false);
                await procesador.CodificarAsync(normalizado, final, a, ct).ConfigureAwait(false);

                // Si el bloque cambió de nombre, sobran los archivos con el nombre anterior.
                if (previo is not null && previo.Archivo != Path.GetFileName(final))
                    BorrarArchivosDe(salida, Path.GetFileNameWithoutExtension(previo.Archivo));

                // La duración se mide sobre el audio final: la velocidad la cambia.
                var duracion = Voces.BibliotecaVoces.DuracionWav(normalizado);
                estados[b.Indice] = new EstadoBloque(b.Indice, b.Nombre, huella, Path.GetFileName(final),
                    Math.Round(duracion, 2), [.. b.Avisos, .. avisos], DateTimeOffset.Now);
                generados++;
                hecho += pesos[i];
                progreso.Report(new EventoNarracion(TipoEventoNarracion.BloqueListo, b.Indice, bloques.Count,
                    $"Bloque {b.Indice} listo ({duracion:0.0} s)", hecho / pesoTotal));
            }
        }
        finally
        {
            // Lo generado hasta un fallo o una cancelación se guarda: no se repetirá.
            proyecto = almacen.Guardar(proyecto with { Bloques = Fusionar(proyecto.Bloques, estados, bloques.Count) });
        }

        // Bloques que ya no existen en el guion.
        foreach (var sobrante in previos.Values.Where(p => p.Indice > bloques.Count))
            BorrarArchivosDe(salida, Path.GetFileNameWithoutExtension(sobrante.Archivo));

        string? completo = null;
        JsonObject? infoCompleto = null;
        if (bloques.Count > 0 && estados.Count == bloques.Count)
        {
            progreso.Report(new EventoNarracion(TipoEventoNarracion.Montando, 0, bloques.Count, "Montando el audio completo", 1));
            (completo, infoCompleto) = await MontarCompletoAsync(proyecto, estados.Values.ToList(), normalizados, temporal, ct).ConfigureAwait(false);
        }
        var manifiesto = EscribirManifiesto(proyecto, bloques, estados, infoCompleto);
        try { Directory.Delete(temporal, recursive: true); }
        catch (IOException) { }
        return new ResultadoNarracion(proyecto, generados, sinCambios, completo, manifiesto);
    }

    private async Task<(Pcm Montaje, Pcm Medida, List<string> Avisos)> GenerarBloqueAsync(
        Bloque b, AjustesNarracion a, string temporal, Action<double, string> avance, CancellationToken ct)
    {
        var trozos = b.Eventos.SelectMany(e => e is EventoVoz v
                ? Guion.PartirEnFrases(v.Texto, a.MaxCaracteres).Select(t => (Evento)(v with { Texto = t }))
                : [e])
            .ToList();
        var totalLetras = Math.Max(1, trozos.OfType<EventoVoz>().Sum(v => v.Texto.Length));
        var letrasHechas = 0;
        var avisos = new List<string>();
        var partes = new List<(Pcm Audio, double Ganancia)>();
        int? frecuencia = null;
        var anteriorVoz = false;
        var gananciaSusurro = sintetizador.Variantes.Contains(Voces.CatalogoVariantes.Susurro) ? 0 : a.SusurroDb;

        foreach (var evento in trozos)
        {
            if (evento is EventoSilencio s)
            {
                partes.Add((Pcm.Silencio(s.Segundos, frecuencia ?? FrecuenciaPorDefecto), 0));
                anteriorVoz = false;
                continue;
            }
            var v = (EventoVoz)evento;
            avance((double)letrasHechas / totalLetras, $"Bloque {b.Indice}: «{Resumen(v.Texto)}»");
            var variante = v.Variante is { } id && sintetizador.Variantes.Contains(id) ? id : null;
            var ruta = await sintetizador.SintetizarAsync(v.Texto, variante, a,
                Path.Combine(temporal, $"{b.Archivo}_{partes.Count:000}.wav"), ct).ConfigureAwait(false);
            var audio = Pcm.LeerWav(ruta);
            if (a.RecortarSilencios)
                audio = audio.Recortar(a.UmbralSilencioDb, a.MargenRecorte);
            if (frecuencia is null)
            {
                frecuencia = audio.Frecuencia;
                // Los silencios añadidos antes de conocer la frecuencia del modelo se rehacen con la correcta.
                for (var i = 0; i < partes.Count; i++)
                    partes[i] = (Pcm.Silencio(partes[i].Audio.Segundos, audio.Frecuencia), 0);
            }
            var esperado = v.Texto.Length / LetrasPorSegundo;
            if (audio.Segundos > 2.5 * esperado + 1 || audio.Segundos < 0.4 * esperado)
                avisos.Add($"Duración anómala ({audio.Segundos:0.0} s, se esperaban ~{esperado:0.0} s): «{Resumen(v.Texto)}». Revisa cómo suena.");
            if (anteriorVoz)
                partes.Add((Pcm.Silencio(a.PausaEntreFrases, audio.Frecuencia), 0));
            partes.Add((audio, v.Susurro ? gananciaSusurro : 0));
            anteriorVoz = true;
            letrasHechas += v.Texto.Length;
        }
        var sr = frecuencia ?? FrecuenciaPorDefecto;
        Pcm Montar(bool conGanancia)
        {
            var piezas = new List<Pcm> { Pcm.Silencio(a.SilencioInicio, sr) };
            piezas.AddRange(partes.Select(p => conGanancia && p.Ganancia != 0 ? p.Audio.ConGanancia(p.Ganancia) : p.Audio));
            piezas.Add(Pcm.Silencio(a.SilencioFin, sr));
            return Pcm.Unir(piezas, sr);
        }
        avance(1, $"Bloque {b.Indice}: ajustando el volumen");
        return (Montar(true), Montar(false), avisos);
    }

    private async Task<(string, JsonObject)> MontarCompletoAsync(Proyecto p, IReadOnlyList<EstadoBloque> estados,
        string normalizados, string temporal, CancellationToken ct)
    {
        var a = p.Ajustes;
        var piezas = new List<Pcm>();
        var inicios = new JsonArray();
        double t = 0;
        foreach (var e in estados)
        {
            var audio = Pcm.LeerWav(Path.Combine(normalizados, Path.ChangeExtension(e.Archivo, ".wav")));
            if (piezas.Count > 0)
            {
                var hueco = Pcm.Silencio(a.PausaEntreBloques, audio.Frecuencia);
                piezas.Add(hueco);
                t += hueco.Segundos;
            }
            inicios.Add(new JsonObject { ["indice"] = e.Indice, ["inicio_s"] = Math.Round(t, 3) });
            piezas.Add(audio);
            t += audio.Segundos;
        }
        var unido = Path.Combine(temporal, "completo.wav");
        Pcm.Unir(piezas, piezas[0].Frecuencia).EscribirWav(unido);
        var destino = Path.Combine(almacen.CarpetaSalida(p.Id), ArchivoCompleto(p));
        await procesador.CodificarAsync(unido, destino, a, ct).ConfigureAwait(false);
        return (destino, new JsonObject { ["archivo"] = Path.GetFileName(destino), ["duracion_s"] = Math.Round(t, 2), ["bloques"] = inicios });
    }

    private string EscribirManifiesto(Proyecto p, IReadOnlyList<Bloque> bloques, IDictionary<int, EstadoBloque> estados, JsonObject? completo)
    {
        var lista = new JsonArray();
        foreach (var b in bloques)
        {
            estados.TryGetValue(b.Indice, out var e);
            lista.Add(new JsonObject
            {
                ["indice"] = b.Indice,
                ["nombre"] = b.Nombre,
                ["archivo"] = e?.Archivo,
                ["duracion_s"] = e?.Duracion,
                ["texto"] = b.Texto,
                ["eventos"] = new JsonArray(b.Eventos.Select(ev => (JsonNode)(ev switch
                {
                    EventoVoz v => new JsonObject { ["tipo"] = "voz", ["texto"] = v.Texto, ["susurro"] = v.Susurro, ["variante"] = v.Variante },
                    EventoSilencio s => new JsonObject { ["tipo"] = "silencio", ["segundos"] = s.Segundos },
                    _ => new JsonObject(),
                })).ToArray()),
                ["avisos"] = new JsonArray((e?.Avisos ?? b.Avisos).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["huella"] = e?.Huella,
            });
        }
        var manifiesto = new JsonObject
        {
            ["proyecto"] = p.Nombre,
            ["voz"] = p.Ajustes.VozId,
            ["modelo"] = sintetizador.Modelo,
            ["semilla"] = p.Ajustes.Semilla,
            ["formato"] = Extension(p.Ajustes),
            ["generado_el"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["bloques"] = lista,
            ["duracion_total_s"] = Math.Round(estados.Values.Sum(e => e.Duracion), 2),
            ["completo"] = completo,
        };
        var ruta = Path.Combine(almacen.CarpetaSalida(p.Id), ArchivoManifiesto);
        File.WriteAllText(ruta, manifiesto.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return ruta;
    }

    private static IReadOnlyList<EstadoBloque> Fusionar(IReadOnlyList<EstadoBloque> previos, SortedDictionary<int, EstadoBloque> nuevos, int total)
    {
        var resultado = previos.Where(p => p.Indice <= total).ToDictionary(p => p.Indice);
        foreach (var (indice, estado) in nuevos)
            resultado[indice] = estado;
        return resultado.Values.OrderBy(e => e.Indice).ToList();
    }

    private static void BorrarArchivosDe(string salida, string archivoSinExtension)
    {
        foreach (var ruta in new[]
                 {
                     Path.Combine(salida, archivoSinExtension + ".mp3"),
                     Path.Combine(salida, archivoSinExtension + ".wav"),
                     Path.Combine(salida, CarpetaNormalizados, archivoSinExtension + ".wav"),
                 })
        {
            try
            {
                if (File.Exists(ruta))
                    File.Delete(ruta);
            }
            catch (IOException)
            {
                // Puede estar sonando en el reproductor: ya se limpiará la próxima vez.
            }
        }
    }

    private static string Resumen(string texto) => texto.Length <= 60 ? texto : texto[..57] + "…";
}

/// <summary>Volumen y codificación con ffmpeg.</summary>
public sealed partial class ProcesadorFfmpeg(string ffmpeg, IEjecutorProcesos ejecutor) : IProcesadorAudio
{
    public async Task NormalizarAsync(string entrada, string medida, string salidaWav, AjustesNarracion a, CancellationToken ct)
    {
        // 1) Medir la sonoridad. 2) Aplicar la ganancia que falta y un limitador para los picos aislados
        //    (la normalización lineal pura se quedaba corta por culpa de las plosivas).
        var medicion = await Ejecutar(["-i", medida, "-af", "loudnorm=print_format=json", "-f", "null", "-"], ct).ConfigureAwait(false);
        var ganancia = 0.0;
        if (BloqueJson().Matches(medicion).LastOrDefault() is { } m
            && JsonNode.Parse(m.Value)?["input_i"]?.GetValue<string>() is { } texto
            && double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var actual)
            && double.IsFinite(actual))
        {
            ganancia = a.Lufs - actual;
        }
        var limite = Math.Pow(10, a.PicoDb / 20);
        Directory.CreateDirectory(Path.GetDirectoryName(salidaWav)!);
        await Ejecutar(["-y", "-i", entrada,
            "-af", FiltroVelocidadYTono(a) + FormattableString.Invariant($"volume={ganancia:0.00}dB,alimiter=limit={limite:0.0000}:attack=5:release=50:level=0"),
            "-ar", a.Frecuencia.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-c:a", "pcm_f32le", salidaWav], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// rubberband cambia la velocidad sin tocar el tono y el tono sin tocar la velocidad. Conservar los formantes evita
    /// el efecto «ardilla» o «robot» al subir o bajar el tono de una voz.
    /// </summary>
    internal static string FiltroVelocidadYTono(AjustesNarracion a)
    {
        if (!a.CambiaVelocidadOTono)
            return "";
        var velocidad = Math.Clamp(a.Velocidad, AjustesNarracion.VelocidadMinima, AjustesNarracion.VelocidadMaxima);
        var tono = Math.Pow(2, Math.Clamp(a.TonoSemitonos, -AjustesNarracion.TonoMaximo, AjustesNarracion.TonoMaximo) / 12);
        return FormattableString.Invariant($"rubberband=tempo={velocidad:0.###}:pitch={tono:0.#####}:formant=preserved:pitchq=quality:transients=mixed,");
    }

    public Task CodificarAsync(string wav, string destino, AjustesNarracion a, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        string[] codec = a.Formato == FormatoSalida.Wav ? ["-c:a", "pcm_s16le"] : ["-c:a", "libmp3lame", "-b:a", a.Bitrate];
        return Ejecutar(["-y", "-i", wav, "-ar", a.Frecuencia.ToString(CultureInfo.InvariantCulture), "-ac", "1", .. codec, destino], ct);
    }

    private async Task<string> Ejecutar(string[] argumentos, CancellationToken ct)
    {
        var r = await ejecutor.EjecutarAsync(ffmpeg, ["-hide_banner", "-nostdin", .. argumentos],
            new Dictionary<string, string>(), _ => { }, ct).ConfigureAwait(false);
        if (r.CodigoSalida != 0)
            throw new InvalidOperationException($"ffmpeg ha fallado: {r.Salida.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}");
        return r.Salida;
    }

    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex BloqueJson();
}
