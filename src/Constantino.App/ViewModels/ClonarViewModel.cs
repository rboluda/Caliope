using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Audio;
using Constantino.Core.Instalaciones;
using Constantino.Core.Voces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

public sealed partial class ClonarViewModel : ObservableObject
{
    private readonly ServicioMotor _motor;
    private readonly ServicioVoces _voces;
    private readonly ServicioInstalacion _instalacion;
    private readonly INavigationService _navegacion;
    private string? _rutaSeleccion;

    public ClonarViewModel(ServicioMotor motor, ServicioVoces voces, ServicioInstalacion instalacion, ServicioGrabacion grabacion,
        ServicioReproductor reproductor, INavigationService navegacion)
    {
        _motor = motor;
        _voces = voces;
        _instalacion = instalacion;
        _navegacion = navegacion;
        Grabacion = grabacion;
        Reproductor = reproductor;
        TextoParaLeer = TextosDeReferencia.Para(Idioma);
    }

    public ServicioGrabacion Grabacion { get; }
    public ServicioReproductor Reproductor { get; }
    public ServicioMotor Motor => _motor;
    public ObservableCollection<string> Dispositivos { get; } = [];
    public ObservableCollection<string> Avisos { get; } = [];
    public IReadOnlyList<OpcionIdioma> IdiomasDisponibles { get; } = Idiomas.Todos.Select(i => new OpcionIdioma(i.Clave, i.Nombre)).ToList();

