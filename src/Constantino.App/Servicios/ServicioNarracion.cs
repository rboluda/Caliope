using System.IO;
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

/// <summary>El narrador del núcleo leyendo con el motor Qwen y una voz de la biblioteca (o una de sus variantes).</summary>
public sealed class SintetizadorMotor(ServicioMotor motor, VozParaNarrar voz, string modelo) : ISintetizador
{
    public string FirmaVoz { get; } = voz.Firma;
    public string Modelo => modelo;
    public IReadOnlyCollection<string> Variantes => voz.IdsVariantes;

    public async Task<string> SintetizarAsync(string texto, string? variante, AjustesNarracion ajustes, string salida, CancellationToken ct)
    {
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
        new(Path.Combine((await instalacion.InstaladorAsync()).Rutas.Raiz, "proyectos"));

    /// <summary>Lo que identifica al modelo de <paramref name="motorVoz"/> en las huellas, o null si no está instalado.</summary>
    public Task<string?> ModeloAsync(string motorVoz) => motor.FirmaModeloAsync(motorVoz);

    public Task<bool> ChatterboxInstaladoAsync() => motor.ChatterboxInstaladoAsync();

    public async Task<VozParaNarrar?> VozAsync(string? id)
    {
        if (id is null)
            return null;
        var biblioteca = await voces.BibliotecaAsync();
        if (biblioteca.Obtener(id) is not { } voz)
            return null;
        var variantes = biblioteca.Variantes(id).Select(v => (v, biblioteca.ReferenciaDeVariante(id, v.Id))).ToList();
        return new VozParaNarrar(voz, biblioteca.ReferenciaDe(id), variantes);
    }

    public async Task<Narrador> NarradorAsync(VozParaNarrar voz, string modelo)
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        if (!File.Exists(rutas.Ffmpeg))
            throw new MotorNoDisponibleException("Faltan las herramientas básicas: instálalas desde Componentes.");
        return new Narrador(await AlmacenAsync(), new SintetizadorMotor(motor, voz, modelo),
            new ProcesadorFfmpeg(rutas.Ffmpeg, new EjecutorProcesos()));
    }
}
