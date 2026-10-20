using System.Windows;
using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaNarrar : Page, INavigableView<NarrarViewModel>
{
    public NarrarViewModel ViewModel { get; }

    public PaginaNarrar(NarrarViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.CargarAsync();
    }

    /// <summary>Inserta la etiqueta del botón donde está el cursor.</summary>
    private void AlInsertar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string etiqueta })
            Insertar(etiqueta);
    }

    private void AlInsertarBloque(object sender, RoutedEventArgs e) => Insertar("\n\n# nuevo bloque\n");

    private void Insertar(string texto)
    {
        var posicion = Editor.SelectionStart;
        Editor.SelectedText = texto;
        Editor.CaretIndex = posicion + texto.Length;
        Editor.Focus();
    }
}
