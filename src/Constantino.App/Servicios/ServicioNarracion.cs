using System.IO;
using System.Text.Json;
using Constantino.Core;
using Constantino.Core.Audio;
using Constantino.Core.Instalaciones;
using Constantino.Core.Narracion;
using Constantino.Core.Voces;

namespace Constantino.App.Servicios;

/// <summary>Una voz de la biblioteca lista para narrar: su grabación y las de sus variantes.</summary>
public sealed record VozParaNarrar(Voz Voz, string Referencia, IReadOnlyList<(Variante Variante, string Referencia)> Variantes)
{
    public IReadOnlyCollection<string> IdsVariantes => Variantes.Select(v => v.Variante.Id).ToList();

    /// <summary>Cambia si cambia la voz, su grabación o sus variantes: los bloques generados quedan desactualizados.</summary>
    public string Firma
    {
        get
        {
            var firma = $"{Voz.Id}|{Voz.Transcripcion}|{Huella(Referencia)}";
            // Sin variantes la firma es la de antes de existir, para no invalidar lo ya generado.
            return Variantes.Count == 0
                ? firma
                : firma + "|" + string.Join(";", Variantes.OrderBy(v => v.Variante.Id)
                    .Select(v => $"{v.Variante.Id}:{v.Variante.Transcripcion}:{Huella(v.Referencia)}"));
        }
    }

    private static string Huella(string ruta)
    {
        var info = new FileInfo(ruta);
        return info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "0|0";
    }
}

/// <summary>
/// El narrador del núcleo leyendo con el motor de voz y una voz de la biblioteca (o una de sus variantes), y con las
/// demás voces de la biblioteca para [voz: …].
/// </summary>
public sealed class SintetizadorMotor(ServicioMotor motor, VozParaNarrar voz, string modelo,
    IReadOnlyDictionary<string, string>? otrasVoces = null, Func<string, VozParaNarrar?>? cargarVoz = null) : ISintetizador
{
    private readonly Dictionary<string, VozParaNarrar?> _cargadas = [];

    public string FirmaVoz { get; } = voz.Firma;
    public string Modelo => modelo;
    public IReadOnlyCollection<string> Variantes => voz.IdsVariantes;
    public IReadOnlyDictionary<string, string> OtrasVoces { get; } = otrasVoces ?? new Dictionary<string, string>();

    public string FirmaDeVoz(string vozId) => Cargar(vozId)?.Firma ?? "falta";

    private VozParaNarrar? Cargar(string id)
    {
        if (!_cargadas.TryGetValue(id, out var v))
            _cargadas[id] = v = cargarVoz?.Invoke(id);
        return v;
    }

    public async Task<string> SintetizarAsync(string texto, string? variante, string? vozId, AjustesNarracion ajustes, string salida, CancellationToken ct)
    {
        if (vozId is not null && Cargar(vozId) is { } otra)
        {
            var o = await motor.SintetizarAsync(otra.Voz, otra.Referencia, texto, salida, ajustes.Semilla, ajustes.Temperatura, ct,
                ajustes.Motor, ajustes.Intensidad);
            return o.Salida;
        }
        var (laVoz, referencia) = (voz.Voz, voz.Referencia);
        if (variante is not null && voz.Variantes.FirstOrDefault(v => v.Variante.Id == variante) is { Variante: { } v } elegida)
            (laVoz, referencia) = (voz.Voz with { Transcripcion = v.Transcripcion }, elegida.Referencia);
        var r = await motor.SintetizarAsync(laVoz, referencia, texto, salida, ajustes.Semilla, ajustes.Temperatura, ct,
            ajustes.Motor, ajustes.Intensidad);
        return r.Salida;
    }
}

/// <summary>Proyectos de narración de la carpeta de datos actual y lo necesario para narrarlos.</summary>
public sealed class ServicioNarracion(ServicioInstalacion instalacion, ServicioMotor motor, ServicioVoces voces)
{
    public async Task<AlmacenProyectos> AlmacenAsync() =>
        new((await instalacion.InstaladorAsync()).Rutas.Narraciones);

    /// <summary>Lo que identifica al modelo de <paramref name="motorVoz"/> en las huellas, o null si no está instalado.</summary>
    public Task<string?> ModeloAsync(string motorVoz) => motor.FirmaModeloAsync(motorVoz);