    [ObservableProperty] public partial int Dispositivo { get; set; }
    [ObservableProperty] public partial bool SinMicrofono { get; set; }
    [ObservableProperty] public partial string TextoParaLeer { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayAudio))]
    public partial Pcm? Audio { get; set; }

    public bool HayAudio => Audio is not null;

    [ObservableProperty] public partial double Inicio { get; set; }
    [ObservableProperty] public partial double Fin { get; set; }
    [ObservableProperty] public partial string Seleccion { get; set; } = "";
    [ObservableProperty] public partial string Origen { get; set; } = "";

    [ObservableProperty] public partial string Transcripcion { get; set; } = "";
    [ObservableProperty] public partial bool SinTranscripcion { get; set; }
    [ObservableProperty] public partial bool WhisperInstalado { get; set; }
    [ObservableProperty] public partial bool Transcribiendo { get; set; }
    [ObservableProperty] public partial string? AvisoTranscripcion { get; set; }

    [ObservableProperty] public partial string Nombre { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsEspanol))]
    public partial string Idioma { get; set; } = "Spanish";

    public bool EsEspanol => Idioma == "Spanish";

    [ObservableProperty] public partial bool Consentimiento { get; set; }
    [ObservableProperty] public partial string TextoPrueba { get; set; } = T("Sábado por la noche. Diluvia. En la puerta de la discoteca hay una chica empapada, temblando de frío.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre), nameof(PuedeQuitarMusica))]
    public partial bool Ocupado { get; set; }

    public bool Libre => !Ocupado;

    [ObservableProperty] public partial string? Mensaje { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Guardada { get; set; }

    public async Task CargarAsync()
    {
        var actual = Dispositivo;
        Dispositivos.Clear();
        foreach (var d in ServicioGrabacion.Dispositivos())
            Dispositivos.Add(d);
        SinMicrofono = Dispositivos.Count == 0;
        Dispositivo = Math.Clamp(actual, 0, Math.Max(0, Dispositivos.Count - 1));
        var instalador = await _instalacion.InstaladorAsync();
        WhisperInstalado = instalador.EstaInstalado(ServicioMotor.ModeloWhisper);
        HaySeparador = instalador.EstaInstalado(ServicioMotor.ComponenteSeparador);
        if (!HaySeparador)
            QuitarMusica = false;
    }

    // --- Limpiar la grabación ----------------------------------------------------------------------

    /// <summary>La grabación tal como llegó, mientras se está usando la limpia (para comparar o deshacer).</summary>
    private Pcm? _original;
    private string? _rutaOriginal;

    [ObservableProperty] public partial bool QuitarMusica { get; set; } = true;
    [ObservableProperty] public partial bool QuitarRuido { get; set; } = true;
    [ObservableProperty] public partial bool RepararDistorsion { get; set; } = true;

    /// <summary>El separador de voz (Demucs) está instalado: sin él no se puede quitar la música.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeQuitarMusica))]
    public partial bool HaySeparador { get; set; }

    public bool PuedeQuitarMusica => HaySeparador && Libre;

    [ObservableProperty] public partial bool Limpiada { get; set; }
    [ObservableProperty] public partial bool Limpiando { get; set; }
    [ObservableProperty] public partial string? Limpieza { get; set; }

    /// <summary>
    /// Limpia la grabación entera (no solo el tramo, para poder moverlo después): repara la distorsión, quita la música
    /// con el separador de voz y el ruido de fondo. Siempre parte del original, así que se puede repetir con otras
    /// opciones.
    /// </summary>
    [RelayCommand]
    private async Task LimpiarAsync()
    {
        if (Audio is null)
            return;
        if (!RepararDistorsion && !(QuitarMusica && HaySeparador) && !QuitarRuido)
        {
            Error = T("Marca al menos una cosa que limpiar.");
            return;
        }
        Error = null;
        Ocupado = Limpiando = true;
        Reproductor.Detener();
        var original = _original ?? Audio;
        try
        {
            var rutas = (await _instalacion.InstaladorAsync()).Rutas;
            if (!File.Exists(rutas.Ffmpeg))
                throw new InvalidOperationException(T("Faltan las herramientas básicas: instálalas desde Componentes."));
            var ejecutor = new EjecutorProcesos();
            async Task Ffmpeg(string[] argumentos)
            {
                var r = await ejecutor.EjecutarAsync(rutas.Ffmpeg, argumentos, new Dictionary<string, string>(), _ => { }, CancellationToken.None);
                if (r.CodigoSalida != 0)
                    throw new InvalidOperationException(T("No se ha podido limpiar la grabación (FFmpeg ha fallado)."));
            }

            var actual = await _voces.ArchivoTemporalAsync("limpieza-original");
            original.EscribirWav(actual);
            var hecho = new List<string>();
            if (RepararDistorsion)
            {
                Limpieza = T("Reparando la distorsión…");
                var reparada = await _voces.ArchivoTemporalAsync("limpieza-reparada");
                await Ffmpeg(LimpiezaGrabacion.Reparar(actual, reparada));
                actual = reparada;
                hecho.Add(T("distorsión reparada"));
            }
            var sinMusica = QuitarMusica && HaySeparador;
            if (sinMusica)
            {
                Limpieza = T("Quitando la música y los ruidos (separador de voz)…");
                var voz = await _voces.ArchivoTemporalAsync("limpieza-voz");
                var fondo = await _voces.ArchivoTemporalAsync("limpieza-fondo");
                await _motor.SepararAsync(actual, voz, fondo);
                actual = voz;
                hecho.Add(T("música quitada"));
            }
            var final = await _voces.ArchivoTemporalAsync("limpieza-final");
            if (QuitarRuido)
            {
                Limpieza = T("Quitando el ruido de fondo…");
                // Tras el separador queda poco ruido: se usa el nivel de una grabación limpia. Si no, el medido.
                await Ffmpeg(LimpiezaGrabacion.QuitarRuido(actual, final, sinMusica ? null : AnalisisGrabacion.Analizar(original).RuidoDb));
                hecho.Add(T("ruido reducido"));
            }
            else
            {
                await Ffmpeg(LimpiezaGrabacion.AMono(actual, final));
            }

            var limpia = LimpiezaGrabacion.Igualar(Pcm.LeerWav(final));
            _original = original;
            _rutaOriginal = null;
            Audio = limpia;
            Limpiada = true;
            ActualizarSeleccion();
            Limpieza = T("Limpia ({0}). Escucha el tramo y compáralo con el original: si suena peor (metálica, apagada), deshaz la limpieza o prueba con menos opciones.",
                string.Join(", ", hecho));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // En el propio recuadro: el mensaje de error general queda al final de la página, fuera de la vista.
            var motivo = e is InvalidOperationException or IOException ? e.Message : ServicioMotor.Explicar(e);
            Limpieza = T("No se ha podido limpiar: {0}", motivo);
        }
        finally
        {
            Ocupado = Limpiando = false;
        }
    }

    /// <summary>El mismo tramo, pero de la grabación original (para comparar con la limpia).</summary>
    [RelayCommand]
    private async Task EscucharOriginalAsync()
    {
        if (_original is null || TodosLosTramos() is not { Count: > 0 } tramos)
            return;
        if (_rutaOriginal is null || !File.Exists(_rutaOriginal))
        {
            _rutaOriginal = await _voces.ArchivoTemporalAsync("clon-tramo-original");
            TramosAudio.Unir(_original, tramos).EscribirWav(_rutaOriginal);
        }
        if (Reproductor.Sonando == _rutaOriginal)
            Reproductor.Detener();
        else
            Reproductor.Reproducir(_rutaOriginal);
    }

    [RelayCommand]
    private void DeshacerLimpieza()
    {
        if (_original is null)
            return;
        Reproductor.Detener();
        Audio = _original;
        _original = null;
        _rutaOriginal = null;
        Limpiada = false;
        Limpieza = null;
        ActualizarSeleccion();
    }

    partial void OnIdiomaChanged(string oldValue, string newValue)
    {
        if (TextoParaLeer == TextosDeReferencia.Para(oldValue))
            TextoParaLeer = TextosDeReferencia.Para(newValue);
    }

    // --- Fuente: micrófono o archivo -----------------------------------------------------------

    [RelayCommand]
    private void EmpezarGrabacion()
    {
        Error = null;
        Reproductor.Detener();
        try
        {
            Grabacion.Empezar(Dispositivo);
        }
        catch (Exception e) when (e is NAudio.MmException or InvalidOperationException)
        {
            Error = T("No se ha podido usar el micrófono: {0}", e.Message);
        }
    }

    [RelayCommand]
    private async Task DetenerGrabacionAsync()
    {
        var audio = await Grabacion.DetenerAsync();
        Origen = T("Grabación del micrófono ({0})", Segundos(audio.Segundos));
        Cargar(audio);
    }

    /// <summary>Si se ha leído el texto propuesto, ya se sabe la transcripción exacta.</summary>
    [RelayCommand]
    private void UsarTextoLeido()
    {
        SinTranscripcion = false;
        Transcripcion = TextoParaLeer;
    }

    [RelayCommand]
    private async Task ImportarArchivoAsync()
    {
        var dialogo = new OpenFileDialog
        {
            Title = T("Grabación de la voz"),
            Filter = "Audio (*.wav;*.mp3;*.flac;*.ogg;*.m4a)|*.wav;*.mp3;*.flac;*.ogg;*.m4a|" + T("Todos los archivos") + "|*.*",
        };
        if (dialogo.ShowDialog() != true)
            return;
        Error = null;
        Ocupado = true;
        try
        {
            // Cualquier formato se convierte a WAV mono para poder dibujarlo y recortarlo.
            var rutas = (await _instalacion.InstaladorAsync()).Rutas;
            if (!File.Exists(rutas.Ffmpeg))
                throw new InvalidOperationException(T("Faltan las herramientas básicas: instálalas desde Componentes."));
            var destino = await _voces.ArchivoTemporalAsync("clon-importado");
            var r = await new EjecutorProcesos().EjecutarAsync(rutas.Ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-i", dialogo.FileName, "-vn", "-ac", "1", "-ar", "24000", "-c:a", "pcm_f32le", destino],
                new Dictionary<string, string>(), _ => { }, CancellationToken.None);
            if (r.CodigoSalida != 0)
                throw new InvalidOperationException(T("No se ha podido leer el audio. Prueba con un WAV o un MP3."));
            var audio = Pcm.LeerWav(destino);
            Origen = $"{Path.GetFileName(dialogo.FileName)} ({Segundos(audio.Segundos)})";
            if (string.IsNullOrWhiteSpace(Nombre))
                Nombre = Path.GetFileNameWithoutExtension(dialogo.FileName);
            Cargar(audio);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException)
        {
            Error = e.Message;
        }
        finally
        {
            Ocupado = false;
        }
    }

    private void Cargar(Pcm audio)
    {
        Guardada = null;
        Mensaje = null;
        _original = null;
        _rutaOriginal = null;
        Limpiada = false;
        Limpieza = null;
        Tramos = [];
        Audio = audio;
        var informe = AnalisisGrabacion.Analizar(audio);
        // Selección inicial: la voz sin los silencios de los extremos, como mucho 25 s.
        Inicio = informe.HayVoz ? informe.InicioVoz : 0;
        Fin = informe.HayVoz ? Math.Min(informe.FinVoz, Inicio + AnalisisGrabacion.IdealMaxima) : audio.Segundos;
        ActualizarSeleccion();
    }

    partial void OnInicioChanged(double value) => ActualizarSeleccion();
    partial void OnFinChanged(double value) => ActualizarSeleccion();
    partial void OnTranscripcionChanged(string value) => ComprobarTranscripcion();
    partial void OnSinTranscripcionChanged(bool value) => ComprobarTranscripcion();

    private void ActualizarSeleccion()
    {
        _rutaSeleccion = null;
        _rutaOriginal = null;
        if (Audio is null)
            return;
        var tramos = TodosLosTramos();
        DuracionSeleccion = TramosAudio.Duracion(tramos);
        Seleccion = tramos.Count > 1
            ? T("{0} tramos seleccionados: {1} en total (unidos con una pausa corta entre ellos)", tramos.Count, Segundos(DuracionSeleccion))
            : T("Tramo seleccionado: {0} – {1} ({2})", Segundos(Inicio), Segundos(Fin), Segundos(Math.Max(0, Fin - Inicio)));
        Avisos.Clear();
        if (tramos.Count > 0)
        {
            foreach (var a in AnalisisGrabacion.Analizar(TramosAudio.Unir(Audio, tramos)).Avisos)
                Avisos.Add(a);
        }
        ComprobarTranscripcion();
    }

    // --- Varios tramos -----------------------------------------------------------------------------

    /// <summary>Tramos ya añadidos (además del que se está marcando con Inicio y Fin).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayTramos))]
    public partial IReadOnlyList<Tramo> Tramos { get; set; } = [];

    public bool HayTramos => Tramos.Count > 0;

    /// <summary>Lo que dura la muestra que se usará: todos los tramos unidos, con sus pausas.</summary>
    [ObservableProperty] public partial double DuracionSeleccion { get; set; }

    /// <summary>Los añadidos y el que se está marcando, ordenados y sin solapes.</summary>
    private IReadOnlyList<Tramo> TodosLosTramos() => TramosAudio.Normalizar(Tramos.Append(new Tramo(Inicio, Fin)));

    /// <summary>El tramo marcado pasa a la lista y se puede marcar otro (también con Ctrl + arrastrar).</summary>
    [RelayCommand]
    private void AnadirTramo()
    {
        if (Fin - Inicio < TramosAudio.Minimo)
        {
            Error = T("Marca primero un tramo arrastrando sobre la onda.");
            return;
        }
        Error = null;
        Tramos = TramosAudio.Normalizar(Tramos.Append(new Tramo(Inicio, Fin)));
        Inicio = Fin = 0;
        ActualizarSeleccion();
    }

    [RelayCommand]
    private void QuitarTramo(Tramo tramo)
    {
        Tramos = [.. Tramos.Where(t => t != tramo)];
        ActualizarSeleccion();
    }

    private void ComprobarTranscripcion()
    {
        AvisoTranscripcion = null;
        if (SinTranscripcion || string.IsNullOrWhiteSpace(Transcripcion) || DuracionSeleccion < 1)
            return;
        try
        {
            BibliotecaVoces.ComprobarTranscripcion(Transcripcion, DuracionSeleccion);
        }
        catch (VozInvalidaException e)
        {
            AvisoTranscripcion = e.Message;
        }
    }

    [RelayCommand]
    private async Task EscucharSeleccionAsync()
    {
        if (await SeleccionAsync() is { } ruta)
        {
            if (Reproductor.Sonando == ruta)
                Reproductor.Detener();
            else
                Reproductor.Reproducir(ruta);
        }
    }

    [RelayCommand]
    private void SeleccionarVoz()
    {
        if (Audio is null)
            return;
        var informe = AnalisisGrabacion.Analizar(Audio);
        Tramos = [];
        Inicio = informe.InicioVoz;
        Fin = Math.Min(informe.FinVoz, Inicio + AnalisisGrabacion.IdealMaxima);
    }

    /// <summary>La muestra que se usará (el tramo o los tramos unidos), en un WAV temporal que se reutiliza mientras no cambie.</summary>
    private async Task<string?> SeleccionAsync()
    {
        var tramos = TodosLosTramos();
        if (Audio is null || TramosAudio.Duracion(tramos) < 0.5)
        {
            Error = T("Selecciona un tramo de la grabación arrastrando sobre la onda.");
            return null;
        }
        if (_rutaSeleccion is null || !File.Exists(_rutaSeleccion))
        {
            _rutaSeleccion = await _voces.ArchivoTemporalAsync("clon-tramo");
            TramosAudio.Unir(Audio, tramos).EscribirWav(_rutaSeleccion);
        }
        return _rutaSeleccion;
    }

    // --- Transcribir, probar y guardar -----------------------------------------------------------

    [RelayCommand]
    private async Task TranscribirAsync()
    {
        if (await SeleccionAsync() is not { } ruta)
            return;
        Error = null;
        Transcribiendo = Ocupado = true;
        try
        {
            var r = await _motor.TranscribirAsync(ruta, Idioma);
            SinTranscripcion = false;
            Transcripcion = r.Texto;
            Mensaje = T("Revisa la transcripción: tiene que ser exactamente lo que se dice.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
            // También en su apartado: el mensaje de error general queda al final de la página.
            AvisoTranscripcion = T("No se ha podido transcribir: {0}", Error);
        }
        finally
        {
            Transcribiendo = Ocupado = false;
        }
    }

    [RelayCommand]
    private async Task ProbarAsync()
    {
        if (!Consentimiento)
        {
            Error = T("Confirma primero que la voz es tuya o que tienes permiso para usarla.");
            return;
        }
        if (AvisoTranscripcion is not null)
        {
            Error = AvisoTranscripcion;
            return;
        }
        if (await SeleccionAsync() is not { } ruta)
            return;
        Error = null;
        Ocupado = true;
        try
        {
            var voz = new Voz("prueba-clon", Nombre, Idioma, OrigenVoz.Clonada, SinTranscripcion ? "" : Transcripcion.Trim(),
                DuracionSeleccion, DateTimeOffset.Now, Consentimiento);
            var salida = await _voces.ArchivoTemporalAsync("clon-prueba");
            var r = await _motor.SintetizarAsync(voz, ruta, TextoPrueba.Trim(), salida);
            Mensaje = T("Así sonaría: {0} generados en {1}.", Segundos(r.Duracion), Segundos(r.Segundos));
            Reproductor.Reproducir(r.Salida);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Ocupado = false;
        }
    }

    [RelayCommand]
    private async Task GuardarAsync()
    {
        if (await SeleccionAsync() is not { } ruta)
            return;
        Error = null;
        Ocupado = true;
        try
        {
            var biblioteca = await _voces.BibliotecaAsync();
            var r = await biblioteca.ImportarAsync(new SolicitudImportacion(
                ruta, Nombre, Idioma, Transcripcion, Consentimiento, OrigenVoz.Clonada, SinTranscripcion: SinTranscripcion));
            if (_voces.Predeterminada is null)
                _voces.MarcarPredeterminada(r.Voz.Id);
            else
                _voces.Notificar();
            Guardada = T("«{0}» está en tu biblioteca.", r.Voz.Nombre) + (r.Aviso is null ? "" : " " + r.Aviso);
        }
        catch (VozInvalidaException e)
        {
            Error = e.Message;
        }
        finally
        {
            Ocupado = false;
        }
    }

    [RelayCommand]
    private void Empezar()
    {
        Reproductor.Detener();
        Audio = null;
        _original = null;
        _rutaOriginal = null;
        Limpiada = false;
        Limpieza = null;
        Tramos = [];
        Transcripcion = Nombre = "";
        SinTranscripcion = Consentimiento = false;
        Guardada = Mensaje = Error = null;
        Avisos.Clear();
    }

    [RelayCommand]
    private void IrAVoces() => _navegacion.Navigate(typeof(PaginaVoces));

    [RelayCommand]
    private void IrAComponentes() => _navegacion.Navigate(typeof(PaginaComponentes));

    private static string Segundos(double s) => $"{s.ToString("0.0", global::Constantino.Core.Idioma.Cultura)} s";
}
