using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaEspacio : Page, INavigableView<EspacioViewModel>
{
    public EspacioViewModel ViewModel { get; }

    public PaginaEspacio(EspacioViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        // Cada vez que se entra se vuelve a medir: puede haber cambiado (una instalación, un proyecto nuevo…).
        Loaded += async (_, _) => await ViewModel.CargarAsync();
    }
}
