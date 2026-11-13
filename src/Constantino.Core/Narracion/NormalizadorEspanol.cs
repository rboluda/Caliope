using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Constantino.Core.Narracion;

/// <summary>
/// Pasa a palabras lo que los motores de voz leen mal en español: cifras, fechas, horas, monedas, porcentajes,
/// ordinales, unidades, números romanos y abreviaturas («1.250 €» → «mil doscientos cincuenta euros»).
/// </summary>
/// <remarks>
/// Sigue la escritura de España: punto para los miles y coma para los decimales. Los números concuerdan con la palabra
/// que les sigue cuando se puede deducir su género («21 personas» → «veintiuna personas», «1 coche» → «un coche»).
/// Lo que no reconoce lo deja como está.
/// </remarks>
public static partial class NormalizadorEspanol
{
    private enum Genero { Masculino, Femenino }

    /// <summary>Hasta aquí se leen los números como cantidad; los más largos, cifra a cifra.</summary>
    private const long Limite = 1_000_000_000_000;

    public static string Normalizar(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return texto;
        texto = Abreviaturas(texto);
        texto = Fechas(texto);
        texto = Horas(texto);
        texto = Signos(texto);
        texto = Monedas(texto);
        texto = Porcentajes(texto);
        texto = Ordinales(texto);
        texto = Unidades(texto);
        texto = Romanos(texto);
        texto = Rangos(texto);
        texto = Numeros(texto);
        return EspaciosDobles().Replace(texto, " ");
    }

    // --- Abreviaturas ---------------------------------------------------------------------------

    /// <summary>Tratamientos y similares: van delante de un nombre, así que su punto nunca cierra la frase.</summary>
    private static readonly (string Escrito, string Dicho)[] Tratamientos =
    [
        ("Srta.", "señorita"), ("Sras.", "señoras"), ("Sres.", "señores"), ("Sra.", "señora"), ("Sr.", "señor"),
        ("Dra.", "doctora"), ("Dr.", "doctor"), ("Dña.", "doña"), ("D.ª", "doña"), ("Uds.", "ustedes"), ("Ud.", "usted"),
        ("Vds.", "ustedes"), ("Vd.", "usted"), ("Avda.", "avenida"), ("Av.", "avenida"), ("Pza.", "plaza"),
        ("n.º", "número"), ("nº", "número"), ("N.º", "número"), ("Nº", "número"), ("núm.", "número"), ("Núm.", "número"),
        ("págs.", "páginas"), ("pág.", "página"), ("tel.", "teléfono"), ("Tel.", "teléfono"), ("Telf.", "teléfono"),
        ("p. ej.", "por ejemplo"), ("p.ej.", "por ejemplo"), ("P. ej.", "por ejemplo"),
        ("EE. UU.", "Estados Unidos"), ("EE.UU.", "Estados Unidos"), ("a. C.", "antes de Cristo"), ("d. C.", "después de Cristo"),
    ];

    /// <summary>Abreviaturas que pueden ir al final de una frase: si cierran la frase, se conserva el punto.</summary>
    private static readonly (string Escrito, string Dicho)[] FinalesDeFrase =
    [
        ("etc.", "etcétera"), ("aprox.", "aproximadamente"),
    ];

    private static string Abreviaturas(string texto)
    {
        foreach (var (escrito, dicho) in Tratamientos)
            texto = Regex.Replace(texto, $@"(?<![\p{{L}}\d]){Regex.Escape(escrito)}(?![\p{{L}}])", dicho);
        texto = Regex.Replace(texto, @"(?<![\p{L}\d])[Cc]/\s?(?=\p{Lu})", "calle ");
        // «s. XIX» → «siglo XIX» (el número romano se lee después).
        texto = Regex.Replace(texto, @"(?<![\p{L}\d])s\.\s?(?=[IVX]+\b)", "siglo ");
        foreach (var (escrito, dicho) in FinalesDeFrase)
        {
            var actual = texto;
            texto = Regex.Replace(actual, $@"(?<![\p{{L}}\d]){Regex.Escape(escrito)}", m =>
            {
                // Si detrás termina el texto o empieza otra frase, el punto también cerraba la frase.
                var resto = actual[(m.Index + m.Length)..].TrimStart();
                return dicho + (resto.Length == 0 || char.IsUpper(resto[0]) ? "." : "");
            });
        }
        return texto;
    }

    // --- Fechas y horas -------------------------------------------------------------------------

