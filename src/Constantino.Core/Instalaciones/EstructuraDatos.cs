using System.Text;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Instalaciones;

/// <summary>Resultado de pasar una carpeta de datos a la estructura nueva.</summary>
/// <param name="Migrada">True si se ha movido algo.</param>
/// <param name="Error">Si no se ha podido, por qué (la carpeta queda como estaba).</param>
public sealed record ResultadoMigracion(bool Migrada, string? Error = null);

/// <summary>
/// Cómo se organiza la carpeta de datos, pensada para que se entienda al abrirla:
/// <code>
/// Mis narraciones\      proyectos y su audio
/// Mis voces\
/// Modelos de voz\       lo que más ocupa
/// Programas internos\   motores y herramientas (no tocar)
/// LÉEME.txt
/// </code>
/// Las versiones anteriores lo guardaban todo suelto en la raíz (herramientas, python, entornos, modelos, voces…):
/// <see cref="Migrar"/> lo reorganiza moviendo carpetas, sin volver a descargar nada.
/// </summary>
public static class EstructuraDatos
{
    public const string Narraciones = "Mis narraciones";
    public const string Voces = "Mis voces";
    public const string Sonidos = "Mis sonidos";
    public const string Doblajes = "Mis doblajes";
    public const string Modelos = "Modelos de voz";
    public const string Interno = "Programas internos";
    public const string Leeme = "LÉEME.txt";

    /// <summary>Carpetas sueltas de la estructura antigua y adónde van.</summary>
    private static readonly (string Antigua, string Nueva)[] Traslados =
    [
        ("proyectos", Narraciones),
        ("voces", Voces),
        ("modelos", Modelos),
        ("herramientas", Path.Combine(Interno, "herramientas")),
        ("python", Path.Combine(Interno, "python")),
        ("entornos", Path.Combine(Interno, "entornos")),
        ("cache", Path.Combine(Interno, "cache")),
        ("descargas", Path.Combine(Interno, "descargas")),
        ("registros", Path.Combine(Interno, "registros")),
        ("tmp", Path.Combine(Interno, "tmp")),
        ("estado.json", Path.Combine(Interno, "estado.json")),
    ];

    /// <summary>True si la carpeta usa todavía la estructura antigua (con carpetas técnicas en la raíz).</summary>
    public static bool EsAntigua(string raiz) =>
        !Directory.Exists(Path.Combine(raiz, Interno)) &&
        Traslados.Any(t => Directory.Exists(Path.Combine(raiz, t.Antigua)) || File.Exists(Path.Combine(raiz, t.Antigua)));

