using System.Runtime.InteropServices;
using Constantino.Core.Actas;
using Constantino.Core.Catalogo;
using Constantino.Core.Doblaje;
using Constantino.Core.Narracion;
using Constantino.Core.Voces;
using Microsoft.Win32.SafeHandles;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Instalaciones;

/// <summary>Lo que ocupa un componente instalado y lo que se libera de verdad al desinstalarlo.</summary>
public sealed record EspacioComponente(Componente Componente, long Ocupa, long Libera);

/// <summary>Algo interno que se puede borrar sin perder nada: se vuelve a crear o a descargar cuando hace falta.</summary>
public sealed record EspacioLimpiable(string Id, string Nombre, string Descripcion, IReadOnlyList<string> Rutas, long Ocupa, long Libera);

public enum TipoTrabajo { Narracion, Doblaje, Acta, Voz }

/// <summary>Algo del usuario (una narración, un doblaje, un acta, una voz) y lo que ocupa.</summary>
public sealed record EspacioTrabajo(TipoTrabajo Tipo, string Id, string Nombre, string Ruta, long Ocupa, DateTimeOffset Fecha);

/// <summary>Todo lo que ocupa Calíope en disco, ordenado de mayor a menor en cada apartado.</summary>
public sealed record InformeEspacio(
    IReadOnlyList<EspacioComponente> Componentes,
    IReadOnlyList<EspacioLimpiable> Limpiables,
    IReadOnlyList<EspacioTrabajo> Trabajos,
    long Interno,
    long DelUsuario);

/// <summary>
/// Mide lo que ocupa cada cosa y lo que se libera al borrarla.
/// <para>
/// Ojo con los programas internos: uv instala cada paquete en una caché y los entornos de los motores usan el mismo
/// archivo («enlace duro»), sin copiarlo. PyTorch, por ejemplo, es un solo archivo en disco aunque aparezca en tres
/// entornos y en la caché. Borrar una de esas apariciones no libera nada; el espacio solo vuelve cuando se borran
/// todas. Por eso cada archivo se identifica por su número en el disco y se cuenta cuántas apariciones tiene y dónde.
/// </para>
/// </summary>
public sealed class Espacio(Instalador instalador, CatalogoComponentes catalogo)
{
    /// <summary>Los temporales más nuevos pueden estar en uso (una grabación a medias): solo se borran los de más de una hora.</summary>
    public static readonly TimeSpan EdadTemporales = TimeSpan.FromHours(1);

    private const string Cache = "cache", Restos = "restos";

    private RutasDatos R => instalador.Rutas;

    /// <summary>La carpeta de un modelo de Hugging Face (HF_HOME es la carpeta de modelos).</summary>
    public static string CarpetaModelo(RutasDatos rutas, string repositorio) =>
        Path.Combine(rutas.Modelos, "hub", "models--" + repositorio.Replace("/", "--", StringComparison.Ordinal));

