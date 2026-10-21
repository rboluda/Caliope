using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Motores;
using Constantino.Core.Voces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Constantino.App.ViewModels;

public sealed record OpcionIdioma(string Clave, string Nombre);

/// <summary>Una variante de la voz en su tarjeta.</summary>
public sealed record FilaVariante(Variante Variante, string Referencia)
{
    public string Nombre => Variante.Nombre;
    public string Detalle => $"{Variante.Etiqueta} · {Variante.DuracionSegundos.ToString("0.#", CultureInfo.GetCultureInfo("es-ES"))} s";
}

/// <summary>Una voz en la lista, con su zona de prueba y su edición de nombre.</summary>
public sealed partial class TarjetaVoz : ObservableObject
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public TarjetaVoz(Voz voz, string referencia, bool predeterminada)
    {
        Voz = voz;
        Referencia = referencia;
        EsPredeterminada = predeterminada;
        TextoPrueba = TextoDeEjemplo(voz.Idioma);
        NombreEditado = voz.Nombre;
        TranscripcionEditada = voz.Transcripcion;
        Variantes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TieneVariantes));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Nombre), nameof(Detalle), nameof(Transcripcion), nameof(AvisoTranscripcion))]
    public partial Voz Voz { get; set; }

    public string Referencia { get; }
    public string Nombre => Voz.Nombre;

    public string Transcripcion => Voz.SoloTimbre
        ? "Sin transcripción: se imita solo el timbre de la voz."
        : $"«{Voz.Transcripcion}»";

    /// <summary>Si la transcripción no encaja con la grabación (y por eso la voz saldrá cortada), la explicación.</summary>
    public string? AvisoTranscripcion
    {
        get
        {
            if (Voz.SoloTimbre)
                return null;
            try
            {
                BibliotecaVoces.ComprobarTranscripcion(Voz.Transcripcion, Voz.DuracionSegundos);
                return null;
            }
            catch (VozInvalidaException e)
            {
                return VocesViewModel.ParaEdicion(e.Message);
            }
        }
    }

    // --- Variantes ---
    public ObservableCollection<FilaVariante> Variantes { get; } = [];
    public bool TieneVariantes => Variantes.Count > 0;
    public IReadOnlyList<TipoVariante> TiposVariante => CatalogoVariantes.Tipos;

    /// <summary>Las voces diseñadas pueden generar variantes con su misma descripción y semilla.</summary>
    public bool PuedeGenerarVariante => Voz.Origen == OrigenVoz.Disenada && !string.IsNullOrWhiteSpace(Voz.Descripcion);

    [ObservableProperty] public partial bool MostrarAnadirVariante { get; set; }
    [ObservableProperty] public partial TipoVariante? TipoVariante { get; set; } = CatalogoVariantes.Tipos[0];
    [ObservableProperty] public partial string ArchivoVariante { get; set; } = "";
    [ObservableProperty] public partial string TranscripcionVariante { get; set; } = "";
    [ObservableProperty] public partial bool SinTranscripcionVariante { get; set; }
    [ObservableProperty] public partial bool TrabajandoVariante { get; set; }
    [ObservableProperty] public partial string? ErrorVariante { get; set; }

    [ObservableProperty] public partial bool EditandoTranscripcion { get; set; }
    [ObservableProperty] public partial string TranscripcionEditada { get; set; }
    [ObservableProperty] public partial string? ErrorTranscripcion { get; set; }

    public string Detalle =>
        $"{Origen(Voz.Origen)} · {Idiomas.Nombre(Voz.Idioma)} · grabación de {Voz.DuracionSegundos.ToString("0.#", Es)} s · añadida el {Voz.Creada.ToString("d", Es)}";

    [ObservableProperty] public partial bool EsPredeterminada { get; set; }
    [ObservableProperty] public partial bool MostrarPrueba { get; set; }
    [ObservableProperty] public partial string TextoPrueba { get; set; }
    [ObservableProperty] public partial bool Generando { get; set; }
    [ObservableProperty] public partial string? ResultadoPrueba { get; set; }
    [ObservableProperty] public partial string? ErrorPrueba { get; set; }
    [ObservableProperty] public partial string? UltimaPrueba { get; set; }
    [ObservableProperty] public partial bool Editando { get; set; }
    [ObservableProperty] public partial string NombreEditado { get; set; }

    private static string Origen(OrigenVoz o) => o switch
    {
        OrigenVoz.Disenada => "Diseñada",
        OrigenVoz.Clonada => "Clonada",
        _ => "Importada",
    };

    private static string TextoDeEjemplo(string idioma) => idioma switch
    {
        "Spanish" => "Sábado por la noche. Diluvia. En la puerta de la discoteca hay una chica empapada, temblando de frío.",
        _ => "It was a dark and stormy night. Somewhere in the woods, an old door creaked open.",
    };
}

