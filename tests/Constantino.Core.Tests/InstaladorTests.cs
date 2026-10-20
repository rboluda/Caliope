using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Constantino.Core.Catalogo;
using Constantino.Core.Instalaciones;

namespace Constantino.Core.Tests;

public sealed class InstaladorTests : IDisposable
{
    private readonly string _datos = Path.Combine(Path.GetTempPath(), "constantino-pruebas", Guid.NewGuid().ToString("N"));
    private readonly ZipFalso _descargador = new();
    private readonly EjecutorFalso _ejecutor = new();
    private readonly List<EventoInstalacion> _eventos = [];

    public void Dispose()
    {
        if (Directory.Exists(_datos))
            Directory.Delete(_datos, recursive: true);
    }

    /// <summary>Catálogo mínimo: base (zip + comprobación) → motor (proceso) → modelo (proceso), y uno sin instalación.</summary>
    private static CatalogoComponentes Catalogo(string versionMotor = "1") => CatalogoComponentes.Leer($$"""
        { "version": 1, "componentes": [
          { "id": "base", "nombre": "Base", "grupo": "G", "necesidad": "imprescindible", "resumen": "r", "detalle": "d",
            "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {},
            "instalacion": { "version": "1",
              "pasos": [
                { "tipo": "descargar", "descripcion": "Bajando", "url": "https://ejemplo/b.zip", "sha256": "x", "destino": "{descargas}/b.zip", "tamanoBytes": 10 },
                { "tipo": "extraer", "descripcion": "Abriendo", "archivo": "{descargas}/b.zip", "destino": "{herramientas}/b", "quitarCarpetaRaiz": true, "incluir": ["bin/b.exe"] }
              ],
              "rutas": ["{herramientas}/b"], "comprobar": ["{herramientas}/b/bin/b.exe"] } },
          { "id": "motor", "nombre": "Motor", "grupo": "G", "necesidad": "imprescindible", "resumen": "r", "detalle": "d",
            "dependeDe": ["base"], "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {},
            "instalacion": { "version": "{{versionMotor}}",
              "pasos": [
                { "tipo": "ejecutar", "descripcion": "Creando entorno", "programa": "{herramientas}/b/bin/b.exe", "argumentos": ["crear", "{entornos}/m", "{indiceTorch}"] },
                { "tipo": "ejecutar", "descripcion": "Comprobando", "programa": "{entornos}/m/python.exe", "argumentos": ["-c", "x"], "debeContener": "dispositivo={dispositivo}" }
              ],
              "rutas": ["{entornos}/m"], "comprobar": ["{entornos}/m/python.exe"] } },
          { "id": "extra", "nombre": "Extra", "grupo": "G", "necesidad": "opcional", "resumen": "r", "detalle": "d",
            "dependeDe": ["base"], "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {}, "disponibleEn": "H9" }
        ] }
        """);

    private Instalador Instalador(CatalogoComponentes? catalogo = null, bool usaGpu = true) =>
        new(catalogo ?? Catalogo(), new RutasDatos(_datos, usaGpu), _ejecutor, _descargador, TimeSpan.FromMilliseconds(10));

    private IProgress<EventoInstalacion> Progreso => new ProgresoSincrono<EventoInstalacion>(_eventos.Add);

    [Fact]
    public void El_plan_incluye_las_dependencias_en_orden()
    {
        var plan = Instalador().Plan(["motor"]);

        Assert.Equal(["base", "motor"], plan.Select(c => c.Id));
    }

    [Fact]
    public void No_se_puede_planificar_algo_sin_instalacion()
    {
        Assert.Throws<InvalidOperationException>(() => Instalador().Plan(["extra"]));
        Assert.Equal(EstadoComponente.NoDisponible, Instalador().EstadoDe("extra"));
    }

    [Fact]
    public async Task Instala_dependencias_y_componente_y_lo_registra()
    {
        var instalador = Instalador();

        await instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None);

