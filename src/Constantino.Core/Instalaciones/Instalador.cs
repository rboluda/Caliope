using System.IO.Compression;
using Constantino.Core.Catalogo;

namespace Constantino.Core.Instalaciones;

/// <summary>
/// Instala, repara y desinstala componentes siguiendo los pasos del catálogo.
/// Todo lo que escribe queda dentro de la carpeta de datos.
/// </summary>
public sealed class Instalador
{
    private const int LineasDeDetalle = 40;

    private readonly CatalogoComponentes _catalogo;
    private readonly RutasDatos _rutas;
    private readonly IEjecutorProcesos _ejecutor;
    private readonly IDescargador _descargador;
    private readonly TimeSpan _intervaloSondeo;
    private readonly object _cerrojoRegistro = new();

    public Instalador(CatalogoComponentes catalogo, RutasDatos rutas, IEjecutorProcesos ejecutor, IDescargador descargador,
        TimeSpan? intervaloSondeo = null)
    {
        _catalogo = catalogo;
        _rutas = rutas;
        _ejecutor = ejecutor;
        _descargador = descargador;
        _intervaloSondeo = intervaloSondeo ?? TimeSpan.FromSeconds(1);
        Estado = new AlmacenEstado(rutas.ArchivoEstado);
    }

    public AlmacenEstado Estado { get; }
    public RutasDatos Rutas => _rutas;

    public string ArchivoRegistro => Path.Combine(_rutas.Registros, $"instalacion-{DateTime.Now:yyyyMMdd}.log");

    public EstadoComponente EstadoDe(string id)
    {
        var c = _catalogo[id];
        if (c.Instalacion is not { } inst)
            return EstadoComponente.NoDisponible;
        var completo = inst.Comprobar.All(r => File.Exists(_rutas.Ruta(r)) || Directory.Exists(_rutas.Ruta(r)));
        if (!Estado.Leer().TryGetValue(id, out var registro))
            return EstadoComponente.NoInstalado;
        if (!completo)
            return EstadoComponente.Danado;
        return registro.Version == inst.Version ? EstadoComponente.Instalado : EstadoComponente.Actualizable;
    }

    public bool EstaInstalado(string id) => EstadoDe(id) is EstadoComponente.Instalado or EstadoComponente.Actualizable;

    /// <summary>
    /// Componentes a instalar, en orden: primero las dependencias que falten o estén dañadas, después lo pedido.
    /// Con <paramref name="forzar"/> se reinstala lo pedido aunque ya esté bien (reparar).
    /// </summary>
    public IReadOnlyList<Componente> Plan(IEnumerable<string> ids, bool forzar = false)
    {
        var pedidos = ids.Distinct().OrderBy(IndiceEnCatalogo).ToList();
        var plan = new List<Componente>();
        foreach (var id in pedidos)
        {
            foreach (var dep in _catalogo.DependenciasDe(id).Append(_catalogo[id]))
            {
                if (plan.Contains(dep))
                    continue;
                if (!dep.SePuedeInstalar)
                    throw new InvalidOperationException($"«{dep.Nombre}» todavía no se puede instalar con esta versión.");
                var pedido = dep.Id == id;
                if (EstadoDe(dep.Id) != EstadoComponente.Instalado || (pedido && forzar))
                    plan.Add(dep);
            }
        }
        return plan;
    }

