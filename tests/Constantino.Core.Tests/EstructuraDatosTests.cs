using Constantino.Core.Instalaciones;

namespace Constantino.Core.Tests;

public sealed class EstructuraDatosTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "constantino-pruebas", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_raiz))
            return;
        // Las uniones se quitan antes, solas: el borrado recursivo no sabe con ellas.
        foreach (var d in Directory.EnumerateDirectories(_raiz, "*", SearchOption.AllDirectories)
                     .Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            Directory.Delete(d);
        Directory.Delete(_raiz, recursive: true);
    }

    /// <summary>Una carpeta como la dejaban las versiones anteriores: todo suelto en la raíz.</summary>
    private void CrearAntigua()
    {
        foreach (var d in new[] { "herramientas/uv", "python/cpython-3.11.17", "entornos/qwen3tts", "modelos/hub", "cache/uv",
                     "registros", "tmp", "voces/mi-voz", "proyectos/prueba/salida" })
            Directory.CreateDirectory(Path.Combine(_raiz, d));
        File.WriteAllText(Path.Combine(_raiz, "estado.json"), "{}");
        File.WriteAllText(Path.Combine(_raiz, "voces", "mi-voz", "voz.json"), "{}");
        File.WriteAllText(Path.Combine(_raiz, "python", "cpython-3.11.17", "python.exe"), "");
        // Como hace uv: un enlace por versión menor, con la ruta absoluta del destino.
        Union(Path.Combine(_raiz, "python", "cpython-3.11"), Path.Combine(_raiz, "python", "cpython-3.11.17"));
        File.WriteAllText(Path.Combine(_raiz, "entornos", "qwen3tts", "pyvenv.cfg"),
            $"home = {Path.Combine(_raiz, "python", "cpython-3.11")}\nimplementation = CPython\nversion_info = 3.11.13\n");
    }

    private static void Union(string enlace, string destino)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", enlace, destino }, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
    }

    [Fact]
    public void Migra_la_estructura_antigua_y_corrige_los_entornos()
    {
        CrearAntigua();
        Assert.True(EstructuraDatos.EsAntigua(_raiz));

        var r = EstructuraDatos.Migrar(_raiz);

        Assert.True(r.Migrada);
        Assert.Null(r.Error);
        Assert.False(EstructuraDatos.EsAntigua(_raiz));
        var rutas = new RutasDatos(_raiz, usaGpu: true);
        Assert.False(rutas.EstructuraAntigua);
        Assert.True(File.Exists(Path.Combine(rutas.Voces, "mi-voz", "voz.json")));
        Assert.True(Directory.Exists(Path.Combine(rutas.Narraciones, "prueba")));
        Assert.True(Directory.Exists(Path.Combine(rutas.Modelos, "hub")));
        Assert.True(File.Exists(rutas.ArchivoEstado));
        Assert.True(File.Exists(Path.Combine(_raiz, EstructuraDatos.Leeme)));
        // Lo técnico ya no está suelto en la raíz.
        string[] esperadas = [EstructuraDatos.Leeme, EstructuraDatos.Modelos, EstructuraDatos.Narraciones, EstructuraDatos.Voces, EstructuraDatos.Interno];
        Assert.Equal(esperadas.Order(StringComparer.Ordinal),
            Directory.EnumerateFileSystemEntries(_raiz).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal));
        var cfg = File.ReadAllText(Path.Combine(rutas.Entornos, "qwen3tts", "pyvenv.cfg"));
        Assert.Contains($"home = {Path.Combine(rutas.Python, "cpython-3.11")}", cfg);
        Assert.Contains("version_info = 3.11.13", cfg);
        // El enlace de uv apunta ya a la carpeta movida.
        Assert.True(File.Exists(Path.Combine(rutas.Python, "cpython-3.11", "python.exe")));
    }

    [Fact]
    public void Si_algo_esta_en_uso_deshace_lo_movido_y_sigue_con_la_estructura_antigua()
    {
        CrearAntigua();
        // Un archivo abierto dentro de «python» impide moverla, a mitad de la migración.
        using (new FileStream(Path.Combine(_raiz, "python", "cpython-3.11.17", "python.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = EstructuraDatos.Migrar(_raiz);

            Assert.False(r.Migrada);
            Assert.NotNull(r.Error);
        }
        Assert.True(EstructuraDatos.EsAntigua(_raiz));
        Assert.True(File.Exists(Path.Combine(_raiz, "voces", "mi-voz", "voz.json")));
        Assert.True(Directory.Exists(Path.Combine(_raiz, "herramientas", "uv")));
        Assert.False(Directory.Exists(Path.Combine(_raiz, EstructuraDatos.Interno)));
        var rutas = new RutasDatos(_raiz, usaGpu: true);
        Assert.True(rutas.EstructuraAntigua);
        Assert.Equal(Path.Combine(_raiz, "voces"), rutas.Voces);
        Assert.Equal(Path.Combine(_raiz, "estado.json"), rutas.ArchivoEstado);
    }

    [Fact]
    public void Lee_la_ruta_del_python_grabada_en_el_lanzador_de_uv()
    {
        Directory.CreateDirectory(_raiz);
        var lanzador = Path.Combine(_raiz, "python.exe");
        // Así la guarda uv: como recurso, rodeada de binario y seguida del manifiesto XML.
        byte[] relleno = [0x4D, 0x5A, 0x00, 0x01, 0x20, 0x05, 0x00, 0x00];
        File.WriteAllBytes(lanzador, [.. relleno, .. System.Text.Encoding.ASCII.GetBytes(@"C:\Datos\Programas internos\python\cpython-3.11\python.exe"),
            0x02, .. System.Text.Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?>")]);

        Assert.Equal(@"C:\Datos\Programas internos\python\cpython-3.11\python.exe", EstructuraDatos.RutaIncrustada(lanzador));
        Assert.Null(EstructuraDatos.RutaIncrustada(Path.Combine(_raiz, "no-existe.exe")));
    }

    [Fact]
    public void Una_carpeta_nueva_no_se_crea_ni_se_migra()
    {
        var r = EstructuraDatos.Migrar(_raiz);

        Assert.False(r.Migrada);
        Assert.False(Directory.Exists(_raiz));
        var rutas = new RutasDatos(_raiz, usaGpu: false);
        Assert.Equal(Path.Combine(_raiz, EstructuraDatos.Interno, "entornos"), rutas.Entornos);
        Assert.Equal(Path.Combine(_raiz, EstructuraDatos.Modelos), rutas.Variables["modelos"]);
    }
}
