namespace Constantino.Core.Equipo;

/// <summary>Una tarjeta gráfica. La VRAM solo se conoce con fiabilidad en las NVIDIA (vía nvidia-smi).</summary>
public sealed record Gpu(
    string Nombre,
    string Fabricante,
    double? VramTotalGb,
    double? VramUsadaGb,
    string? Driver,
    bool CompatibleCuda)
{
    public double? VramLibreGb => VramTotalGb - VramUsadaGb;
}

public sealed record InfoDisco(string Ruta, string Unidad, double LibreGb, double TotalGb, bool EnOneDrive);

public sealed record InformeEquipo(
    string SistemaOperativo,
    string Procesador,
    int Nucleos,
    double RamGb,
    IReadOnlyList<Gpu> Gpus,
    InfoDisco? Disco)
{
    /// <summary>La GPU compatible con CUDA con más memoria, si hay alguna.</summary>
    public Gpu? GpuPrincipal => Gpus.Where(g => g.CompatibleCuda).MaxBy(g => g.VramTotalGb ?? 0);
}
