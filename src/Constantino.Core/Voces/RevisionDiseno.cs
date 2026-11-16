using Constantino.Core.Motores;
using Constantino.Core.Narracion;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Voces;

/// <summary>Un cambio de «Más así»: la misma voz (misma semilla), con la descripción retocada.</summary>
public enum CambioDiseno
{
    Igual,
    MasGrave,
    MasAguda,
    MasPausada,
    MasAgil,
    MasMayor,
    MasJoven,
    MasIntensa,
    MasSuave,
}

public sealed record OpcionCambio(CambioDiseno Cambio, string Nombre);

/// <summary>Lo pedido en «Más así»: los cambios, con qué fuerza (0 un poco, 1 bastante, 2 mucho) y desde qué voz.</summary>
/// <param name="Base">La descripción de la voz de partida, sin los cambios.</param>
public sealed record CambiosPedidos(string Base, IReadOnlyList<CambioDiseno> Cambios, MedidasVoz Partida, int Fuerza)
{
    public const int FuerzaMaxima = 2;
    public string Instruccion => RevisionDiseno.Instruccion(Base, Cambios, Fuerza);
}

/// <summary>
/// Revisa las voces que inventa el diseñador, que no siempre obedece: en las pruebas, «hombre de sesenta años» dio
/// voces de 200–280 Hz, y con la misma descripción de «voz grave» salieron de 75 a 114 Hz según la semilla. También
/// comprueba que lee exactamente el texto de muestra: será la transcripción de la voz, y si no coincide la voz clonada
/// sale cortada.
/// </summary>
public static class RevisionDiseno
{
    /// <summary>Margen sobre los rangos de tono: es una comprobación de «suena a lo pedido», no una medida fina.</summary>
    private const double Margen = 0.08;

    /// <summary>
    /// Tono medio esperable (Hz) para lo pedido, o null si no hay nada que comprobar (sexo indiferente). Los rangos son
    /// los habituales al hablar: hombres de 85 a 155 Hz, mujeres de 160 a 255 Hz; los niños, más agudos.
    /// </summary>
    public static (double Min, double Max)? RangoTono(SexoVoz sexo, string timbre, int edad)
    {
        if (sexo == SexoVoz.Indiferente)
            return null;
        if (edad < 16)
            return (180, 350);
        var (min, max) = (sexo, timbre) switch
        {
            (SexoVoz.Hombre, "grave") => (70.0, 125.0),
            (SexoVoz.Hombre, "aguda") => (105.0, 180.0),
            (SexoVoz.Hombre, _) => (85.0, 150.0),
            (SexoVoz.Mujer, "grave") => (135.0, 200.0),
            (SexoVoz.Mujer, "aguda") => (185.0, 300.0),
            _ => (160.0, 250.0),
        };
        return (min * (1 - Margen), max * (1 + Margen));
    }

    /// <summary>Lo que no encaja de una voz generada (vacío si está bien).</summary>
    /// <param name="oido">Lo que Whisper ha entendido (null si no está instalado: no se comprueba la lectura).</param>
    /// <param name="rango">Tono esperado (null: no se comprueba).</param>
    /// <param name="cambio">Para «Más así»: el cambio pedido y las medidas de la voz de partida.</param>
    public static IReadOnlyList<string> Problemas(MedidasVoz medidas, string? oido, string texto, string idioma,
        (double Min, double Max)? rango, CambiosPedidos? cambio = null)
    {
        var problemas = new List<string>();
        if (oido is not null && !LeeElTexto(texto, oido, idioma))
            problemas.Add(T("Se salta o cambia palabras del texto de muestra (la voz clonada podría salir cortada)."));
        if (medidas.Sonoridad < 0.2)
            problemas.Add(T("Apenas tiene voz: casi todo es susurro o silencio."));
        else if (rango is { } r && medidas.TonoHz > 0)
        {
            if (medidas.TonoHz > r.Max)
                problemas.Add(T("Suena más aguda de lo pedido ({0:0} Hz).", medidas.TonoHz));
            else if (medidas.TonoHz < r.Min)
                problemas.Add(T("Suena más grave de lo pedido ({0:0} Hz).", medidas.TonoHz));
        }
        if (cambio is not null)
            problemas.AddRange(NoCumplidos(cambio, medidas).Select(c => T("No se nota «{0}».", NombreCambio(c).ToLowerInvariant())));
        return problemas;
    }