    public Task<bool> ChatterboxInstaladoAsync() => motor.ChatterboxInstaladoAsync();

    public async Task<VozParaNarrar?> VozAsync(string? id) => id is null ? null : Cargar(await voces.BibliotecaAsync(), id);

    private static VozParaNarrar? Cargar(BibliotecaVoces biblioteca, string id)
    {
        if (biblioteca.Obtener(id) is not { } voz)
            return null;
        var variantes = biblioteca.Variantes(id).Select(v => (v, biblioteca.ReferenciaDeVariante(id, v.Id))).ToList();
        return new VozParaNarrar(voz, biblioteca.ReferenciaDe(id), variantes);
    }

    /// <summary>Las demás voces de la biblioteca, por nombre y por id normalizados (para [voz: …]).</summary>
    public async Task<IReadOnlyList<Voz>> OtrasVocesAsync(string? menos)
    {
        var biblioteca = await voces.BibliotecaAsync();
        return biblioteca.Listar().Where(v => v.Id != menos).ToList();
    }

    // --- Sonidos y diccionario ------------------------------------------------------------------

    private Task? _preparandoSonidos;

    /// <summary>
    /// «Mis sonidos». Con <paramref name="esperar"/>, espera a que estén creados los incluidos (unos segundos la
    /// primera vez); sin él, devuelve lo que haya ya (para pintar la pantalla sin esperas).
    /// </summary>
    public async Task<BibliotecaSonidos> SonidosAsync(bool esperar = true)
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        var b = new BibliotecaSonidos(rutas.Sonidos);
        _preparandoSonidos ??= Task.Run(() => b.PrepararAsync(rutas.Ffmpeg, new EjecutorProcesos()));
        if (esperar)
            await _preparandoSonidos;
        return b;
    }

    /// <summary>Crea los sonidos incluidos en segundo plano al arrancar, para que estén listos al narrar.</summary>
    public async Task PrepararSonidosAsync()
    {
        try { await SonidosAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions OpcionesJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private async Task<string> RutaDiccionarioAsync() =>
        Path.Combine((await instalacion.InstaladorAsync()).Rutas.Narraciones, "pronunciacion.json");

    /// <summary>El diccionario de pronunciación (común a todos los proyectos).</summary>
    public async Task<IReadOnlyList<Pronunciacion>> DiccionarioAsync()
    {
        var ruta = await RutaDiccionarioAsync();
        try
        {
            return File.Exists(ruta)
                ? JsonSerializer.Deserialize<List<Pronunciacion>>(await File.ReadAllTextAsync(ruta), OpcionesJson) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task GuardarDiccionarioAsync(IEnumerable<Pronunciacion> entradas)
    {
        var ruta = await RutaDiccionarioAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        var limpias = entradas.Where(e => !string.IsNullOrWhiteSpace(e.Escrito) && !string.IsNullOrWhiteSpace(e.Dicho))
            .Select(e => new Pronunciacion(e.Escrito.Trim(), e.Dicho.Trim())).ToList();
        await File.WriteAllTextAsync(ruta, JsonSerializer.Serialize(limpias, OpcionesJson));
    }

    /// <param name="esperarSonidos">False para pintar la pantalla sin esperar a que se creen los sonidos incluidos.</param>
    public async Task<Narrador> NarradorAsync(VozParaNarrar voz, string modelo, bool esperarSonidos = true)
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        if (!File.Exists(rutas.Ffmpeg))
            throw new MotorNoDisponibleException("Faltan las herramientas básicas: instálalas desde Componentes.");
        var biblioteca = await voces.BibliotecaAsync();
        var otras = new Dictionary<string, string>();
        foreach (var v in biblioteca.Listar().Where(v => v.Id != voz.Voz.Id))
        {
            otras.TryAdd(Textos.Slug(v.Nombre, v.Id), v.Id);
            otras.TryAdd(v.Id, v.Id);
        }
        var sintetizador = new SintetizadorMotor(motor, voz, modelo, otras, id => Cargar(biblioteca, id));
        return new Narrador(await AlmacenAsync(), sintetizador, new ProcesadorFfmpeg(rutas.Ffmpeg, new EjecutorProcesos()),
            await SonidosAsync(esperarSonidos), await DiccionarioAsync());
    }
}
