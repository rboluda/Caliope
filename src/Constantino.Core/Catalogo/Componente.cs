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

/// <param name="SinRtx50">No funciona con las tarjetas RTX 50 (su PyTorch es anterior a esa generación).</param>
public sealed record Requisitos(double? VramMinimaGb = null, double? VramRecomendadaGb = null, bool FuncionaEnCpu = true, bool SinRtx50 = false);

public sealed record Licencia(string Nombre, string Url);

/// <summary>Un componente instalable: núcleo, motor, modelo o extra.</summary>
/// <param name="GrupoExclusivo">Componentes del mismo grupo son alternativas: basta con instalar uno.</param>
/// <param name="Instalacion">Null si esta versión de Constantino todavía no sabe instalarlo.</param>
/// <param name="DisponibleEn">Si aún no se puede instalar, cuándo llegará (para mostrarlo).</param>
/// <param name="Consejo">«¿Lo necesito?»: si conviene instalarlo y si hace falta otro además (en lenguaje llano).</param>
/// <param name="Tecnico">Detalles técnicos (versiones, librerías): solo dentro de «Más información».</param>
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
    string? GrupoExclusivo = null,
    Instalacion? Instalacion = null,
    string? DisponibleEn = null,
    string? Consejo = null,
    string? Tecnico = null)
{
    public bool SePuedeInstalar => Instalacion is not null;
}
