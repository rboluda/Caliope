namespace Constantino.Core.Instalaciones;

/// <summary>Una carpeta de la carpeta de datos, explicada y con lo que ocupa.</summary>
public sealed record UsoCarpeta(string Nombre, string Descripcion, string Ruta, long Bytes);

/// <summary>Qué hay en la carpeta de datos y cuánto ocupa cada parte, para enseñárselo al usuario.</summary>
public static class Almacenamiento
{
    public static IReadOnlyList<UsoCarpeta> Medir(RutasDatos rutas) =>
    [
        new("Mis narraciones", "Tus proyectos. El audio de cada uno está en su carpeta «Audio».", rutas.Narraciones, Tamano(rutas.Narraciones)),
        new("Mis voces", "Las voces de tu biblioteca y sus variantes.", rutas.Voces, Tamano(rutas.Voces)),
        new("Mis sonidos", "Efectos, ambientes y música. Puedes añadir tus propios audios.", rutas.Sonidos, Tamano(rutas.Sonidos)),
        new("Modelos de voz", "Lo que genera la voz. Se instalan y se quitan desde Componentes.", rutas.Modelos, Tamano(rutas.Modelos)),
        // Los entornos de los motores comparten sus archivos con la caché (enlaces): contarlos dos veces daría
        // un total inflado, así que se mide todo lo interno salvo los entornos.
        new("Programas internos", "Motores y herramientas. No borres nada de aquí: si algo falla, usa «Reparar» en Componentes.",
            rutas.Interno, rutas.EstructuraAntigua ? TamanoInternoAntiguo(rutas) : Tamano(rutas.Interno, excluir: rutas.Entornos)),
    ];

    private static long TamanoInternoAntiguo(RutasDatos r) =>
        new[] { r.Herramientas, r.Python, r.Cache, r.Descargas, r.Registros, r.Temporales }.Sum(c => Tamano(c));

    /// <summary>Suma de los archivos de <paramref name="carpeta"/> (sin entrar en <paramref name="excluir"/>).</summary>
    public static long Tamano(string carpeta, string? excluir = null)
    {
        if (!Directory.Exists(carpeta))
            return 0;
        long total = 0;
        var pendientes = new Stack<string>([carpeta]);
        while (pendientes.Count > 0)
        {
            var actual = pendientes.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(actual))
                {
                    try { total += new FileInfo(f).Length; }
                    catch (IOException) { }
                }
                foreach (var d in Directory.EnumerateDirectories(actual))
                {
                    if (excluir is not null && string.Equals(Path.GetFullPath(d), Path.GetFullPath(excluir), StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Los enlaces de directorio (si los hay) no se siguen: su contenido ya está en otro sitio.
                    if (new DirectoryInfo(d).LinkTarget is null)
                        pendientes.Push(d);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Una carpeta inaccesible no impide medir el resto.
            }
        }
        return total;
    }
}
