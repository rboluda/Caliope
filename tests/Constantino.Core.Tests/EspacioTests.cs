using System.Runtime.InteropServices;
using Constantino.Core.Catalogo;
using Constantino.Core.Instalaciones;

namespace Constantino.Core.Tests;

/// <summary>
/// «Espacio»: lo que se libera de verdad cuando los entornos comparten archivos con la caché y entre sí (enlaces duros,
/// como hace uv), y lo que borra «Desinstalar».
/// </summary>
public sealed class EspacioTests : IDisposable
{
    private const int K = 1024;
    private readonly string _datos = Path.Combine(Path.GetTempPath(), "constantino-pruebas", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_datos))
            Directory.Delete(_datos, recursive: true);
    }

    /// <summary>Dos componentes con un modelo propio cada uno y otro modelo compartido.</summary>
    private static CatalogoComponentes Catalogo() => CatalogoComponentes.Leer("""
        { "version": 1, "componentes": [
          { "id": "a", "nombre": "A", "grupo": "G", "necesidad": "opcional", "resumen": "r", "detalle": "d",
            "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {},
            "instalacion": { "version": "1",
              "pasos": [
                { "tipo": "modelo", "descripcion": "m", "python": "{entornos}/a/python.exe", "repositorio": "org/solo-a", "revision": "r", "tamanoBytes": 1 },
                { "tipo": "modelo", "descripcion": "m", "python": "{entornos}/a/python.exe", "repositorio": "org/compartido", "revision": "r", "tamanoBytes": 1 }
              ],
              "rutas": ["{entornos}/a"], "comprobar": ["{entornos}/a/python.exe"] } },
          { "id": "b", "nombre": "B", "grupo": "G", "necesidad": "opcional", "resumen": "r", "detalle": "d",
            "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {},
            "instalacion": { "version": "1",
              "pasos": [
                { "tipo": "modelo", "descripcion": "m", "python": "{entornos}/b/python.exe", "repositorio": "org/compartido", "revision": "r", "tamanoBytes": 1 }
              ],
              "rutas": ["{entornos}/b"], "comprobar": ["{entornos}/b/python.exe"] } }
        ] }
        """);

    private RutasDatos Rutas => new(_datos, usaGpu: true);

    /// <summary>
    /// a: un archivo solo suyo, otro compartido con b y la caché (como PyTorch) y otro compartido solo con la caché.
    /// Más un archivo solo en la caché, un modelo de un componente quitado y un entorno huérfano.
    /// </summary>
    private (Instalador Instalador, Espacio Espacio) Preparar()
    {
        var r = Rutas;
        var catalogo = Catalogo();
        var instalador = new Instalador(catalogo, r, new EjecutorNulo(), new DescargadorNulo());
        Escribir(Path.Combine(r.Entornos, "a", "python.exe"), 1);
        Escribir(Path.Combine(r.Entornos, "b", "python.exe"), 1);
        Escribir(Path.Combine(r.Entornos, "a", "propio.dll"), 100 * K);
        Escribir(Path.Combine(r.Cache, "uv", "torch.dll"), 100 * K);
        Enlazar(Path.Combine(r.Entornos, "a", "torch.dll"), Path.Combine(r.Cache, "uv", "torch.dll"));
        Enlazar(Path.Combine(r.Entornos, "b", "torch.dll"), Path.Combine(r.Cache, "uv", "torch.dll"));
        Escribir(Path.Combine(r.Cache, "uv", "numpy.dll"), 100 * K);
        Enlazar(Path.Combine(r.Entornos, "a", "numpy.dll"), Path.Combine(r.Cache, "uv", "numpy.dll"));
        Escribir(Path.Combine(r.Cache, "uv", "viejo.whl"), 100 * K);
        Escribir(Path.Combine(Espacio.CarpetaModelo(r, "org/solo-a"), "pesos.bin"), 200 * K);
        Escribir(Path.Combine(Espacio.CarpetaModelo(r, "org/compartido"), "pesos.bin"), 300 * K);
        Escribir(Path.Combine(Espacio.CarpetaModelo(r, "org/quitado"), "pesos.bin"), 70 * K);
        Escribir(Path.Combine(r.Entornos, "huerfano", "x.dll"), 80 * K);
        instalador.Estado.Registrar("a", new RegistroInstalacion("1", DateTimeOffset.Now, "cuda"));
        instalador.Estado.Registrar("b", new RegistroInstalacion("1", DateTimeOffset.Now, "cuda"));
        return (instalador, new Espacio(instalador, catalogo));
    }

    [Fact]
    public void Solo_cuenta_como_liberado_lo_que_nadie_mas_usa()
    {
        var (_, espacio) = Preparar();

        var informe = espacio.Medir();

        var a = informe.Componentes.Single(c => c.Componente.Id == "a");
        // Ocupa todo lo suyo, compartido o no: propio + torch + numpy + sus dos modelos (+ python.exe).
        Assert.Equal(100 * K * 3 + 200 * K + 300 * K + 1, a.Ocupa);
        // Se libera lo propio, lo que solo comparte con la caché (que se vacía al desinstalar) y su modelo propio;
        // no torch (lo usa b) ni el modelo compartido.
        Assert.Equal(100 * K + 100 * K + 200 * K + 1, a.Libera);

        var cache = informe.Limpiables.Single(l => l.Id == "cache");
        Assert.Equal(300 * K, cache.Ocupa);
        Assert.Equal(100 * K, cache.Libera); // solo viejo.whl: torch y numpy siguen en los entornos

        var restos = informe.Limpiables.Single(l => l.Id == "restos");
        Assert.Equal(70 * K + 80 * K, restos.Libera);
        Assert.Contains(restos.Rutas, x => x.EndsWith("models--org--quitado", StringComparison.Ordinal));
        Assert.Contains(restos.Rutas, x => x.EndsWith("huerfano", StringComparison.Ordinal));
    }

    [Fact]
    public void Desinstalar_borra_sus_modelos_salvo_los_compartidos_y_vacia_la_cache()
    {
        var (instalador, _) = Preparar();
        var r = Rutas;

        instalador.Desinstalar("a");

        Assert.False(Directory.Exists(Path.Combine(r.Entornos, "a")));
        Assert.False(Directory.Exists(Espacio.CarpetaModelo(r, "org/solo-a")));
        Assert.True(File.Exists(Path.Combine(Espacio.CarpetaModelo(r, "org/compartido"), "pesos.bin")), "b lo sigue usando");
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Cache));
        // b sigue teniendo su torch: borrar la caché solo quita uno de sus nombres.
        Assert.Equal(100 * K, new FileInfo(Path.Combine(r.Entornos, "b", "torch.dll")).Length);
        Assert.Equal(EstadoComponente.Instalado, instalador.EstadoDe("b"));
    }

    [Fact]
    public void Limpiar_temporales_respeta_los_recientes_y_los_restos_se_quitan_enteros()
    {
        var (_, espacio) = Preparar();
        var r = Rutas;
        var viejo = Path.Combine(r.Temporales, "viejo.wav");
        var nuevo = Path.Combine(r.Temporales, "nuevo.wav");
        Escribir(viejo, 10);
        Escribir(nuevo, 10);
        File.SetLastWriteTime(viejo, DateTime.Now.AddHours(-2));
        var informe = espacio.Medir();

        Assert.Empty(espacio.Limpiar(informe.Limpiables.Single(l => l.Id == "temporales")));
        Assert.Empty(espacio.Limpiar(informe.Limpiables.Single(l => l.Id == "restos")));

        Assert.False(File.Exists(viejo));
        Assert.True(File.Exists(nuevo), "puede estar en uso");
        Assert.False(Directory.Exists(Espacio.CarpetaModelo(r, "org/quitado")));
        Assert.False(Directory.Exists(Path.Combine(r.Entornos, "huerfano")));
        Assert.True(Directory.Exists(Path.Combine(r.Entornos, "a")), "lo instalado no es un resto");
    }

    [Fact]
    public void Lista_lo_del_usuario_con_lo_que_ocupa()
    {
        var (_, espacio) = Preparar();
        var narraciones = new Narracion.AlmacenProyectos(Rutas.Narraciones);
        var p = narraciones.Crear("Leyenda", new Narracion.AjustesNarracion());
        Escribir(Path.Combine(narraciones.CarpetaDe(p.Id), "Audio", "bloque.wav"), 5000);

        var t = Assert.Single(espacio.Medir().Trabajos);

        Assert.Equal(TipoTrabajo.Narracion, t.Tipo);
        Assert.Equal("Leyenda", t.Nombre);
        Assert.True(t.Ocupa >= 5000);
    }

    private static void Escribir(string ruta, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllBytes(ruta, new byte[bytes]);
    }

    private static void Enlazar(string nuevo, string existente)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(nuevo)!);
        Assert.True(CreateHardLinkW(nuevo, existente, IntPtr.Zero), $"no se pudo crear el enlace {nuevo}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string nuevo, string existente, IntPtr seguridad);

    private sealed class EjecutorNulo : IEjecutorProcesos
    {
        public Task<ResultadoProceso> EjecutarAsync(string programa, IReadOnlyList<string> argumentos,
            IReadOnlyDictionary<string, string> entorno, Action<string> alEscribir, CancellationToken ct) =>
            throw new InvalidOperationException("no se ejecuta nada");
    }

    private sealed class DescargadorNulo : IDescargador
    {
        public Task DescargarAsync(string url, string destino, string sha256, IProgress<(long Recibidos, long? Total)> progreso,
            CancellationToken ct) => throw new InvalidOperationException("no se descarga nada");
    }
}
