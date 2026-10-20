using System.Windows.Controls;
using Constantino.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Constantino.App.Paginas;

public partial class PaginaAsistente : Page, INavigableView<AsistenteViewModel>
{
    public AsistenteViewModel ViewModel { get; }

    private bool _iniciado;

    public PaginaAsistente(AsistenteViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_iniciado)
                return;
            _iniciado = true;
            await ViewModel.IniciarAsync();
        };
    }
}
