using System.Diagnostics;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Instalaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>
/// El aviso de la parte de arriba de la ventana: una versión nueva de Calíope o mejoras para componentes ya
/// instalados. Todo se resuelve con un botón.
/// </summary>
public sealed partial class AvisosViewModel(
    ServicioActualizaciones actualizaciones, ServicioInstalacion instalacion, INavigationService navegacion) : ObservableObject
{
    private VersionPublicada? _version;
    private IReadOnlyList<string> _actualizables = [];

    [ObservableProperty] public partial bool Visible { get; set; }
    [ObservableProperty] public partial string Titulo { get; set; } = "";
    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string TextoBoton { get; set; } = "";
    [ObservableProperty] public partial bool HayNovedades { get; set; }
    [ObservableProperty] public partial bool Ocupado { get; set; }
    [ObservableProperty] public partial double Progreso { get; set; }

    /// <summary>Al arrancar: primero la aplicación (si toca comprobar hoy); si está al día, los componentes.</summary>
    public async Task ComprobarAsync(bool forzar = false)
    {
        if (await actualizaciones.ComprobarAsync(forzar) is { } v)
        {
            MostrarVersion(v);
            return;
        }
        await ComprobarComponentesAsync();
    }

    public void MostrarVersion(VersionPublicada v)
    {
        _version = v;
        _actualizables = [];
        Titulo = T("Hay una versión nueva de Calíope: {0}", v.Version.ToString(3));
        Mensaje = ServicioActualizaciones.PuedeActualizarseSola
            ? T("Se descarga e instala sola en un momento; Calíope se volverá a abrir al terminar.")
            : T("Descárgala desde la página de la versión.");
        TextoBoton = ServicioActualizaciones.PuedeActualizarseSola ? T("Actualizar ahora") : T("Ver la versión nueva");
        HayNovedades = !string.IsNullOrWhiteSpace(v.Pagina);
        Visible = true;
    }

    /// <summary>Componentes instalados con una versión más nueva en el catálogo (llega con cada versión de Calíope).</summary>
    private async Task ComprobarComponentesAsync()
    {
        var instalador = await instalacion.InstaladorAsync();
        var ids = instalacion.Catalogo.Componentes
            .Where(c => instalador.EstadoDe(c.Id) == EstadoComponente.Actualizable)
            .ToList();
        if (ids.Count == 0)
            return;
        _version = null;
        _actualizables = ids.Select(c => c.Id).ToList();
        Titulo = ids.Count == 1 ? T("Hay una mejora para un componente") : T("Hay mejoras para {0} componentes", ids.Count);
        Mensaje = string.Join(", ", ids.Select(c => c.Nombre)) + ".";
        TextoBoton = T("Actualizar");
        HayNovedades = false;
        Visible = true;
    }

    [RelayCommand]
    private async Task AccionAsync()
    {
        if (_version is { } v)
        {
            if (!ServicioActualizaciones.PuedeActualizarseSola || v.Instalador is null)
            {
                Abrir(v.Pagina);
                return;
            }
            Ocupado = true;
            Mensaje = T("Descargando la versión nueva…");
            try
            {
                var archivo = await actualizaciones.DescargarAsync(v, new Progress<double>(p =>
                {
                    Progreso = p;
                    Mensaje = T("Descargando la versión nueva… {0:P0}", p);
                }));
                Mensaje = T("Instalando… Calíope se cerrará y volverá a abrirse.");
                ServicioActualizaciones.InstalarYSalir(archivo);
            }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.IO.IOException or TaskCanceledException)
            {
                Mensaje = T("No se ha podido descargar. Comprueba la conexión y vuelve a intentarlo.");
                Ocupado = false;
            }
            return;
        }
        if (_actualizables.Count > 0)
        {
            Visible = false;
            navegacion.Navigate(typeof(PaginaComponentes));
            await instalacion.InstalarAsync(_actualizables);
        }
    }

    [RelayCommand]
    private void VerNovedades()
    {
        if (_version is { } v)
            Abrir(v.Pagina);
    }

    [RelayCommand]
    private void Cerrar() => Visible = false;

    private static void Abrir(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
