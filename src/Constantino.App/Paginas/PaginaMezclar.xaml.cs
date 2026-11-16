using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaMezclar : Page, INavigableView<MezclarViewModel>
{
    public MezclarViewModel ViewModel { get; }

    public PaginaMezclar(MezclarViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.CargarAsync();
    }
}
