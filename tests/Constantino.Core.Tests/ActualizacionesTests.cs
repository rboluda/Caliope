namespace Constantino.Core.Tests;

public class ActualizacionesTests
{
    private const string Respuesta = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/rboluda/Caliope/releases/tag/v1.2.0",
          "draft": false,
          "prerelease": false,
          "body": "Novedades",
          "assets": [
            { "name": "notas.txt", "browser_download_url": "https://ejemplo/notas.txt" },
            { "name": "Caliope-1.2.0-Instalador.exe", "browser_download_url": "https://ejemplo/Caliope-1.2.0-Instalador.exe" }
          ]
        }
        """;

    [Fact]
    public void Lee_la_version_y_el_instalador_de_la_respuesta_de_GitHub()
    {
        var v = Actualizaciones.Leer(Respuesta)!;

        Assert.Equal(new Version(1, 2, 0), v.Version);
        Assert.Equal("https://ejemplo/Caliope-1.2.0-Instalador.exe", v.Instalador);
        Assert.Equal("https://github.com/rboluda/Caliope/releases/tag/v1.2.0", v.Pagina);
        Assert.Equal("Novedades", v.Notas);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.1.9", true)]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.0.0", false)]
    [InlineData("1.3.0", false)]
    public void Solo_avisa_si_la_publicada_es_mas_nueva(string actual, bool avisa) =>
        Assert.Equal(avisa, Actualizaciones.EsMasNueva(Actualizaciones.Leer(Respuesta)!, Version.Parse(actual)));

    [Fact]
    public void Ignora_borradores_y_versiones_previas()
    {
        Assert.Null(Actualizaciones.Leer(Respuesta.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(Actualizaciones.Leer(Respuesta.Replace("\"draft\": false", "\"draft\": true")));
    }

    [Theory]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("1.2.3-beta", "1.2.3")]
    [InlineData("V2.0.1", "2.0.1")]
    public void Entiende_las_etiquetas_de_version(string etiqueta, string esperada) =>
        Assert.Equal(Version.Parse(esperada), Actualizaciones.VersionDeEtiqueta(etiqueta));

    [Fact]
    public void Una_etiqueta_que_no_es_version_no_cuenta() =>
        Assert.Null(Actualizaciones.VersionDeEtiqueta("ultima"));
}