    public async Task InstalarAsync(IEnumerable<string> ids, IProgress<EventoInstalacion> progreso, CancellationToken ct, bool forzar = false)
    {
        var plan = Plan(ids, forzar);
        Registrar($"=== Instalación: {string.Join(", ", plan.Select(c => c.Id))} (dispositivo {_rutas.Variables["dispositivo"]})");
        try
        {
            foreach (var componente in plan)
                await InstalarComponenteAsync(componente, Informador(progreso), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Registrar("=== Cancelado por el usuario");
            throw;
        }
        catch (InstalacionFallidaException e)
        {
            Registrar($"=== ERROR en {e.ComponenteId} ({e.Paso}): {e.Message}");
            throw;
        }
        catch (Exception e)
        {
            Registrar($"=== ERROR inesperado: {e}");
            throw;
        }
    }

    /// <summary>Componentes instalados que dejarían de funcionar sin <paramref name="id"/>.</summary>
    public IReadOnlyList<Componente> DependientesInstalados(string id) => _catalogo.Componentes
        .Where(c => c.Id != id && EstadoDe(c.Id) != EstadoComponente.NoInstalado && EstadoDe(c.Id) != EstadoComponente.NoDisponible)
        .Where(c => _catalogo.DependenciasDe(c.Id).Any(d => d.Id == id))
        .ToList();

    public void Desinstalar(string id)
    {
        var c = _catalogo[id];
        if (c.Instalacion is not { } inst)
            return;
        var dependientes = DependientesInstalados(id);
        if (dependientes.Count > 0)
        {
            throw new ComponenteEnUsoException(
                $"Antes hay que desinstalar lo que depende de «{c.Nombre}»: {string.Join(", ", dependientes.Select(d => d.Nombre))}.");
        }
        Registrar($"=== Desinstalando {id}");
        foreach (var ruta in inst.Rutas.Select(_rutas.Ruta))
        {
            try
            {
                if (Directory.Exists(ruta))
                    Directory.Delete(ruta, recursive: true);
                else if (File.Exists(ruta))
                    File.Delete(ruta);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ComponenteEnUsoException(
                    $"No se ha podido borrar {ruta}. Cierra los programas que lo estén usando y vuelve a intentarlo. ({e.Message})");
            }
        }
        Estado.Quitar(id);
    }

    private async Task InstalarComponenteAsync(Componente c, IProgress<EventoInstalacion> progreso, CancellationToken ct)
    {
        var inst = c.Instalacion!;
        var total = inst.Pasos.Count;
        progreso.Report(new EventoInstalacion(TipoEvento.InicioComponente, c.Id, $"Instalando {c.Nombre}", TotalPasos: total));

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var paso = inst.Pasos[i];
            var numero = i + 1;
            progreso.Report(new EventoInstalacion(TipoEvento.Paso, c.Id, paso.Descripcion, null, numero, total));
            void Avance(double? fraccion, string mensaje) =>
                progreso.Report(new EventoInstalacion(TipoEvento.Progreso, c.Id, mensaje, fraccion, numero, total));
            void Linea(string texto) =>
                progreso.Report(new EventoInstalacion(TipoEvento.Linea, c.Id, texto, null, numero, total));

            try
            {
                switch (paso)
                {
                    case PasoDescargar d:
                        await DescargarAsync(d, Avance, ct).ConfigureAwait(false);
                        break;
                    case PasoExtraer x:
                        await Task.Run(() => Extraer(x, Avance), ct).ConfigureAwait(false);
                        break;
                    case PasoEjecutar e:
                        await EjecutarAsync(c, e, Avance, Linea, ct).ConfigureAwait(false);
                        break;
                    case PasoModelo m:
                        await DescargarModeloAsync(c, m, Avance, Linea, ct).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException($"Tipo de paso desconocido: {paso.GetType().Name}");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException and not InstalacionFallidaException)
            {
                throw new InstalacionFallidaException(c.Id, paso.Descripcion, $"{paso.Descripcion}: {e.Message}", causa: e);
            }
        }

        var faltan = inst.Comprobar.Select(_rutas.Ruta).Where(r => !File.Exists(r) && !Directory.Exists(r)).ToList();
        if (faltan.Count > 0)
        {
            throw new InstalacionFallidaException(c.Id, "Comprobación final",
                $"La instalación de «{c.Nombre}» terminó, pero faltan archivos: {string.Join(", ", faltan)}");
        }
        Estado.Registrar(c.Id, new RegistroInstalacion(inst.Version, DateTimeOffset.Now, _rutas.Variables["dispositivo"]));
        progreso.Report(new EventoInstalacion(TipoEvento.ComponenteCompletado, c.Id, $"{c.Nombre} instalado", 1, total, total));
    }

    private async Task DescargarAsync(PasoDescargar d, Action<double?, string> avance, CancellationToken ct)
    {
        var medidor = new Medidor();
        var progreso = new ProgresoSincrono<(long Recibidos, long? Total)>(p =>
        {
            var total = p.Total ?? d.TamanoBytes;
            medidor.Anotar(p.Recibidos, total);
            avance(total > 0 ? Math.Min(1, (double)p.Recibidos / total) : null, medidor.Texto(d.Descripcion, p.Recibidos, total));
        });
        await _descargador.DescargarAsync(_rutas.Expandir(d.Url), _rutas.Ruta(d.Destino), d.Sha256, progreso, ct).ConfigureAwait(false);
    }

    internal void Extraer(PasoExtraer x, Action<double?, string> avance)
    {
        var archivo = _rutas.Ruta(x.Archivo);
        var destino = _rutas.Ruta(x.Destino);
        if (Directory.Exists(destino))
            Directory.Delete(destino, recursive: true);
        Directory.CreateDirectory(destino);
        var raizDestino = destino + Path.DirectorySeparatorChar;
        var incluir = x.Incluir?.Select(Normalizar).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using (var zip = ZipFile.OpenRead(archivo))
        {
            var entradas = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var hechas = 0;
            foreach (var entrada in entradas)
            {
                var relativa = Normalizar(entrada.FullName);
                if (x.QuitarCarpetaRaiz)
                {
                    var barra = relativa.IndexOf('/');
                    if (barra < 0)
                        continue;
                    relativa = relativa[(barra + 1)..];
                }
                if (incluir is not null && !incluir.Contains(relativa))
                    continue;
                var ruta = Path.GetFullPath(Path.Combine(destino, relativa));
                // Protección contra rutas maliciosas en el zip («../»).
                if (!ruta.StartsWith(raizDestino, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"El archivo comprimido contiene una ruta no válida: {entrada.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
                entrada.ExtractToFile(ruta, overwrite: true);
                avance((double)++hechas / entradas.Count, $"{x.Descripcion} · {relativa}");
            }
            if (incluir is not null)
            {
                var falta = incluir.Where(r => !File.Exists(Path.Combine(destino, r))).ToList();
                if (falta.Count > 0)
                    throw new InvalidDataException($"No están en el archivo comprimido: {string.Join(", ", falta)}");
            }
        }
        if (x.BorrarArchivo)
            File.Delete(archivo);

        static string Normalizar(string r) => r.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>Cómo medir el avance de un proceso: bytes actuales frente a los esperados.</summary>
    private sealed record Medicion(Func<long> Medir, long Esperados, bool Aproximado);

    /// <param name="argumentosLiterales">Para pasos internos cuyos argumentos llevan llaves propias (scripts de Python).</param>
    private async Task EjecutarAsync(Componente c, PasoEjecutar e, Action<double?, string> avance, Action<string> linea,
        CancellationToken ct, Medicion? medicion = null, bool argumentosLiterales = false)
    {
        var programa = _rutas.Ruta(e.Programa);
        var argumentos = argumentosLiterales ? e.Argumentos.ToList() : e.Argumentos.Select(_rutas.Expandir).ToList();
        Registrar($"[{c.Id}] > {programa} {string.Join(' ', argumentos)}");

        if (medicion is null && e.Progreso is { } medida)
        {
            var carpetas = medida.Carpetas.Select(_rutas.Ruta).ToList();
            var iniciales = carpetas.Select(TamanoCarpeta).ToList();
            // Se toma la carpeta que más ha crecido: la caché durante la descarga, el entorno al instalar desde la caché.
            medicion = new Medicion(
                () => carpetas.Select((ruta, i) => Math.Max(0, TamanoCarpeta(ruta) - iniciales[i])).Max(),
                medida.Esperados(_rutas.UsaGpu),
                Aproximado: true);
        }

        // Los procesos pueden avisar de algo (p. ej. un reintento) con líneas «AVISO: …»: se muestra junto al avance.
        string? aviso = null;
        ResultadoProceso r;
        using (var fin = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var sondeo = medicion is null
                ? Task.CompletedTask
                : SondearAsync(medicion, () => aviso is null ? e.Descripcion : $"{e.Descripcion} · {aviso}", avance, fin.Token);
            try
            {
                r = await _ejecutor.EjecutarAsync(programa, argumentos, _rutas.EntornoProcesos, t =>
                {
                    Registrar($"[{c.Id}]   {t}");
                    if (t.StartsWith("AVISO: ", StringComparison.Ordinal))
                    {
                        aviso = t["AVISO: ".Length..];
                        if (medicion is null)
                            avance(null, $"{e.Descripcion} · {aviso}");
                    }
                    linea(t);
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                await fin.CancelAsync().ConfigureAwait(false);
                await sondeo.ConfigureAwait(false);
            }
        }

        if (r.CodigoSalida != 0)
        {
            throw new InstalacionFallidaException(c.Id, e.Descripcion,
                $"{e.Descripcion}: {DiagnosticoErrores.Explicar(r.Salida)}", UltimasLineas(r.Salida));
        }
        if (e.DebeContener is { } esperado && !r.Salida.Contains(_rutas.Expandir(esperado), StringComparison.OrdinalIgnoreCase))
        {
            var explicacion = esperado.Contains("{dispositivo}", StringComparison.Ordinal)
                ? DiagnosticoErrores.Explicar(r.Salida)
                : $"no se obtuvo el resultado esperado («{_rutas.Expandir(esperado)}»).";
            throw new InstalacionFallidaException(c.Id, e.Descripcion, $"{e.Descripcion}: {explicacion}", UltimasLineas(r.Salida));
        }
        if (medicion is not null)
            avance(1, $"{e.Descripcion} · completado");
    }

    /// <summary>Informa del avance cada cierto tiempo midiendo bytes, hasta que se cancela <paramref name="ct"/>.</summary>
    private Task SondearAsync(Medicion m, Func<string> descripcion, Action<double?, string> avance, CancellationToken ct) => Task.Run(async () =>
    {
        var medidor = new Medidor();
        while (!ct.IsCancellationRequested)
        {
            var bytes = m.Medir();
            medidor.Anotar(bytes, m.Esperados);
            avance(m.Esperados > 0 ? Math.Min(0.99, (double)bytes / m.Esperados) : null,
                medidor.Texto(descripcion(), bytes, m.Esperados, m.Aproximado));
            try { await Task.Delay(_intervaloSondeo, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }, CancellationToken.None);

    /// <summary>
    /// Descarga un modelo con huggingface_hub. Si se corta la conexión lo reintenta solo (continúa donde iba);
    /// los errores que no son de red (revisión inexistente…) fallan a la primera.
    /// </summary>
    private async Task DescargarModeloAsync(Componente c, PasoModelo m, Action<double?, string> avance, Action<string> linea, CancellationToken ct)
    {
        var carpeta = Path.Combine(_rutas.Modelos, "hub", "models--" + m.Repositorio.Replace("/", "--"));
        const string script = """
            import sys, time
            from huggingface_hub import snapshot_download
            from huggingface_hub.errors import RepositoryNotFoundError, RevisionNotFoundError, EntryNotFoundError
            INTENTOS = 5
            for intento in range(1, INTENTOS + 1):
                try:
                    ruta = snapshot_download(repo_id=sys.argv[1], revision=sys.argv[2])
                    break
                except (RepositoryNotFoundError, RevisionNotFoundError, EntryNotFoundError):
                    raise
                except Exception as e:
                    if intento == INTENTOS:
                        raise
                    print(f"{type(e).__name__}: {e}", flush=True)
                    print(f"AVISO: se cortó la conexión; reintentando ({intento + 1} de {INTENTOS})", flush=True)
                    time.sleep(min(30, 2 ** intento))
            print("descargado=" + ruta, flush=True)
            """;
        var paso = new PasoEjecutar(m.Descripcion, m.Python, ["-c", script, m.Repositorio, m.Revision], "descargado=");
        // huggingface_hub no informa del avance de forma fiable: se mide lo que ocupa la carpeta del modelo
        // (en total, no lo que crece: si se reanuda una descarga, lo ya bajado cuenta).
        await EjecutarAsync(c, paso, avance, linea, ct, new Medicion(() => TamanoCarpeta(carpeta), m.TamanoBytes, Aproximado: false),
            argumentosLiterales: true).ConfigureAwait(false);
    }

    internal static long TamanoCarpeta(string carpeta)
    {
        if (!Directory.Exists(carpeta))
            return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(carpeta, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try { total += new FileInfo(f).Length; }
            catch (IOException) { }
        }
        return total;
    }

    private int IndiceEnCatalogo(string id) =>
        _catalogo.Componentes.ToList().FindIndex(c => c.Id == id) is var i and >= 0
            ? i
            : throw new KeyNotFoundException($"No existe el componente «{id}».");

    private IProgress<EventoInstalacion> Informador(IProgress<EventoInstalacion> destino) => new ProgresoSincrono<EventoInstalacion>(e =>
    {
        if (e.Tipo is TipoEvento.InicioComponente or TipoEvento.Paso or TipoEvento.ComponenteCompletado)
            Registrar($"[{e.ComponenteId}] {e.Mensaje}");
        destino.Report(e);
    });

    private void Registrar(string linea)
    {
        lock (_cerrojoRegistro)
        {
            try
            {
                Directory.CreateDirectory(_rutas.Registros);
                File.AppendAllText(ArchivoRegistro, $"{DateTime.Now:HH:mm:ss} {linea}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // El registro es de ayuda: si falla, la instalación sigue.
            }
        }
    }

    private static string UltimasLineas(string salida) =>
        string.Join(Environment.NewLine, salida.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(LineasDeDetalle));
}

/// <summary>IProgress que entrega en el mismo hilo, en orden (Progress&lt;T&gt; los manda al grupo de hilos y pueden desordenarse).</summary>
public sealed class ProgresoSincrono<T>(Action<T> accion) : IProgress<T>
{
    public void Report(T value) => accion(value);
}
