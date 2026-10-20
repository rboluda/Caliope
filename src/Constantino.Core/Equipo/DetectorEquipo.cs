using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Constantino.Core.Equipo;

/// <summary>Analiza el hardware para recomendar qué instalar. Nada de lo que hace modifica el sistema.</summary>
public static class DetectorEquipo
{
    private const double MibPorGib = 1024.0;

    public static async Task<InformeEquipo> DetectarAsync(string carpetaDatos, CancellationToken ct = default)
    {
        var nvidia = await DetectarNvidiaAsync(ct).ConfigureAwait(false);
        var gpus = nvidia.Concat(DetectarOtrasGpus(nvidia)).ToList();
        return new InformeEquipo(
            SistemaOperativo(),
            Procesador(),
            Environment.ProcessorCount,
            RamTotalGb(),
            gpus,
            Disco(carpetaDatos));
    }

    /// <summary>Consulta nvidia-smi, que instala el driver de NVIDIA. Sin él no hay CUDA.</summary>
    private static async Task<IReadOnlyList<Gpu>> DetectarNvidiaAsync(CancellationToken ct)
    {
        try
        {
            var inicio = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total,memory.used,driver_version --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proceso = Process.Start(inicio);
            if (proceso is null)
                return [];
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(10));
            var salida = await proceso.StandardOutput.ReadToEndAsync(limite.Token).ConfigureAwait(false);
            await proceso.WaitForExitAsync(limite.Token).ConfigureAwait(false);
            return proceso.ExitCode == 0 ? LeerSalidaNvidiaSmi(salida) : [];
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            // nvidia-smi no existe (no hay NVIDIA o falta el driver) o no respondió.
            return [];
        }
    }

    /// <summary>Interpreta «nombre, MiB totales, MiB usados, driver» por línea.</summary>
    internal static IReadOnlyList<Gpu> LeerSalidaNvidiaSmi(string salida)
    {
        var gpus = new List<Gpu>();
        foreach (var linea in salida.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var campos = linea.Split(',', StringSplitOptions.TrimEntries);
            if (campos.Length < 4)
                continue;
            gpus.Add(new Gpu(
                campos[0],
                "NVIDIA",
                Mib(campos[1]) / MibPorGib,
                Mib(campos[2]) / MibPorGib,
                campos[3],
                CompatibleCuda: true));
        }
        return gpus;

        static double? Mib(string texto) =>
            double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Resto de tarjetas (Intel, AMD…) vía WMI, solo para informar: no sirven para CUDA.</summary>
    private static IEnumerable<Gpu> DetectarOtrasGpus(IReadOnlyList<Gpu> nvidia)
    {
        var resultado = new List<Gpu>();
        try
        {
            using var buscador = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController");
            foreach (var obj in buscador.Get())
            {
                using (obj)
                {
                    var nombre = (obj["Name"] as string)?.Trim();
                    if (string.IsNullOrEmpty(nombre) || nvidia.Any(n => n.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var fabricante = (obj["AdapterCompatibility"] as string)?.Trim() ?? "";
                    // Una NVIDIA que nvidia-smi no ve suele indicar que falta el driver.
                    resultado.Add(new Gpu(nombre, fabricante, null, null, null, CompatibleCuda: false));
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (COMException)
        {
        }
        return resultado;
    }

    private static string SistemaOperativo()
    {
        var v = Environment.OSVersion.Version;
        var nombre = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : v.Major == 10 ? "Windows 10" : "Windows";
        return $"{nombre} (compilación {v.Build}, {RuntimeInformation.OSArchitecture})";
    }

    private static string Procesador()
    {
        using var clave = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (clave?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Desconocido";
    }

    private static double RamTotalGb()
    {
        var estado = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref estado) ? estado.ullTotalPhys / (1024.0 * 1024 * 1024) : 0;
    }

    internal static InfoDisco? Disco(string carpeta)
    {
        try
        {
            var raiz = Path.GetPathRoot(Path.GetFullPath(carpeta));
            if (string.IsNullOrEmpty(raiz))
                return null;
            var unidad = new DriveInfo(raiz);
            const double gib = 1024.0 * 1024 * 1024;
            return new InfoDisco(carpeta, unidad.Name, unidad.AvailableFreeSpace / gib, unidad.TotalSize / gib,
                CarpetaDatos.EstaEnOneDrive(carpeta));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
