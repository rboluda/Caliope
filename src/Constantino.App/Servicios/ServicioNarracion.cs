using System.IO;
using Constantino.Core.Instalaciones;
using Constantino.Core.Narracion;
using Constantino.Core.Voces;

namespace Constantino.App.Servicios;

/// <summary>El narrador del núcleo leyendo con el motor Qwen y una voz de la biblioteca.</summary>
public sealed class SintetizadorMotor(ServicioMotor motor, Voz voz, string referencia, string modelo) : ISintetizador
{
    public string FirmaVoz { get; } = Firma(voz, referencia);
    public string Modelo => modelo;
    public bool TieneVarianteSusurro => false;

    public async Task<string> SintetizarAsync(string texto, bool susurro, AjustesNarracion ajustes, string salida, CancellationToken ct)
    {
        var r = await motor.SintetizarAsync(voz, referencia, texto, salida, ajustes.Semilla, ajustes.Temperatura, ct);
        return r.Salida;
    }

    /// <summary>Cambia si cambia la voz o su grabación: los bloques generados con la anterior quedan desactualizados.</summary>
    public static string Firma(Voz voz, string referencia)
    {
        var info = new FileInfo(referencia);
        return $"{voz.Id}|{voz.Transcripcion}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
    }
}

/// <summary>Proyectos de narración de la carpeta de datos actual y lo necesario para narrarlos.</summary>
public sealed class ServicioNarracion(ServicioInstalacion instalacion, ServicioMotor motor, ServicioVoces voces)
{
    public async Task<AlmacenProyectos> AlmacenAsync() =>
        new(Path.Combine((await instalacion.InstaladorAsync()).Rutas.Raiz, "proyectos"));

    /// <summary>Lo que identifica al modelo en las huellas, o null si no hay modelo Base instalado.</summary>
    public async Task<string?> ModeloAsync() =>
        await motor.ModeloBaseAsync() is { } m ? $"{m.Componente}@{m.Revision}" : null;

    public async Task<(Voz Voz, string Referencia)?> VozAsync(string? id)
    {
        if (id is null)
            return null;
        var biblioteca = await voces.BibliotecaAsync();
        return biblioteca.Obtener(id) is { } voz ? (voz, biblioteca.ReferenciaDe(id)) : null;
    }

    public async Task<Narrador> NarradorAsync(Voz voz, string referencia, string modelo)
    {
        var rutas = (await instalacion.InstaladorAsync()).Rutas;
        if (!File.Exists(rutas.Ffmpeg))
            throw new MotorNoDisponibleException("Falta ffmpeg: instala el Núcleo desde Componentes.");
        return new Narrador(await AlmacenAsync(), new SintetizadorMotor(motor, voz, referencia, modelo),
            new ProcesadorFfmpeg(rutas.Ffmpeg, new EjecutorProcesos()));
    }
}
