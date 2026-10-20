using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constantino.Core.Instalaciones;

namespace Constantino.Core.Voces;

public sealed class VozInvalidaException(string mensaje) : Exception(mensaje);

/// <summary>Datos para añadir una voz a partir de una grabación.</summary>
public sealed record SolicitudImportacion(
    string Archivo,
    string Nombre,
    string Idioma,
    string Transcripcion,
    bool Consentimiento,
    OrigenVoz Origen = OrigenVoz.Importada,
    string? Descripcion = null,
    int? Semilla = null,
    bool SinTranscripcion = false);

/// <param name="Aviso">Algo mejorable pero aceptable (p. ej. una grabación más corta de lo ideal).</param>
public sealed record ResultadoImportacion(Voz Voz, string? Aviso);

/// <summary>
/// Biblioteca de voces en disco: una carpeta por voz con <c>voz.json</c> y <c>referencia.wav</c>
/// (siempre WAV mono a 24 kHz, convertido con ffmpeg desde cualquier formato).
/// </summary>
public sealed class BibliotecaVoces(string carpeta, string ffmpeg, IEjecutorProcesos ejecutor)
{
    public const string ArchivoFicha = "voz.json";
    public const string ArchivoReferencia = "referencia.wav";
    public const double DuracionMinima = 3, DuracionMaxima = 60, IdealMinima = 8, IdealMaxima = 25;
    private const int LongitudMinimaTranscripcion = 10;

    /// <summary>
    /// Letras por segundo de grabación que se consideran coherentes. Al hablar se dicen unas 12–16;
    /// el margen cubre pausas y gente que habla muy rápido o muy despacio.
    /// </summary>
    public const double LetrasPorSegundoMinimas = 4, LetrasPorSegundoMaximas = 30, LetrasPorSegundoHabituales = 13;

    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Carpeta => carpeta;