    private static readonly string[] Meses =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static string Fechas(string texto) =>
        Fecha().Replace(texto, m =>
        {
            var dia = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var mes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var anio = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (dia is < 1 or > 31 || mes is < 1 or > 12)
                return m.Value;
            if (m.Groups[3].Value.Length == 2)
                anio += anio < 50 ? 2000 : 1900;
            return $"{Cardinal(dia)} de {Meses[mes - 1]} de {Cardinal(anio)}";
        });

    /// <summary>«14:30», «9:05 h» y «14.30 h» (con punto solo si lleva la «h»: «3.50» es un decimal).</summary>
    private static string Horas(string texto) => HoraConPunto().Replace(Hora().Replace(texto, LeerHora), LeerHora);

    private static string LeerHora(Match m)
    {
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (h > 24 || min > 59)
            return m.Value;
        // Las horas son femeninas: «la una», «las veintiuna».
        var hora = Cardinal(h, Genero.Femenino);
        var minutos = min switch
        {
            0 => m.Groups[3].Success ? " horas" : " en punto",
            15 => " y cuarto",
            30 => " y media",
            _ => " y " + Cardinal(min),
        };
        return hora + minutos;
    }

    // --- Monedas y porcentajes ------------------------------------------------------------------

    private sealed record Moneda(string Singular, string Plural, Genero Genero, string Centimo, string Centimos);

    private static readonly Dictionary<string, Moneda> Monedas_ = new(StringComparer.Ordinal)
    {
        ["€"] = new("euro", "euros", Genero.Masculino, "céntimo", "céntimos"),
        ["EUR"] = new("euro", "euros", Genero.Masculino, "céntimo", "céntimos"),
        ["$"] = new("dólar", "dólares", Genero.Masculino, "centavo", "centavos"),
        ["USD"] = new("dólar", "dólares", Genero.Masculino, "centavo", "centavos"),
        ["£"] = new("libra", "libras", Genero.Femenino, "penique", "peniques"),
    };

    private static string Monedas(string texto)
    {
        // «3 M€», «3,5 M€»: millones.
        texto = Millones().Replace(texto, m => m.Groups[1].Value == "1"
            ? $"un millón de {Monedas_[m.Groups[2].Value].Plural}"
            : $"{LeerCantidad(m.Groups[1].Value, Genero.Masculino, false)} millones de {Monedas_[m.Groups[2].Value].Plural}");
        texto = MonedaDetras().Replace(texto, m => Importe(m.Groups[1].Value, Monedas_[m.Groups[2].Value]));
        return MonedaDelante().Replace(texto, m => Importe(m.Groups[2].Value, Monedas_[m.Groups[1].Value]));
    }

    private static string Importe(string cifra, Moneda moneda)
    {
        if (!Partir(cifra, out var entero, out var decimales) || entero >= Limite)
            return cifra + " " + moneda.Plural;
        var centimos = decimales.Length == 0 ? 0 : int.Parse(decimales.PadRight(2, '0')[..2], CultureInfo.InvariantCulture);
        var partes = new List<string>();
        if (entero > 0 || centimos == 0)
        {
            var nombre = entero == 1 ? moneda.Singular : moneda.Plural;
            var de = entero >= 1_000_000 && entero % 1_000_000 == 0 ? " de" : "";
            partes.Add($"{Cardinal(entero, moneda.Genero, apocope: true)}{de} {nombre}");
        }
        if (centimos > 0)
            partes.Add($"{Cardinal(centimos, Genero.Masculino, apocope: true)} {(centimos == 1 ? moneda.Centimo : moneda.Centimos)}");
        return string.Join(" con ", partes);
    }

    private static string Porcentajes(string texto) =>
        Porcentaje().Replace(texto, m => LeerCantidad(m.Groups[1].Value, Genero.Masculino, false) + " por ciento");

    // --- Ordinales ------------------------------------------------------------------------------

    private static readonly string[] OrdinalesUnidades =
        ["", "primero", "segundo", "tercero", "cuarto", "quinto", "sexto", "séptimo", "octavo", "noveno"];

    private static readonly string[] OrdinalesDecenas =
        ["", "décimo", "vigésimo", "trigésimo", "cuadragésimo", "quincuagésimo", "sexagésimo", "septuagésimo", "octogésimo", "nonagésimo"];

    private static readonly string[] OrdinalesDiez =
        ["décimo", "undécimo", "duodécimo", "decimotercero", "decimocuarto", "decimoquinto", "decimosexto", "decimoséptimo",
         "decimoctavo", "decimonoveno"];

