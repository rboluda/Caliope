using Constantino.Core;

namespace Constantino.Core.Tests;

public class TextosTests
{
    [Theory]
    [InlineData("Ábuelo Ñoño", "abuelo-nono")]
    [InlineData("  ", "sin-nombre")]
    public void Slug_quita_tildes_y_espacios(string texto, string esperado) =>
        Assert.Equal(esperado, Textos.Slug(texto));

    [Theory]
    [InlineData("La cazadora: parte 1?", "La cazadora parte 1")]
    [InlineData("Ñoño / «miedo»", "Ñoño «miedo»")]
    [InlineData("...", "narracion")]
    public void NombreDeArchivo_conserva_lo_legible_y_quita_lo_prohibido(string texto, string esperado) =>
        Assert.Equal(esperado, Textos.NombreDeArchivo(texto));

    [Theory]
    [InlineData(1_700_000L, "1,7 MB")]
    [InlineData(340_000L, "340 KB")]
    [InlineData(4_300_000_000L, "4,3 GB")]
    [InlineData(25_000_000L, "25 MB")]
    public void Los_tamanos_se_muestran_en_la_unidad_que_mejor_se_lee(long bytes, string esperado) =>
        Assert.Equal(esperado, Formato.Bytes(bytes));
}
