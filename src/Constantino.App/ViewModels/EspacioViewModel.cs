using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Instalaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Wpf.Ui;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>Un componente instalado en «Espacio».</summary>
public sealed record FilaEspacioComponente(EspacioComponente Datos)
{
    public string Nombre => Datos.Componente.Nombre;
    public string Libera => EspacioViewModel.Tamano(Datos.Libera);
    public string Detalle => Datos.Ocupa - Datos.Libera > 100_000_000
        ? T("Ocupa {0}; el resto lo comparte con otros componentes y se queda.", Formato.Bytes(Datos.Ocupa))
        : T("Ocupa {0}.", Formato.Bytes(Datos.Ocupa));
}

/// <summary>Algo interno que se puede borrar sin perder nada.</summary>
public sealed record FilaEspacioLimpiable(EspacioLimpiable Datos)
{
    public string Nombre => Datos.Nombre;
    public string Descripcion => Datos.Descripcion;
    public string Libera => EspacioViewModel.Tamano(Datos.Libera);
    public bool HayAlgo => Datos.Ocupa > 0;

    /// <summary>Para quien quiera saberlo: dónde está y, si no se libera todo, por qué.</summary>
    public string Detalle => string.Join("\n", Datos.Rutas.Where(Directory.Exists)) + (Datos.Ocupa - Datos.Libera > 100_000_000
        ? "\n" + T("Ocupa {0}, pero buena parte son los mismos archivos que usan los componentes instalados: esos no se liberan.", Formato.Bytes(Datos.Ocupa))
        : "");
}

/// <summary>Una narración, un doblaje, un acta o una voz del usuario.</summary>
public sealed record FilaEspacioTrabajo(EspacioTrabajo Datos)
{
    public string Nombre => Datos.Nombre;
    public string Tipo => Datos.Tipo switch
    {
        TipoTrabajo.Narracion => T("Narración"),
        TipoTrabajo.Doblaje => T("Doblaje"),
        TipoTrabajo.Acta => T("Acta"),
        _ => T("Voz"),
    };
    public string Fecha => Datos.Fecha.ToLocalTime().ToString("d", Idioma.Cultura);
    public string Ocupa => Formato.Bytes(Datos.Ocupa);
    public bool EsVoz => Datos.Tipo == TipoTrabajo.Voz;
    public bool SePuedeBorrar => !EsVoz;
}

