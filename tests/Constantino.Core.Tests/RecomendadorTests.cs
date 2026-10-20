using Constantino.Core.Catalogo;
using Constantino.Core.Equipo;
using Constantino.Core.Recomendaciones;

namespace Constantino.Core.Tests;

public class RecomendadorTests
{
    private static readonly CatalogoComponentes Catalogo = CatalogoComponentes.CargarPredeterminado();

    private static InformeEquipo Equipo(Gpu? gpu, double libreGb = 200, bool oneDrive = false, double ramGb = 32) => new(
        "Windows 11", "CPU de prueba", 12, ramGb,
        gpu is null ? [] : [gpu],
        new InfoDisco(@"C:\datos", @"C:\", libreGb, 1000, oneDrive));

    private static Gpu Nvidia(double vramGb, double usadaGb = 0.5, string driver = "617.42") =>
        new("NVIDIA GeForce de prueba", "NVIDIA", vramGb, usadaGb, driver, CompatibleCuda: true);

    private static NivelRecomendacion Nivel(InformeRecomendacion r, string id) => r.Para(id).Nivel;

    [Fact]
    public void Con_8_GB_propone_el_modelo_grande_y_voicedesign()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(8)));

        Assert.True(r.UsaGpu);
        Assert.Equal(NivelRecomendacion.Imprescindible, Nivel(r, "nucleo"));
        Assert.Equal(NivelRecomendacion.Imprescindible, Nivel(r, "motor-qwen"));
        Assert.Equal(NivelRecomendacion.Imprescindible, Nivel(r, "modelo-qwen-base-1.7b"));
        Assert.Equal(NivelRecomendacion.Opcional, Nivel(r, "modelo-qwen-base-0.6b"));
        Assert.Equal(NivelRecomendacion.Recomendado, Nivel(r, "modelo-qwen-voicedesign"));
        Assert.Equal(NivelRecomendacion.Opcional, Nivel(r, "whisper"));
        Assert.Equal(NivelRecomendacion.Opcional, Nivel(r, "chatterbox"));
    }

    [Fact]
    public void La_descarga_propuesta_suma_solo_lo_imprescindible_y_recomendado()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(8)));

        var esperado = new[] { "nucleo", "motor-qwen", "modelo-qwen-base-1.7b", "modelo-qwen-voicedesign" }
            .Sum(id => Catalogo[id].Tamano.ConGpuGb);
        Assert.Equal(esperado, r.DescargaPropuestaGb, 3);
        Assert.Equal(4, r.Propuestos.Count());
    }

    [Fact]
    public void Con_4_GB_propone_el_modelo_ligero_y_desaconseja_los_grandes()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(4)));

        Assert.True(r.UsaGpu);
        Assert.Equal(NivelRecomendacion.Imprescindible, Nivel(r, "modelo-qwen-base-0.6b"));
        Assert.Equal(NivelRecomendacion.NoRecomendado, Nivel(r, "modelo-qwen-base-1.7b"));
        Assert.Equal(NivelRecomendacion.NoRecomendado, Nivel(r, "modelo-qwen-voicedesign"));
        Assert.Equal(NivelRecomendacion.NoRecomendado, Nivel(r, "chatterbox"));
        Assert.Equal(NivelRecomendacion.Opcional, Nivel(r, "whisper"));
    }

    [Fact]
    public void Sin_gpu_usa_el_procesador_con_el_modelo_ligero()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(gpu: null));

        Assert.False(r.UsaGpu);
        Assert.Equal(NivelRecomendacion.Imprescindible, Nivel(r, "modelo-qwen-base-0.6b"));
        Assert.Equal(NivelRecomendacion.Opcional, Nivel(r, "modelo-qwen-base-1.7b"));
        Assert.Equal(NivelRecomendacion.Opcional, Nivel(r, "modelo-qwen-voicedesign"));
        Assert.Contains(r.Avisos, a => a.Titulo == "Sin tarjeta NVIDIA");
        Assert.Equal(Catalogo["motor-qwen"].Tamano.SinGpuGb,
            r.DescargaPropuestaGb - Catalogo["nucleo"].Tamano.SinGpuGb - Catalogo["modelo-qwen-base-0.6b"].Tamano.SinGpuGb, 3);
    }

    [Fact]
    public void Una_gpu_con_muy_poca_memoria_no_se_usa()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(2)));

        Assert.False(r.UsaGpu);
        Assert.Contains(r.Avisos, a => a.Titulo == "Tarjeta con poca memoria");
    }

    [Fact]
    public void Una_nvidia_sin_driver_genera_un_problema()
    {
        var sinDriver = new Gpu("NVIDIA GeForce RTX 3060", "NVIDIA", null, null, null, CompatibleCuda: false);
        var r = Recomendador.Recomendar(Catalogo, Equipo(sinDriver));

        Assert.Contains(r.Avisos, a => a.Severidad == SeveridadAviso.Problema && a.Titulo == "Falta el driver de NVIDIA");
    }

    [Theory]
    [InlineData("552.44", true)]
    [InlineData("570.00", false)]
    [InlineData("617.42", false)]
    public void Avisa_de_drivers_antiguos(string driver, bool avisa)
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(8, driver: driver)));

        Assert.Equal(avisa, r.Avisos.Any(a => a.Titulo == "Driver de NVIDIA antiguo"));
    }

    [Fact]
    public void Avisa_si_otros_programas_ocupan_la_tarjeta()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(8, usadaGb: 3.9)));

        var aviso = Assert.Single(r.Avisos, a => a.Titulo == "Otros programas usan la tarjeta");
        Assert.Contains("3,9 GB", aviso.Mensaje);
    }

    [Fact]
    public void Avisa_de_onedrive_y_de_falta_de_espacio()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(8), libreGb: 10, oneDrive: true));

        Assert.Contains(r.Avisos, a => a.Titulo == "La carpeta está en OneDrive");
        Assert.Contains(r.Avisos, a => a.Titulo == "Poco espacio en disco");
    }

    [Fact]
    public void Avisa_de_poca_ram()
    {
        var r = Recomendador.Recomendar(Catalogo, Equipo(Nvidia(8), ramGb: 8));

        Assert.Contains(r.Avisos, a => a.Titulo == "Poca memoria RAM");
    }

    [Fact]
    public void Todos_los_componentes_tienen_recomendacion_con_motivo()
    {
        foreach (var gpu in new[] { null, Nvidia(4), Nvidia(8), Nvidia(24) })
        {
            var r = Recomendador.Recomendar(Catalogo, Equipo(gpu));
            Assert.All(Catalogo.Componentes, c => Assert.False(string.IsNullOrWhiteSpace(r.Para(c.Id).Motivo)));
        }
    }
}
