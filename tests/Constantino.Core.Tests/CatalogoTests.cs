using Constantino.Core.Catalogo;
using Constantino.Core.Motores;

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
    public void Chatterbox_se_instala_en_su_propio_entorno_con_el_paquete_de_castellano()
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
        Assert.Contains(pasos.OfType<PasoEjecutar>().SelectMany(p => p.Argumentos),
            a => a.StartsWith("chatterbox-tts @ https://github.com/resemble-ai/chatterbox/archive/", StringComparison.Ordinal));
        // Del repositorio general solo la huella de voz y las condiciones; el resto, del paquete de castellano.
        var modelos = pasos.OfType<PasoModelo>().ToList();
        Assert.Equal(2, modelos.Count);
        Assert.All(modelos, m => Assert.Equal("{entornos}/chatterbox/Scripts/python.exe", m.Python));
        Assert.Equal(["ve.pt", "conds.pt"], modelos[0].Archivos!);
        Assert.Equal("ResembleAI/Chatterbox-Multilingual-es-es", modelos[1].Repositorio);
        Assert.Contains("t3_es_es.safetensors", modelos[1].Archivos!);
        Assert.True(c.Requisitos.SinRtx50);
    }

    [Fact]
    public void VoxCpm_se_instala_sin_las_dependencias_de_sus_demos()
    {
        var c = CatalogoComponentes.CargarPredeterminado()["voxcpm2"];

        var pasos = c.Instalacion!.Pasos;
        var argumentos = pasos.OfType<PasoEjecutar>().SelectMany(p => p.Argumentos).ToList();
        Assert.Contains("--no-deps", argumentos);
        Assert.Contains("voxcpm==2.0.3", argumentos);
        foreach (var sobra in new[] { "gradio", "modelscope", "funasr", "datasets" })
            Assert.DoesNotContain(argumentos, a => a.StartsWith(sobra + "==", StringComparison.Ordinal));
        Assert.Contains(pasos.OfType<PasoEjecutar>(), p => p.Argumentos.Contains("{indiceTorch}"));
        var modelo = pasos.OfType<PasoModelo>().Single();
        Assert.Equal("openbmb/VoxCPM2", modelo.Repositorio);
        Assert.Equal("{entornos}/voxcpm2/Scripts/python.exe", modelo.Python);
        Assert.False(c.Requisitos.FuncionaEnCpu);
    }

    [Fact]
    public void El_doblaje_tiene_separador_de_voz_y_traductor_con_versiones_fijadas()
    {
        var catalogo = CatalogoComponentes.CargarPredeterminado();

        var separador = catalogo["separador-voz"];
        Assert.Contains("demucs==4.1.0", separador.Instalacion!.Pasos.OfType<PasoEjecutar>().SelectMany(p => p.Argumentos));
        var modelo = separador.Instalacion.Pasos.OfType<PasoModelo>().Single();
        Assert.Equal("adefossez/HTDemucs", modelo.Repositorio);
        Assert.Equal(40, modelo.Revision.Length);

        var traductor = catalogo["traductor"];
        var descargas = traductor.Instalacion!.Pasos.OfType<PasoDescargar>().ToList();
        Assert.Equal(2, descargas.Count);
        Assert.All(descargas, d => Assert.Equal(64, d.Sha256.Length));
        Assert.Contains(traductor.Instalacion.Comprobar, r => r.EndsWith("llama-server.exe", StringComparison.Ordinal));
        Assert.Equal(separador.Grupo, traductor.Grupo);
    }

    [Fact]
    public void Cada_motor_tiene_su_componente_en_el_catalogo()
    {
        var catalogo = CatalogoComponentes.CargarPredeterminado();

        foreach (var motor in MotoresDeVoz.Todos)
        {
            var c = catalogo[MotoresDeVoz.Componente(motor)];
            if (motor != MotoresDeVoz.Qwen)
            {
                Assert.Equal(motor, MotoresDeVoz.DeComponente(c.Id));
                Assert.Contains($"{{entornos}}/{MotoresDeVoz.Entorno(motor)}/Scripts/python.exe", c.Instalacion!.Comprobar);
            }
        }
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
