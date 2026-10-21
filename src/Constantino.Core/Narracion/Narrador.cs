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

    /// <summary>Otras voces de la biblioteca para [voz: …]: nombre o id normalizado → id.</summary>
    IReadOnlyDictionary<string, string> OtrasVoces => new Dictionary<string, string>();

    /// <summary>Firma de otra voz (cambia si cambia su grabación).</summary>
    string FirmaDeVoz(string vozId) => vozId;

    /// <summary>
    /// Lee <paramref name="texto"/> con la voz del proyecto (o su <paramref name="variante"/>), o con otra voz de la
    /// biblioteca si se indica <paramref name="vozId"/>, y devuelve la ruta de un WAV.
    /// </summary>
    Task<string> SintetizarAsync(string texto, string? variante, string? vozId, AjustesNarracion ajustes, string salida, CancellationToken ct);
}

/// <summary>Sonidos para [sfx: …] (en la app, la carpeta «Mis sonidos»).</summary>
public interface ISonidos
{
    /// <summary>Nombres normalizados de los efectos disponibles.</summary>
    IReadOnlyCollection<string> Efectos { get; }

    string? RutaEfecto(string nombre);

    /// <summary>Ambiente de fondo (lluvia, hoguera…), por nombre.</summary>
    string? RutaAmbiente(string nombre) => null;

    /// <summary>Música de fondo, por nombre.</summary>
    string? RutaMusica(string nombre) => null;
}

/// <summary>Una versión alternativa de un bloque, generada con otra semilla.</summary>
/// <param name="Archivo">WAV ya normalizado, listo para escuchar.</param>
public sealed record Toma(int Semilla, string Archivo, double Duracion, IReadOnlyList<string> Avisos);

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

    /// <summary>Convierte cualquier audio (un efecto) a WAV mono a <paramref name="frecuencia"/>.</summary>
    Task ConvertirAsync(string origen, string destinoWav, int frecuencia, CancellationToken ct);

    /// <summary>Acelera (o frena) sin cambiar el tono: <paramref name="factor"/> 1,1 = un 10 % más rápido.</summary>
    Task CambiarTempoAsync(string wav, string destinoWav, double factor, CancellationToken ct);

    /// <summary>
    /// Mezcla ambiente y/o música bajo la voz, en bucle y con «ducking» (bajan solos cuando habla la voz). Los
    /// volúmenes son en dB respecto a como están los archivos.
    /// </summary>
    Task MezclarFondoAsync(string voz, string? ambiente, double volumenAmbiente, string? musica, double volumenMusica,
        double duracion, string destinoWav, AjustesNarracion ajustes, CancellationToken ct);
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

/// <param name="Avisos">Lo que conviene saber del resultado (revisión, duración…).</param>
public sealed record ResultadoNarracion(Proyecto Proyecto, int Generados, int SinCambios, string? Completo, string Manifiesto,
    IReadOnlyList<string>? Avisos = null);

