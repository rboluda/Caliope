using System.Text.Json;
using System.Text.RegularExpressions;

namespace Constantino.Core.Tests;

/// <summary>
/// Que no se quede ningún texto sin traducir: cada texto envuelto en T(...) en el código, en {l:T '...'} en el XAML
/// y cada texto visible del catálogo tiene su traducción al inglés, con los mismos huecos {0}, {1}…
/// </summary>
public partial class IdiomaTests
{
    private static readonly string Raiz = BuscarRaiz();
    private static readonly IReadOnlyDictionary<string, string> Ingles = Idioma.Tabla(Idioma.Ingles);

    private static string BuscarRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Constantino.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encuentra la raíz del repositorio.");
    }

    private static IEnumerable<string> Fuentes(string patron) =>
        Directory.EnumerateFiles(Path.Combine(Raiz, "src"), patron, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // T("…") o T("…" + "…"): las partes concatenadas forman un solo texto.
    [GeneratedRegex(@"(?:(?<![\w.])|(?<=Idioma\.))T\(\s*(""(?:[^""\\]|\\.)*""(?:\s*\+\s*""(?:[^""\\]|\\.)*"")*)")]
    private static partial Regex LlamadaCs();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\{l:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex ExtensionXaml();

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex Hueco();

    private static IEnumerable<(string Texto, string Origen)> Textos()
    {
        foreach (var f in Fuentes("*.cs"))
            foreach (Match m in LlamadaCs().Matches(File.ReadAllText(f)))
                yield return (Regex.Unescape(string.Concat(Literal().Matches(m.Groups[1].Value).Select(l => l.Groups[1].Value))),
                    Path.GetFileName(f));
        foreach (var f in Fuentes("*.xaml"))
            foreach (Match m in ExtensionXaml().Matches(File.ReadAllText(f)))
                // El atributo ya viene decodificado de XML en tiempo de ejecución: se decodifica igual aquí.
                yield return (System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Replace("\\'", "'").Replace("\\\\", "\\"),
                    Path.GetFileName(f));

        using var catalogo = JsonDocument.Parse(File.ReadAllText(Path.Combine(Raiz, "catalogo", "catalogo.json")));
        foreach (var c in catalogo.RootElement.GetProperty("componentes").EnumerateArray())
        {
            foreach (var campo in new[] { "nombre", "grupo", "resumen", "detalle", "consejo", "tecnico", "disponibleEn" })
                if (c.TryGetProperty(campo, out var v) && v.GetString() is { Length: > 0 } s)
                    yield return (s, "catalogo.json");
            if (c.TryGetProperty("habilita", out var habilita))
                foreach (var h in habilita.EnumerateArray())
                    yield return (h.GetString()!, "catalogo.json");
            if (c.TryGetProperty("instalacion", out var inst))
                foreach (var p in inst.GetProperty("pasos").EnumerateArray())
                    yield return (p.GetProperty("descripcion").GetString()!, "catalogo.json");
        }
    }

    [Fact]
    public void Todos_los_textos_tienen_traduccion_al_ingles()
    {
        var faltan = Textos().Where(t => !Ingles.ContainsKey(t.Texto)).Select(t => $"{t.Origen}: {t.Texto}").Distinct().ToList();

        Assert.True(faltan.Count == 0, $"Faltan {faltan.Count} traducciones:\n" + string.Join("\n", faltan.Take(40)));
    }

    [Fact]
    public void Las_traducciones_conservan_los_huecos()
    {
        static string Huecos(string s) => string.Join(",", Hueco().Matches(s).Select(m => m.Groups[1].Value).Distinct().Order());

        var distintos = Ingles.Where(p => Huecos(p.Key) != Huecos(p.Value)).Select(p => p.Key).ToList();

        Assert.True(distintos.Count == 0, "Huecos distintos en:\n" + string.Join("\n", distintos));
    }

    [Fact]
    public void Ninguna_traduccion_esta_vacia()
    {
        Assert.DoesNotContain(Ingles, p => string.IsNullOrWhiteSpace(p.Value));
    }

    [Fact]
    public void Sin_activar_ingles_los_textos_salen_en_espanol()
    {
        Assert.Equal("Guardar", Idioma.T("Guardar"));
        Assert.Equal("4,5 GB", Formato.Gb(4.5));
    }
}
