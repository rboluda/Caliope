using System.IO.Compression;
using System.Text.Json;
using Constantino.Core.Catalogo;
using static Constantino.Core.Idioma;

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
                    throw new InvalidOperationException(T("«{0}» todavía no se puede instalar con esta versión.", dep.Nombre));
                var pedido = dep.Id == id;
                if (EstadoDe(dep.Id) != EstadoComponente.Instalado || (pedido && forzar))
                    plan.Add(dep);
            }
        }
        return plan;
    }

    /// <summary>Cuántos modelos se descargan a la vez en segundo plano (cada uno ya usa varias conexiones).</summary>
    public const int ModelosALaVez = 3;

    /// <summary>
    /// Instala el plan en orden. Cuando a un componente solo le quedan modelos por descargar, esas descargas siguen en
    /// segundo plano (hasta <see cref="ModelosALaVez"/> a la vez) y se pasa al siguiente: así se aprovecha la conexión
    /// mientras se preparan los entornos. Lo que dependa de un componente que aún se descarga, espera a que termine.
    /// </summary>
    public async Task InstalarAsync(IEnumerable<string> ids, IProgress<EventoInstalacion> progreso, CancellationToken ct, bool forzar = false)
    {
        var plan = Plan(ids, forzar);
        Registrar($"=== Instalación: {string.Join(", ", plan.Select(c => c.Id))} (dispositivo {_rutas.Variables["dispositivo"]})");
        var informador = Informador(progreso);
        using var todo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var turnos = new SemaphoreSlim(ModelosALaVez);
        var enSegundoPlano = new Dictionary<string, Task>(StringComparer.Ordinal);
        try
        {
            try
            {
                foreach (var componente in plan)
                {
                    foreach (var dep in _catalogo.DependenciasDe(componente.Id))
                        if (enSegundoPlano.TryGetValue(dep.Id, out var pendiente))
                            await pendiente.ConfigureAwait(false);
                    // Si una descarga en segundo plano ya ha fallado, no se sigue instalando lo demás.
                    if (enSegundoPlano.Values.FirstOrDefault(t => t.IsFaulted) is { } fallida)
                        await fallida.ConfigureAwait(false);
                    if (await InstalarComponenteAsync(componente, informador, todo.Token, turnos).ConfigureAwait(false) is { } resto)
                        enSegundoPlano[componente.Id] = resto;
                }
                if (enSegundoPlano.Values.Any(t => !t.IsCompleted))
                {
                    informador.Report(new EventoInstalacion(TipoEvento.Paso, "", T("Terminando de descargar los modelos"), Paso: 1, TotalPasos: 1));
                    await EsperarTodasAsync(enSegundoPlano.Values).ConfigureAwait(false);
                }
            }
            catch (Exception) when (enSegundoPlano.Values.Any(t => !t.IsCompleted))
            {
                // Si algo falla (o se cancela), se paran también las descargas en segundo plano: lo bajado se conserva
                // y se reanuda al reintentar.
                await todo.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(enSegundoPlano.Values).ConfigureAwait(false); }
                catch (Exception) { }
                throw;
            }
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
                T("Antes hay que desinstalar lo que depende de «{0}»: {1}.", c.Nombre, string.Join(", ", dependientes.Select(d => d.Nombre))));
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
                    T("No se ha podido borrar {0}. Cierra los programas que lo estén usando y vuelve a intentarlo. ({1})", ruta, e.Message));
            }
        }
        Estado.Quitar(id);
    }

    /// <summary>Espera a todas las tareas y, si alguna falla, lanza su error (no el de la primera de la lista).</summary>
    private static async Task EsperarTodasAsync(IEnumerable<Task> tareas)
    {
        var lista = tareas.ToList();
        while (lista.Count > 0)
        {
            var hecha = await Task.WhenAny(lista).ConfigureAwait(false);
            await hecha.ConfigureAwait(false);
            lista.Remove(hecha);
        }
    }

    /// <summary>
    /// Instala <paramref name="c"/>. Si al final solo le quedan modelos, devuelve la tarea que los descarga en segundo
    /// plano (y termina de registrar el componente); si no, null.
    /// </summary>
    private async Task<Task?> InstalarComponenteAsync(Componente c, IProgress<EventoInstalacion> progreso, CancellationToken ct,
        SemaphoreSlim turnos)
    {
        var inst = c.Instalacion!;
        var total = inst.Pasos.Count;
        progreso.Report(new EventoInstalacion(TipoEvento.InicioComponente, c.Id, T("Instalando {0}", c.Nombre), TotalPasos: total));
        // Desde aquí solo quedan descargas de modelos: van en segundo plano.
        var primerModelo = total;
        while (primerModelo > 0 && inst.Pasos[primerModelo - 1] is PasoModelo)
            primerModelo--;

        await PasosAsync(c, 0, primerModelo, progreso, segundoPlano: false, ct).ConfigureAwait(false);
        if (primerModelo == total)
        {
            Finalizar(c, progreso);
            return null;
        }
        return Task.Run(async () =>
        {
            await turnos.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PasosAsync(c, primerModelo, total, progreso, segundoPlano: true, ct).ConfigureAwait(false);
            }
            finally
            {
                turnos.Release();
            }
            Finalizar(c, progreso);
        }, ct);
    }

    private async Task PasosAsync(Componente c, int desde, int hasta, IProgress<EventoInstalacion> progreso, bool segundoPlano,
        CancellationToken ct)
    {
        var inst = c.Instalacion!;
        var total = inst.Pasos.Count;
        for (var i = desde; i < hasta; i++)
        {
            ct.ThrowIfCancellationRequested();
            var paso = inst.Pasos[i];
            var numero = i + 1;
            progreso.Report(new EventoInstalacion(TipoEvento.Paso, c.Id, paso.Descripcion, null, numero, total, segundoPlano));
            void Avance(double? fraccion, string mensaje) =>
                progreso.Report(new EventoInstalacion(TipoEvento.Progreso, c.Id, mensaje, fraccion, numero, total, segundoPlano));
            void Linea(string texto) =>
                progreso.Report(new EventoInstalacion(TipoEvento.Linea, c.Id, texto, null, numero, total, segundoPlano));

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
    }

    private void Finalizar(Componente c, IProgress<EventoInstalacion> progreso)
    {
        var inst = c.Instalacion!;
        var total = inst.Pasos.Count;
        var faltan = inst.Comprobar.Select(_rutas.Ruta).Where(r => !File.Exists(r) && !Directory.Exists(r)).ToList();
        if (faltan.Count > 0)
        {
            throw new InstalacionFallidaException(c.Id, T("Comprobación final"),
                T("La instalación de «{0}» terminó, pero faltan archivos: {1}", c.Nombre, string.Join(", ", faltan)));
        }
        Estado.Registrar(c.Id, new RegistroInstalacion(inst.Version, DateTimeOffset.Now, _rutas.Variables["dispositivo"]));
        progreso.Report(new EventoInstalacion(TipoEvento.ComponenteCompletado, c.Id, T("{0} instalado", c.Nombre), 1, total, total));
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
                    throw new InvalidDataException(T("El archivo comprimido contiene una ruta no válida: {0}", entrada.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
                entrada.ExtractToFile(ruta, overwrite: true);
                avance((double)++hechas / entradas.Count, $"{x.Descripcion} · {relativa}");
            }
            if (incluir is not null)
            {
                var falta = incluir.Where(r => !File.Exists(Path.Combine(destino, r))).ToList();
                if (falta.Count > 0)
                    throw new InvalidDataException(T("No están en el archivo comprimido: {0}", string.Join(", ", falta)));
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

        if (r.CodigoSalida != 0 && e.ToleraError is { } tolerado && r.Salida.Contains(tolerado, StringComparison.OrdinalIgnoreCase))
        {
            // p. ej. uv instala Python pero Windows no le deja crear el enlace «cpython-3.11-…» (os error 448): sin
            // ese enlace todo funciona, pero si ha quedado uno que no se puede recorrer se quita.
            Registrar($"[{c.Id}]   (error conocido, se sigue: «{tolerado}»)");
            EstructuraDatos.QuitarUnionesInaccesibles(_rutas.Python);
        }
        else if (r.CodigoSalida != 0)
        {
            throw new InstalacionFallidaException(c.Id, e.Descripcion,
                $"{e.Descripcion}: {DiagnosticoErrores.Explicar(r.Salida)}", UltimasLineas(r.Salida));
        }
        if (e.DebeContener is { } esperado && !r.Salida.Contains(_rutas.Expandir(esperado), StringComparison.OrdinalIgnoreCase))
        {
            var explicacion = esperado.Contains("{dispositivo}", StringComparison.Ordinal)
                ? DiagnosticoErrores.Explicar(r.Salida)
                : T("no se obtuvo el resultado esperado («{0}»).", _rutas.Expandir(esperado));
            throw new InstalacionFallidaException(c.Id, e.Descripcion, $"{e.Descripcion}: {explicacion}", UltimasLineas(r.Salida));
        }
        if (medicion is not null)
            avance(1, T("{0} · completado", e.Descripcion));
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
            import json, sys, time
            from huggingface_hub import snapshot_download
            from huggingface_hub.errors import RepositoryNotFoundError, RevisionNotFoundError, EntryNotFoundError
            INTENTOS = 5
            for intento in range(1, INTENTOS + 1):
                try:
                    ruta = snapshot_download(repo_id=sys.argv[1], revision=sys.argv[2], allow_patterns=json.loads(sys.argv[3]))
                    break
                except (RepositoryNotFoundError, RevisionNotFoundError, EntryNotFoundError):
                    raise
                except Exception as e:
                    if intento == INTENTOS:
                        raise
                    print(f"{type(e).__name__}: {e}", flush=True)
                    print("AVISO: " + sys.argv[4].format(intento + 1, INTENTOS), flush=True)
                    time.sleep(min(30, 2 ** intento))
            print("descargado=" + ruta, flush=True)
            """;
        var archivos = m.Archivos is { Count: > 0 } a ? JsonSerializer.Serialize(a) : "null";
        var paso = new PasoEjecutar(m.Descripcion, m.Python, ["-c", script, m.Repositorio, m.Revision, archivos, T("se cortó la conexión; reintentando ({0} de {1})")], "descargado=");
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
