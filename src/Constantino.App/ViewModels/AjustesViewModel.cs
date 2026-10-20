using System.Reflection;
using Constantino.App.Servicios;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Constantino.App.ViewModels;

public sealed record OpcionTema(TemaApp Valor, string Nombre);

public sealed partial class AjustesViewModel : ObservableObject
{
    private readonly ServicioAjustes _ajustes;

    public IReadOnlyList<OpcionTema> Temas { get; } =
    [
        new(TemaApp.Sistema, "Como Windows"),
        new(TemaApp.Claro, "Claro"),
        new(TemaApp.Oscuro, "Oscuro"),
    ];

    public AjustesViewModel(ServicioAjustes ajustes)
    {
        _ajustes = ajustes;
        Tema = ajustes.Actual.Tema;
    }

    [ObservableProperty] public partial TemaApp Tema { get; set; }

    public string CarpetaDatos => _ajustes.CarpetaDatosEfectiva;

    public string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "desconocida";

    partial void OnTemaChanged(TemaApp value)
    {
        if (value == _ajustes.Actual.Tema)
            return;
        _ajustes.Guardar(_ajustes.Actual with { Tema = value });
        _ajustes.AplicarTema();
    }
}
