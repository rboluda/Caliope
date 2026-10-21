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

/// <summary>Un programa o modelo en el que se apoya Calíope, para los créditos.</summary>
public sealed record Credito(string Nombre, string Autor, string Licencia, string Url, string Uso);

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

    // --- Acerca de ---
    public string Autor => Marca.Autor;
    public string UrlAutor => Marca.UrlAutor;
    public string UrlRepositorio => Marca.UrlRepositorio;
    public string Copyright => $"© {DateTime.Now.Year} {Marca.Autor} · Licencia {Marca.Licencia}";

    /// <summary>Las herramientas de terceros que Calíope reúne: sin ellas no habría nada que mostrar.</summary>
    public IReadOnlyList<Credito> Creditos { get; } =
    [
        new("Qwen3-TTS", "Alibaba (equipo Qwen)", "Apache 2.0", "https://github.com/QwenLM/Qwen3-TTS",
            "El motor de voz principal: clona voces, las diseña a partir de una descripción y lee los guiones."),
        new("Chatterbox Multilingual", "Resemble AI", "MIT", "https://github.com/resemble-ai/chatterbox",
            "El motor alternativo, con control de la intensidad emocional."),
        new("Perth", "Resemble AI", "MIT", "https://github.com/resemble-ai/perth",
            "La marca de agua inaudible que Chatterbox añade a todo lo que genera."),
        new("Whisper (large-v3-turbo)", "OpenAI", "MIT", "https://github.com/openai/whisper",
            "La transcripción automática y la revisión de que la voz lee bien."),
        new("PyTorch", "PyTorch Foundation", "BSD-3", "https://pytorch.org",
            "Ejecuta los modelos de voz en la tarjeta gráfica o en el procesador."),
        new("Transformers y Hugging Face Hub", "Hugging Face", "Apache 2.0", "https://huggingface.co",
            "Descarga y carga de los modelos."),
        new("FFmpeg", "Proyecto FFmpeg", "GPL", "https://ffmpeg.org",
            "Convierte el audio, iguala el volumen, mezcla el fondo y crea los sonidos incluidos."),
        new("Rubber Band", "Breakfast Quay", "GPL", "https://breakfastquay.com/rubberband/",
            "Cambia la velocidad sin tocar el tono y el tono sin tocar la velocidad."),
        new("uv y Python", "Astral · Python Software Foundation", "MIT / Apache 2.0 · PSF", "https://github.com/astral-sh/uv",
            "Instalan cada motor en su propio entorno, sin tocar nada más del equipo."),
        new(".NET y WPF", "Microsoft", "MIT", "https://dotnet.microsoft.com",
            "La base de la aplicación."),
        new("WPF-UI", "lepo.co", "MIT", "https://github.com/lepoco/wpfui",
            "El aspecto de las ventanas y los controles."),
        new("CommunityToolkit.Mvvm", ".NET Foundation", "MIT", "https://github.com/CommunityToolkit/dotnet",
            "La organización interna de las pantallas."),
        new("NAudio", "Mark Heath", "MIT", "https://github.com/naudio/NAudio",
            "La grabación con el micrófono."),
        new("Inno Setup", "Jordan Russell y Martijn Laan", "Inno Setup License", "https://jrsoftware.org/isinfo.php",
            "El instalador de Calíope."),
    ];

    [RelayCommand]
    private static void AbrirEnlace(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

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
