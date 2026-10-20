using Constantino.Core.Voces;

namespace Constantino.Core.Tests;

public class DisenoVozTests
{
    [Fact]
    public void Compone_la_descripcion_en_ingles_con_el_acento_castellano()
    {
        var d = new DescripcionGuiada(SexoVoz.Hombre, 40, "grave", "calida", "pausado", "misterioso", "castellano", "Like a campfire storyteller.");

        var texto = d.Componer("Spanish");

        Assert.StartsWith("A man around 40 years old, speaking Castilian Spanish from Spain", texto);
        Assert.Contains("not Latin American", texto);
        Assert.Contains("Deep, low-pitched, warm voice.", texto);
        Assert.Contains("Slow, calm pace", texto);
        Assert.Contains("Mysterious and slightly ominous tone.", texto);
        Assert.EndsWith("Like a campfire storyteller.", texto);
    }

    [Fact]
    public void En_otros_idiomas_pide_un_hablante_nativo_y_no_usa_el_acento_espanol()
    {
        var d = new DescripcionGuiada(SexoVoz.Mujer, 30, "aguda", "clara", "agil", "alegre", "castellano");

        var texto = d.Componer("Chinese");

        Assert.Contains("A woman around 30 years old, a native Mandarin Chinese speaker", texto);
        Assert.DoesNotContain("Castilian", texto);
    }

    [Fact]
    public void Una_clave_desconocida_no_rompe_la_descripcion()
    {
        var texto = new DescripcionGuiada(SexoVoz.Indiferente, 200, "?", "?", "?", "?").Componer("English");

        Assert.Contains("A person around 95 years old", texto);
        Assert.Contains("Deep, low-pitched, warm voice.", texto); // primeras opciones de cada lista
    }

    [Fact]
    public void Todos_los_ejemplos_usan_opciones_que_existen()
    {
        foreach (var (_, d) in CatalogoDiseno.Ejemplos)
        {
            Assert.NotNull(CatalogoDiseno.Buscar(CatalogoDiseno.Timbres, d.Timbre));
            Assert.NotNull(CatalogoDiseno.Buscar(CatalogoDiseno.Texturas, d.Textura));
            Assert.NotNull(CatalogoDiseno.Buscar(CatalogoDiseno.Ritmos, d.Ritmo));
            Assert.NotNull(CatalogoDiseno.Buscar(CatalogoDiseno.Tonos, d.Tono));
            Assert.NotNull(CatalogoDiseno.Buscar(CatalogoDiseno.Acentos, d.Acento));
        }
    }

    /// <summary>
    /// El texto de muestra se guarda como transcripción de la voz: tiene que pasar la comprobación de coherencia
    /// tanto si se lee deprisa (12 s) como despacio (25 s).
    /// </summary>
    [Theory]
    [MemberData(nameof(TodosLosIdiomas))]
    public void Los_textos_de_muestra_pasan_la_validacion_de_transcripcion(string idioma)
    {
        var texto = TextosDeReferencia.Para(idioma);

        BibliotecaVoces.ComprobarTranscripcion(texto, 12);
        BibliotecaVoces.ComprobarTranscripcion(texto, 25);
    }

    public static IEnumerable<object[]> TodosLosIdiomas => Idiomas.Todos.Select(i => new object[] { i.Clave });
}
