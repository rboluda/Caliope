using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaAjustes : Page, INavigableView<AjustesViewModel>
{
    public AjustesViewModel ViewModel { get; }

    public PaginaAjustes(AjustesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
