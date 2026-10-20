using Constantino.Core.Catalogo;

namespace Constantino.Core.Tests;

public class CatalogoTests
{
    [Fact]
    public void El_catalogo_real_es_valido()
    {
        var catalogo = CatalogoComponentes.CargarPredeterminado();

        Assert.Equal(1, catalogo.Version);
        Assert.Contains(catalogo.Componentes, c => c.Id == "nucleo" && c.Necesidad == Necesidad.Imprescindible);
        Assert.All(catalogo.Componentes, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Detalle));
            Assert.NotEmpty(c.Licencias);
            Assert.True(c.Tamano.ConGpuGb > 0);
        });
    }

    [Fact]
    public void Chatterbox_se_instala_en_su_propio_entorno_y_solo_baja_el_modelo_multilingue()
    {
        var c = CatalogoComponentes.CargarPredeterminado()["chatterbox"];

        Assert.Null(c.DisponibleEn);
        var pasos = c.Instalacion!.Pasos;
        Assert.Contains(pasos.OfType<PasoEjecutar>(), p => p.Argumentos.Contains("{indiceTorchCu124}"));
        // Sin gradio: solo lo usan sus demos y ocupa más de 1 GB.
        Assert.DoesNotContain(pasos.OfType<PasoEjecutar>().SelectMany(p => p.Argumentos), a => a.StartsWith("gradio", StringComparison.Ordinal));
        // Perth (la marca de agua) necesita pkg_resources: sin setuptools, Chatterbox falla al generar.
        Assert.Contains(pasos.OfType<PasoEjecutar>().SelectMany(p => p.Argumentos), a => a.StartsWith("setuptools==", StringComparison.Ordinal));
        Assert.Contains(pasos.OfType<PasoEjecutar>(), p => p.Argumentos.Any(a => a.Contains("PerthImplicitWatermarker", StringComparison.Ordinal)));
        var modelo = pasos.OfType<PasoModelo>().Single();
        Assert.Equal("{entornos}/chatterbox/Scripts/python.exe", modelo.Python);
        Assert.Contains("t3_mtl23ls_v2.safetensors", modelo.Archivos!);
    }

    [Fact]
    public void Todo_depende_del_nucleo_salvo_el_propio_nucleo()
    {
        var catalogo = CatalogoComponentes.CargarPredeterminado();

        foreach (var c in catalogo.Componentes.Where(c => c.Id != "nucleo"))
            Assert.Contains(catalogo.DependenciasDe(c.Id), d => d.Id == "nucleo");
    }

    [Fact]
    public void Las_dependencias_salen_en_orden_de_instalacion()
    {
        var catalogo = CatalogoComponentes.CargarPredeterminado();

        var deps = catalogo.DependenciasDe("modelo-qwen-base-1.7b").Select(c => c.Id).ToList();

        Assert.Equal(["nucleo", "motor-qwen"], deps);
    }

    [Fact]
    public void Rechaza_una_dependencia_que_no_existe()
    {
        var e = Assert.Throws<CatalogoInvalidoException>(() => CatalogoComponentes.Leer(Catalogo(
            Componente("a", dependeDe: "\"fantasma\""))));

        Assert.Contains("fantasma", e.Message);
    }

    [Fact]
    public void Rechaza_ids_repetidos()
    {
        var e = Assert.Throws<CatalogoInvalidoException>(() => CatalogoComponentes.Leer(Catalogo(
            Componente("a"), Componente("a"))));

        Assert.Contains("repetido", e.Message);
    }

    [Fact]
    public void Rechaza_ciclos()
    {
        var e = Assert.Throws<CatalogoInvalidoException>(() => CatalogoComponentes.Leer(Catalogo(
            Componente("a", dependeDe: "\"b\""), Componente("b", dependeDe: "\"a\""))));

        Assert.Contains("ciclo", e.Message);
    }

    [Fact]
    public void Rechaza_un_grupo_exclusivo_de_un_solo_componente()
    {
        var e = Assert.Throws<CatalogoInvalidoException>(() => CatalogoComponentes.Leer(Catalogo(
            Componente("a", grupoExclusivo: "solo"))));

        Assert.Contains("solo", e.Message);
    }

    [Fact]
    public void Las_listas_ausentes_quedan_vacias()
    {
        var catalogo = CatalogoComponentes.Leer("""
            { "version": 1, "componentes": [ {
              "id": "a", "nombre": "A", "grupo": "G", "necesidad": "opcional", "resumen": "r", "detalle": "d",
              "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {} } ] }
            """);

        Assert.Empty(catalogo["a"].DependeDe);
        Assert.Empty(catalogo["a"].Licencias);
    }

    private static string Catalogo(params string[] componentes) =>
        $$"""{ "version": 1, "componentes": [ {{string.Join(",", componentes)}} ] }""";

    private static string Componente(string id, string dependeDe = "", string? grupoExclusivo = null) =>
        $$"""
        { "id": "{{id}}", "nombre": "{{id}}", "grupo": "G", "necesidad": "opcional", "resumen": "r", "detalle": "d",
          "habilita": [], "dependeDe": [{{dependeDe}}], "licencias": [],
          {{(grupoExclusivo is null ? "" : $"\"grupoExclusivo\": \"{grupoExclusivo}\",")}}
          "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": { "funcionaEnCpu": true } }
        """;
}
