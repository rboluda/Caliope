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
}
