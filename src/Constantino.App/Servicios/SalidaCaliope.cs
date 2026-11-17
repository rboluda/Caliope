using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Constantino.Core.Instalaciones;
using static Constantino.Core.Idioma;

namespace Constantino.App.Servicios;

/// <summary>
/// La «Salida de Calíope»: un micrófono virtual que Teams, Zoom o Discord pueden elegir para oír la voz en directo.
///
/// Windows no deja que un programa haga de micrófono, así que se usa VB-CABLE (VB-Audio, donationware,
/// https://vb-audio.com/Cable/): un cable de sonido virtual con dos extremos. Calíope reproduce la voz convertida en
/// uno («CABLE Input») y Teams la oye por el otro («CABLE Output»).
///
/// Su licencia no permite meterlo en el instalador de otro programa sin permiso del autor, así que Calíope no lo
/// reparte: lo descarga de la web oficial de VB-Audio (paquete fijo, comprobado con su huella) y abre SU instalador,
/// donde el usuario pulsa «Install Driver». Después solo le cambia el nombre a los dos extremos, como se haría a mano
/// en la configuración de sonido de Windows: en Teams aparece «Salida de Calíope (VB-Audio Virtual Cable)».
/// Instalar un controlador y renombrar dispositivos de sonido necesita permisos de administrador: se hace en un
/// proceso aparte de Calíope (<see cref="OrdenPreparar"/>), que Windows pide confirmar una sola vez.
/// </summary>
public static class SalidaCaliope
{
    public const string UrlPaquete = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    public const string Sha256Paquete = "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90";
    public const string WebVbCable = "https://vb-audio.com/Cable/";

    /// <summary>Nombre del extremo que Teams ve como micrófono.</summary>
    public const string NombreMicrofono = "Salida de Calíope";

    /// <summary>Nombre del extremo donde Calíope reproduce la voz (aparece entre los altavoces; no hay que elegirlo).</summary>
    public const string NombreInterno = "Calíope (uso interno)";

    public const string OrdenPreparar = "--preparar-salida";
    public const string OrdenRenombrar = "--renombrar-salida";

    private const string InterfazVbCable = "VB-Audio Virtual Cable";

    public enum Estado
    {
        /// <summary>VB-CABLE no está instalado.</summary>
        SinInstalar,

        /// <summary>Instalado, pero con el nombre de fábrica.</summary>
        SinRenombrar,

        /// <summary>Lista: en Teams aparece «Salida de Calíope».</summary>
        Lista,
    }

    /// <summary>Cómo está la salida en este equipo (lee los dispositivos de sonido de Windows; no necesita permisos).</summary>
    public static Estado Comprobar()
    {
        try
        {
            var extremos = ExtremosVbCable(escribir: false);
            if (extremos.Count == 0)
                return Estado.SinInstalar;
            return extremos.Any(e => e.Captura && e.Nombre == NombreMicrofono) ? Estado.Lista : Estado.SinRenombrar;
        }
        catch (COMException)
        {
            return Estado.SinInstalar;
        }
    }

    /// <summary>
    /// Descarga el paquete oficial de VB-CABLE (si no está ya), lo descomprime y abre su instalador con permisos de
    /// administrador; al terminar, renombra los dos extremos. Devuelve el estado final.
    /// </summary>
    public static async Task<Estado> PrepararAsync(RutasDatos rutas, Descargador descargador, CancellationToken ct)
    {
        var zip = Path.Combine(rutas.Descargas, "VBCABLE_Driver_Pack46.zip");
        await descargador.DescargarAsync(UrlPaquete, zip, Sha256Paquete, new Progress<(long, long?)>(), ct);
        var carpeta = Path.Combine(rutas.Descargas, "vbcable-pack46");
        if (Directory.Exists(carpeta))
            Directory.Delete(carpeta, recursive: true);
        ZipFile.ExtractToDirectory(zip, carpeta);
        await EjecutarElevadoAsync([OrdenPreparar, carpeta], ct);
        return Comprobar();
    }

    /// <summary>Solo renombra (VB-CABLE ya estaba instalado, o hizo falta reiniciar para que apareciera).</summary>
    public static async Task<Estado> RenombrarAsync(CancellationToken ct)
    {
        await EjecutarElevadoAsync([OrdenRenombrar], ct);
        return Comprobar();
    }