    private static string Ordinales(string texto) =>
        Ordinal().Replace(texto, m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var marca = m.Groups[2].Value;
            if (n is < 1 or > 100)
                return m.Value;
            var genero = marca == "ª" || marca == "a" ? Genero.Femenino : Genero.Masculino;
            var apocope = marca == "er" || (genero == Genero.Masculino && AntesDeNombre(texto, m.Index + m.Length));
            return OrdinalEnLetras(n, genero, apocope);
        });

    private static string OrdinalEnLetras(int n, Genero genero, bool apocope)
    {
        string palabras;
        if (n == 100)
            palabras = "centésimo";
        else if (n is >= 10 and < 20)
            palabras = OrdinalesDiez[n - 10];
        else if (n < 10)
            palabras = OrdinalesUnidades[n];
        else
            palabras = (OrdinalesDecenas[n / 10] + " " + OrdinalesUnidades[n % 10]).Trim();
        if (genero == Genero.Femenino)
            return string.Join(" ", palabras.Split(' ').Select(p => p[..^1] + "a"));
        // «primer piso», «tercer día», «decimotercer aniversario».
        return apocope && (palabras.EndsWith("primero", StringComparison.Ordinal) || palabras.EndsWith("tercero", StringComparison.Ordinal))
            ? palabras[..^1]
            : palabras;
    }

    // --- Unidades -------------------------------------------------------------------------------

    private sealed record Unidad(string Singular, string Plural, Genero Genero);

    private static readonly Dictionary<string, Unidad> Unidades_ = new(StringComparer.Ordinal)
    {
        ["km/h"] = new("kilómetro por hora", "kilómetros por hora", Genero.Masculino),
        ["km"] = new("kilómetro", "kilómetros", Genero.Masculino),
        ["m"] = new("metro", "metros", Genero.Masculino),
        ["cm"] = new("centímetro", "centímetros", Genero.Masculino),
        ["mm"] = new("milímetro", "milímetros", Genero.Masculino),
        ["kg"] = new("kilo", "kilos", Genero.Masculino),
        ["g"] = new("gramo", "gramos", Genero.Masculino),
        ["l"] = new("litro", "litros", Genero.Masculino),
        ["L"] = new("litro", "litros", Genero.Masculino),
        ["ml"] = new("mililitro", "mililitros", Genero.Masculino),
        ["h"] = new("hora", "horas", Genero.Femenino),
        ["min"] = new("minuto", "minutos", Genero.Masculino),
        ["°C"] = new("grado", "grados", Genero.Masculino),
        ["ºC"] = new("grado", "grados", Genero.Masculino),
        ["°"] = new("grado", "grados", Genero.Masculino),
    };

    private static string Unidades(string texto) =>
        Unidad_().Replace(texto, m =>
        {
            var u = Unidades_[m.Groups[2].Value];
            var cifra = m.Groups[1].Value;
            var uno = Partir(cifra, out var entero, out var decimales) && entero == 1 && decimales.Length == 0;
            return $"{LeerCantidad(cifra, u.Genero, true)} {(uno ? u.Singular : u.Plural)}";
        });

    // --- Números romanos ------------------------------------------------------------------------

    private static string Romanos(string texto)
    {
        // «siglo XIX» → «siglo diecinueve».
        texto = Siglo().Replace(texto, m => m.Groups[1].Value + Cardinal(Romano(m.Groups[2].Value)));
        // «Felipe II» → «Felipe segundo»; «Alfonso XIII» → «Alfonso trece». Solo detrás de un nombre propio.
        return RomanoTrasNombre().Replace(texto, m =>
        {
            var n = Romano(m.Groups[2].Value);
            return m.Groups[1].Value + (n <= 10 ? OrdinalEnLetras(n, Genero.Masculino, false) : Cardinal(n));
        });
    }

    private static int Romano(string r)
    {
        int Valor(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, _ => 0 };
        var total = 0;
        for (var i = 0; i < r.Length; i++)
        {
            var v = Valor(r[i]);
            total += i + 1 < r.Length && Valor(r[i + 1]) > v ? -v : v;
        }
        return total;
    }

    // --- Rangos, signos y números sueltos -------------------------------------------------------

    /// <summary>«1990-2000» → «1990 a 2000» (solo dos números: un teléfono con guiones no es un rango).</summary>
    private static string Rangos(string texto) => Rango().Replace(texto, "$1 a $2");

    private static string Signos(string texto) =>
        Signo().Replace(texto, m => m.Groups[1].Value + (m.Groups[2].Value == "+" ? "más " : "menos "));

    private static string Numeros(string texto) =>
        Numero().Replace(texto, m =>
        {
            // Un año («en 2021 hubo…») se lee entero: «dos mil veintiuno», nunca «veintiún».
            if (m.Value.Length == 4 && m.Value.All(char.IsDigit) && m.Value[0] is '1' or '2')
                return Cardinal(long.Parse(m.Value, CultureInfo.InvariantCulture));
            var siguiente = PalabraSiguiente(texto, m.Index + m.Length);
            var genero = siguiente is null ? Genero.Masculino : GeneroDe(siguiente);
            return LeerCantidad(m.Value, genero, AntesDeNombre(texto, m.Index + m.Length));
        });

    /// <summary>Lee una cifra escrita («1.250», «3,5», «007», «1.3.1»).</summary>
    private static string LeerCantidad(string cifra, Genero genero, bool antesDeNombre)
    {
        // Muchos separadores que no son de miles (versiones, direcciones): parte a parte, con «punto».
        if (!Partir(cifra, out var entero, out var decimales))
            return cifra.All(char.IsDigit)
                ? CifraACifra(cifra)
                : string.Join(" punto ", cifra.Split('.', ',').Where(p => p.Length > 0).Select(p => LeerCantidad(p, Genero.Masculino, false)));
        var soloCifras = cifra.All(char.IsDigit);
        // Ceros a la izquierda («007») o números muy largos sin separadores (teléfonos): cifra a cifra.
        if (soloCifras && ((cifra.Length > 1 && cifra[0] == '0') || cifra.Length >= 9) || entero >= Limite)
            return CifraACifra(cifra.Replace(".", "").Replace(",", ""));
        if (decimales.Length > 0)
        {
            var parteDecimal = decimales[0] == '0' || decimales.Length > 3
                ? CifraACifra(decimales)
                : Cardinal(long.Parse(decimales, CultureInfo.InvariantCulture));
            return $"{Cardinal(entero)} coma {parteDecimal}";
        }
        var de = antesDeNombre && entero >= 1_000_000 && entero % 1_000_000 == 0 ? " de" : "";
        return Cardinal(entero, genero, antesDeNombre) + de;
    }

    /// <summary>
    /// Separa la parte entera y la decimal. Vale «1.250», «1.250,5», «3,5» y «3.5» (un punto sin grupos de tres es
    /// decimal). Devuelve false si los separadores no encajan con ningún número (por ejemplo, «1.3.1»).
    /// </summary>
    private static bool Partir(string cifra, out long entero, out string decimales)
    {
        entero = 0;
        decimales = "";
        var coma = cifra.Split(',');
        if (coma.Length > 2)
            return false;
        var izquierda = coma[0];
        if (coma.Length == 2)
            decimales = coma[1];
        var puntos = izquierda.Split('.');
        if (puntos.Length > 1)
        {
            var miles = puntos[0].Length is >= 1 and <= 3 && puntos.Skip(1).All(p => p.Length == 3);
            if (!miles)
            {
                // «3.5»: decimal con punto, solo si no hay coma y hay un único punto.
                if (puntos.Length != 2 || coma.Length == 2)
                    return false;
                izquierda = puntos[0];
                decimales = puntos[1];
            }
            else
            {
                izquierda = string.Concat(puntos);
            }
        }
        if (izquierda.Length == 0 || izquierda.Length > 13 || !izquierda.All(char.IsDigit) || !decimales.All(char.IsDigit))
            return false;
        entero = long.Parse(izquierda, CultureInfo.InvariantCulture);
        return true;
    }

    private static readonly string[] Digitos = ["cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve"];

    private static string CifraACifra(string cifras) =>
        string.Join(" ", cifras.Where(char.IsDigit).Select(c => Digitos[c - '0']));

    // --- Cardinales -----------------------------------------------------------------------------

    private static readonly string[] Primeros =
    [
        "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve", "diez", "once", "doce", "trece",
        "catorce", "quince", "dieciséis", "diecisiete", "dieciocho", "diecinueve", "veinte", "veintiuno", "veintidós",
        "veintitrés", "veinticuatro", "veinticinco", "veintiséis", "veintisiete", "veintiocho", "veintinueve",
    ];

    private static readonly string[] Decenas = ["", "", "", "treinta", "cuarenta", "cincuenta", "sesenta", "setenta", "ochenta", "noventa"];

    private static readonly string[] Centenas =
        ["", "ciento", "doscientos", "trescientos", "cuatrocientos", "quinientos", "seiscientos", "setecientos", "ochocientos", "novecientos"];

    /// <param name="apocope">Delante de un nombre: «un coche», «veintiún años» (en masculino).</param>
    private static string Cardinal(long n, Genero genero = Genero.Masculino, bool apocope = false)
    {
        if (n == 0)
            return "cero";
        var partes = new List<string>();
        var millones = n / 1_000_000;
        var miles = n / 1000 % 1000;
        var resto = n % 1000;
        if (millones > 0)
            partes.Add(millones == 1 ? "un millón" : Cardinal(millones, Genero.Masculino, apocope: true) + " millones");
        if (miles > 0)
            partes.Add(miles == 1 ? "mil" : MenorDeMil((int)miles, genero, apocope: true) + " mil");
        if (resto > 0)
            partes.Add(MenorDeMil((int)resto, genero, apocope));
        return string.Join(" ", partes);
    }

    private static string MenorDeMil(int n, Genero genero, bool apocope)
    {
        var partes = new List<string>();
        var c = n / 100;
        var r = n % 100;
        if (c > 0)
        {
            var centena = n == 100 ? "cien" : Centenas[c];
            if (genero == Genero.Femenino && c > 1)
                centena = centena[..^2] + "as";
            partes.Add(centena);
        }
        if (r > 0)
        {
            var decena = r < 30 ? Primeros[r] : Decenas[r / 10] + (r % 10 > 0 ? " y " + Primeros[r % 10] : "");
            partes.Add(Uno(decena, genero, apocope));
        }
        return string.Join(" ", partes);
    }

    /// <summary>Concordancia del «uno» final: «una», «veintiuna», «un», «veintiún».</summary>
    private static string Uno(string palabras, Genero genero, bool apocope)
    {
        if (!palabras.EndsWith("uno", StringComparison.Ordinal))
            return palabras;
        if (genero == Genero.Femenino)
            return palabras[..^1] + "a";
        if (!apocope)
            return palabras;
        return palabras == "veintiuno" ? "veintiún" : palabras[..^1];
    }

    // --- Género de la palabra siguiente ---------------------------------------------------------

    /// <summary>Palabras tras las que el número va suelto, sin concordar: «el 1 de mayo», «uno de cada tres».</summary>
    private static readonly HashSet<string> NoNombres = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "del", "a", "al", "y", "e", "o", "u", "ni", "en", "por", "con", "para", "que", "entre", "sobre", "hasta", "desde",
        "sin", "tras", "es", "son", "fue", "era", "más", "menos", "coma", "punto", "mil",
    };

    private static readonly HashSet<string> Femeninas = new(StringComparer.OrdinalIgnoreCase)
    {
        "vez", "veces", "noche", "noches", "mujer", "mujeres", "calle", "calles", "llave", "llaves", "clase", "clases", "parte",
        "partes", "muerte", "muertes", "gente", "tarde", "tardes", "nave", "naves", "fuente", "fuentes", "imagen", "imágenes",
        "flor", "flores", "piel", "pieles", "ley", "leyes", "red", "redes", "luz", "luces", "voz", "voces", "cruz", "cruces",
        "mano", "manos", "foto", "fotos", "moto", "motos", "radio", "nariz", "raíz", "raíces", "carne", "carnes", "nube", "nubes",
        "torre", "torres", "frase", "frases", "serie", "series", "especie", "especies", "base", "bases", "fase", "fases", "mente",
        "mentes", "suerte", "suertes", "señal", "señales", "cárcel", "cárceles", "crisis", "dosis", "tesis", "orden", "sal",
        "catedral", "catedrales", "sartén", "sartenes", "miel", "nieve", "sangre", "sed", "leche", "llamada", "hora", "horas",
    };

    private static readonly HashSet<string> Masculinas = new(StringComparer.OrdinalIgnoreCase)
    {
        "día", "días", "mapa", "mapas", "problema", "problemas", "programa", "programas", "sistema", "sistemas", "tema", "temas",
        "idioma", "idiomas", "clima", "climas", "planeta", "planetas", "poema", "poemas", "drama", "dramas", "esquema",
        "esquemas", "dilema", "dilemas", "sofá", "sofás", "papá", "papás", "tranvía", "tranvías", "pijama", "pijamas",
        "mediodía", "cometa", "cometas", "telegrama", "telegramas", "diagrama", "diagramas", "fantasma", "fantasmas",
        "panorama", "síntoma", "síntomas", "aroma", "aromas", "enigma", "enigmas", "dogma", "dogmas", "lema", "lemas",
    };

    private static Genero GeneroDe(string palabra)
    {
        if (Femeninas.Contains(palabra))
            return Genero.Femenino;
        if (Masculinas.Contains(palabra))
            return Genero.Masculino;
        var p = palabra.ToLowerInvariant();
        // Las profesiones en -ista valen para los dos: se lee en masculino genérico.
        if (p.EndsWith("ista", StringComparison.Ordinal) || p.EndsWith("istas", StringComparison.Ordinal))
            return Genero.Masculino;
        string[] femeninos = ["a", "as", "ción", "ciones", "sión", "siones", "dad", "dades", "tad", "tades", "tud", "tudes", "umbre", "umbres"];
        return femeninos.Any(f => p.EndsWith(f, StringComparison.Ordinal)) ? Genero.Femenino : Genero.Masculino;
    }

    private static string? PalabraSiguiente(string texto, int desde)
    {
        var m = Siguiente().Match(texto, desde);
        return m.Success && m.Index == desde ? m.Groups[1].Value : null;
    }

    /// <summary>Si detrás viene un nombre (no una preposición ni un signo): «1 coche» → «un coche».</summary>
    private static bool AntesDeNombre(string texto, int desde) =>
        PalabraSiguiente(texto, desde) is { } p && !NoNombres.Contains(p);

    // --- Expresiones ----------------------------------------------------------------------------

    private const string Cifra = @"\d{1,3}(?:\.\d{3})+(?:,\d+)?|\d+(?:[.,]\d+)?";

    [GeneratedRegex(@"(?<![\d/])(\d{1,2})[/-](\d{1,2})[/-](\d{4}|\d{2})(?![\d/])")]
    private static partial Regex Fecha();

    [GeneratedRegex(@"(?<![\d:.,])(\d{1,2}):(\d{2})(\s?h\b)?(?![\d:])")]
    private static partial Regex Hora();

    [GeneratedRegex(@"(?<![\d:.,])(\d{1,2})\.(\d{2})(\s?h\b)(?![\d:])")]
    private static partial Regex HoraConPunto();

    [GeneratedRegex(@"(?<![\d.,])(" + Cifra + @")\s?M(€|\$|£)(?!\w)")]
    private static partial Regex Millones();

    [GeneratedRegex(@"(?<![\d.,\p{L}])(" + Cifra + @")\s?(€|EUR|USD|\$|£)(?!\p{L})")]
    private static partial Regex MonedaDetras();

    [GeneratedRegex(@"(€|\$|£)\s?(" + Cifra + @")(?![\d\p{L}])")]
    private static partial Regex MonedaDelante();

    [GeneratedRegex(@"(?<![\d.,\p{L}])(" + Cifra + @")\s?%")]
    private static partial Regex Porcentaje();

    [GeneratedRegex(@"(?<![\d.,\p{L}])(\d{1,3})\.?(º|ª|er)(?![\p{L}\d])")]
    private static partial Regex Ordinal();

    [GeneratedRegex(@"(?<![\d.,\p{L}])(" + Cifra + @")\s?(km/h|km|cm|mm|kg|ml|min|°C|ºC|°|m|g|l|L|h)(?![\p{L}\d/])")]
    private static partial Regex Unidad_();

    [GeneratedRegex(@"(\b[Ss]iglos?\s+)([IVX]{1,6})\b")]
    private static partial Regex Siglo();

    [GeneratedRegex(@"(\b\p{Lu}\p{Ll}+\s+)(II|III|IV|VI|VII|VIII|IX|XI|XII|XIII|XIV|XV|XVI|XVII|XVIII|XIX|XX)\b")]
    private static partial Regex RomanoTrasNombre();

    [GeneratedRegex(@"(?<![\d.,\-–])(\d+)\s?[-–]\s?(\d+)(?![\d.,]*\s?[-–]\s?\d)")]
    private static partial Regex Rango();

    [GeneratedRegex(@"(^|[\s(])([-−+])(?=\d)")]
    private static partial Regex Signo();

    [GeneratedRegex(@"(?<![\p{L}\d])\d+(?:[.,]\d+)*(?![\p{L}\d])")]
    private static partial Regex Numero();

    [GeneratedRegex(@"\s+(\p{L}+)")]
    private static partial Regex Siguiente();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex EspaciosDobles();
}