        Assert.Equal(EstadoComponente.Instalado, instalador.EstadoDe("base"));
        Assert.Equal(EstadoComponente.Instalado, instalador.EstadoDe("motor"));
        Assert.Equal("cuda", instalador.Estado.Leer()["motor"].Dispositivo);
        Assert.False(File.Exists(Path.Combine(_datos, "descargas", "b.zip")), "el zip se borra tras extraerlo");
        Assert.False(File.Exists(Path.Combine(_datos, "herramientas", "b", "bin", "otro.txt")), "solo se extrae lo incluido");
        Assert.Contains(_ejecutor.Llamadas, l => l.Contains("https://download.pytorch.org/whl/cu128"));
        Assert.Equal(TipoEvento.ComponenteCompletado, _eventos.Last().Tipo);
        Assert.Contains(_eventos, e => e is { Tipo: TipoEvento.Progreso, ComponenteId: "base" } && e.Fraccion > 0);
        Assert.True(File.Exists(instalador.ArchivoRegistro));
    }

    [Fact]
    public async Task Sin_gpu_usa_el_indice_de_cpu()
    {
        var instalador = Instalador(usaGpu: false);

        await instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None);

        Assert.Contains(_ejecutor.Llamadas, l => l.Contains("https://download.pytorch.org/whl/cpu"));
        Assert.Equal("cpu", instalador.Estado.Leer()["motor"].Dispositivo);
    }

    [Fact]
    public async Task Lo_ya_instalado_no_se_repite_salvo_al_reparar()
    {
        var instalador = Instalador();
        await instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None);

        Assert.Empty(instalador.Plan(["motor"]));
        Assert.Equal(["motor"], instalador.Plan(["motor"], forzar: true).Select(c => c.Id));
    }

    [Fact]
    public async Task Detecta_componentes_danados_y_actualizables()
    {
        var instalador = Instalador();
        await instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None);

        File.Delete(Path.Combine(_datos, "entornos", "m", "python.exe"));
        Assert.Equal(EstadoComponente.Danado, instalador.EstadoDe("motor"));
        Assert.Equal(["motor"], instalador.Plan(["motor"]).Select(c => c.Id));

        await instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None);
        Assert.Equal(EstadoComponente.Actualizable, Instalador(Catalogo(versionMotor: "2")).EstadoDe("motor"));
    }

    [Fact]
    public async Task Un_proceso_que_falla_detiene_la_instalacion_sin_registrarla()
    {
        _ejecutor.CodigoPara = p => p.EndsWith("python.exe", StringComparison.Ordinal) ? 1 : 0;
        var instalador = Instalador();

        var e = await Assert.ThrowsAsync<InstalacionFallidaException>(() =>
            instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None));

        Assert.Equal("motor", e.ComponenteId);
        Assert.Equal("Comprobando", e.Paso);
        Assert.StartsWith("Comprobando: ", e.Message);
        Assert.Contains("dispositivo=cuda", e.Detalle);
        Assert.Contains("=== ERROR en motor", File.ReadAllText(instalador.ArchivoRegistro));
        Assert.Equal(EstadoComponente.Instalado, instalador.EstadoDe("base"));
        Assert.Equal(EstadoComponente.NoInstalado, instalador.EstadoDe("motor"));
    }

    [Fact]
    public async Task Falla_si_la_salida_no_contiene_lo_esperado()
    {
        _ejecutor.SalidaPara = _ => "dispositivo=cpu";
        var instalador = Instalador(usaGpu: true);

        var e = await Assert.ThrowsAsync<InstalacionFallidaException>(() =>
            instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None));

        Assert.Contains("PyTorch no detecta la tarjeta NVIDIA", e.Message);
    }

    [Fact]
    public async Task Cancelar_detiene_la_instalacion()
    {
        _ejecutor.Bloquear = true;
        var instalador = Instalador();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => instalador.InstalarAsync(["motor"], Progreso, cts.Token));
        Assert.Equal(EstadoComponente.NoInstalado, instalador.EstadoDe("motor"));
    }

    [Fact]
    public async Task No_deja_desinstalar_algo_de_lo_que_dependen_otros()
    {
        var instalador = Instalador();
        await instalador.InstalarAsync(["motor"], Progreso, CancellationToken.None);

        var e = Assert.Throws<ComponenteEnUsoException>(() => instalador.Desinstalar("base"));
        Assert.Contains("Motor", e.Message);

        instalador.Desinstalar("motor");
        Assert.Equal(EstadoComponente.NoInstalado, instalador.EstadoDe("motor"));
        Assert.False(Directory.Exists(Path.Combine(_datos, "entornos", "m")));
        instalador.Desinstalar("base");
        Assert.Empty(instalador.Estado.Leer());
    }

    [Fact]
    public void Rechaza_zips_con_rutas_que_salen_del_destino()
    {
        var instalador = Instalador();
        var zip = Path.Combine(_datos, "malo.zip");
        Directory.CreateDirectory(_datos);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            Escribir(z, "../fuera.txt", "x");

        var paso = new PasoExtraer("Abriendo", zip, "{herramientas}/malo");

        Assert.Throws<InvalidDataException>(() => instalador.Extraer(paso, (_, _) => { }));
        Assert.False(File.Exists(Path.Combine(_datos, "herramientas", "fuera.txt")));
    }

    [Fact]
    public void Todas_las_variables_del_catalogo_real_existen()
    {
        var rutas = new RutasDatos(_datos, usaGpu: true);
        foreach (var c in CatalogoComponentes.CargarPredeterminado().Componentes.Where(c => c.SePuedeInstalar))
        {
            var inst = c.Instalacion!;
            var textos = inst.Rutas.Concat(inst.Comprobar).Concat(inst.Pasos.SelectMany(p => p switch
            {
                PasoDescargar d => [d.Url, d.Destino],
                PasoExtraer x => [x.Archivo, x.Destino],
                PasoEjecutar e => e.Argumentos.Append(e.Programa).Append(e.DebeContener ?? ""),
                PasoModelo m => [m.Python],
                _ => [],
            }));
            foreach (var t in textos)
                rutas.Expandir(t);
        }
    }

    [Fact]
    public void Una_variable_desconocida_es_un_error()
    {
        Assert.Throws<InvalidOperationException>(() => new RutasDatos(_datos, true).Expandir("{inventada}/x"));
    }

    [Fact]
    public async Task Los_modelos_informan_del_avance_por_el_tamano_de_la_carpeta()
    {
        var catalogo = CatalogoComponentes.Leer("""
            { "version": 1, "componentes": [
              { "id": "modelo", "nombre": "Modelo", "grupo": "G", "necesidad": "opcional", "resumen": "r", "detalle": "d",
                "tamano": { "conGpuGb": 1, "sinGpuGb": 1 }, "requisitos": {},
                "instalacion": { "version": "abc",
                  "pasos": [ { "tipo": "modelo", "descripcion": "Bajando modelo", "python": "{entornos}/m/python.exe",
                               "repositorio": "Org/Nombre", "revision": "abc", "tamanoBytes": 1000 } ],
                  "rutas": ["{modelos}/hub/models--Org--Nombre"], "comprobar": ["{modelos}/hub/models--Org--Nombre/listo"] } } ] }
            """);
        var carpeta = Path.Combine(_datos, "modelos", "hub", "models--Org--Nombre");
        _ejecutor.Accion = async args =>
        {
            Assert.Equal(["Org/Nombre", "abc"], args.Skip(2));
            Directory.CreateDirectory(carpeta);
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "parte"), new byte[500]);
            await Task.Delay(100);
            await File.WriteAllTextAsync(Path.Combine(carpeta, "listo"), "");
            return "descargado=ok";
        };

        await Instalador(catalogo).InstalarAsync(["modelo"], Progreso, CancellationToken.None);

        Assert.Contains(_eventos, e => e.Tipo == TipoEvento.Progreso && e.Fraccion is >= 0.5 and < 1);
        Assert.Contains(_eventos, e => e.Tipo == TipoEvento.Progreso && e.Fraccion == 1);
    }

    private static void Escribir(ZipArchive zip, string nombre, string contenido)
    {
        using var s = new StreamWriter(zip.CreateEntry(nombre).Open());
        s.Write(contenido);
    }

    /// <summary>«Descarga» un zip con raiz/bin/b.exe y raiz/bin/otro.txt.</summary>
    private sealed class ZipFalso : IDescargador
    {
        public Task DescargarAsync(string url, string destino, string sha256, IProgress<(long Recibidos, long? Total)> progreso, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            using (var z = ZipFile.Open(destino, ZipArchiveMode.Create))
            {
                Escribir(z, "raiz/bin/b.exe", "binario");
                Escribir(z, "raiz/bin/otro.txt", "sobra");
            }
            progreso.Report((5, 10));
            progreso.Report((10, 10));
            return Task.CompletedTask;
        }
    }

    /// <summary>Simula procesos: el primero crea el python.exe del entorno; todos responden «dispositivo=…».</summary>
    private sealed class EjecutorFalso : IEjecutorProcesos
    {
        public List<string> Llamadas { get; } = [];
        public Func<string, int> CodigoPara { get; set; } = _ => 0;
        public Func<string, string>? SalidaPara { get; set; }
        public Func<IReadOnlyList<string>, Task<string>>? Accion { get; set; }
        public bool Bloquear { get; set; }

        public async Task<ResultadoProceso> EjecutarAsync(string programa, IReadOnlyList<string> argumentos,
            IReadOnlyDictionary<string, string> entorno, Action<string> alEscribir, CancellationToken ct)
        {
            Llamadas.Add($"{programa} {string.Join(' ', argumentos)}");
            if (Bloquear)
                await Task.Delay(Timeout.Infinite, ct);
            if (Accion is not null)
            {
                var texto = await Accion(argumentos);
                alEscribir(texto);
                return new ResultadoProceso(0, texto);
            }
            if (argumentos is ["crear", var entornoNuevo, ..])
            {
                Directory.CreateDirectory(entornoNuevo);
                await File.WriteAllTextAsync(Path.Combine(entornoNuevo, "python.exe"), "", ct);
            }
            var salida = SalidaPara?.Invoke(programa) ?? $"dispositivo={(argumentos.Any(a => a.EndsWith("/cpu", StringComparison.Ordinal)) || entorno.Count == 0 ? "cpu" : Dispositivo(entorno))}";
            alEscribir(salida);
            return new ResultadoProceso(CodigoPara(programa), salida);
        }

        // El entorno no dice el dispositivo; se deduce de la llamada que creó el entorno.
        private string Dispositivo(IReadOnlyDictionary<string, string> _) =>
            Llamadas.Any(l => l.Contains("/whl/cpu", StringComparison.Ordinal)) ? "cpu" : "cuda";
    }
}

