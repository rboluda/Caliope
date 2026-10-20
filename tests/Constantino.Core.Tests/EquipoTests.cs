using Constantino.Core.Equipo;

namespace Constantino.Core.Tests;

public class EquipoTests
{
    [Fact]
    public void Lee_la_salida_de_nvidia_smi()
    {
        var gpus = DetectorEquipo.LeerSalidaNvidiaSmi("NVIDIA GeForce RTX 3050, 8192, 3908, 617.42\r\n");

        var g = Assert.Single(gpus);
        Assert.Equal("NVIDIA GeForce RTX 3050", g.Nombre);
        Assert.Equal(8.0, g.VramTotalGb);
        Assert.Equal(3908 / 1024.0, g.VramUsadaGb!.Value, 3);
        Assert.Equal("617.42", g.Driver);
        Assert.True(g.CompatibleCuda);
    }

    [Fact]
    public void Lee_varias_gpus_e_ignora_lineas_rotas()
    {
        var gpus = DetectorEquipo.LeerSalidaNvidiaSmi("""
            NVIDIA GeForce RTX 4090, 24564, 512, 620.10
            línea sin sentido
            NVIDIA RTX A2000, [N/A], [N/A], 620.10
            """);

        Assert.Equal(2, gpus.Count);
        Assert.Null(gpus[1].VramTotalGb);
    }

    [Theory]
    [InlineData(@"C:\Users\ana\OneDrive - Empresa S.L\Proyectos", true)]
    [InlineData(@"C:\Users\ana\OneDrive\Documentos", true)]
    [InlineData(@"C:\Users\ana\AppData\Local\Constantino", false)]
    [InlineData(@"D:\Constantino", false)]
    [InlineData(@"C:\Users\ana\OneDriveBackup", false)]
    public void Detecta_carpetas_de_onedrive(string ruta, bool esperado)
    {
        Assert.Equal(esperado, CarpetaDatos.EstaEnOneDrive(ruta, [@"C:\Users\ana\OneDrive - Empresa S.L"]));
    }

    [Fact]
    public void La_carpeta_predeterminada_esta_en_localappdata()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(local, CarpetaDatos.Predeterminada);
    }

    [Fact]
    public void Consulta_el_disco_de_una_carpeta_que_aun_no_existe()
    {
        var disco = DetectorEquipo.Disco(Path.Combine(Path.GetTempPath(), "constantino-no-existe", "datos"));

        Assert.NotNull(disco);
        Assert.True(disco.TotalGb > 0);
    }
}
