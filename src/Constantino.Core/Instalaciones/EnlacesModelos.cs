using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Constantino.Core.Instalaciones;

/// <summary>
/// Hugging Face guarda cada modelo una vez («blobs») y en la carpeta de cada versión («snapshots») pone enlaces
/// simbólicos a esos archivos. Windows puede impedir a un proceso seguir enlaces simbólicos creados por un usuario sin
/// permisos de administrador (la protección «Redirection Trust»): para ese proceso los archivos no existen y los
/// motores fallan con mensajes engañosos («Unrecognized model…», «no file named model.safetensors»). Le pasó a
/// Calíope en el PC de desarrollo (27/11/2026): Calíope tenía la protección activada y los motores la heredaban.
/// Los enlaces duros son el mismo archivo con otro nombre: no ocupan más y la protección no les afecta.
/// </summary>
public static class EnlacesModelos
{
    /// <summary>
    /// Cambia por enlaces duros los enlaces simbólicos de las carpetas de versiones de la caché de modelos
    /// (<c>{modelos}\hub</c>). Devuelve cuántos ha cambiado. Lo que no se pueda cambiar se deja como está.
    /// </summary>
    public static int ConvertirSimbolicos(string carpetaModelos)
    {
        var hub = Path.Combine(carpetaModelos, "hub");
        if (!Directory.Exists(hub))
            return 0;
        // Primero la lista entera: cambiar archivos mientras se recorre la carpeta hace que el recorrido se salte algunos.
        var opciones = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        var simbolicos = Directory.EnumerateDirectories(hub, "snapshots", SearchOption.AllDirectories)
            .SelectMany(versiones => Directory.EnumerateFiles(versiones, "*", opciones))
            .Where(a => File.GetAttributes(a).HasFlag(FileAttributes.ReparsePoint))
            .ToList();
        return simbolicos.Count(Convertir);
    }

    /// <summary>Si <paramref name="archivo"/> es un enlace simbólico, lo cambia por un enlace duro a su destino.</summary>
    public static bool Convertir(string archivo)
    {
        // El destino se calcula con el texto del enlace, sin seguirlo: Calíope también puede tener la protección, y
        // entonces seguir el enlace (ResolveLinkTarget) falla. Así le pasó a la 1.7.9: no cambió ninguno.
        if (new FileInfo(archivo).LinkTarget is not { } texto)
            return false;
        var destino = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivo)!, texto));
        if (!File.Exists(destino) || new FileInfo(destino).LinkTarget is not null)
            return false;

        // Primero el enlace nuevo con otro nombre y después el cambio: si algo falla, el simbólico sigue ahí.
        var temporal = archivo + ".enlace-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (!CreateHardLinkW(Largo(temporal), Largo(destino), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            File.Move(temporal, archivo, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            try { File.Delete(temporal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>Con «\\?\» delante, Windows admite rutas de más de 260 caracteres (.NET lo hace solo; la API, no).</summary>
    private static string Largo(string ruta)
    {
        var completa = Path.GetFullPath(ruta);
        return completa.StartsWith(@"\\", StringComparison.Ordinal) ? completa : @"\\?\" + completa;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string nuevo, string existente, IntPtr seguridad);
}