    private static async Task EjecutarElevadoAsync(string[] argumentos, CancellationToken ct)
    {
        var inicio = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var a in argumentos)
            inicio.ArgumentList.Add(a);
        Process? proceso;
        try
        {
            proceso = Process.Start(inicio);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(T("Has cancelado el permiso de administrador."));
        }
        if (proceso is not null)
            await proceso.WaitForExitAsync(ct);
    }

    /// <summary>¿Es Calíope arrancado solo para preparar la salida (con permisos de administrador)?</summary>
    public static bool EsOrdenElevada(string[] args) => args.Length > 0 && args[0] is OrdenPreparar or OrdenRenombrar;

    /// <summary>
    /// Lo que hace el proceso con permisos de administrador: abre el instalador de VB-Audio (si se pidió) y renombra los
    /// extremos. Código de salida: 0 si quedó lista; 2 si hace falta reiniciar para que aparezca el cable.
    /// </summary>
    public static int EjecutarElevada(string[] args)
    {
        try
        {
            if (args[0] == OrdenPreparar && args.Length > 1)
            {
                var instalador = Path.Combine(args[1], Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
                if (File.Exists(instalador))
                    Process.Start(new ProcessStartInfo(instalador) { UseShellExecute = true, WorkingDirectory = args[1] })?.WaitForExit();
            }
            // El cable puede tardar unos segundos en aparecer tras instalarse (o no aparecer hasta reiniciar).
            for (var intento = 0; intento < 20; intento++)
            {
                if (Renombrar())
                    return 0;
                Thread.Sleep(1000);
            }
            return 2;
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    /// <summary>Pone los nombres de Calíope a los extremos de VB-CABLE. Devuelve si encontró el cable.</summary>
    private static bool Renombrar()
    {
        var extremos = ExtremosVbCable(escribir: true);
        foreach (var e in extremos)
            e.CambiarNombre(e.Captura ? NombreMicrofono : NombreInterno);
        return extremos.Any(e => e.Captura);
    }

    // --- Dispositivos de sonido de Windows (Core Audio) ------------------------------------------------------------

    private sealed class Extremo(IPropertyStore? almacen, string nombre, bool captura)
    {
        public string Nombre { get; } = nombre;
        public bool Captura { get; } = captura;

        public void CambiarNombre(string nuevo)
        {
            if (almacen is null || Nombre == nuevo)
                return;
            var clave = PkeyDeviceDesc;
            var valor = PropVariant.Texto(nuevo);
            try
            {
                Marshal.ThrowExceptionForHR(almacen.SetValue(ref clave, ref valor));
                Marshal.ThrowExceptionForHR(almacen.Commit());
            }
            finally
            {
                valor.Liberar();
            }
        }
    }

    private static List<Extremo> ExtremosVbCable(bool escribir)
    {
        var lista = new List<Extremo>();
        var enumerador = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        foreach (var (flujo, captura) in new[] { (EDataFlow.Render, false), (EDataFlow.Capture, true) })
        {
            Marshal.ThrowExceptionForHR(enumerador.EnumAudioEndpoints(flujo, DeviceStateActive, out var coleccion));
            Marshal.ThrowExceptionForHR(coleccion.GetCount(out var n));
            for (uint i = 0; i < n; i++)
            {
                Marshal.ThrowExceptionForHR(coleccion.Item(i, out var dispositivo));
                Marshal.ThrowExceptionForHR(dispositivo.OpenPropertyStore(StgmRead, out var lectura));
                var interfaz = Leer(lectura, PkeyDeviceInterfaceFriendlyName);
                if (interfaz is null || !interfaz.Contains(InterfazVbCable, StringComparison.OrdinalIgnoreCase))
                    continue;
                var nombre = Leer(lectura, PkeyDeviceDesc) ?? "";
                IPropertyStore? almacen = null;
                if (escribir)
                    Marshal.ThrowExceptionForHR(dispositivo.OpenPropertyStore(StgmReadWrite, out almacen));
                lista.Add(new Extremo(almacen, nombre, captura));
            }
        }
        return lista;
    }

    private static string? Leer(IPropertyStore almacen, PropertyKey clave)
    {
        if (almacen.GetValue(ref clave, out var valor) != 0)
            return null;
        try
        {
            return valor.Tipo == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(valor.Puntero) : null;
        }
        finally
        {
            valor.Liberar();
        }
    }

    private const uint DeviceStateActive = 0x1;
    private const uint StgmRead = 0x0;
    private const uint StgmReadWrite = 0x2;

    private static readonly PropertyKey PkeyDeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey PkeyDeviceInterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formato, uint id)
    {
        public Guid Formato = formato;
        public uint Id = id;
    }

    /// <summary>PROPVARIANT reducido a lo que hace falta aquí: textos (VT_LPWSTR).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Tipo;
        [FieldOffset(8)] public IntPtr Puntero;

        public static PropVariant Texto(string texto) => new() { Tipo = 31, Puntero = Marshal.StringToCoTaskMemUni(texto) };

        public void Liberar() => PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant valor);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flujo, uint estados, out IMMDeviceCollection coleccion);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint n);
        [PreserveSig] int Item(uint indice, out IMMDevice dispositivo);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint contexto, IntPtr parametros, [MarshalAs(UnmanagedType.IUnknown)] out object interfaz);
        [PreserveSig] int OpenPropertyStore(uint acceso, out IPropertyStore almacen);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint n);
        [PreserveSig] int GetAt(uint indice, out PropertyKey clave);
        [PreserveSig] int GetValue(ref PropertyKey clave, out PropVariant valor);
        [PreserveSig] int SetValue(ref PropertyKey clave, ref PropVariant valor);
        [PreserveSig] int Commit();
    }
}
