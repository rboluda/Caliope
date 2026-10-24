namespace Constantino.Core.Equipo;

/// <summary>Una tarjeta gráfica. La VRAM solo se conoce con fiabilidad en las NVIDIA (vía nvidia-smi).</summary>
/// <param name="CapacidadCuda">La generación de la tarjeta para CUDA (6.1 una GTX 1070, 8.6 una RTX 3050…), si se conoce.</param>
public sealed record Gpu(
    string Nombre,
    string Fabricante,
    double? VramTotalGb,
    double? VramUsadaGb,
    string? Driver,
    bool CompatibleCuda,
    double? CapacidadCuda = null)
{
    /// <summary>Capacidad CUDA a partir de la cual sirve PyTorch 2.11 para CUDA 12.8 (Turing, 2018: RTX 20 y GTX 16).</summary>
    public const double CapacidadModerna = 7.5;

    public double? VramLibreGb => VramTotalGb - VramUsadaGb;

    /// <summary>
    /// GTX 10xx y anteriores (Volta, Pascal, Maxwell): PyTorch para CUDA 12.8 ya no trae código para ellas, así que se instala
    /// el de CUDA 12.6, y no tienen media precisión útil, así que los modelos van en float32 (ocupan el doble).
    /// </summary>
    public bool Antigua => CapacidadCuda < CapacidadModerna;
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
