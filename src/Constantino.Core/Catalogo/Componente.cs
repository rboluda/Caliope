namespace Constantino.Core.Catalogo;

/// <summary>Lo que el catálogo dice de un componente, antes de mirar el equipo.</summary>
public enum Necesidad
{
    Imprescindible,
    Recomendado,
    Opcional,
}

public sealed record TamanoDescarga(double ConGpuGb, double SinGpuGb)
{
    public double Para(bool conGpu) => conGpu ? ConGpuGb : SinGpuGb;
}

public sealed record Requisitos(double? VramMinimaGb = null, double? VramRecomendadaGb = null, bool FuncionaEnCpu = true);

public sealed record Licencia(string Nombre, string Url);

/// <summary>Un componente instalable: núcleo, motor, modelo o extra.</summary>
/// <param name="GrupoExclusivo">Componentes del mismo grupo son alternativas: basta con instalar uno.</param>
public sealed record Componente(
    string Id,
    string Nombre,
    string Grupo,
    Necesidad Necesidad,
    string Resumen,
    string Detalle,
    IReadOnlyList<string> Habilita,
    IReadOnlyList<string> DependeDe,
    TamanoDescarga Tamano,
    Requisitos Requisitos,
    IReadOnlyList<Licencia> Licencias,
    string? GrupoExclusivo = null);