    /// <summary>Todo lo de un componente: lo que dice su instalación y las carpetas de sus modelos de Hugging Face.</summary>
    public static IReadOnlyList<string> RutasDe(RutasDatos rutas, Componente c) => c.Instalacion is not { } inst
        ? []
        : inst.Rutas.Select(rutas.Ruta)
            .Concat(inst.Pasos.OfType<PasoModelo>().Select(p => CarpetaModelo(rutas, p.Repositorio)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Componentes que están en el disco (bien, dañados o con una versión anterior).</summary>
    public IReadOnlyList<Componente> Instalados() => catalogo.Componentes
        .Where(c => instalador.EstadoDe(c.Id) is not (EstadoComponente.NoInstalado or EstadoComponente.NoDisponible))
        .ToList();

    /// <summary>
    /// Carpetas que no son de ningún componente instalado: modelos y entornos de componentes ya quitados (o de
    /// versiones anteriores de Calíope) y la carpeta de ModelScope, que ya no se usa.
    /// </summary>
    public IReadOnlyList<string> RutasRestos()
    {
        var propias = new HashSet<string>(Instalados().SelectMany(c => RutasDe(R, c)).Select(Normalizar), StringComparer.OrdinalIgnoreCase);
        var candidatas = new List<string>();
        var hub = Path.Combine(R.Modelos, "hub");
        if (Directory.Exists(hub))
            candidatas.AddRange(Directory.EnumerateDirectories(hub, "models--*"));
        if (Directory.Exists(R.Entornos))
            candidatas.AddRange(Directory.EnumerateDirectories(R.Entornos));
        candidatas.Add(Path.Combine(R.Modelos, "modelscope"));
        return candidatas.Where(Directory.Exists).Where(d => !propias.Contains(Normalizar(d))).ToList();
    }

    /// <summary>Mide todo. Con muchos gigas tarda unos segundos: llamarlo en segundo plano.</summary>
    public InformeEspacio Medir(CancellationToken ct = default)
    {
        var instalados = Instalados();
        var restos = RutasRestos();
        var censo = new Censo();
        foreach (var c in instalados)
            foreach (var ruta in RutasDe(R, c))
                censo.Recorrer(ruta, c.Id, ct);
        censo.Recorrer(R.Cache, Cache, ct);
        foreach (var ruta in restos)
            censo.Recorrer(ruta, Restos, ct);
        var descargas = Tamano(R.Descargas);
        var registros = Tamano(R.Registros);
        var temporales = TemporalesViejos().Sum(f => f.Length);

        var componentes = instalados
            .Select(c => new EspacioComponente(c, censo.Ocupa(c.Id), censo.Libera(c.Id, Cache)))
            .OrderByDescending(e => e.Libera)
            .ToList();

        var limpiables = new List<EspacioLimpiable>
        {
            new(Cache, T("Caché de instalación"),
                T("Copia de los paquetes de Python que se usaron al instalar los componentes. Los componentes instalados no la necesitan para funcionar; si más adelante instalas o reparas algo, se vuelve a descargar lo que haga falta."),
                [R.Cache], censo.Ocupa(Cache), censo.Libera(Cache)),
            new(Restos, T("Restos de componentes quitados"),
                T("Modelos y programas que quedaron de componentes que ya no están instalados o de versiones anteriores de Calíope."),
                restos, censo.Ocupa(Restos), censo.Libera(Restos)),
            new("descargas", T("Descargas a medias"),
                T("Lo que se quedó a medio descargar al cancelar o fallar una instalación. Si vuelves a instalar, se descarga de nuevo."),
                [R.Descargas], descargas, descargas),
            new("temporales", T("Archivos temporales"),
                T("Pruebas de voz y audios intermedios que ya no hacen falta (de más de una hora)."),
                [R.Temporales], temporales, temporales),
            new("registros", T("Registros de instalación"),
                T("El detalle de lo que pasó en cada instalación. Solo sirven para buscar el motivo de un fallo."),
                [R.Registros], registros, registros),
        };

        var trabajos = Trabajos().OrderByDescending(t => t.Ocupa).ToList();
        return new InformeEspacio(componentes, limpiables.OrderByDescending(l => l.Libera).ToList(), trabajos,
            censo.Total + descargas + registros + Tamano(R.Temporales),
            trabajos.Sum(t => t.Ocupa));
    }

    /// <summary>Lo del usuario: narraciones, doblajes, actas y voces.</summary>
    public IEnumerable<EspacioTrabajo> Trabajos()
    {
        var narraciones = new AlmacenProyectos(R.Narraciones);
        foreach (var p in narraciones.Listar())
            yield return new(TipoTrabajo.Narracion, p.Id, p.Nombre, narraciones.CarpetaDe(p.Id), Tamano(narraciones.CarpetaDe(p.Id)), p.Modificado);
        var doblajes = new AlmacenDoblajes(R.Doblajes);
        foreach (var p in doblajes.Listar())
            yield return new(TipoTrabajo.Doblaje, p.Id, p.Nombre, doblajes.CarpetaDe(p.Id), Tamano(doblajes.CarpetaDe(p.Id)), p.Creado);
        var actas = new AlmacenActas(R.Actas);
        foreach (var p in actas.Listar())
            yield return new(TipoTrabajo.Acta, p.Id, p.Titulo, actas.CarpetaDe(p.Id), Tamano(actas.CarpetaDe(p.Id)), p.Creado);
        var voces = new BibliotecaVoces(R.Voces, R.Ffmpeg, new EjecutorProcesos());
        foreach (var v in voces.Listar())
            yield return new(TipoTrabajo.Voz, v.Id, v.Nombre, voces.CarpetaDe(v.Id), Tamano(voces.CarpetaDe(v.Id)), v.Creada);
    }

    /// <summary>Borra algo interno de <see cref="Medir"/>. Devuelve lo que no se pudo borrar (en uso), si algo.</summary>
    public IReadOnlyList<string> Limpiar(EspacioLimpiable limpiable)
    {
        var fallos = new List<string>();
        if (limpiable.Id == "temporales")
        {
            foreach (var f in TemporalesViejos())
                BorrarArchivo(f.FullName, fallos);
            return fallos;
        }
        foreach (var ruta in limpiable.Rutas)
        {
            // Las carpetas fijas (caché, descargas, registros) se vacían; los restos se quitan enteros.
            if (limpiable.Id == Restos)
                BorrarCarpeta(ruta, fallos);
            else if (Directory.Exists(ruta))
                foreach (var hijo in Directory.EnumerateFileSystemEntries(ruta))
                    BorrarEntrada(hijo, fallos);
        }
        return fallos;
    }

    private IEnumerable<FileInfo> TemporalesViejos()
    {
        if (!Directory.Exists(R.Temporales))
            return [];
        var limite = DateTime.Now - EdadTemporales;
        return new DirectoryInfo(R.Temporales).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.LastWriteTime < limite);
    }

    // --- Borrar sin seguir enlaces y sin que un archivo de solo lectura lo impida ------------------------------------

    /// <summary>Borra una carpeta entera. Un enlace de carpeta se quita sin tocar su destino.</summary>
    public static void BorrarCarpeta(string ruta, List<string> fallos)
    {
        if (Directory.Exists(ruta))
            BorrarEntrada(ruta, fallos);
    }

    private static void BorrarEntrada(string ruta, List<string> fallos)
    {
        var info = new FileInfo(ruta);
        var atributos = info.Attributes;
        if ((atributos & FileAttributes.Directory) == 0)
        {
            BorrarArchivo(ruta, fallos);
            return;
        }
        if ((atributos & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var hijo in Directory.EnumerateFileSystemEntries(ruta))
                BorrarEntrada(hijo, fallos);
        }
        try
        {
            new DirectoryInfo(ruta).Attributes = FileAttributes.Directory;
            Directory.Delete(ruta, recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Si dentro quedó algo en uso, la carpeta tampoco se puede quitar: ese archivo ya está en la lista.
            if (!fallos.Any(f => f.StartsWith(ruta, StringComparison.OrdinalIgnoreCase)))
                fallos.Add(ruta);
        }
    }

    private static void BorrarArchivo(string ruta, List<string> fallos)
    {
        try
        {
            File.SetAttributes(ruta, FileAttributes.Normal);
            File.Delete(ruta);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            fallos.Add(ruta);
        }
    }

    // --- Medir ------------------------------------------------------------------------------------------------------

    /// <summary>Suma de una carpeta (sin seguir enlaces de carpeta).</summary>
    public static long Tamano(string ruta) => Almacenamiento.Tamano(ruta);

    private static string Normalizar(string ruta) => Path.GetFullPath(ruta).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// Cada archivo (por su identidad en el disco) con su tamaño, cuántos nombres tiene en total y dónde están los que se
    /// han visto. Un archivo se libera al borrar un grupo de carpetas solo si todos sus nombres están dentro de ellas.
    /// </summary>
    private sealed class Censo
    {
        private sealed class Archivo(long tamano, uint enlaces)
        {
            public long Tamano { get; } = tamano;
            public uint Enlaces { get; } = enlaces;
            public Dictionary<string, int> Duenos { get; } = new(StringComparer.Ordinal);
        }

        private const long UmbralIdentidad = 64 * 1024;

        private readonly Dictionary<(uint, ulong), Archivo> _archivos = [];
        private ulong _sinIdentidad;

        public long Total => _archivos.Values.Sum(a => a.Tamano);

        public void Recorrer(string carpeta, string dueno, CancellationToken ct)
        {
            if (!Directory.Exists(carpeta))
                return;
            var pendientes = new Stack<DirectoryInfo>([new DirectoryInfo(carpeta)]);
            while (pendientes.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var actual = pendientes.Pop();
                try
                {
                    foreach (var e in actual.EnumerateFileSystemInfos())
                    {
                        if (e is DirectoryInfo d)
                        {
                            if ((d.Attributes & FileAttributes.ReparsePoint) == 0)
                                pendientes.Push(d);
                        }
                        else if (e is FileInfo f)
                        {
                            Anotar(f, dueno);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Una carpeta inaccesible no impide medir el resto.
                }
            }
        }

        private void Anotar(FileInfo f, string dueno)
        {
            // Identificar cada archivo cuesta: con los pequeños (cientos de miles, poco espacio) se supone que son solo
            // de su carpeta, salvo los de la caché, que casi siempre están también en algún entorno y no se cuentan como
            // liberables. Medido con 48 GB: 4 s en vez de 20, y como mucho 0,15 GB de diferencia por componente.
            (uint, ulong) clave;
            uint enlaces;
            if (f.Length < UmbralIdentidad || !Identidad(f.FullName, out clave, out enlaces))
            {
                clave = (0, (1UL << 63) | _sinIdentidad++);
                enlaces = dueno == Cache ? uint.MaxValue : 1;
            }
            if (!_archivos.TryGetValue(clave, out var a))
                _archivos[clave] = a = new Archivo(f.Length, enlaces);
            a.Duenos[dueno] = a.Duenos.GetValueOrDefault(dueno) + 1;
        }

        public long Ocupa(string dueno) => _archivos.Values.Where(a => a.Duenos.ContainsKey(dueno)).Sum(a => a.Tamano);

        /// <summary>
        /// Lo que se libera de <paramref name="dueno"/> al borrarlo junto con <paramref name="tambien"/>: sus archivos que no
        /// tienen nombres en ningún otro sitio.
        /// </summary>
        public long Libera(string dueno, params string[] tambien) => _archivos.Values
            .Where(a => a.Duenos.ContainsKey(dueno)
                        && a.Duenos.Keys.All(d => d == dueno || tambien.Contains(d))
                        && a.Duenos.Values.Sum() >= a.Enlaces)
            .Sum(a => a.Tamano);

        // BY_HANDLE_FILE_INFORMATION: las fechas son dos DWORD seguidos, sin alinear a 8 bytes.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct InformacionArchivo
        {
            public uint Atributos;
            public long Creado, Accedido, Escrito;
            public uint Volumen, TamanoAlto, TamanoBajo, Enlaces, IndiceAlto, IndiceBajo;
        }

        private static bool Identidad(string ruta, out (uint, ulong) clave, out uint enlaces)
        {
            clave = default;
            enlaces = 1;
            // Sin pedir acceso de lectura (solo atributos): funciona aunque otro programa lo tenga abierto.
            using var h = CreateFileW(@"\\?\" + ruta, 0x80, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (h.IsInvalid || !GetFileInformationByHandle(h, out var info))
                return false;
            clave = (info.Volumen, ((ulong)info.IndiceAlto << 32) | (ulong)info.IndiceBajo);
            enlaces = Math.Max(1, info.Enlaces);
            return true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string nombre, uint acceso, uint compartir, IntPtr seguridad, uint creacion,
            uint banderas, IntPtr plantilla);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle archivo, out InformacionArchivo info);
    }
}