/// <summary>
/// «Espacio»: lo que ocupa Calíope y cómo liberarlo. Lo interno se borra sin preguntar más que una vez (se vuelve a
/// descargar o a crear); lo del usuario va a la papelera de Windows, de donde se puede recuperar.
/// </summary>
public sealed partial class EspacioViewModel(ServicioEspacio espacio, ServicioInstalacion instalacion, INavigationService navegacion)
    : ObservableObject
{
    public ObservableCollection<FilaEspacioComponente> Componentes { get; } = [];
    public ObservableCollection<FilaEspacioLimpiable> Limpiables { get; } = [];
    public ObservableCollection<FilaEspacioTrabajo> Trabajos { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Midiendo { get; set; }

    public bool Libre => !Midiendo;

    [ObservableProperty] public partial string Resumen { get; set; } = "";
    [ObservableProperty] public partial string LimpiezaSegura { get; set; } = "";
    [ObservableProperty] public partial bool HayLimpiezaSegura { get; set; }
    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool SinTrabajos { get; set; }
    [ObservableProperty] public partial string CarpetaDatos { get; set; } = "";

    public async Task CargarAsync() => await MedirAsync();

    [RelayCommand]
    private async Task MedirAsync()
    {
        if (Midiendo)
            return;
        Midiendo = true;
        Error = null;
        Mensaje = T("Midiendo lo que ocupa cada cosa… (unos segundos)");
        try
        {
            var rutas = (await instalacion.InstaladorAsync()).Rutas;
            CarpetaDatos = rutas.Raiz;
            var informe = await espacio.MedirAsync();
            Rellenar(Componentes, informe.Componentes.Select(c => new FilaEspacioComponente(c)));
            Rellenar(Limpiables, informe.Limpiables.Select(l => new FilaEspacioLimpiable(l)));
            Rellenar(Trabajos, informe.Trabajos.Select(t => new FilaEspacioTrabajo(t)));
            SinTrabajos = Trabajos.Count == 0;
            var seguro = informe.Limpiables.Sum(l => l.Libera);
            HayLimpiezaSegura = seguro > 0;
            LimpiezaSegura = T("Se pueden liberar {0} sin perder nada: lo que haga falta se vuelve a descargar o a crear.", Tamano(seguro));
            var libre = LibreEnDisco(rutas.Raiz);
            Resumen = T("Calíope ocupa {0}: {1} de programas y modelos y {2} de lo tuyo.", Formato.Bytes(informe.Interno + informe.DelUsuario),
                          Formato.Bytes(informe.Interno), Tamano(informe.DelUsuario))
                      + (libre is { } l2 ? " " + T("Quedan {0} libres en el disco.", Formato.Bytes(l2)) : "");
            Mensaje = "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = T("No se ha podido medir: {0}", e.Message);
            Mensaje = "";
        }
        finally
        {
            Midiendo = false;
        }
    }

    /// <summary>Como <see cref="Formato.Bytes"/>, pero «nada» en vez de «1 KB» cuando no hay nada.</summary>
    public static string Tamano(long bytes) => bytes <= 0 ? T("nada") : Formato.Bytes(bytes);

    private static void Rellenar<TFila>(ObservableCollection<TFila> coleccion, IEnumerable<TFila> filas)
    {
        coleccion.Clear();
        foreach (var f in filas)
            coleccion.Add(f);
    }

    private static long? LibreEnDisco(string ruta)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ruta))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Con una instalación en marcha no se toca nada: la caché y las descargas son suyas.</summary>
    private bool Ocupado()
    {
        if (!instalacion.Ocupado)
            return false;
        Error = T("Hay una instalación en marcha. Espera a que termine para liberar espacio.");
        return true;
    }

    [RelayCommand]
    private async Task DesinstalarAsync(FilaEspacioComponente fila)
    {
        if (Ocupado() || !await ConfirmarAsync(T("Desinstalar {0}", fila.Nombre),
                T("Se borrará de tu equipo: el programa, sus modelos (salvo los que use otro componente) y la caché de instalación. Se liberarán unos {0}. Podrás volver a instalarlo cuando quieras desde Componentes.", fila.Libera),
                T("Desinstalar")))
            return;
        await EjecutarAsync(T("Desinstalando {0}…", fila.Nombre), async () =>
        {
            if (await instalacion.DesinstalarAsync(fila.Datos.Componente.Id) is { } aviso)
                Error = aviso;
        });
    }

    [RelayCommand]
    private async Task LimpiarAsync(FilaEspacioLimpiable fila)
    {
        if (Ocupado() || !await ConfirmarAsync(fila.Nombre, fila.Descripcion + "\n\n" + T("Se liberarán unos {0}.", fila.Libera), T("Borrar")))
            return;
        await EjecutarAsync(T("Borrando {0}…", fila.Nombre.ToLower(Idioma.Cultura)), async () => Avisar(await espacio.LimpiarAsync(fila.Datos)));
    }

    /// <summary>Todo lo interno de una vez (caché, restos, descargas, temporales y registros).</summary>
    [RelayCommand]
    private async Task LimpiarTodoAsync()
    {
        var lista = string.Join("\n", Limpiables.Where(l => l.HayAlgo).Select(l => $"• {l.Nombre} ({l.Libera})"));
        if (Ocupado() || !await ConfirmarAsync(T("Liberar espacio"), T("Se borrará:\n\n{0}\n\nNo se pierde nada tuyo ni deja de funcionar ningún componente.", lista), T("Borrar")))
            return;
        await EjecutarAsync(T("Liberando espacio…"), async () =>
        {
            var fallos = new List<string>();
            foreach (var l in Limpiables.Where(l => l.HayAlgo).ToList())
                fallos.AddRange(await espacio.LimpiarAsync(l.Datos));
            Avisar(fallos);
        });
    }

    [RelayCommand]
    private async Task APapeleraAsync(FilaEspacioTrabajo fila)
    {
        if (!fila.SePuedeBorrar || !await ConfirmarAsync(T("Mover a la papelera"),
                T("«{0}» ({1}, {2}) irá a la papelera de Windows. Si te arrepientes, lo puedes recuperar desde allí.", fila.Nombre, fila.Tipo.ToLower(Idioma.Cultura), fila.Ocupa),
                T("Mover a la papelera")))
            return;
        await EjecutarAsync(T("Moviendo a la papelera…"), () => Task.Run(() =>
            FileSystem.DeleteDirectory(fila.Datos.Ruta, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)));
    }

    [RelayCommand]
    private static void Abrir(string ruta)
    {
        if (Directory.Exists(ruta))
            Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });
    }

    [RelayCommand]
    private void IrAVoces() => navegacion.Navigate(typeof(PaginaVoces));

    [RelayCommand]
    private void IrAComponentes() => navegacion.Navigate(typeof(PaginaComponentes));

    private async Task EjecutarAsync(string mensaje, Func<Task> accion)
    {
        Error = null;
        Midiendo = true;
        Mensaje = mensaje;
        try
        {
            await accion();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Error = e.Message;
        }
        finally
        {
            Midiendo = false;
        }
        var error = Error;
        await MedirAsync();
        Error ??= error;
    }

    private void Avisar(IReadOnlyList<string> fallos)
    {
        if (fallos.Count > 0)
            Error = T("No se ha podido borrar todo: {0} archivo(s) estaban en uso. Cierra lo que los use (o reinicia Calíope) y vuelve a intentarlo.", fallos.Count);
    }

    private static async Task<bool> ConfirmarAsync(string titulo, string mensaje, string accion)
    {
        var caja = new MessageBox
        {
            Title = titulo,
            Content = mensaje,
            PrimaryButtonText = accion,
            CloseButtonText = T("Cancelar"),
        };
        return await caja.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