    /// <summary>
    /// Pasa <paramref name="raiz"/> a la estructura nueva. Todo se mueve dentro del mismo disco (es instantáneo y
    /// conserva los enlaces de la caché). Si algo falla, deshace lo movido y la carpeta sigue con la estructura antigua.
    /// </summary>
    public static ResultadoMigracion Migrar(string raiz)
    {
        if (!EsAntigua(raiz))
        {
            // Una carpeta que aún no existe (asistente sin terminar) no se crea solo por esto.
            if (Directory.Exists(raiz))
            {
                // Se repite en cada arranque: repara una migración anterior que no corrigiera los enlaces.
                CorregirEnlacesPython(raiz);
                CorregirEntornos(raiz);
                RepararLanzadores(raiz);
                EscribirLeeme(raiz);
            }
            return new ResultadoMigracion(false);
        }

        var hechos = new List<(string Desde, string Hasta)>();
        try
        {
            Directory.CreateDirectory(Path.Combine(raiz, Interno));
            foreach (var (antigua, nueva) in Traslados)
            {
                var desde = Path.Combine(raiz, antigua);
                var hasta = Path.Combine(raiz, nueva);
                if (Directory.Exists(desde) && !Directory.Exists(hasta))
                    Directory.Move(desde, hasta);
                else if (File.Exists(desde) && !File.Exists(hasta))
                    File.Move(desde, hasta);
                else
                    continue;
                hechos.Add((desde, hasta));
            }
            CorregirEnlacesPython(raiz);
            CorregirEntornos(raiz);
            EscribirLeeme(raiz);
            // Lo que falle aquí ya no deshace la migración: el motor se puede reparar desde Componentes.
            try { RepararLanzadores(raiz); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            return new ResultadoMigracion(hechos.Count > 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Se deshace en orden inverso para no dejar la carpeta a medias.
            foreach (var (desde, hasta) in Enumerable.Reverse(hechos))
            {
                try
                {
                    if (Directory.Exists(hasta))
                        Directory.Move(hasta, desde);
                    else if (File.Exists(hasta))
                        File.Move(hasta, desde);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Lo que no se pueda devolver se queda en la estructura nueva; no hay nada mejor que hacer.
                }
            }
            try { Directory.Delete(Path.Combine(raiz, Interno)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new ResultadoMigracion(false, e.Message);
        }
    }

    /// <summary>
    /// uv crea en su carpeta de Python un enlace por versión menor («cpython-3.11-…» → «cpython-3.11.17-…») con la ruta
    /// absoluta del destino, y los entornos apuntan a ese enlace. Si la carpeta se ha movido, el enlace queda roto:
    /// se vuelve a crear apuntando a la versión que está al lado.
    /// </summary>
    internal static void CorregirEnlacesPython(string raiz)
    {
        var python = Path.Combine(raiz, Interno, "python");
        if (!Directory.Exists(python))
            return;
        foreach (var enlace in Directory.EnumerateDirectories(python))
        {
            var info = new DirectoryInfo(enlace);
            if (info.LinkTarget is not { } destino || Directory.Exists(destino))
                continue;
            var nuevo = Path.Combine(python, Path.GetFileName(destino.TrimEnd('\\', '/')));
            if (!Directory.Exists(nuevo))
                continue;
            Directory.Delete(enlace); // solo el enlace, no lo que había detrás
            CrearUnion(enlace, nuevo);
        }
    }

    /// <summary>
    /// Quita las uniones de <paramref name="carpeta"/> que Windows no deja recorrer (os error 448, «punto de montaje
    /// no confiable»): uv las crea por versión menor, y si no se pueden abrir, los entornos nuevos tienen que apuntar
    /// directamente a la versión que hay detrás. Solo se borra la unión, nunca lo que hay dentro.
    /// </summary>
    public static void QuitarUnionesInaccesibles(string carpeta)
    {
        if (!Directory.Exists(carpeta))
            return;
        foreach (var enlace in Directory.EnumerateDirectories(carpeta))
        {
            if (new DirectoryInfo(enlace).LinkTarget is null)
                continue;
            try
            {
                _ = Directory.EnumerateFileSystemEntries(enlace).Any();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { Directory.Delete(enlace); }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Una unión de directorio (no necesita permisos de administrador, a diferencia de los enlaces simbólicos).</summary>
    private static void CrearUnion(string enlace, string destino)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", enlace, destino },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        if (p.ExitCode != 0 || !Directory.Exists(enlace))
            throw new IOException(T("No se ha podido recrear el enlace {0}: {1}", enlace, p.StandardError.ReadToEnd().Trim()));
    }

    /// <summary>
    /// Cada entorno de Python apunta en su pyvenv.cfg a la ruta absoluta de su Python: al mover las carpetas
    /// hay que corregirla o el motor no arrancaría.
    /// </summary>
    internal static void CorregirEntornos(string raiz)
    {
        var entornos = Path.Combine(raiz, Interno, "entornos");
        if (!Directory.Exists(entornos))
            return;
        var pythonAntiguo = Path.Combine(raiz, "python");
        var pythonNuevo = Path.Combine(raiz, Interno, "python");
        foreach (var cfg in Directory.EnumerateDirectories(entornos).Select(d => Path.Combine(d, "pyvenv.cfg")).Where(File.Exists))
        {
            var lineas = File.ReadAllLines(cfg);
            var cambiado = false;
            for (var i = 0; i < lineas.Length; i++)
            {
                var partes = lineas[i].Split('=', 2);
                if (partes.Length != 2 || partes[0].Trim() != "home")
                    continue;
                var home = partes[1].Trim();
                if (home.StartsWith(pythonAntiguo, StringComparison.OrdinalIgnoreCase))
                {
                    lineas[i] = $"home = {pythonNuevo}{home[pythonAntiguo.Length..]}";
                    cambiado = true;
                }
            }
            if (cambiado)
                File.WriteAllLines(cfg, lineas);
        }
    }

    /// <summary>
    /// El python.exe de cada entorno es un lanzador de uv con la ruta absoluta del Python base grabada dentro. Si esa
    /// ruta ya no existe (la carpeta se ha movido), se crea un entorno temporal con el Python en su sitio actual y se
    /// copia su lanzador: solo lleva esa ruta, así que sirve para todos los entornos de la misma versión. Los
    /// paquetes instalados no se tocan.
    /// </summary>
    internal static void RepararLanzadores(string raiz)
    {
        var interno = Path.Combine(raiz, Interno);
        var entornos = Path.Combine(interno, "entornos");
        var uv = Path.Combine(interno, "herramientas", "uv", "uv.exe");
        if (!Directory.Exists(entornos) || !File.Exists(uv))
            return;
        var rotos = Directory.EnumerateDirectories(entornos)
            .Where(e => File.Exists(Path.Combine(e, "pyvenv.cfg")))
            .Where(e => RutaIncrustada(Path.Combine(e, "Scripts", "python.exe")) is not { } ruta || !File.Exists(ruta))
            .ToList();
        if (rotos.Count == 0)
            return;

        var temporal = Path.Combine(interno, "tmp", "lanzador-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var inicio = new System.Diagnostics.ProcessStartInfo(uv)
            {
                ArgumentList = { "venv", temporal, "--python", "3.11", "--quiet" },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            inicio.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(interno, "python");
            inicio.Environment["UV_PYTHON_PREFERENCE"] = "only-managed";
            inicio.Environment["UV_NO_CONFIG"] = "1";
            inicio.Environment["UV_CACHE_DIR"] = Path.Combine(interno, "cache", "uv");
            using (var p = System.Diagnostics.Process.Start(inicio)!)
            {
                p.StandardOutput.ReadToEnd();
                var error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new IOException(T("uv no ha podido crear el lanzador: {0}", error.Trim()));
            }
            foreach (var entorno in rotos)
            {
                foreach (var exe in new[] { "python.exe", "pythonw.exe" })
                {
                    var origen = Path.Combine(temporal, "Scripts", exe);
                    if (File.Exists(origen))
                        File.Copy(origen, Path.Combine(entorno, "Scripts", exe), overwrite: true);
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporal))
            {
                try { Directory.Delete(temporal, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>La ruta del Python base que lleva grabada un lanzador de uv, o null si no se encuentra.</summary>
    internal static string? RutaIncrustada(string lanzador)
    {
        if (!File.Exists(lanzador))
            return null;
        var texto = Encoding.Latin1.GetString(File.ReadAllBytes(lanzador));
        var m = System.Text.RegularExpressions.Regex.Match(texto, @"[A-Za-z]:\\[^\x00-\x1f""<>|*?]*?\\python\.exe");
        return m.Success ? m.Value : null;
    }

    /// <summary>Un LÉEME en la raíz que explica cada carpeta a quien la abra.</summary>
    public static void EscribirLeeme(string raiz)
    {
        Directory.CreateDirectory(raiz);
        // Cada párrafo se traduce entero; sus saltos de línea se sangran como el resto del texto.
        static string Sangrar(string parrafo) => "    " + parrafo.Replace("\n", Environment.NewLine + "    ");
        var titulo = T("CARPETA DE CALÍOPE");
        var texto = new StringBuilder()
            .AppendLine(titulo)
            .AppendLine(new string('=', titulo.Length))
            .AppendLine()
            .AppendLine($"{Narraciones}\\")
            .AppendLine(Sangrar(T("Tus proyectos de narración. Dentro de cada uno, la carpeta «Audio» tiene la narración\ncompleta y un archivo por bloque, listos para usar.")))
            .AppendLine()
            .AppendLine($"{Voces}\\")
            .AppendLine(Sangrar(T("Las voces de tu biblioteca: la grabación de cada una y sus variantes.")))
            .AppendLine()
            .AppendLine($"{Sonidos}\\")
            .AppendLine(Sangrar(T("Efectos, ambientes y música para tus narraciones. Puedes añadir tus propios audios a cada carpeta.")))
            .AppendLine()
            .AppendLine($"{Doblajes}\\")
            .AppendLine(Sangrar(T("Tus vídeos doblados. Dentro de cada uno, la carpeta «Audio» tiene el vídeo doblado, su audio\ny los subtítulos.")))
            .AppendLine()
            .AppendLine($"{Modelos}\\")
            .AppendLine(Sangrar(T("Los modelos que generan la voz. Es lo que más ocupa. Se instalan y se quitan desde\nCalíope → Componentes.")))
            .AppendLine()
            .AppendLine($"{Interno}\\")
            .AppendLine(Sangrar(T("Los motores y herramientas que usa Calíope por dentro. No borres ni muevas nada de aquí:\nsi algo falla, usa «Reparar» en Calíope → Componentes.")))
            .AppendLine()
            .AppendLine(T("Puedes copiar «Mis narraciones», «Mis voces» y «Mis sonidos» para hacer una copia de seguridad."))
            .ToString();
        var ruta = Path.Combine(raiz, Leeme);
        if (!File.Exists(ruta) || File.ReadAllText(ruta) != texto)
            File.WriteAllText(ruta, texto, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
