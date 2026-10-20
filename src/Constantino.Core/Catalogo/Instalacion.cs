using System.Text.Json.Serialization;

namespace Constantino.Core.Catalogo;

/// <summary>
/// Cómo se instala un componente. Las rutas y argumentos admiten variables entre llaves
/// ({datos}, {uv}, {indiceTorch}…) que resuelve <c>Instalaciones.RutasDatos</c>.
/// </summary>
/// <param name="Version">Lo que se instala; si cambia en una versión nueva del catálogo, el componente se puede actualizar.</param>
/// <param name="Rutas">Lo que ocupa en disco: se borra al desinstalar.</param>
/// <param name="Comprobar">Archivos que deben existir para dar el componente por instalado.</param>
public sealed record Instalacion(
    string Version,
    IReadOnlyList<PasoInstalacion> Pasos,
    IReadOnlyList<string> Rutas,
    IReadOnlyList<string> Comprobar);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "tipo")]
[JsonDerivedType(typeof(PasoDescargar), "descargar")]
[JsonDerivedType(typeof(PasoExtraer), "extraer")]
[JsonDerivedType(typeof(PasoEjecutar), "ejecutar")]
[JsonDerivedType(typeof(PasoModelo), "modelo")]
public abstract record PasoInstalacion(string Descripcion);

/// <summary>Descarga un archivo y comprueba su SHA-256. Si ya está descargado y es correcto, no se repite.</summary>
public sealed record PasoDescargar(string Descripcion, string Url, string Sha256, string Destino, long TamanoBytes)
    : PasoInstalacion(Descripcion);

/// <summary>Descomprime un .zip. Con <paramref name="Incluir"/> solo extrae esas rutas (relativas, tras quitar la carpeta raíz).</summary>
public sealed record PasoExtraer(
    string Descripcion,
    string Archivo,
    string Destino,
    bool QuitarCarpetaRaiz = false,
    IReadOnlyList<string>? Incluir = null,
    bool BorrarArchivo = true) : PasoInstalacion(Descripcion);

/// <summary>Ejecuta un programa. Falla si termina con error o si su salida no contiene <paramref name="DebeContener"/>.</summary>
/// <param name="Progreso">Si se indica, el avance se estima por lo que crecen esas carpetas.</param>
public sealed record PasoEjecutar(
    string Descripcion,
    string Programa,
    IReadOnlyList<string> Argumentos,
    string? DebeContener = null,
    MedidaProgreso? Progreso = null) : PasoInstalacion(Descripcion);

/// <summary>
/// Estimación del avance de un proceso que no lo informa (p. ej. uv): se mide cuánto crece la mayor
/// de <paramref name="Carpetas"/> respecto al principio del paso, frente a lo que se espera que ocupe.
/// </summary>
public sealed record MedidaProgreso(IReadOnlyList<string> Carpetas, long BytesConGpu, long BytesSinGpu)
{
    public long Esperados(bool conGpu) => conGpu ? BytesConGpu : BytesSinGpu;
}

/// <summary>Descarga un modelo de Hugging Face con el Python de <paramref name="Python"/>, fijado a una revisión exacta.</summary>
public sealed record PasoModelo(string Descripcion, string Python, string Repositorio, string Revision, long TamanoBytes)
    : PasoInstalacion(Descripcion);