public sealed partial class VocesViewModel : ObservableObject
{
    private readonly ServicioVoces _voces;
    private readonly INavigationService _navegacion;

    public VocesViewModel(ServicioVoces voces, ServicioMotor motor, ServicioReproductor reproductor, INavigationService navegacion)
    {
        _voces = voces;
        _navegacion = navegacion;
        Motor = motor;
        Reproductor = reproductor;
        voces.Cambiada += async (_, _) => await CargarAsync();
    }

    public ServicioMotor Motor { get; }
    public ServicioReproductor Reproductor { get; }
    public ObservableCollection<TarjetaVoz> Voces { get; } = [];
    public IReadOnlyList<OpcionIdioma> IdiomasDisponibles { get; } = Idiomas.Todos.Select(i => new OpcionIdioma(i.Clave, i.Nombre)).ToList();

    [ObservableProperty] public partial bool SinVoces { get; set; }

    /// <summary>Motores instalados, para escuchar la misma voz con cada uno.</summary>
    public ObservableCollection<OpcionMotor> Motores { get; } = [new(MotoresDeVoz.Qwen, "Qwen3-TTS")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayVariosMotores))]
    public partial string MotorPrueba { get; set; } = MotoresDeVoz.Qwen;

    public bool HayVariosMotores => Motores.Count > 1;
    [ObservableProperty] public partial string? ErrorGeneral { get; set; }

    // --- Formulario de importación ---
    [ObservableProperty] public partial bool MostrarImportar { get; set; }
    [ObservableProperty] public partial string ArchivoImportar { get; set; } = "";
    [ObservableProperty] public partial string NombreImportar { get; set; } = "";
    [ObservableProperty] public partial string IdiomaImportar { get; set; } = "Spanish";
    [ObservableProperty] public partial string TranscripcionImportar { get; set; } = "";
    [ObservableProperty] public partial bool ConsentimientoImportar { get; set; }
    [ObservableProperty] public partial bool SinTranscripcionImportar { get; set; }
    [ObservableProperty] public partial bool Importando { get; set; }
    [ObservableProperty] public partial string? ErrorImportar { get; set; }
    [ObservableProperty] public partial string? AvisoImportar { get; set; }

    public async Task CargarAsync()
    {
        if (await Motor.ChatterboxInstaladoAsync() && Motores.All(m => m.Id != MotoresDeVoz.Chatterbox))
        {
            Motores.Add(new OpcionMotor(MotoresDeVoz.Chatterbox, "Chatterbox"));
            OnPropertyChanged(nameof(HayVariosMotores));
        }
        var biblioteca = await _voces.BibliotecaAsync();
        var predeterminada = _voces.Predeterminada;
        Voces.Clear();
        foreach (var v in biblioteca.Listar())
        {
            var tarjeta = new TarjetaVoz(v, biblioteca.ReferenciaDe(v.Id), v.Id == predeterminada);
            CargarVariantes(biblioteca, tarjeta);
            Voces.Add(tarjeta);
        }
        SinVoces = Voces.Count == 0;
    }

    private static void CargarVariantes(BibliotecaVoces biblioteca, TarjetaVoz t)
    {
        t.Variantes.Clear();
        foreach (var v in biblioteca.Variantes(t.Voz.Id))
            t.Variantes.Add(new FilaVariante(v, biblioteca.ReferenciaDeVariante(t.Voz.Id, v.Id)));
    }

    // --- Variantes -----------------------------------------------------------------------------

    [RelayCommand]
    private void AlternarVariante(TarjetaVoz t)
    {
        t.ErrorVariante = null;
        t.MostrarAnadirVariante = !t.MostrarAnadirVariante;
    }

    [RelayCommand]
    private void ExaminarVariante(TarjetaVoz t)
    {
        var dialogo = new OpenFileDialog
        {
            Title = "Grabación de la variante",
            Filter = "Audio (*.wav;*.mp3;*.flac;*.ogg;*.m4a)|*.wav;*.mp3;*.flac;*.ogg;*.m4a|Todos los archivos|*.*",
        };
        if (dialogo.ShowDialog() == true)
            t.ArchivoVariante = dialogo.FileName;
    }

    /// <summary>Para una voz diseñada: la misma descripción y semilla, con el estilo de la variante añadido.</summary>
    [RelayCommand]
    private async Task GenerarVarianteAsync(TarjetaVoz t)
    {
        if (t.TipoVariante is not { } tipo || t.Voz.Descripcion is not { } descripcion)
            return;
        t.ErrorVariante = null;
        t.TrabajandoVariante = true;
        try
        {
            var instruccion = CatalogoVariantes.InstruccionPara(descripcion, tipo);
            var texto = TextosDeReferencia.Para(t.Voz.Idioma);
            var salida = await _voces.ArchivoTemporalAsync($"variante-{tipo.Id}");
            var r = await Motor.DisenarAsync(instruccion, t.Voz.Idioma, texto, salida, t.Voz.Semilla ?? 1234);
            var biblioteca = await _voces.BibliotecaAsync();
            await biblioteca.AnadirVarianteAsync(t.Voz.Id, tipo.Id, tipo.Nombre, r.Salida, texto, sinTranscripcion: false, instruccion);
            CargarVariantes(biblioteca, t);
            t.MostrarAnadirVariante = false;
            Reproductor.Reproducir(biblioteca.ReferenciaDeVariante(t.Voz.Id, tipo.Id));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            t.ErrorVariante = e is VozInvalidaException ? e.Message : ServicioMotor.Explicar(e);
        }
        finally
        {
            t.TrabajandoVariante = false;
        }
    }

    [RelayCommand]
    private async Task ImportarVarianteAsync(TarjetaVoz t)
    {
        if (t.TipoVariante is not { } tipo)
            return;
        t.ErrorVariante = null;
        t.TrabajandoVariante = true;
        try
        {
            var biblioteca = await _voces.BibliotecaAsync();
            await biblioteca.AnadirVarianteAsync(t.Voz.Id, tipo.Id, tipo.Nombre, t.ArchivoVariante, t.TranscripcionVariante,
                t.SinTranscripcionVariante);
            CargarVariantes(biblioteca, t);
            t.MostrarAnadirVariante = false;
            t.ArchivoVariante = t.TranscripcionVariante = "";
        }
        catch (VozInvalidaException e)
        {
            t.ErrorVariante = e.Message;
        }
        finally
        {
            t.TrabajandoVariante = false;
        }
    }

    [RelayCommand]
    private void EscucharVariante(FilaVariante v) => Alternar(v.Referencia);

    [RelayCommand]
    private async Task BorrarVarianteAsync(FilaVariante v)
    {
        var tarjeta = Voces.FirstOrDefault(t => t.Variantes.Contains(v));
        if (tarjeta is null)
            return;
        if (Reproductor.Sonando == v.Referencia)
            Reproductor.Detener();
        var biblioteca = await _voces.BibliotecaAsync();
        biblioteca.BorrarVariante(tarjeta.Voz.Id, v.Variante.Id);
        CargarVariantes(biblioteca, tarjeta);
    }

    // --- Compartir voces -------------------------------------------------------------------------

    [RelayCommand]
    private async Task ExportarAsync(TarjetaVoz t)
    {
        var dialogo = new SaveFileDialog
        {
            Title = "Exportar la voz",
            FileName = $"{Constantino.Core.Textos.NombreDeArchivo(t.Nombre)} - voz de Calíope.zip",
            Filter = "Voz de Calíope (*.zip)|*.zip",
            DefaultExt = ".zip",
        };
        if (dialogo.ShowDialog() != true)
            return;
        try
        {
            (await _voces.BibliotecaAsync()).Exportar(t.Voz.Id, dialogo.FileName);
            ErrorGeneral = null;
            AvisoImportar = $"«{t.Nombre}» exportada a {dialogo.FileName}. Quien la reciba puede añadirla con «Abrir una voz compartida…».";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VozInvalidaException)
        {
            ErrorGeneral = $"No se ha podido exportar: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task AbrirPaqueteAsync()
    {
        var dialogo = new OpenFileDialog
        {
            Title = "Abrir una voz compartida",
            Filter = "Voz de Calíope (*.zip)|*.zip",
        };
        if (dialogo.ShowDialog() != true)
            return;
        var permiso = new MessageBox
        {
            Title = "¿Tienes permiso para usar esta voz?",
            Content = "Solo puedes usar voces que sean tuyas o de personas que te hayan dado permiso expreso para usarlas.",
            PrimaryButtonText = "Sí, tengo permiso",
            CloseButtonText = "Cancelar",
        };
        if (await permiso.ShowDialogAsync() != MessageBoxResult.Primary)
            return;
        try
        {
            var voz = (await _voces.BibliotecaAsync()).ImportarPaquete(dialogo.FileName, consentimiento: true);
            if (_voces.Predeterminada is null)
                _voces.MarcarPredeterminada(voz.Id);
            await CargarAsync();
            ErrorGeneral = null;
            AvisoImportar = $"«{voz.Nombre}» añadida a tu biblioteca.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VozInvalidaException)
        {
            ErrorGeneral = e is VozInvalidaException ? e.Message : $"No se ha podido abrir: {e.Message}";
        }
    }

    [RelayCommand]
    private void AbrirImportar()
    {
        ErrorImportar = null;
        AvisoImportar = null;
        MostrarImportar = true;
    }

    [RelayCommand]
    private void CancelarImportar() => MostrarImportar = false;

    [RelayCommand]
    private void Examinar()
    {
        var dialogo = new OpenFileDialog
        {
            Title = "Grabación de la voz",
            Filter = "Audio (*.wav;*.mp3;*.flac;*.ogg;*.m4a)|*.wav;*.mp3;*.flac;*.ogg;*.m4a|Todos los archivos|*.*",
        };
        if (dialogo.ShowDialog() != true)
            return;
        ArchivoImportar = dialogo.FileName;
        if (string.IsNullOrWhiteSpace(NombreImportar))
            NombreImportar = Path.GetFileNameWithoutExtension(dialogo.FileName);
        // Si al lado hay un .txt con el mismo nombre, se propone como transcripción.
        // Si trae secciones (como las referencias de voz-narrador), solo interesa la de TRANSCRIPCION.
        var txt = Path.ChangeExtension(dialogo.FileName, ".txt");
        if (string.IsNullOrWhiteSpace(TranscripcionImportar) && File.Exists(txt))
        {
            var texto = File.ReadAllText(txt).Replace("\r\n", "\n");
            const string seccion = "TRANSCRIPCION:\n";
            TranscripcionImportar = (texto.Contains(seccion, StringComparison.Ordinal)
                ? texto.Split(seccion, 2)[1].Split("\n\n", 2)[0]
                : texto).Trim();
        }
    }

    [RelayCommand]
    private async Task ImportarAsync()
    {
        ErrorImportar = null;
        AvisoImportar = null;
        Importando = true;
        try
        {
            var biblioteca = await _voces.BibliotecaAsync();
            var r = await biblioteca.ImportarAsync(new SolicitudImportacion(
                ArchivoImportar, NombreImportar, IdiomaImportar, TranscripcionImportar, ConsentimientoImportar,
                SinTranscripcion: SinTranscripcionImportar));
            if (_voces.Predeterminada is null)
                _voces.MarcarPredeterminada(r.Voz.Id);
            await CargarAsync();
            AvisoImportar = r.Aviso is null ? $"«{r.Voz.Nombre}» añadida a tu biblioteca." : $"«{r.Voz.Nombre}» añadida. {r.Aviso}";
            ArchivoImportar = NombreImportar = TranscripcionImportar = "";
            ConsentimientoImportar = SinTranscripcionImportar = false;
            MostrarImportar = false;
        }
        catch (VozInvalidaException e)
        {
            ErrorImportar = e.Message;
        }
        finally
        {
            Importando = false;
        }
    }

    [RelayCommand]
    private void Escuchar(TarjetaVoz t) => Alternar(t.Referencia);

    [RelayCommand]
    private void EscucharPrueba(TarjetaVoz t)
    {
        if (t.UltimaPrueba is { } ruta)
            Alternar(ruta);
    }

    private void Alternar(string ruta)
    {
        if (Reproductor.Sonando == ruta)
            Reproductor.Detener();
        else
            Reproductor.Reproducir(ruta);
    }

    [RelayCommand]
    private void AlternarPrueba(TarjetaVoz t) => t.MostrarPrueba = !t.MostrarPrueba;

    [RelayCommand]
    private async Task ProbarAsync(TarjetaVoz t)
    {
        if (string.IsNullOrWhiteSpace(t.TextoPrueba))
            return;
        t.ErrorPrueba = null;
        t.ResultadoPrueba = null;
        t.Generando = true;
        try
        {
            var salida = await _voces.ArchivoTemporalAsync("prueba-" + t.Voz.Id);
            var motor = MotorPrueba;
            var r = await Motor.SintetizarAsync(t.Voz, t.Referencia, t.TextoPrueba.Trim(), salida, motor: motor);
            t.UltimaPrueba = r.Salida;
            var es = CultureInfo.GetCultureInfo("es-ES");
            t.ResultadoPrueba = $"{MotoresDeVoz.Nombre(motor)}: {r.Duracion.ToString("0.#", es)} s de audio generados en {r.Segundos.ToString("0.#", es)} s" +
                                (r.VramPicoGib is { } v ? $" · {v.ToString("0.#", es)} GB de memoria de la tarjeta" : "");
            Reproductor.Reproducir(r.Salida);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            t.ErrorPrueba = ServicioMotor.Explicar(e);
        }
        finally
        {
            t.Generando = false;
        }
    }

    [RelayCommand]
    private void EditarNombre(TarjetaVoz t)
    {
        t.NombreEditado = t.Nombre;
        t.Editando = true;
    }

    [RelayCommand]
    private void CancelarNombre(TarjetaVoz t) => t.Editando = false;

    [RelayCommand]
    private async Task GuardarNombreAsync(TarjetaVoz t)
    {
        try
        {
            t.Voz = (await _voces.BibliotecaAsync()).Renombrar(t.Voz.Id, t.NombreEditado);
            t.Editando = false;
        }
        catch (VozInvalidaException e)
        {
            ErrorGeneral = e.Message;
        }
    }

    /// <summary>Al editar una voz ya creada, la alternativa es dejar la transcripción vacía, no marcar una casilla.</summary>
    internal static string ParaEdicion(string mensaje) =>
        mensaje.Replace("o marca «No tengo la transcripción»", "o déjala vacía para imitar solo el timbre");

    [RelayCommand]
    private void EditarTranscripcion(TarjetaVoz t)
    {
        t.TranscripcionEditada = t.Voz.Transcripcion;
        t.ErrorTranscripcion = null;
        t.EditandoTranscripcion = true;
    }

    [RelayCommand]
    private void CancelarTranscripcion(TarjetaVoz t) => t.EditandoTranscripcion = false;

    [RelayCommand]
    private async Task GuardarTranscripcionAsync(TarjetaVoz t)
    {
        try
        {
            t.Voz = (await _voces.BibliotecaAsync()).CambiarTranscripcion(t.Voz.Id, t.TranscripcionEditada);
            t.EditandoTranscripcion = false;
            t.ErrorTranscripcion = null;
        }
        catch (VozInvalidaException e)
        {
            t.ErrorTranscripcion = ParaEdicion(e.Message);
        }
    }

    [RelayCommand]
    private void Predeterminada(TarjetaVoz t) => _voces.MarcarPredeterminada(t.Voz.Id);

    [RelayCommand]
    private async Task BorrarAsync(TarjetaVoz t)
    {
        var caja = new MessageBox
        {
            Title = $"Borrar «{t.Nombre}»",
            Content = "Se borrará la voz y su grabación de referencia. No se puede deshacer.",
            PrimaryButtonText = "Borrar",
            CloseButtonText = "Cancelar",
        };
        if (await caja.ShowDialogAsync() != MessageBoxResult.Primary)
            return;
        if (Reproductor.Sonando is { } sonando && (sonando == t.Referencia || sonando == t.UltimaPrueba))
            Reproductor.Detener();
        try
        {
            (await _voces.BibliotecaAsync()).Borrar(t.Voz.Id);
        }
        catch (IOException e)
        {
            ErrorGeneral = $"No se ha podido borrar: {e.Message}";
            return;
        }
        if (_voces.Predeterminada == t.Voz.Id)
            _voces.MarcarPredeterminada(null);
        else
            await CargarAsync();
    }

    [RelayCommand]
    private Task ApagarMotorAsync() => Motor.ApagarAsync();

    [RelayCommand]
    private void IrAComponentes() => _navegacion.Navigate(typeof(PaginaComponentes));
}