    /// <summary>Los cambios pedidos que no se notan en la voz nueva.</summary>
    public static IReadOnlyList<CambioDiseno> NoCumplidos(CambiosPedidos cambio, MedidasVoz nueva) =>
        cambio.Cambios.Where(c => !Cumple(c, cambio.Partida, nueva)).ToList();

    /// <summary>Letras distintas a partir de las que se considera que se ha saltado o cambiado algo (una palabra mediana).</summary>
    private const int LetrasDistintas = 6;

    /// <summary>
    /// Si lo que entiende Whisper coincide con el texto. Se compara letra a letra y sin espacios: Whisper a veces parte
    /// mal las palabras («guardabosques encendió» → «guardabosque se encendió»), y eso no es un error de la voz; saltarse
    /// una palabra sí cambia muchas letras.
    /// </summary>
    public static bool LeeElTexto(string texto, string oido, string idioma)
    {
        string Letras(string s) => string.Concat(Revision.Palabras(idioma == "Spanish" ? NormalizadorEspanol.Normalizar(s) : s));
        return DistanciaLetras(Letras(texto), Letras(oido)) < LetrasDistintas;
    }

    private static int DistanciaLetras(string a, string b)
    {
        var anterior = new int[b.Length + 1];
        var actual = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            anterior[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            actual[0] = i;
            for (var j = 1; j <= b.Length; j++)
                actual[j] = Math.Min(Math.Min(anterior[j] + 1, actual[j - 1] + 1), anterior[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (anterior, actual) = (actual, anterior);
        }
        return anterior[b.Length];
    }

    /// <summary>
    /// Si la voz nueva ha cambiado en el sentido pedido, lo bastante para notarse (un semitono de tono, un 8 % de ritmo, un
    /// 12 % de entonación). «Más mayor» y «más joven» no se pueden medir: se dan por buenos.
    /// </summary>
    public static bool Cumple(CambioDiseno cambio, MedidasVoz partida, MedidasVoz nueva) => cambio switch
    {
        CambioDiseno.MasGrave => nueva.TonoHz < partida.TonoHz * 0.944,
        CambioDiseno.MasAguda => nueva.TonoHz > partida.TonoHz * 1.059,
        // El mismo texto: más pausada es más larga.
        CambioDiseno.MasPausada => nueva.Duracion > partida.Duracion * 1.08,
        CambioDiseno.MasAgil => nueva.Duracion < partida.Duracion * 0.92,
        CambioDiseno.MasIntensa => partida.Movimiento <= 0 || nueva.Movimiento > partida.Movimiento * 1.12,
        CambioDiseno.MasSuave => partida.Movimiento <= 0 || nueva.Movimiento < partida.Movimiento * 0.88,
        _ => true,
    };

    /// <summary>Los cambios que se pueden comprobar midiendo.</summary>
    public static bool SeMide(CambioDiseno cambio) => cambio is not (CambioDiseno.Igual or CambioDiseno.MasMayor or CambioDiseno.MasJoven);

    /// <summary>El cambio contrario («más grave» frente a «más aguda»): no se pueden pedir los dos a la vez.</summary>
    public static CambioDiseno? Opuesto(CambioDiseno cambio) => cambio switch
    {
        CambioDiseno.MasGrave => CambioDiseno.MasAguda,
        CambioDiseno.MasAguda => CambioDiseno.MasGrave,
        CambioDiseno.MasPausada => CambioDiseno.MasAgil,
        CambioDiseno.MasAgil => CambioDiseno.MasPausada,
        CambioDiseno.MasMayor => CambioDiseno.MasJoven,
        CambioDiseno.MasJoven => CambioDiseno.MasMayor,
        CambioDiseno.MasIntensa => CambioDiseno.MasSuave,
        CambioDiseno.MasSuave => CambioDiseno.MasIntensa,
        _ => null,
    };

    /// <summary>«Tono 112 Hz · 160 palabras por minuto», para quien quiera el dato.</summary>
    public static string Resumen(MedidasVoz medidas, string texto)
    {
        var palabras = Revision.Palabras(texto).Count;
        var ppm = medidas.Duracion > 0 ? palabras / medidas.Duracion * 60 : 0;
        return T("Tono {0:0} Hz · {1:0} palabras por minuto", medidas.TonoHz, ppm);
    }

    // --- Más así ----------------------------------------------------------------------------------

    public static IReadOnlyList<OpcionCambio> Cambios { get; } =
    [
        new(CambioDiseno.Igual, T("Parecidas")),
        new(CambioDiseno.MasGrave, T("Más grave")),
        new(CambioDiseno.MasAguda, T("Más aguda")),
        new(CambioDiseno.MasPausada, T("Más pausada")),
        new(CambioDiseno.MasAgil, T("Más ágil")),
        new(CambioDiseno.MasMayor, T("Más mayor")),
        new(CambioDiseno.MasJoven, T("Más joven")),
        new(CambioDiseno.MasIntensa, T("Más intensa")),
        new(CambioDiseno.MasSuave, T("Más suave")),
    ];

    public static string NombreCambio(CambioDiseno cambio) => Cambios.First(c => c.Cambio == cambio).Nombre;

    /// <summary>
    /// La descripción con los cambios pedidos. La fuerza va de 0 («un poco») a 2 («mucho»): si un cambio no se nota, se
    /// vuelve a pedir con más fuerza.
    /// </summary>
    public static string Instruccion(string instruccion, IReadOnlyList<CambioDiseno> cambios, int fuerza)
    {
        var frases = cambios.Select(c => Frase(c, fuerza)).Where(f => f.Length > 0).ToList();
        return frases.Count == 0 ? instruccion : $"{instruccion.Trim().TrimEnd('.')}. {string.Join(" ", frases)}";
    }

    private static string Frase(CambioDiseno cambio, int fuerza)
    {
        var cuanto = fuerza switch { 0 => "clearly", 1 => "much", _ => "very much" };
        return cambio switch
        {
            CambioDiseno.MasGrave => $"Make the voice {cuanto} deeper and lower-pitched.",
            CambioDiseno.MasAguda => $"Make the voice {cuanto} higher-pitched.",
            CambioDiseno.MasPausada => $"Speak {cuanto} more slowly, with longer pauses.",
            CambioDiseno.MasAgil => $"Speak {cuanto} faster and more lively.",
            CambioDiseno.MasMayor => $"Make the speaker sound {cuanto} older.",
            CambioDiseno.MasJoven => $"Make the speaker sound {cuanto} younger.",
            CambioDiseno.MasIntensa => $"Make the delivery {cuanto} more intense and dramatic.",
            CambioDiseno.MasSuave => $"Make the delivery {cuanto} softer and gentler.",
            _ => "",
        };
    }

    /// <summary>Los cambios que mueven el tono a propósito: con ellos no se comprueba el rango de tono pedido al principio.</summary>
    public static bool CambiaElTono(CambioDiseno cambio) =>
        cambio is CambioDiseno.MasGrave or CambioDiseno.MasAguda or CambioDiseno.MasMayor or CambioDiseno.MasJoven;
}

/// <summary>Un párrafo de narración para escuchar cómo sonará una voz al narrar (distinto del texto de muestra).</summary>
public static class TextosDeNarracion
{
    public static string Para(string idioma) => idioma switch
    {
        "Spanish" => "Dicen que en lo más profundo del bosque hay una casa que nadie se atreve a visitar. Aquella noche, cuando el " +
                     "viento dejó de soplar, alguien encendió una luz en la ventana del desván. Y entonces, muy despacio, la puerta " +
                     "empezó a abrirse.",
        "English" => "They say that deep in the forest there is a house nobody dares to visit. That night, when the wind stopped " +
                     "blowing, someone lit a lamp in the attic window. And then, very slowly, the door began to open.",
        _ => TextosDeReferencia.Para(idioma),
    };
}
