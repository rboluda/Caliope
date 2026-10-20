namespace Constantino.Core.Instalaciones;

public enum EstadoComponente
{
    /// <summary>Esta versión de Constantino aún no sabe instalarlo.</summary>
    NoDisponible,
    NoInstalado,
    Instalado,
    /// <summary>Registrado como instalado, pero faltan archivos: hay que reparar.</summary>
    Danado,
    /// <summary>Instalado con una versión distinta de la del catálogo.</summary>
    Actualizable,
}

public enum TipoEvento
{
    InicioComponente,
    Paso,
    Progreso,
    Linea,
    ComponenteCompletado,
}

/// <param name="Fraccion">De 0 a 1 dentro del paso actual, o null si no se puede medir.</param>
public sealed record EventoInstalacion(
    TipoEvento Tipo,
    string ComponenteId,
    string Mensaje,
    double? Fraccion = null,
    int Paso = 0,
    int TotalPasos = 0);

public sealed class InstalacionFallidaException(string componenteId, string paso, string mensaje, string? detalle = null, Exception? causa = null)
    : Exception(mensaje, causa)
{
    public string ComponenteId { get; } = componenteId;
    public string Paso { get; } = paso;
    /// <summary>Últimas líneas de salida del proceso, para el registro.</summary>
    public string? Detalle { get; } = detalle;
}

public sealed class ComponenteEnUsoException(string mensaje) : Exception(mensaje);
