using Constantino.Core.Instalaciones;

namespace Constantino.Core.Tests;

public sealed class EnlacesModelosTests : IDisposable
{
    private readonly string _raiz = Directory.CreateTempSubdirectory("enlaces").FullName;

    public void Dispose() => Directory.Delete(_raiz, true);

    [Fact]
    public void Cambia_los_simbolicos_de_las_versiones_por_duros_sin_tocar_el_contenido()
    {
        var modelo = Path.Combine(_raiz, "hub", "models--Qwen--Prueba");
        var blob = Path.Combine(modelo, "blobs", "81b57e8e");
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllText(blob, """{"model_type": "qwen3_tts"}""");
        var version = Path.Combine(modelo, "snapshots", "fd4b2543");
        Directory.CreateDirectory(Path.Combine(version, "speech_tokenizer"));
        var config = Path.Combine(version, "config.json");
        var normal = Path.Combine(version, "normal.txt");
        File.WriteAllText(normal, "x");
        // Varios en la misma carpeta y en una subcarpeta (con pocos, el recorrido no se saltaba ninguno).
        var enlaces = new[] { config }
            .Concat(Enumerable.Range(1, 6).Select(i => Path.Combine(version, $"a{i}.json")))
            .Concat(Enumerable.Range(1, 6).Select(i => Path.Combine(version, "speech_tokenizer", $"b{i}.json")))
            .ToList();
        try
        {
            // Relativos, como los crea Hugging Face.
            foreach (var e in enlaces)
                File.CreateSymbolicLink(e, Path.GetRelativePath(Path.GetDirectoryName(e)!, blob));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // Windows sin permiso para crear enlaces simbólicos (sin modo de desarrollador): nada que probar.
        }

        Assert.Equal(enlaces.Count, EnlacesModelos.ConvertirSimbolicos(_raiz));

        foreach (var archivo in enlaces)
        {
            Assert.Null(new FileInfo(archivo).LinkTarget);
            Assert.Equal("""{"model_type": "qwen3_tts"}""", File.ReadAllText(archivo));
        }
        // Es el mismo archivo, no una copia: lo que se escribe en uno se ve en el otro.
        File.AppendAllText(blob, " ");
        Assert.EndsWith(" ", File.ReadAllText(config));
        Assert.Equal("x", File.ReadAllText(normal));
        Assert.Empty(Directory.EnumerateFiles(version, "*.enlace-*", SearchOption.AllDirectories));
        Assert.Equal(0, EnlacesModelos.ConvertirSimbolicos(_raiz));
    }

    [Fact]
    public void Sin_carpeta_de_modelos_no_hace_nada()
    {
        Assert.Equal(0, EnlacesModelos.ConvertirSimbolicos(Path.Combine(_raiz, "no-existe")));
    }
}
