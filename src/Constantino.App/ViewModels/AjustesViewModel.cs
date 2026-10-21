using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Instalaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Constantino.App.ViewModels;

public sealed record OpcionTema(TemaApp Valor, string Nombre);

/// <summary>Una carpeta de la carpeta de datos en el panel «Almacenamiento».</summary>
public sealed record FilaCarpeta(string Nombre, string Descripcion, string Ruta, string Tamano);

public sealed partial class AjustesViewModel : ObservableObject
{
    private readonly ServicioAjustes _ajustes;
    private readonly ServicioInstalacion _instalacion;
    private readonly ServicioActualizaciones _actualizaciones;
    private readonly AvisosViewModel _avisos;

    public IReadOnlyList<OpcionTema> Temas { get; } =
    [
        new(TemaApp.Sistema, "Como Windows"),
        new(TemaApp.Claro, "Claro"),
        new(TemaApp.Oscuro, "Oscuro"),
    ];

    public AjustesViewModel(ServicioAjustes ajustes, ServicioInstalacion instalacion, ServicioActualizaciones actualizaciones,
        AvisosViewModel avisos)
    {
        _ajustes = ajustes;
        _instalacion = instalacion;
        _actualizaciones = actualizaciones;
        _avisos = avisos;
        BuscarActualizaciones = ajustes.Actual.BuscarActualizaciones;
        Tema = ajustes.Actual.Tema;
    }

    [ObservableProperty] public partial TemaApp Tema { get; set; }

    [ObservableProperty] public partial bool BuscarActualizaciones { get; set; }
    [ObservableProperty] public partial string? EstadoActualizacion { get; set; }
    [ObservableProperty] public partial bool Buscando { get; set; }

    partial void OnBuscarActualizacionesChanged(bool value)
    {
        if (value != _ajustes.Actual.BuscarActualizaciones)
            _ajustes.Guardar(_ajustes.Actual with { BuscarActualizaciones = value });
    }

    [RelayCommand]
    private async Task BuscarAhoraAsync()
    {
        Buscando = true;
        EstadoActualizacion = "Buscando…";
        var v = await _actualizaciones.ComprobarAsync(forzar: true);
        Buscando = false;
        if (v is null)
        {
            EstadoActualizacion = $"Tienes la última versión ({Version}).";
            return;
        }
        EstadoActualizacion = $"Hay una versión nueva: {v.Version.ToString(3)}. Mira el aviso de arriba.";
        _avisos.MostrarVersion(v);
    }

    public string CarpetaDatos => _ajustes.CarpetaDatosEfectiva;

    public ObservableCollection<FilaCarpeta> Carpetas { get; } = [];
    [ObservableProperty] public partial string TotalOcupado { get; set; } = "Calculando…";

    /// <summary>Mide cada carpeta (puede tardar unos segundos con muchos gigas: se hace en segundo plano).</summary>
    public async Task CargarAsync()
    {
        var rutas = (await _instalacion.InstaladorAsync()).Rutas;
        var usos = await Task.Run(() => Almacenamiento.Medir(rutas));
        Carpetas.Clear();
        foreach (var u in usos)
            Carpetas.Add(new FilaCarpeta(u.Nombre, u.Descripcion, u.Ruta, Formato.Bytes(u.Bytes)));
        TotalOcupado = $"En total, unos {Formato.Gb(usos.Sum(u => u.Bytes) / 1e9)}.";
    }

    [RelayCommand]
    private static void Abrir(string ruta)
    {
        Directory.CreateDirectory(ruta);
        Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });
    }

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