public sealed class DescargadorTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "constantino-descargas", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Contenido = Encoding.UTF8.GetBytes(new string('a', 300_000) + new string('b', 300_000));
    private static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(Contenido));

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private static readonly IProgress<(long, long?)> SinProgreso = new ProgresoSincrono<(long, long?)>(_ => { });

    [Fact]
    public async Task Descarga_y_verifica()
    {
        var servidor = new ServidorFalso();
        var destino = Path.Combine(_carpeta, "f.zip");

        await new Descargador(new HttpClient(servidor)).DescargarAsync("https://x/f.zip", destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(Contenido, await File.ReadAllBytesAsync(destino));
        Assert.False(File.Exists(destino + ".part"));
    }

    [Fact]
    public async Task Continua_una_descarga_a_medias()
    {
        var servidor = new ServidorFalso();
        var destino = Path.Combine(_carpeta, "f.zip");
        Directory.CreateDirectory(_carpeta);
        await File.WriteAllBytesAsync(destino + ".part", Contenido[..250_000]);

        await new Descargador(new HttpClient(servidor)).DescargarAsync("https://x/f.zip", destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(250_000, servidor.DesdeByte);
        Assert.Equal(Contenido, await File.ReadAllBytesAsync(destino));
    }

    [Fact]
    public async Task Si_ya_esta_descargado_no_vuelve_a_pedirlo()
    {
        var servidor = new ServidorFalso();
        var destino = Path.Combine(_carpeta, "f.zip");
        Directory.CreateDirectory(_carpeta);
        await File.WriteAllBytesAsync(destino, Contenido);

        await new Descargador(new HttpClient(servidor)).DescargarAsync("https://x/f.zip", destino, Sha, SinProgreso, CancellationToken.None);

        Assert.Equal(0, servidor.Peticiones);
    }

    [Fact]
    public async Task Rechaza_un_archivo_que_no_coincide()
    {
        var destino = Path.Combine(_carpeta, "f.zip");

        await Assert.ThrowsAsync<DescargaCorruptaException>(() => new Descargador(new HttpClient(new ServidorFalso()))
            .DescargarAsync("https://x/f.zip", destino, new string('0', 64), SinProgreso, CancellationToken.None));

        Assert.False(File.Exists(destino));
        Assert.False(File.Exists(destino + ".part"));
    }

    private sealed class ServidorFalso : HttpMessageHandler
    {
        public int Peticiones { get; private set; }
        public long? DesdeByte { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Peticiones++;
            DesdeByte = request.Headers.Range?.Ranges.First().From;
            var desde = (int)(DesdeByte ?? 0);
            var respuesta = new HttpResponseMessage(desde > 0 ? System.Net.HttpStatusCode.PartialContent : System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Contenido[desde..]),
            };
            return Task.FromResult(respuesta);
        }
    }
}
