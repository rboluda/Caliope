using System.Collections.ObjectModel;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>
/// Convertir voz (CosyVoice 3): una grabación (por ejemplo, tú leyendo con la interpretación que quieres) con el timbre de
/// una voz de la biblioteca. Conserva lo que se dice, el ritmo, las pausas, la emoción y el acento. Medido: parecido 0,98
/// con la voz destino, mismo ritmo y el acento de España de la lectura.
/// </summary>
public sealed partial class ConvertirViewModel : ObservableObject
{
    private readonly ServicioMotor _motor;
    private readonly ServicioVoces _voces;
    private readonly ServicioInstalacion _instalacion;
    private readonly INavigationService _navegacion;

    public ConvertirViewModel(ServicioMotor motor, ServicioVoces voces, ServicioReproductor reproductor, ServicioInstalacion instalacion,
        INavigationService navegacion)
    {
        _motor = motor;
        _voces = voces;
        _instalacion = instalacion;
        _navegacion = navegacion;
        Reproductor = reproductor;
    }

    public ServicioReproductor Reproductor { get; }
    public ServicioMotor Motor => _motor;
    public ObservableCollection<OpcionVozMezcla> Voces { get; } = [];

    [ObservableProperty] public partial bool Instalado { get; set; } = true;
    [ObservableProperty] public partial bool SinVoces { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeConvertir))]
    public partial string Origen { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeConvertir))]
    public partial OpcionVozMezcla? Voz { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeConvertir), nameof(Libre))]
    public partial bool Trabajando { get; set; }

    public bool Libre => !Trabajando;
    public bool PuedeConvertir => !Trabajando && Voz is not null && File.Exists(Origen);

    [ObservableProperty] public partial string? Resultado { get; set; }
    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }

    public async Task CargarAsync()
    {
        Instalado = (await _instalacion.InstaladorAsync()).EstaInstalado(ServicioMotor.ComponenteConversion);
        var biblioteca = await _voces.BibliotecaAsync();
        var elegida = Voz?.Voz.Id;
        Voces.Clear();
        foreach (var v in biblioteca.Listar())
            Voces.Add(new OpcionVozMezcla(v, biblioteca.ReferenciaDe(v.Id)));
        Voz = Voces.FirstOrDefault(v => v.Voz.Id == elegida) ?? Voces.FirstOrDefault();
        SinVoces = Voces.Count == 0;
    }

    partial void OnOrigenChanged(string value) => Resultado = null;
    partial void OnVozChanged(OpcionVozMezcla? value) => Resultado = null;

    [RelayCommand]
    private void Examinar()
    {
        var dialogo = new OpenFileDialog
        {
            Title = T("Grabación que quieres convertir"),
            Filter = "Audio (*.wav;*.mp3;*.flac;*.ogg;*.m4a)|*.wav;*.mp3;*.flac;*.ogg;*.m4a|" + T("Todos los archivos") + "|*.*",
        };
        if (dialogo.ShowDialog() == true)
            Origen = dialogo.FileName;
    }

    [RelayCommand]
    private void EscucharOrigen()
    {
        if (File.Exists(Origen))
            Escuchar(Origen);
    }

    [RelayCommand]
    private void EscucharVoz()
    {
        if (Voz is not null)
            Escuchar(Voz.Referencia);
    }

    [RelayCommand]
    private async Task ConvertirAsync()
    {
        if (Voz is null || !File.Exists(Origen))
            return;
        Error = null;
        Trabajando = true;
        Reproductor.Detener();
        Mensaje = T("Convirtiendo la voz…");
        try
        {
            var salida = await _voces.ArchivoTemporalAsync("convertida");
            var r = await _motor.ConvertirVozAsync(Origen, Voz.Referencia, salida);
            Resultado = r.Salida;
            Mensaje = T("Listo: {0:0.#} s de audio convertidos en {1:0.#} s.", r.Duracion, r.Segundos);
            Reproductor.Reproducir(r.Salida);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
            Mensaje = "";
        }
        finally
        {
            Trabajando = false;
        }
    }

    [RelayCommand]
    private void EscucharResultado()
    {
        if (Resultado is not null)
            Escuchar(Resultado);
    }

    [RelayCommand]
    private void GuardarComo()
    {
        if (Resultado is null)
            return;
        var dialogo = new SaveFileDialog
        {
            Title = T("Guardar la voz convertida"),
            FileName = $"{Path.GetFileNameWithoutExtension(Origen)} - {Voz?.Nombre}.wav",
            Filter = "WAV (*.wav)|*.wav",
            DefaultExt = ".wav",
        };
        if (dialogo.ShowDialog() != true)
            return;
        try
        {
            File.Copy(Resultado, dialogo.FileName, overwrite: true);
            Mensaje = T("Guardado en {0}.", dialogo.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = T("No se ha podido guardar: {0}", e.Message);
        }
    }

    private void Escuchar(string ruta)
    {
        if (Reproductor.Sonando == ruta)
            Reproductor.Detener();
        else
            Reproductor.Reproducir(ruta);
    }

    [RelayCommand]
    private void IrAComponentes() => _navegacion.Navigate(typeof(PaginaComponentes));
}
