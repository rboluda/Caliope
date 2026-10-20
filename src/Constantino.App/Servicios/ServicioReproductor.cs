using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Constantino.App.Servicios;

/// <summary>Reproduce un audio cada vez; al empezar otro, el anterior se detiene.</summary>
public sealed partial class ServicioReproductor : ObservableObject
{
    private readonly MediaPlayer _reproductor = new();

    public ServicioReproductor()
    {
        _reproductor.MediaEnded += (_, _) => Detener();
        _reproductor.MediaFailed += (_, _) => Detener();
    }

    /// <summary>La ruta que suena ahora, o null.</summary>
    [ObservableProperty] public partial string? Sonando { get; set; }

    public void Reproducir(string ruta)
    {
        _reproductor.Stop();
        _reproductor.Open(new Uri(ruta));
        _reproductor.Play();
        Sonando = ruta;
    }

    public void Detener()
    {
        _reproductor.Stop();
        _reproductor.Close();
        Sonando = null;
    }
}
