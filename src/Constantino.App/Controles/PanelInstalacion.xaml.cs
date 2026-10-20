using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Constantino.App.Servicios;

namespace Constantino.App.Controles;

/// <summary>Progreso de la instalación en curso: filas por componente, barra, errores y salida técnica.</summary>
public partial class PanelInstalacion : UserControl
{
    public PanelInstalacion()
    {
        InitializeComponent();
        // La salida técnica sigue a la última línea mientras se instala.
        ((INotifyCollectionChanged)ListaLineas.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && ListaLineas.Items.Count > 0)
                ListaLineas.ScrollIntoView(ListaLineas.Items[^1]);
        };
    }

    private ServicioInstalacion? Servicio => DataContext as ServicioInstalacion;

    private void AlCancelar(object sender, RoutedEventArgs e) => Servicio?.Cancelar();

    private async void AlAbrirRegistro(object sender, RoutedEventArgs e)
    {
        if (Servicio is { } s)
            await s.AbrirRegistroAsync();
    }
}