/// <summary>
/// Genera los bloques de un proyecto: frase a frase con el sintetizador, recorta silencios, monta pausas y
/// susurros, normaliza y codifica. Los bloques cuya huella no ha cambiado se conservan. Al final une todo en un
/// archivo completo y escribe un manifiesto con la duración y el inicio de cada bloque para el montaje de vídeo.
/// </summary>
public sealed partial class Narrador(AlmacenProyectos almacen, ISintetizador sintetizador, IProcesadorAudio procesador,
    ISonidos? sonidos = null, IReadOnlyList<Pronunciacion>? diccionario = null, IRevisor? revisor = null)
{
    /// <summary>Máximo que se acelera el audio completo para que quepa en la duración pedida.</summary>
    public const double AceleracionMaxima = 1.25;

    private const string CarpetaTomas = ".tomas";

    /// <summary>Lee el guion con todo lo que sabe el narrador: variantes, otras voces, efectos y pronunciaciones.</summary>
    public IReadOnlyList<Bloque> Leer(Proyecto p, string guion) =>
        Guion.Leer(guion, p.Ajustes.Pausa, p.Ajustes.PausaLarga,
            new ContextoGuion(sintetizador.Variantes, sintetizador.OtrasVoces, sonidos?.Efectos, diccionario));

    /// <summary>La huella con la que se compara un bloque ya generado (la misma en pantalla y al generar).</summary>
    public string Huella(Proyecto p, Bloque b) =>
        Huellas.DeBloque(b, p.AjustesDe(b), sintetizador.FirmaVoz, sintetizador.Modelo,
            sintetizador.FirmaDeVoz, nombre => Huellas.FirmaArchivo(sonidos?.RutaEfecto(nombre)));

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
        var bloques = Leer(proyecto, guion);
        var salida = almacen.CarpetaSalida(proyecto.Id);
        var normalizados = Path.Combine(salida, CarpetaNormalizados);
        var temporal = Path.Combine(salida, CarpetaTemporal);
        Directory.CreateDirectory(normalizados);
        Directory.CreateDirectory(temporal);

        var previos = proyecto.Bloques.ToDictionary(b => b.Indice);
        var estados = new SortedDictionary<int, EstadoBloque>();
        var nuevos = new List<int>();
        var avisosResultado = new List<string>();
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
                var ab = proyecto.AjustesDe(b);
                var huella = Huella(proyecto, b);
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
                var (montaje, medida, avisos) = await GenerarBloqueAsync(b, ab, temporal, (fraccion, texto) =>
                    progreso.Report(new EventoNarracion(TipoEventoNarracion.Frase, b.Indice, bloques.Count, texto,
                        (inicioBloque + pesos[i] * fraccion) / pesoTotal)), ct).ConfigureAwait(false);

                var wav = Path.Combine(temporal, $"{b.Archivo}.wav");
                var wavMedida = Path.Combine(temporal, $"{b.Archivo}_medida.wav");
                montaje.EscribirWav(wav);
                medida.EscribirWav(wavMedida);
                await procesador.NormalizarAsync(wav, wavMedida, normalizado, ab, ct).ConfigureAwait(false);
                await procesador.CodificarAsync(normalizado, final, ab, ct).ConfigureAwait(false);

                // Si el bloque cambió de nombre, sobran los archivos con el nombre anterior.
                if (previo is not null && previo.Archivo != Path.GetFileName(final))
                    BorrarArchivosDe(salida, Path.GetFileNameWithoutExtension(previo.Archivo));

                // La duración se mide sobre el audio final: la velocidad la cambia.
                var duracion = Voces.BibliotecaVoces.DuracionWav(normalizado);
                estados[b.Indice] = new EstadoBloque(b.Indice, b.Nombre, huella, Path.GetFileName(final),
                    Math.Round(duracion, 2), [.. b.Avisos, .. avisos], DateTimeOffset.Now);
                generados++;
                nuevos.Add(b.Indice);
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

        // Revisión: escuchar lo nuevo y repetir lo que se haya leído mal.
        if (revisor is not null && a.Revisar && nuevos.Count > 0)
        {
            proyecto = await RevisarAsync(proyecto, guion, nuevos, avisosResultado, progreso, ct).ConfigureAwait(false);
            foreach (var e in proyecto.Bloques)
                estados[e.Indice] = e;
        }

        // Bloques que ya no existen en el guion.
        foreach (var sobrante in previos.Values.Where(p => p.Indice > bloques.Count))
            BorrarArchivosDe(salida, Path.GetFileNameWithoutExtension(sobrante.Archivo));

        string? completo = null;
        JsonObject? infoCompleto = null;
        if (bloques.Count > 0 && estados.Count == bloques.Count)
        {
            progreso.Report(new EventoNarracion(TipoEventoNarracion.Montando, 0, bloques.Count, "Montando el audio completo", 1));
            (completo, infoCompleto) = await MontarCompletoAsync(proyecto, estados.Values.ToList(), normalizados, temporal,
                avisosResultado, ct).ConfigureAwait(false);
        }
        var manifiesto = EscribirManifiesto(proyecto, bloques, estados, infoCompleto);
        try { Directory.Delete(temporal, recursive: true); }
        catch (IOException) { }
        return new ResultadoNarracion(proyecto, generados, sinCambios, completo, manifiesto, avisosResultado);
    }

    /// <summary>
    /// Escucha cada bloque recién generado con el revisor (Whisper) y lo compara con su texto. Para los que se han
    /// leído mal (frases cortadas, palabras inventadas) genera dos tomas más y se queda con la que mejor se ajusta;
    /// las demás quedan en «Otras tomas». Todo de una vez para no cambiar de modelo más de lo necesario.
    /// </summary>
    private async Task<Proyecto> RevisarAsync(Proyecto proyecto, string guion, IReadOnlyList<int> indices, List<string> avisos,
        IProgress<EventoNarracion> progreso, CancellationToken ct)
    {
        var bloques = Leer(proyecto, guion).ToDictionary(b => b.Indice);
        var normalizados = Path.Combine(almacen.CarpetaSalida(proyecto.Id), CarpetaNormalizados);
        var fallidos = new List<(Bloque Bloque, ComparacionLectura Original)>();
        for (var k = 0; k < indices.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            if (!bloques.TryGetValue(indices[k], out var b) || b.Vacio)
                continue;
            progreso.Report(new EventoNarracion(TipoEventoNarracion.Montando, b.Indice, bloques.Count,
                $"Revisando que se lee bien: bloque {b.Indice}", (double)k / indices.Count));
            var oido = await revisor!.TranscribirAsync(Path.Combine(normalizados, $"{b.Archivo}.wav"), ct).ConfigureAwait(false);
            var c = Revision.Comparar(b.TextoLeido, oido);
            if (c.MalLeido)
                fallidos.Add((b, c));
        }
        if (fallidos.Count == 0)
            return proyecto;

        // Primero todas las tomas nuevas (motor de voz) y luego todas las escuchas (Whisper).
        var candidatas = new List<(Bloque Bloque, ComparacionLectura Original, IReadOnlyList<Toma> Tomas)>();
        foreach (var (b, original) in fallidos)
        {
            progreso.Report(new EventoNarracion(TipoEventoNarracion.Bloque, b.Indice, bloques.Count,
                $"El bloque {b.Indice} no se ha leído bien: probando otras tomas", 1));
            candidatas.Add((b, original, await GenerarTomasAsync(proyecto, guion, b.Indice, 2, progreso, ct).ConfigureAwait(false)));
        }
        foreach (var (b, original, tomas) in candidatas)
        {
            var mejor = (Semilla: (int?)null, Comparacion: original);
            foreach (var t in tomas)
            {
                var c = Revision.Comparar(b.TextoLeido, await revisor!.TranscribirAsync(t.Archivo, ct).ConfigureAwait(false));
                if (c.Errores < mejor.Comparacion.Errores)
                    mejor = (t.Semilla, c);
            }
            if (mejor.Semilla is { } semilla)
                proyecto = await ElegirTomaAsync(proyecto, guion, b.Indice, semilla, ct).ConfigureAwait(false);
            var aviso = mejor.Comparacion.MalLeido
                ? $"Revisa cómo suena: la voz no dice exactamente el texto ({mejor.Comparacion.Errores} diferencias). Prueba «Otras tomas» o cambia la frase."
                : $"Se ha leído mal a la primera ({original.Errores} diferencias) y se ha corregido solo con otra toma.";
            avisos.Add($"Bloque {b.Indice}: {aviso}");
            proyecto = ConAviso(proyecto, b.Indice, aviso);
        }
        return proyecto;
    }

    private Proyecto ConAviso(Proyecto p, int indice, string aviso) =>
        almacen.Guardar(p with
        {
            Bloques = p.Bloques.Select(e => e.Indice == indice ? e with { Avisos = [.. e.Avisos, aviso] } : e).ToList(),
        });

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
            if (evento is EventoEfecto efecto)
            {
                if (sonidos?.RutaEfecto(efecto.Nombre) is { } rutaEfecto)
                {
                    var wavEfecto = Path.Combine(temporal, $"{b.Archivo}_{partes.Count:000}_efecto.wav");
                    await procesador.ConvertirAsync(rutaEfecto, wavEfecto, frecuencia ?? FrecuenciaPorDefecto, ct).ConfigureAwait(false);
                    partes.Add((Pcm.LeerWav(wavEfecto), 0));
                }
                anteriorVoz = false;
                continue;
            }
            var v = (EventoVoz)evento;
            avance((double)letrasHechas / totalLetras, $"Bloque {b.Indice}: «{Resumen(v.Texto)}»");
            // Las variantes son de la voz del proyecto; otra voz se lee con su grabación principal.
            var variante = v.VozId is null && v.Variante is { } id && sintetizador.Variantes.Contains(id) ? id : null;
            var ruta = await sintetizador.SintetizarAsync(v.Texto, variante, v.VozId, a,
                Path.Combine(temporal, $"{b.Archivo}_{partes.Count:000}.wav"), ct).ConfigureAwait(false);
            var audio = Pcm.LeerWav(ruta);
            if (a.RecortarSilencios)
                audio = audio.Recortar(a.UmbralSilencioDb, a.MargenRecorte);
            if (frecuencia is null)
            {
                frecuencia = audio.Frecuencia;
                // Lo añadido antes de conocer la frecuencia del modelo (silencios, efectos) se rehace con la correcta.
                for (var i = 0; i < partes.Count; i++)
                    if (partes[i].Audio.Frecuencia != audio.Frecuencia)
                        partes[i] = (Pcm.Silencio(partes[i].Audio.Segundos, audio.Frecuencia), 0);
            }
            var esperado = v.Texto.Length / LetrasPorSegundo;
            if (audio.Segundos > 2.5 * esperado + 1 || audio.Segundos < 0.4 * esperado)
                avisos.Add($"Duración anómala ({audio.Segundos:0.0} s, se esperaban ~{esperado:0.0} s): «{Resumen(v.Texto)}». Revisa cómo suena.");
            if (anteriorVoz)
                partes.Add((Pcm.Silencio(a.PausaEntreFrases, audio.Frecuencia), 0));
            // Otra voz no sabemos si tiene variante de susurro: se simula bajando el volumen.
            partes.Add((audio, v.Susurro ? (v.VozId is null ? gananciaSusurro : a.SusurroDb) : 0));
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
        string normalizados, string temporal, List<string> avisos, CancellationToken ct)
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
        var info = new JsonObject();

        // Que quepa en la duración pedida (p. ej. 59 s para un Short): se acelera sin cambiar el tono, con un límite.
        if (a.DuracionMaxima is > 0 and var maxima && t > maxima + 0.05)
        {
            var factor = t / maxima;
            if (factor <= AceleracionMaxima)
            {
                var rapido = Path.Combine(temporal, "completo-rapido.wav");
                await procesador.CambiarTempoAsync(unido, rapido, factor, ct).ConfigureAwait(false);
                unido = rapido;
                foreach (var nodo in inicios)
                    nodo!["inicio_s"] = Math.Round((double)nodo["inicio_s"]! / factor, 3);
                t /= factor;
                info["acelerado"] = Math.Round(factor, 3);
                avisos.Add($"El audio completo se ha acelerado un {(factor - 1) * 100:0} % para que dure {maxima:0} s.");
            }
            else
            {
                avisos.Add($"El audio completo dura {t:0} s y el máximo es {maxima:0} s: haría falta acelerarlo un {(factor - 1) * 100:0} %, " +
                           "demasiado para que suene natural. Acorta el guion o reduce las pausas.");
            }
        }

        // Ambiente y música de fondo, que bajan solos cuando habla la voz.
        var ambiente = a.Ambiente is { Length: > 0 } nombreAmbiente ? sonidos?.RutaAmbiente(nombreAmbiente) : null;
        var musica = a.Musica is { Length: > 0 } nombreMusica ? sonidos?.RutaMusica(nombreMusica) : null;
        if (ambiente is not null || musica is not null)
        {
            var mezclado = Path.Combine(temporal, "completo-fondo.wav");
            await procesador.MezclarFondoAsync(unido, ambiente, a.VolumenAmbiente, musica, a.VolumenMusica, t, mezclado, a, ct).ConfigureAwait(false);
            unido = mezclado;
            info["ambiente"] = a.Ambiente;
            info["musica"] = a.Musica;
        }

        var destino = Path.Combine(almacen.CarpetaSalida(p.Id), ArchivoCompleto(p));
        await procesador.CodificarAsync(unido, destino, a, ct).ConfigureAwait(false);
        info["archivo"] = Path.GetFileName(destino);
        info["duracion_s"] = Math.Round(t, 2);
        info["bloques"] = inicios;
        return (destino, info);
    }

    // --- Otras tomas ---------------------------------------------------------------------------

    /// <summary>Genera <paramref name="cantidad"/> versiones nuevas del bloque con semillas al azar, para escoger la mejor.</summary>
    public async Task<IReadOnlyList<Toma>> GenerarTomasAsync(Proyecto proyecto, string guion, int indice, int cantidad,
        IProgress<EventoNarracion> progreso, CancellationToken ct)
    {
        var b = Leer(proyecto, guion).FirstOrDefault(x => x.Indice == indice)
                ?? throw new InvalidOperationException($"No hay bloque {indice}.");
        var carpeta = CarpetaTomasDe(proyecto, b);
        var temporal = Path.Combine(almacen.CarpetaSalida(proyecto.Id), CarpetaTemporal);
        Directory.CreateDirectory(carpeta);
        Directory.CreateDirectory(temporal);
        var usadas = new HashSet<int>(TomasDe(proyecto, guion, indice).Select(t => t.Semilla)) { proyecto.AjustesDe(b).Semilla };
        var tomas = new List<Toma>();
        for (var k = 0; k < cantidad; k++)
        {
            ct.ThrowIfCancellationRequested();
            int semilla;
            do semilla = Random.Shared.Next(1, 100_000); while (!usadas.Add(semilla));
            var ab = proyecto.AjustesDe(b) with { Semilla = semilla };
            var (montaje, medida, avisos) = await GenerarBloqueAsync(b, ab, temporal, (f, texto) =>
                progreso.Report(new EventoNarracion(TipoEventoNarracion.Frase, b.Indice, cantidad,
                    $"Toma {k + 1} de {cantidad}: {texto}", (k + f) / cantidad)), ct).ConfigureAwait(false);
            var wav = Path.Combine(temporal, $"toma_{semilla}.wav");
            var wavMedida = Path.Combine(temporal, $"toma_{semilla}_medida.wav");
            montaje.EscribirWav(wav);
            medida.EscribirWav(wavMedida);
            var destino = Path.Combine(carpeta, $"{semilla}.wav");
            await procesador.NormalizarAsync(wav, wavMedida, destino, ab, ct).ConfigureAwait(false);
            tomas.Add(new Toma(semilla, destino, Math.Round(Voces.BibliotecaVoces.DuracionWav(destino), 2), avisos));
        }
        return tomas;
    }

    /// <summary>Las tomas ya generadas de un bloque (sin la que está en uso).</summary>
    public IReadOnlyList<Toma> TomasDe(Proyecto proyecto, string guion, int indice)
    {
        if (Leer(proyecto, guion).FirstOrDefault(x => x.Indice == indice) is not { } b)
            return [];
        var carpeta = CarpetaTomasDe(proyecto, b);
        if (!Directory.Exists(carpeta))
            return [];
        return Directory.EnumerateFiles(carpeta, "*.wav")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var s) ? new Toma(s, f, Math.Round(Voces.BibliotecaVoces.DuracionWav(f), 2), []) : null)
            .OfType<Toma>()
            .Where(t => t.Semilla != proyecto.AjustesDe(b).Semilla)
            .OrderBy(t => File.GetCreationTimeUtc(t.Archivo))
            .ToList();
    }

    /// <summary>
    /// Usa la toma <paramref name="semilla"/> para el bloque: pasa a ser su audio y queda apuntada en el proyecto.
    /// La toma que había se guarda como una más, por si se quiere volver a ella. Después hay que volver a montar el
    /// audio completo (<see cref="NarrarAsync"/>, que no regenera nada).
    /// </summary>
    public async Task<Proyecto> ElegirTomaAsync(Proyecto proyecto, string guion, int indice, int semilla, CancellationToken ct)
    {
        var b = Leer(proyecto, guion).FirstOrDefault(x => x.Indice == indice)
                ?? throw new InvalidOperationException($"No hay bloque {indice}.");
        var carpeta = CarpetaTomasDe(proyecto, b);
        var origen = Path.Combine(carpeta, $"{semilla}.wav");
        if (!File.Exists(origen))
            throw new InvalidOperationException("Esa toma ya no existe.");
        var salida = almacen.CarpetaSalida(proyecto.Id);
        var normalizado = Path.Combine(salida, CarpetaNormalizados, $"{b.Archivo}.wav");
        // La toma actual se conserva entre las alternativas.
        var actual = proyecto.AjustesDe(b).Semilla;
        if (File.Exists(normalizado))
            File.Copy(normalizado, Path.Combine(carpeta, $"{actual}.wav"), overwrite: true);

        var tomas = new Dictionary<string, int>(proyecto.Tomas ?? new Dictionary<string, int>());
        var clave = Huellas.ClaveBloque(b);
        if (semilla == proyecto.Ajustes.Semilla)
            tomas.Remove(clave);
        else
            tomas[clave] = semilla;
        var nuevo = proyecto with { Tomas = tomas };
        var ab = nuevo.AjustesDe(b);

        Directory.CreateDirectory(Path.GetDirectoryName(normalizado)!);
        File.Copy(origen, normalizado, overwrite: true);
        var final = Path.Combine(salida, $"{b.Archivo}.{Extension(ab)}");
        await procesador.CodificarAsync(normalizado, final, ab, ct).ConfigureAwait(false);
        File.Delete(origen);

        var estado = new EstadoBloque(b.Indice, b.Nombre, Huella(nuevo, b), Path.GetFileName(final),
            Math.Round(Voces.BibliotecaVoces.DuracionWav(normalizado), 2), b.Avisos, DateTimeOffset.Now);
        var estados = new SortedDictionary<int, EstadoBloque> { [b.Indice] = estado };
        return almacen.Guardar(nuevo with { Bloques = Fusionar(nuevo.Bloques, estados, Leer(nuevo, guion).Count) });
    }

    private string CarpetaTomasDe(Proyecto p, Bloque b) =>
        Path.Combine(almacen.CarpetaSalida(p.Id), CarpetaTomas, Huellas.ClaveBloque(b));

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
                    EventoVoz v => new JsonObject { ["tipo"] = "voz", ["texto"] = v.Texto, ["susurro"] = v.Susurro, ["variante"] = v.Variante, ["voz"] = v.VozId },
                    EventoEfecto ef => new JsonObject { ["tipo"] = "efecto", ["nombre"] = ef.Nombre },
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

    public Task ConvertirAsync(string origen, string destinoWav, int frecuencia, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinoWav)!);
        // Un efecto añadido por el usuario puede venir a cualquier volumen: se iguala al de una voz.
        return Ejecutar(["-y", "-i", origen, "-af", "loudnorm=I=-20:TP=-2:LRA=11",
            "-ar", frecuencia.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-c:a", "pcm_s16le", destinoWav], ct);
    }

    public Task CambiarTempoAsync(string wav, string destinoWav, double factor, CancellationToken ct) =>
        Ejecutar(["-y", "-i", wav, "-af", FormattableString.Invariant($"rubberband=tempo={factor:0.####}:pitchq=quality:transients=mixed"),
            "-c:a", "pcm_f32le", destinoWav], ct);

    public Task MezclarFondoAsync(string voz, string? ambiente, double volumenAmbiente, string? musica, double volumenMusica,
        double duracion, string destinoWav, AjustesNarracion a, CancellationToken ct)
    {
        var sr = a.Frecuencia.ToString(CultureInfo.InvariantCulture);
        var formato = $"aformat=sample_fmts=fltp:sample_rates={sr}:channel_layouts=mono";
        var entradas = new List<string> { "-y", "-i", voz };
        var fondos = new List<string>();
        var filtro = new System.Text.StringBuilder($"[0:a]{formato},asplit=2[voz][guia];");
        void Fondo(string? ruta, double volumen, string etiqueta)
        {
            if (ruta is null)
                return;
            // En bucle: dura lo que haga falta.
            entradas.AddRange(["-stream_loop", "-1", "-i", ruta]);
            filtro.Append(FormattableString.Invariant($"[{fondos.Count + 1}:a]{formato},volume={volumen:0.#}dB[{etiqueta}];"));
            fondos.Add($"[{etiqueta}]");
        }
        Fondo(ambiente, volumenAmbiente, "amb");
        Fondo(musica, volumenMusica, "mus");
        filtro.Append(fondos.Count == 1 ? $"{fondos[0]}anull[fondo];" : $"{string.Concat(fondos)}amix=inputs={fondos.Count}:normalize=0[fondo];");
        var fin = Math.Max(0, duracion - 2.5);
        filtro.Append(FormattableString.Invariant($"[fondo]afade=t=in:d=1.5,afade=t=out:st={fin:0.##}:d=2.5[fondoSuave];"));
        // Ducking: el fondo se comprime cuando suena la voz.
        filtro.Append("[fondoSuave][guia]sidechaincompress=threshold=0.02:ratio=8:attack=30:release=600[fondoBajo];");
        filtro.Append("[voz][fondoBajo]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[salida]");
        return Ejecutar([.. entradas, "-filter_complex", filtro.ToString(), "-map", "[salida]", "-c:a", "pcm_f32le", destinoWav], ct);
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
