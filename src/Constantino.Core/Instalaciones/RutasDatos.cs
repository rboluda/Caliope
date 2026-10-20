using System.Text.RegularExpressions;

namespace Constantino.Core.Instalaciones;

/// <summary>Estructura de la carpeta de datos y variables que usan los pasos del catálogo.</summary>
public sealed partial class RutasDatos
{
    public RutasDatos(string raiz, bool usaGpu)
    {
        Raiz = Path.GetFullPath(raiz);
        UsaGpu = usaGpu;
        Variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["datos"] = Raiz,
            ["herramientas"] = Herramientas,
            ["python"] = Python,
            ["entornos"] = Entornos,
            ["modelos"] = Modelos,
            ["descargas"] = Descargas,
            ["cache"] = Cache,
            ["uv"] = Uv,
            ["ffmpeg"] = Ffmpeg,
            ["dispositivo"] = usaGpu ? "cuda" : "cpu",
            ["indiceTorch"] = usaGpu ? "https://download.pytorch.org/whl/cu128" : "https://download.pytorch.org/whl/cpu",
        };
    }

    public string Raiz { get; }
    public bool UsaGpu { get; }
    public string Herramientas => Path.Combine(Raiz, "herramientas");
    public string Python => Path.Combine(Raiz, "python");
    public string Entornos => Path.Combine(Raiz, "entornos");
    public string Modelos => Path.Combine(Raiz, "modelos");
    public string Descargas => Path.Combine(Raiz, "descargas");
    public string Cache => Path.Combine(Raiz, "cache");
    public string Registros => Path.Combine(Raiz, "registros");
    public string Uv => Path.Combine(Herramientas, "uv", "uv.exe");
    public string Ffmpeg => Path.Combine(Herramientas, "ffmpeg", "bin", "ffmpeg.exe");
    public string ArchivoEstado => Path.Combine(Raiz, "estado.json");

    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>Sustituye {variables}. Una variable desconocida es un error del catálogo.</summary>
    public string Expandir(string texto) => Variable().Replace(texto, m =>
        Variables.TryGetValue(m.Groups[1].Value, out var valor)
            ? valor
            : throw new InvalidOperationException($"Variable desconocida en el catálogo: {m.Value}"));

    /// <summary>Expande y normaliza una ruta (las del catálogo usan «/»).</summary>
    public string Ruta(string texto) => Path.GetFullPath(Expandir(texto));

    /// <summary>
    /// Entorno de todos los procesos que lanza el instalador: todo dentro de la carpeta de datos,
    /// sin usar ni tocar ningún Python del sistema.
    /// </summary>
    public IReadOnlyDictionary<string, string> EntornoProcesos => new Dictionary<string, string>
    {
        ["UV_PYTHON_INSTALL_DIR"] = Python,
        ["UV_PYTHON_PREFERENCE"] = "only-managed",
        ["UV_CACHE_DIR"] = Path.Combine(Cache, "uv"),
        ["UV_NO_CONFIG"] = "1",
        ["UV_NO_PROGRESS"] = "1",
        ["HF_HOME"] = Modelos,
        ["HF_HUB_DISABLE_XET"] = "1",
        ["HF_HUB_DISABLE_TELEMETRY"] = "1",
        ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1",
        ["PYTHONUTF8"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
        // Sin búfer: los avisos de los scripts llegan en el momento, no al terminar.
        ["PYTHONUNBUFFERED"] = "1",
        ["PYTHONNOUSERSITE"] = "1",
        ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
    };

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Variable();
}
