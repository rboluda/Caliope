using System.Text.RegularExpressions;

namespace Constantino.Core.Instalaciones;

/// <summary>
/// Estructura de la carpeta de datos (ver <see cref="EstructuraDatos"/>) y variables que usan los pasos del catálogo.
/// Si la carpeta aún tiene la estructura antigua (no se pudo migrar), se usan las rutas antiguas.
/// </summary>
public sealed partial class RutasDatos
{
    public RutasDatos(string raiz, bool usaGpu)
    {
        Raiz = Path.GetFullPath(raiz);
        UsaGpu = usaGpu;
        EstructuraAntigua = EstructuraDatos.EsAntigua(Raiz);
        Interno = EstructuraAntigua ? Raiz : Path.Combine(Raiz, EstructuraDatos.Interno);
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
            // Chatterbox fija torch 2.6, que no tiene versión para CUDA 12.8.
            ["indiceTorchCu124"] = usaGpu ? "https://download.pytorch.org/whl/cu124" : "https://download.pytorch.org/whl/cpu",
        };
    }

    public string Raiz { get; }
    public bool UsaGpu { get; }
    public bool EstructuraAntigua { get; }

    // Lo del usuario, a la vista.
    public string Narraciones => Path.Combine(Raiz, EstructuraAntigua ? "proyectos" : EstructuraDatos.Narraciones);
    public string Voces => Path.Combine(Raiz, EstructuraAntigua ? "voces" : EstructuraDatos.Voces);
    public string Sonidos => Path.Combine(Raiz, EstructuraDatos.Sonidos);
    public string Doblajes => Path.Combine(Raiz, EstructuraDatos.Doblajes);
    public string Modelos => Path.Combine(Raiz, EstructuraAntigua ? "modelos" : EstructuraDatos.Modelos);

    // Lo interno, todo junto en «Programas internos».
    public string Interno { get; }
    public string Herramientas => Path.Combine(Interno, "herramientas");
    public string Python => Path.Combine(Interno, "python");
    public string Entornos => Path.Combine(Interno, "entornos");
    public string Descargas => Path.Combine(Interno, "descargas");
    public string Cache => Path.Combine(Interno, "cache");
    public string Registros => Path.Combine(Interno, "registros");
    public string Temporales => Path.Combine(Interno, "tmp");
    public string Uv => Path.Combine(Herramientas, "uv", "uv.exe");
    public string Ffmpeg => Path.Combine(Herramientas, "ffmpeg", "bin", "ffmpeg.exe");
    public string ArchivoEstado => Path.Combine(Interno, "estado.json");

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
        // Sin dejar nada fuera de la carpeta de datos: ni python3.11.exe en %USERPROFILE%\.local\bin ni el registro.
        ["UV_PYTHON_INSTALL_BIN"] = "0",
        ["UV_PYTHON_INSTALL_REGISTRY"] = "0",
        ["UV_PYTHON_NO_REGISTRY"] = "1",
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