    public IReadOnlyList<Voz> Listar()
    {
        if (!Directory.Exists(carpeta))
            return [];
        var voces = new List<Voz>();
        foreach (var dir in Directory.EnumerateDirectories(carpeta))
        {
            if (Leer(Path.GetFileName(dir)) is { } voz)
                voces.Add(voz);
        }
        return voces.OrderBy(v => v.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Voz? Obtener(string id) => Leer(id);

    public string CarpetaDe(string id) => Path.Combine(carpeta, id);

    public string ReferenciaDe(string id) => Path.Combine(carpeta, id, ArchivoReferencia);

    public async Task<ResultadoImportacion> ImportarAsync(SolicitudImportacion s, CancellationToken ct = default)
    {
        var nombre = s.Nombre.Trim();
        var transcripcion = s.SinTranscripcion ? "" : NormalizarTexto(s.Transcripcion);
        if (nombre.Length == 0)
            throw new VozInvalidaException("Ponle un nombre a la voz.");
        if (!File.Exists(s.Archivo))
            throw new VozInvalidaException("No se encuentra el archivo de audio.");
        if (!s.SinTranscripcion && transcripcion.Length < LongitudMinimaTranscripcion)
        {
            throw new VozInvalidaException(
                "Escribe la transcripción exacta de lo que se dice en la grabación, o marca «No tengo la transcripción».");
        }
        if (!Idiomas.Existe(s.Idioma))
            throw new VozInvalidaException($"Idioma no admitido: {s.Idioma}.");
        if (!s.Consentimiento)
            throw new VozInvalidaException("Confirma que la voz es tuya o que tienes permiso de su dueño para usarla.");
        if (!File.Exists(ffmpeg))
            throw new VozInvalidaException("Falta ffmpeg: instala el Núcleo desde Componentes.");

        var id = IdLibre(nombre);
        var destino = CarpetaDe(id);
        Directory.CreateDirectory(destino);
        try
        {
            var referencia = Path.Combine(destino, ArchivoReferencia);
            var r = await ejecutor.EjecutarAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-i", s.Archivo, "-vn", "-ac", "1", "-ar", "24000", "-sample_fmt", "s16", referencia],
                new Dictionary<string, string>(), _ => { }, ct).ConfigureAwait(false);
            if (r.CodigoSalida != 0 || !File.Exists(referencia))
                throw new VozInvalidaException("No se ha podido leer el audio. Prueba con un WAV o un MP3.");

            var duracion = DuracionWav(referencia);
            if (duracion is < DuracionMinima or > DuracionMaxima)
            {
                throw new VozInvalidaException(
                    $"La grabación dura {Segundos(duracion)}; tiene que durar entre {DuracionMinima:0} y {DuracionMaxima:0} s (lo ideal, {IdealMinima:0}–{IdealMaxima:0} s).");
            }
            if (!s.SinTranscripcion)
                ComprobarTranscripcion(transcripcion, duracion);
            var voz = new Voz(id, nombre, s.Idioma, s.Origen, transcripcion, Math.Round(duracion, 2), DateTimeOffset.Now,
                s.Consentimiento, s.Descripcion, s.Semilla);
            Escribir(voz);
            string? aviso = duracion is < IdealMinima or > IdealMaxima
                ? $"La grabación dura {Segundos(duracion)}. Funciona, pero el resultado suele ser mejor con {IdealMinima:0}–{IdealMaxima:0} s."
                : null;
            return new ResultadoImportacion(voz, aviso);
        }
        catch
        {
            Directory.Delete(destino, recursive: true);
            throw;
        }
    }

    /// <summary>Cambia el nombre visible; la carpeta (id) se mantiene para no romper proyectos que la usen.</summary>
    public Voz Renombrar(string id, string nombre)
    {
        var voz = Leer(id) ?? throw new VozInvalidaException("La voz ya no existe.");
        if (string.IsNullOrWhiteSpace(nombre))
            throw new VozInvalidaException("El nombre no puede quedar vacío.");
        var nueva = voz with { Nombre = nombre.Trim() };
        Escribir(nueva);
        return nueva;
    }

    /// <summary>Corrige la transcripción. Vacía, la voz pasa a imitar solo el timbre.</summary>
    public Voz CambiarTranscripcion(string id, string transcripcion)
    {
        var voz = Leer(id) ?? throw new VozInvalidaException("La voz ya no existe.");
        var texto = NormalizarTexto(transcripcion);
        if (texto.Length > 0)
            ComprobarTranscripcion(texto, voz.DuracionSegundos);
        var nueva = voz with { Transcripcion = texto };
        Escribir(nueva);
        return nueva;
    }

    public void Borrar(string id)
    {
        var dir = CarpetaDe(id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Una transcripción que no encaja con la duración hace que el modelo corte o balbucee:
    /// se rechaza con una explicación en vez de dejar que falle al generar.
    /// </summary>
    public static void ComprobarTranscripcion(string transcripcion, double duracion)
    {
        var letras = transcripcion.Count(char.IsLetterOrDigit);
        if (letras < LongitudMinimaTranscripcion || letras < duracion * LetrasPorSegundoMinimas)
        {
            throw new VozInvalidaException(
                $"La transcripción no parece corresponder con la grabación: en {Segundos(duracion)} de habla suele haber unas " +
                $"{duracion * LetrasPorSegundoHabituales:0} letras y has escrito {letras}. Escribe todo lo que se dice, palabra por palabra, " +
                "o marca «No tengo la transcripción».");
        }
        if (letras > duracion * LetrasPorSegundoMaximas)
        {
            throw new VozInvalidaException(
                $"La transcripción es demasiado larga para una grabación de {Segundos(duracion)} ({letras} letras): ¿seguro que es la de este audio?");
        }
    }

    /// <summary>Duración de un WAV PCM leyendo sus cabeceras (fmt y data).</summary>
    public static double DuracionWav(string ruta)
    {
        using var lector = new BinaryReader(File.OpenRead(ruta));
        if (Encoding.ASCII.GetString(lector.ReadBytes(4)) != "RIFF")
            throw new VozInvalidaException("El archivo de referencia no es un WAV válido.");
        lector.ReadUInt32();
        if (Encoding.ASCII.GetString(lector.ReadBytes(4)) != "WAVE")
            throw new VozInvalidaException("El archivo de referencia no es un WAV válido.");
        int? bytesPorSegundo = null;
        while (lector.BaseStream.Position + 8 <= lector.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(lector.ReadBytes(4));
            var tamano = lector.ReadUInt32();
            if (id == "fmt ")
            {
                lector.ReadUInt16(); // formato
                lector.ReadUInt16(); // canales
                lector.ReadUInt32(); // frecuencia
                bytesPorSegundo = (int)lector.ReadUInt32();
                lector.BaseStream.Seek(tamano - 12, SeekOrigin.Current);
            }
            else if (id == "data" && bytesPorSegundo is > 0)
            {
                // ffmpeg puede escribir 0xFFFFFFFF si no sabe el tamaño: se usa lo que queda del archivo.
                var datos = tamano == uint.MaxValue ? lector.BaseStream.Length - lector.BaseStream.Position : tamano;
                return (double)datos / bytesPorSegundo.Value;
            }
            else
            {
                lector.BaseStream.Seek(tamano + (tamano & 1), SeekOrigin.Current);
            }
        }
        throw new VozInvalidaException("El archivo de referencia no tiene audio.");
    }

    private Voz? Leer(string id)
    {
        var ficha = Path.Combine(carpeta, id, ArchivoFicha);
        if (!File.Exists(ficha) || !File.Exists(ReferenciaDe(id)))
            return null;
        try
        {
            var voz = JsonSerializer.Deserialize<Voz>(File.ReadAllText(ficha), Opciones);
            return voz is null ? null : voz with { Id = id };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Escribir(Voz voz)
    {
        var ficha = Path.Combine(CarpetaDe(voz.Id), ArchivoFicha);
        var temporal = ficha + ".tmp";
        File.WriteAllText(temporal, JsonSerializer.Serialize(voz, Opciones));
        File.Move(temporal, ficha, overwrite: true);
    }

    private string IdLibre(string nombre)
    {
        var baseId = Textos.Slug(nombre, "voz");
        var id = baseId;
        for (var n = 2; Directory.Exists(CarpetaDe(id)); n++)
            id = $"{baseId}-{n}";
        return id;
    }

    private static string NormalizarTexto(string texto) => string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Segundos(double s) => $"{s.ToString("0.#", CultureInfo.GetCultureInfo("es-ES"))} s";
}
