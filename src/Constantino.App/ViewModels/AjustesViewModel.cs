using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Instalaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static Constantino.Core.Idioma;

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
        new(TemaApp.Sistema, T("Como Windows")),
        new(TemaApp.Claro, T("Claro")),
        new(TemaApp.Oscuro, T("Oscuro")),
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

    // --- Idioma de la interfaz ---
    public IReadOnlyList<OpcionIdioma> IdiomasInterfaz { get; } =
        Idioma.Disponibles.Select(d => new OpcionIdioma(d.Clave, d.Nombre)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayQueReiniciar), nameof(AvisoReinicio), nameof(TextoReiniciar))]
    public partial string IdiomaInterfaz { get; set; } = Idioma.Actual;

    /// <summary>El idioma elegido no es el que está en uso: se aplica al reiniciar.</summary>
    public bool HayQueReiniciar => IdiomaInterfaz != Idioma.Actual;

    // En el idioma elegido, no en el actual: quien lo elige es quien lo entiende.
    public string AvisoReinicio => EnIdioma(IdiomaInterfaz, "Calíope se mostrará en el idioma elegido al reiniciar.");

    public string TextoReiniciar => EnIdioma(IdiomaInterfaz, "Reiniciar ahora");

    private static string EnIdioma(string idioma, string texto) =>
        idioma == Idioma.Espanol ? texto : Idioma.Tabla(idioma).GetValueOrDefault(texto, texto);

    partial void OnIdiomaInterfazChanged(string value)
    {
        if (value != _ajustes.Actual.Idioma)
            _ajustes.Guardar(_ajustes.Actual with { Idioma = value });
    }

    /// <summary>Cierra Calíope y lo vuelve a abrir (el motor de voz se apaga al cerrar).</summary>
    [RelayCommand]
    private void Reiniciar()
    {
        // Con una instalación a medias se esperaría a que termine: cortarla dejaría el componente dañado.
        if (_instalacion.Ocupado)
            return;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

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
        EstadoActualizacion = T("Buscando…");
        var v = await _actualizaciones.ComprobarAsync(forzar: true);
        Buscando = false;
        if (v is null)
        {
            EstadoActualizacion = T("Tienes la última versión ({0}).", Version);
            return;
        }
        EstadoActualizacion = T("Hay una versión nueva: {0}. Mira el aviso de arriba.", v.Version.ToString(3));
        _avisos.MostrarVersion(v);
    }

    public string CarpetaDatos => _ajustes.CarpetaDatosEfectiva;

    // --- Acerca de ---
    public string Autor => Marca.Autor;
    public string UrlAutor => Marca.UrlAutor;
    public string UrlRepositorio => Marca.UrlRepositorio;
    public string Copyright => T("© {0} {1} · Licencia {2}", DateTime.Now.Year, Marca.Autor, Marca.Licencia);

    /// <summary>Las herramientas de terceros que Calíope reúne: sin ellas no habría nada que mostrar.</summary>
    public IReadOnlyList<Credito> Creditos { get; } =
    [
        new("Qwen3-TTS", T("Alibaba (equipo Qwen)"), "Apache 2.0", "https://github.com/QwenLM/Qwen3-TTS",
            T("El motor de voz principal: clona voces, las diseña a partir de una descripción y lee los guiones.")),
        new(T("Chatterbox Multilingual (castellano de España)"), "Resemble AI", "MIT", "https://github.com/resemble-ai/chatterbox",
            T("El motor castellano rápido, con control de la intensidad emocional.")),
        new("VoxCPM2", "OpenBMB", "Apache 2.0", "https://github.com/OpenBMB/VoxCPM",
            T("El motor de máximo parecido: imita la voz con más fidelidad y genera audio a 48 kHz.")),
        new("Demucs", "Meta (Alexandre Défossez)", "MIT", "https://github.com/adefossez/demucs",
            T("Separa la voz de la música y los efectos al doblar un vídeo.")),
        new("llama.cpp", "ggml-org", "MIT", "https://github.com/ggml-org/llama.cpp",
            T("Ejecuta en tu equipo el modelo de lenguaje que traduce los vídeos.")),
        new("Qwen3-4B-Instruct", "Alibaba (equipo Qwen)", "Apache 2.0", "https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507",
            T("El modelo de lenguaje que traduce el guion de los vídeos al castellano.")),
        new("Perth", "Resemble AI", "MIT", "https://github.com/resemble-ai/perth",
            T("La marca de agua inaudible que Chatterbox añade a todo lo que genera.")),
        new("Whisper (large-v3-turbo)", "OpenAI", "MIT", "https://github.com/openai/whisper",
            T("La transcripción automática y la revisión de que la voz lee bien.")),
        new("PyTorch", "PyTorch Foundation", "BSD-3", "https://pytorch.org",
            T("Ejecuta los modelos de voz en la tarjeta gráfica o en el procesador.")),
        new(T("Transformers y Hugging Face Hub"), "Hugging Face", "Apache 2.0", "https://huggingface.co",
            T("Descarga y carga de los modelos.")),
        new("FFmpeg", T("Proyecto FFmpeg"), "GPL", "https://ffmpeg.org",
            T("Convierte el audio, iguala el volumen, mezcla el fondo y crea los sonidos incluidos.")),
        new("Rubber Band", "Breakfast Quay", "GPL", "https://breakfastquay.com/rubberband/",
            T("Cambia la velocidad sin tocar el tono y el tono sin tocar la velocidad.")),
        new(T("uv y Python"), "Astral · Python Software Foundation", "MIT / Apache 2.0 · PSF", "https://github.com/astral-sh/uv",
            T("Instalan cada motor en su propio entorno, sin tocar nada más del equipo.")),
        new(T(".NET y WPF"), "Microsoft", "MIT", "https://dotnet.microsoft.com",
            T("La base de la aplicación.")),
        new("WPF-UI", "lepo.co", "MIT", "https://github.com/lepoco/wpfui",
            T("El aspecto de las ventanas y los controles.")),
        new("CommunityToolkit.Mvvm", ".NET Foundation", "MIT", "https://github.com/CommunityToolkit/dotnet",
            T("La organización interna de las pantallas.")),
        new("NAudio", "Mark Heath", "MIT", "https://github.com/naudio/NAudio",
            T("La grabación con el micrófono.")),
        new("Inno Setup", T("Jordan Russell y Martijn Laan"), "Inno Setup License", "https://jrsoftware.org/isinfo.php",
            T("El instalador de Calíope.")),
    ];

    [RelayCommand]
    private static void AbrirEnlace(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public ObservableCollection<FilaCarpeta> Carpetas { get; } = [];
    [ObservableProperty] public partial string TotalOcupado { get; set; } = T("Calculando…");

    /// <summary>Mide cada carpeta (puede tardar unos segundos con muchos gigas: se hace en segundo plano).</summary>
    public async Task CargarAsync()
    {
        var rutas = (await _instalacion.InstaladorAsync()).Rutas;
        var usos = await Task.Run(() => Almacenamiento.Medir(rutas));
        Carpetas.Clear();
        foreach (var u in usos)
            Carpetas.Add(new FilaCarpeta(u.Nombre, u.Descripcion, u.Ruta, Formato.Bytes(u.Bytes)));
        TotalOcupado = T("En total, unos {0}.", Formato.Gb(usos.Sum(u => u.Bytes) / 1e9));
    }

    [RelayCommand]
    private static void Abrir(string ruta)
    {
        Directory.CreateDirectory(ruta);
        Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });
    }

    public string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? T("desconocida");

    partial void OnTemaChanged(TemaApp value)
    {
        if (value == _ajustes.Actual.Tema)
            return;
        _ajustes.Guardar(_ajustes.Actual with { Tema = value });
        _ajustes.AplicarTema();
    }
}
