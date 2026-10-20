namespace Constantino.Core.Voces;

public enum SexoVoz
{
    Hombre,
    Mujer,
    Indiferente,
}

/// <summary>Una opción de la descripción guiada: lo que ve el usuario y cómo se le dice al modelo.</summary>
public sealed record OpcionDiseno(string Clave, string Nombre, string Ingles);

/// <summary>
/// La voz descrita con opciones sencillas. VoiceDesign entiende mejor las instrucciones en inglés (o chino),
/// así que se componen en inglés aunque el usuario elija en español; el texto final se puede editar a mano.
/// </summary>
public sealed record DescripcionGuiada(
    SexoVoz Sexo,
    int Edad,
    string Timbre,
    string Textura,
    string Ritmo,
    string Tono,
    string? Acento = null,
    string? Extra = null)
{
    public string Componer(string idioma)
    {
        var quien = Sexo switch
        {
            SexoVoz.Hombre => "A man",
            SexoVoz.Mujer => "A woman",
            _ => "A person",
        };
        var acento = idioma == "Spanish" && CatalogoDiseno.Buscar(CatalogoDiseno.Acentos, Acento) is { } a
            ? a.Ingles
            : $"a native {IdiomaIngles(idioma)} speaker with a natural accent";
        var partes = new List<string>
        {
            $"{quien} around {Math.Clamp(Edad, 10, 95)} years old, {acento}.",
            $"{Capitalizar(Texto(CatalogoDiseno.Timbres, Timbre))}, {Texto(CatalogoDiseno.Texturas, Textura)} voice.",
            $"{Capitalizar(Texto(CatalogoDiseno.Ritmos, Ritmo))}.",
            $"{Capitalizar(Texto(CatalogoDiseno.Tonos, Tono))} tone.",
        };
        if (!string.IsNullOrWhiteSpace(Extra))
            partes.Add(Capitalizar(Extra.Trim().TrimEnd('.')) + ".");
        return string.Join(" ", partes);
    }

    private static string Texto(IReadOnlyList<OpcionDiseno> opciones, string clave) =>
        CatalogoDiseno.Buscar(opciones, clave)?.Ingles ?? opciones[0].Ingles;

    private static string Capitalizar(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string IdiomaIngles(string idioma) => idioma switch
    {
        "Chinese" => "Mandarin Chinese",
        _ => idioma,
    };
}

public static class CatalogoDiseno
{
    public static IReadOnlyList<OpcionDiseno> Timbres { get; } =
    [
        new("grave", "Grave", "deep, low-pitched"),
        new("media", "Media", "medium-pitched"),
        new("aguda", "Aguda", "high-pitched"),
    ];

    public static IReadOnlyList<OpcionDiseno> Texturas { get; } =
    [
        new("calida", "Cálida", "warm"),
        new("suave", "Suave", "soft, smooth"),
        new("aterciopelada", "Aterciopelada", "velvety, rich"),
        new("clara", "Clara", "clear, crisp"),
        new("ronca", "Algo ronca", "slightly hoarse and raspy"),
        new("aspera", "Áspera", "rough, gravelly"),
        new("aireada", "Con aire, susurrante", "breathy, airy"),
    ];

    public static IReadOnlyList<OpcionDiseno> Ritmos { get; } =
    [
        new("muy-pausado", "Muy pausado", "very slow, unhurried pace with long dramatic pauses"),
        new("pausado", "Pausado", "slow, calm pace with natural pauses between sentences"),
        new("normal", "Normal", "natural conversational pace"),
        new("agil", "Ágil", "lively, fairly fast pace"),
    ];

    public static IReadOnlyList<OpcionDiseno> Tonos { get; } =
    [
        new("intimo", "Íntimo", "intimate and close, as if speaking to a single listener"),
        new("misterioso", "Misterioso", "mysterious and slightly ominous"),
        new("narrativo", "Cuentacuentos", "warm storyteller"),
        new("inquietante", "Inquietante", "unsettling, eerie and tense"),
        new("serio", "Serio", "serious and authoritative"),
        new("dramatico", "Dramático", "dramatic and intense"),
        new("alegre", "Alegre", "cheerful and friendly"),
    ];

    /// <summary>Solo para español: el acento es lo que más cuesta acertar, así que se pide muy explícito.</summary>
    public static IReadOnlyList<OpcionDiseno> Acentos { get; } =
    [
        new("castellano", "Castellano de España",
            "speaking Castilian Spanish from Spain with a clear peninsular accent (distinguishing the z and soft c from s), not Latin American"),
        new("andaluz", "Andaluz", "speaking Spanish with an Andalusian accent from southern Spain"),
        new("canario", "Canario", "speaking Spanish with a Canary Islands accent"),
        new("mexicano", "Mexicano", "speaking Mexican Spanish with a Mexican accent"),
        new("rioplatense", "Argentino / rioplatense", "speaking Rioplatense Spanish with an Argentinian accent"),
        new("latino", "Latino neutro", "speaking neutral Latin American Spanish"),
    ];

    public static OpcionDiseno? Buscar(IReadOnlyList<OpcionDiseno> opciones, string? clave) =>
        clave is null ? null : opciones.FirstOrDefault(o => o.Clave == clave);

    /// <summary>Puntos de partida para no empezar en blanco.</summary>
    public static IReadOnlyList<(string Nombre, DescripcionGuiada Descripcion)> Ejemplos { get; } =
    [
        ("Narrador de hoguera", new(SexoVoz.Hombre, 40, "grave", "calida", "pausado", "misterioso", "castellano",
            "Like someone telling a scary story around a campfire at night")),
        ("Abuelo cuentacuentos", new(SexoVoz.Hombre, 70, "grave", "ronca", "muy-pausado", "narrativo", "castellano",
            "Like a grandfather telling an old legend by the fireside")),
        ("Narradora misteriosa", new(SexoVoz.Mujer, 35, "media", "suave", "pausado", "misterioso", "castellano",
            "Like someone sharing a secret late at night")),
        ("Locutora de radio nocturna", new(SexoVoz.Mujer, 45, "media", "aterciopelada", "pausado", "intimo", "castellano",
            "Like a late-night radio host")),
        ("Voz inquietante", new(SexoVoz.Hombre, 55, "grave", "aspera", "muy-pausado", "inquietante", "castellano",
            "Whispering at times, as if hiding something terrible")),
    ];
}

/// <summary>
/// Textos de muestra para diseñar voces. Con fonemas variados y unos 15–20 s de lectura: el audio de la variante
/// elegida se guarda tal cual como grabación de referencia, con este texto como transcripción exacta.
/// </summary>
public static class TextosDeReferencia
{
    public static string Para(string idioma) => idioma switch
    {
        "Spanish" => "Aquella noche de invierno, el viejo guardabosques encendió la chimenea y nos contó una historia que jamás he olvidado. " +
                     "Hablaba despacio, con calma, mientras la lluvia golpeaba los cristales y el fuego crujía junto a nosotros.",
        "English" => "That winter night, the old forest keeper lit the fireplace and told us a story I have never forgotten. " +
                     "He spoke slowly and calmly, while the rain tapped against the windows and the fire crackled beside us.",
        "French" => "Cette nuit d'hiver, le vieux garde forestier alluma la cheminée et nous raconta une histoire que je n'ai jamais oubliée. " +
                    "Il parlait lentement, avec calme, pendant que la pluie frappait les vitres et que le feu crépitait près de nous.",
        "German" => "In jener Winternacht zündete der alte Förster den Kamin an und erzählte uns eine Geschichte, die ich nie vergessen habe. " +
                    "Er sprach langsam und ruhig, während der Regen gegen die Fenster schlug und das Feuer neben uns knisterte.",
        "Italian" => "Quella notte d'inverno, il vecchio guardaboschi accese il camino e ci raccontò una storia che non ho mai dimenticato. " +
                     "Parlava piano, con calma, mentre la pioggia batteva sui vetri e il fuoco scoppiettava accanto a noi.",
        "Portuguese" => "Naquela noite de inverno, o velho guarda-florestal acendeu a lareira e contou-nos uma história que nunca esqueci. " +
                        "Falava devagar, com calma, enquanto a chuva batia nos vidros e o fogo estalava ao nosso lado.",
        "Russian" => "Той зимней ночью старый лесник разжёг камин и рассказал нам историю, которую я никогда не забуду. " +
                     "Он говорил медленно и спокойно, пока дождь стучал в окна, а огонь тихо потрескивал рядом с нами.",
        "Chinese" => "那个冬天的夜晚，老护林人点燃了壁炉，给我们讲了一个我永远不会忘记的故事。" +
                     "他说话很慢，很平静，雨点轻轻敲打着窗户，炉火在我们身边噼啪作响。",
        "Japanese" => "あの冬の夜、年老いた森番は暖炉に火をともし、私が決して忘れることのない物語を語ってくれました。" +
                      "彼はゆっくりと落ち着いて話し、雨は窓をたたき、火は私たちのそばでぱちぱちと音を立てていました。",
        "Korean" => "그 겨울밤, 늙은 산지기는 벽난로에 불을 피우고 내가 결코 잊지 못할 이야기를 들려주었다. " +
                    "그는 천천히, 차분하게 말했고, 빗방울은 창문을 두드렸으며 불은 우리 곁에서 탁탁 소리를 냈다.",
        _ => Para("English"),
    };
}
