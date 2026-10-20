using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Constantino.Core;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Motores;
using Constantino.Core.Narracion;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Constantino.App.ViewModels;

public enum EstadoBloqueVista
{
    Pendiente,
    Generado,
    Desactualizado,
    Generando,
}

/// <summary>Un bloque del guion en la lista: su estado frente a lo ya generado.</summary>
public sealed partial class FilaBloque(int indice, string nombre, string resumen) : ObservableObject
{
    public int Indice { get; } = indice;
    public string Nombre { get; } = nombre;
    public string Titulo => $"{Indice}. {Nombre}";
    public string Resumen { get; } = resumen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Etiqueta), nameof(Apariencia), nameof(Escuchable))]
    public partial EstadoBloqueVista Estado { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Escuchable))]
    public partial string? Archivo { get; set; }

    [ObservableProperty] public partial string Duracion { get; set; } = "";
    [ObservableProperty] public partial string? Avisos { get; set; }

    public bool Escuchable => Archivo is not null && Estado != EstadoBloqueVista.Generando;

    public string Etiqueta => Estado switch
    {
        EstadoBloqueVista.Generado => "Generado",
        EstadoBloqueVista.Desactualizado => "Por actualizar",
        EstadoBloqueVista.Generando => "Generando…",
        _ => "Pendiente",
    };

    public ControlAppearance Apariencia => Estado switch
    {
        EstadoBloqueVista.Generado => ControlAppearance.Success,
        EstadoBloqueVista.Desactualizado => ControlAppearance.Caution,
        EstadoBloqueVista.Generando => ControlAppearance.Primary,
        _ => ControlAppearance.Secondary,
    };
}

public sealed record OpcionProyecto(string Id, string Nombre);

public sealed record OpcionVoz(string Id, string Nombre);

public sealed record OpcionFormato(FormatoSalida Valor, string Nombre);

public sealed record OpcionMotor(string Id, string Nombre);

/// <summary>Una etiqueta de variante de la voz, para insertarla en el guion.</summary>
public sealed record EtiquetaVariante(string Nombre, string Etiqueta);

public sealed partial class NarrarViewModel : ObservableObject
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private readonly ServicioNarracion _narracion;
    private readonly ServicioVoces _voces;
    private readonly INavigationService _navegacion;
    private readonly DispatcherTimer _demora;
    private Proyecto? _proyecto;
    private bool _cargando;
    private CancellationTokenSource? _cancelacion;

    public NarrarViewModel(ServicioNarracion narracion, ServicioVoces voces, ServicioReproductor reproductor, ServicioMotor motor,
        INavigationService navegacion)
    {
        _narracion = narracion;
        _voces = voces;
        _navegacion = navegacion;
        Reproductor = reproductor;
        Motor = motor;
        // Guardar y recalcular los bloques mientras se escribe, pero no en cada tecla.
        _demora = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _demora.Tick += async (_, _) =>
        {
            _demora.Stop();
            await GuardarYRefrescarAsync();
        };
    }

    public ServicioReproductor Reproductor { get; }
    public ServicioMotor Motor { get; }

    public ObservableCollection<OpcionProyecto> Proyectos { get; } = [];
    public ObservableCollection<OpcionVoz> Voces { get; } = [];
    public ObservableCollection<FilaBloque> Bloques { get; } = [];

    public IReadOnlyList<OpcionFormato> Formatos { get; } =
        [new(FormatoSalida.Mp3, "MP3 (192 kbps)"), new(FormatoSalida.Wav, "WAV (sin pérdida)")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayProyecto))]
    public partial OpcionProyecto? ProyectoSeleccionado { get; set; }

    public bool HayProyecto => ProyectoSeleccionado is not null;

    [ObservableProperty] public partial string TextoGuion { get; set; } = "";
    [ObservableProperty] public partial string? VozId { get; set; }
    [ObservableProperty] public partial double Temperatura { get; set; } = 0.9;
    [ObservableProperty] public partial double Semilla { get; set; } = 1234;
    [ObservableProperty] public partial double Pausa { get; set; } = 0.6;
    [ObservableProperty] public partial double PausaLarga { get; set; } = 1.5;
    [ObservableProperty] public partial double PausaEntreBloques { get; set; } = 0.3;
    [ObservableProperty] public partial double SusurroDb { get; set; } = -8;
    [ObservableProperty] public partial double Lufs { get; set; } = -16;
    [ObservableProperty] public partial FormatoSalida Formato { get; set; } = FormatoSalida.Mp3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoVelocidad))]
    public partial double Velocidad { get; set; } = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoTono))]
    public partial double TonoSemitonos { get; set; }

    public string TextoVelocidad => Math.Abs(Velocidad - 1) < 0.001 ? "normal" : $"{Velocidad.ToString("0.00", Es)}×";

    public string TextoTono => Math.Abs(TonoSemitonos) < 0.001
        ? "natural"
        : $"{(TonoSemitonos > 0 ? "+" : "")}{TonoSemitonos.ToString("0.#", Es)} semitonos";

    // --- Motor de voz ---
    public ObservableCollection<OpcionMotor> Motores { get; } = [new(MotoresDeVoz.Qwen, "Qwen3-TTS")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsChatterbox))]
    public partial string MotorVoz { get; set; } = MotoresDeVoz.Qwen;

    public bool EsChatterbox => MotorVoz == MotoresDeVoz.Chatterbox;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoIntensidad))]
    public partial double Intensidad { get; set; } = 0.5;

    public string TextoIntensidad => Intensidad switch
    {
        < 0.4 => "contenida",
        < 0.65 => "natural",
        < 0.85 => "intensa",
        _ => "exagerada",
    };

    /// <summary>Explica el motor elegido, o cómo conseguir el otro.</summary>
    [ObservableProperty] public partial string? AyudaMotor { get; set; }

    /// <summary>Etiquetas de las variantes de la voz elegida (además de [whispers]).</summary>
    public ObservableCollection<EtiquetaVariante> EtiquetasVoz { get; } = [];

    [ObservableProperty] public partial string? AyudaVariantes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Generando { get; set; }

    public bool Libre => !Generando;

    [ObservableProperty] public partial double Fraccion { get; set; }
    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string Resumen { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NombreCompleto))]
    public partial string? Completo { get; set; }

    public string? NombreCompleto => Completo is null ? null : Path.GetFileName(Completo);

    /// <summary>Carpeta donde se guardan los audios del proyecto.</summary>
    [ObservableProperty] public partial string? Salida { get; set; }

    [ObservableProperty] public partial string? AvisoSalida { get; set; }
    [ObservableProperty] public partial bool MostrarNuevo { get; set; }
    [ObservableProperty] public partial string NombreNuevo { get; set; } = "";
    [ObservableProperty] public partial bool SinVoces { get; set; }

    public string TextoExpresividad => Temperatura switch
    {
        < 0.7 => "Estable y contenida",
        < 1.0 => "Natural",
        _ => "Muy expresiva (más riesgo de fallos)",
    };

    // --- Carga ---------------------------------------------------------------------------------

    public async Task CargarAsync()
    {
        var biblioteca = await _voces.BibliotecaAsync();
        Voces.Clear();
        foreach (var v in biblioteca.Listar())
            Voces.Add(new OpcionVoz(v.Id, v.Nombre));
        SinVoces = Voces.Count == 0;

        var hayChatterbox = await _narracion.ChatterboxInstaladoAsync();
        if (hayChatterbox && Motores.All(m => m.Id != MotoresDeVoz.Chatterbox))
            Motores.Add(new OpcionMotor(MotoresDeVoz.Chatterbox, "Chatterbox"));
        AyudaMotor = hayChatterbox ? null : "Instala Chatterbox en «Componentes» para poder comparar con otro motor.";

        var almacen = await _narracion.AlmacenAsync();
        var actual = ProyectoSeleccionado?.Id;
        Proyectos.Clear();
        foreach (var p in almacen.Listar())
            Proyectos.Add(new OpcionProyecto(p.Id, p.Nombre));
        ProyectoSeleccionado = Proyectos.FirstOrDefault(p => p.Id == actual) ?? Proyectos.FirstOrDefault();
        if (ProyectoSeleccionado is not null && _proyecto?.Id == ProyectoSeleccionado.Id)
            await RefrescarBloquesAsync();
    }

    partial void OnProyectoSeleccionadoChanged(OpcionProyecto? value) => _ = AbrirAsync(value);

    private async Task AbrirAsync(OpcionProyecto? opcion)
    {
        _demora.Stop();
        if (opcion is null)
        {
            _proyecto = null;
            Bloques.Clear();
            return;
        }
        var almacen = await _narracion.AlmacenAsync();
        _proyecto = almacen.Leer(opcion.Id);
        if (_proyecto is null)
            return;
        _cargando = true;
        AvisoSalida = null;
        var a = _proyecto.Ajustes;
        TextoGuion = almacen.LeerGuion(opcion.Id);
        VozId = a.VozId ?? _voces.Predeterminada;
        Temperatura = a.Temperatura;
        Semilla = a.Semilla;
        Pausa = a.Pausa;
        PausaLarga = a.PausaLarga;
        PausaEntreBloques = a.PausaEntreBloques;
        SusurroDb = a.SusurroDb;
        Lufs = a.Lufs;
        Formato = a.Formato;
        Velocidad = a.Velocidad;
        MotorVoz = a.Motor;
        Intensidad = a.Intensidad;
        TonoSemitonos = a.TonoSemitonos;
        _cargando = false;
        Error = null;
        await RefrescarBloquesAsync();
    }

    // --- Cambios: se guardan con un pequeño retraso ------------------------------------------

    partial void OnTextoGuionChanged(string value) => Programar();
    partial void OnVozIdChanged(string? value) => Programar();
    partial void OnTemperaturaChanged(double value)
    {
        OnPropertyChanged(nameof(TextoExpresividad));
        Programar();
    }
    partial void OnSemillaChanged(double value) => Programar();
    partial void OnPausaChanged(double value) => Programar();
    partial void OnPausaLargaChanged(double value) => Programar();
    partial void OnPausaEntreBloquesChanged(double value) => Programar();
    partial void OnSusurroDbChanged(double value) => Programar();
    partial void OnLufsChanged(double value) => Programar();
    partial void OnFormatoChanged(FormatoSalida value) => Programar();
    partial void OnVelocidadChanged(double value) => Programar();
    partial void OnMotorVozChanged(string value) => Programar();
    partial void OnIntensidadChanged(double value) => Programar();
    partial void OnTonoSemitonosChanged(double value) => Programar();

    private void Programar()
    {
        if (_cargando || _proyecto is null)
            return;
        _demora.Stop();
        _demora.Start();
    }

    private AjustesNarracion AjustesActuales() => _proyecto!.Ajustes with
    {
        VozId = VozId,
        Temperatura = Math.Round(Temperatura, 2),
        Semilla = (int)Semilla,
        Pausa = Pausa,
        PausaLarga = PausaLarga,
        PausaEntreBloques = PausaEntreBloques,
        SusurroDb = SusurroDb,
        Lufs = Lufs,
        Formato = Formato,
        Velocidad = Math.Round(Velocidad, 2),
        Motor = MotorVoz,
        Intensidad = Math.Round(Intensidad, 2),
        TonoSemitonos = Math.Round(TonoSemitonos, 1),
    };

    private async Task GuardarYRefrescarAsync()
    {
        if (_proyecto is null)
            return;
        var almacen = await _narracion.AlmacenAsync();
        almacen.GuardarGuion(_proyecto.Id, TextoGuion);
        var ajustes = AjustesActuales();
        if (ajustes != _proyecto.Ajustes)
            _proyecto = almacen.Guardar(_proyecto with { Ajustes = ajustes });
        await RefrescarBloquesAsync();
    }

    /// <summary>Compara cada bloque del guion con lo generado: pendiente, generado o por actualizar.</summary>
    private async Task RefrescarBloquesAsync()
    {
        if (_proyecto is null)
            return;
        var ajustes = AjustesActuales();
        var voz = await _narracion.VozAsync(VozId);
        var bloques = Guion.Leer(TextoGuion, ajustes.Pausa, ajustes.PausaLarga, voz?.IdsVariantes);
        ActualizarEtiquetas(voz);
        var modelo = await _narracion.ModeloAsync(ajustes.Motor);
        var salida = (await _narracion.AlmacenAsync()).CarpetaSalida(_proyecto.Id);
        Salida = salida;
        var estados = _proyecto.Bloques.ToDictionary(b => b.Indice);

        Bloques.Clear();
        foreach (var b in bloques)
        {
            var fila = new FilaBloque(b.Indice, b.Nombre, b.Vacio ? "(solo silencio)" : Recortar(b.TextoLeido, 160));
            if (estados.TryGetValue(b.Indice, out var e) && File.Exists(Path.Combine(salida, e.Archivo)))
            {
                var huella = voz is { } v && modelo is not null
                    ? Huellas.DeBloque(b, ajustes, v.Firma, modelo)
                    : null;
                fila.Estado = huella == e.Huella ? EstadoBloqueVista.Generado : EstadoBloqueVista.Desactualizado;
                fila.Archivo = Path.Combine(salida, e.Archivo);
                fila.Duracion = Segundos(e.Duracion);
                fila.Avisos = e.Avisos.Count > 0 ? string.Join("\n", e.Avisos) : null;
            }
            else if (b.Avisos.Count > 0)
            {
                fila.Avisos = string.Join("\n", b.Avisos);
            }
            Bloques.Add(fila);
        }

        var completo = Path.Combine(salida, Narrador.ArchivoCompleto(_proyecto with { Ajustes = ajustes }));
        Completo = File.Exists(completo) ? completo : null;
        var generados = Bloques.Count(f => f.Estado == EstadoBloqueVista.Generado);
        var porActualizar = Bloques.Count(f => f.Estado == EstadoBloqueVista.Desactualizado);
        var duracion = _proyecto.Bloques.Where(e => e.Indice <= bloques.Count).Sum(e => e.Duracion);
        Resumen = Bloques.Count == 0
            ? "El guion está vacío."
            : $"{Bloques.Count} bloques · {generados} generados" +
              (porActualizar > 0 ? $" · {porActualizar} por actualizar" : "") +
              (duracion > 0 ? $" · {Duracion(duracion)} de audio" : "");
    }

    private void ActualizarEtiquetas(VozParaNarrar? voz)
    {
        EtiquetasVoz.Clear();
        if (voz is null)
        {
            AyudaVariantes = null;
            return;
        }
        foreach (var (v, _) in voz.Variantes.Where(v => v.Variante.Id != Constantino.Core.Voces.CatalogoVariantes.Susurro))
            EtiquetasVoz.Add(new EtiquetaVariante(v.Nombre, v.Etiqueta + " "));
        var tieneSusurro = voz.IdsVariantes.Contains(Constantino.Core.Voces.CatalogoVariantes.Susurro);
        AyudaVariantes = voz.Variantes.Count == 0
            ? "Esta voz no tiene variantes: [whispers] se simula bajando el volumen. Puedes añadirle variantes en «Voces»."
            : "Variantes de esta voz: " + string.Join(", ", voz.Variantes.Select(v => $"{v.Variante.Etiqueta} {v.Variante.Nombre.ToLowerInvariant()}")) +
              (tieneSusurro ? "." : ". [whispers] se simula bajando el volumen.");
    }

    // --- Proyectos ---------------------------------------------------------------------------

    [RelayCommand]
    private void Nuevo()
    {
        NombreNuevo = "";
        MostrarNuevo = true;
    }

    [RelayCommand]
    private void CancelarNuevo() => MostrarNuevo = false;

    [RelayCommand]
    private async Task CrearProyectoAsync()
    {
        if (string.IsNullOrWhiteSpace(NombreNuevo))
            return;
        var almacen = await _narracion.AlmacenAsync();
        var p = almacen.Crear(NombreNuevo, new AjustesNarracion(VozId: _voces.Predeterminada));
        MostrarNuevo = false;
        Proyectos.Insert(0, new OpcionProyecto(p.Id, p.Nombre));
        ProyectoSeleccionado = Proyectos[0];
    }

    [RelayCommand]
    private async Task BorrarProyectoAsync()
    {
        if (_proyecto is null)
            return;
        var caja = new MessageBox
        {
            Title = $"Borrar «{_proyecto.Nombre}»",
            Content = "Se borrarán el guion y todos los audios generados del proyecto. No se puede deshacer.",
            PrimaryButtonText = "Borrar",
            CloseButtonText = "Cancelar",
        };
        if (await caja.ShowDialogAsync() != MessageBoxResult.Primary)
            return;
        Reproductor.Detener();
        (await _narracion.AlmacenAsync()).Borrar(_proyecto.Id);
        _proyecto = null;
        ProyectoSeleccionado = null;
        await CargarAsync();
    }

    [RelayCommand]
    private async Task AbrirCarpetaAsync()
    {
        if (_proyecto is null)
            return;
        var carpeta = (await _narracion.AlmacenAsync()).CarpetaSalida(_proyecto.Id);
        Directory.CreateDirectory(carpeta);
        Process.Start(new ProcessStartInfo(carpeta) { UseShellExecute = true });
    }

    // --- Sacar los audios -----------------------------------------------------------------------

    [RelayCommand]
    private void GuardarCompletoComo()
    {
        if (Completo is { } ruta && _proyecto is { } p)
            GuardarComo(ruta, Textos.NombreDeArchivo(p.Nombre) + Path.GetExtension(ruta));
    }

    [RelayCommand]
    private void GuardarBloqueComo(FilaBloque fila)
    {
        if (fila.Archivo is { } ruta)
            GuardarComo(ruta, Path.GetFileName(ruta));
    }

    private void GuardarComo(string origen, string nombre)
    {
        var extension = Path.GetExtension(origen);
        var dialogo = new SaveFileDialog
        {
            Title = "Guardar el audio",
            FileName = nombre,
            DefaultExt = extension,
            Filter = $"Audio {extension.TrimStart('.').ToUpperInvariant()}|*{extension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (dialogo.ShowDialog() != true)
            return;
        try
        {
            File.Copy(origen, dialogo.FileName, overwrite: true);
            AvisoSalida = $"Guardado en {dialogo.FileName}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AvisoSalida = $"No se ha podido guardar: {e.Message}";
        }
    }

    /// <summary>Copia la narración completa y los bloques generados a la carpeta que se elija.</summary>
    [RelayCommand]
    private void ExportarTodo()
    {
        if (_proyecto is not { } p)
            return;
        var dialogo = new OpenFolderDialog
        {
            Title = "Carpeta donde copiar los audios",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (dialogo.ShowDialog() != true)
            return;
        var destino = Path.Combine(dialogo.FolderName, Textos.NombreDeArchivo(p.Nombre));
        var archivos = Bloques.Where(f => f.Archivo is not null).Select(f => f.Archivo!).ToList();
        if (Completo is { } completo)
            archivos.Insert(0, completo);
        try
        {
            Directory.CreateDirectory(destino);
            foreach (var a in archivos)
                File.Copy(a, Path.Combine(destino, Path.GetFileName(a)), overwrite: true);
            AvisoSalida = $"{archivos.Count} audios copiados a {destino}";
            Process.Start(new ProcessStartInfo(destino) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AvisoSalida = $"No se ha podido copiar: {e.Message}";
        }
    }

    [RelayCommand]
    private void CopiarCompleto()
    {
        if (Completo is { } ruta)
            Copiar(ruta);
    }

    [RelayCommand]
    private void CopiarBloque(FilaBloque fila)
    {
        if (fila.Archivo is { } ruta)
            Copiar(ruta);
    }

    /// <summary>Al portapapeles como archivo: se pega en el Explorador o en un editor de vídeo.</summary>
    private void Copiar(string ruta)
    {
        System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { ruta });
        AvisoSalida = $"«{Path.GetFileName(ruta)}» copiado: pégalo en una carpeta o en tu editor de vídeo.";
    }

    [RelayCommand]
    private void MostrarBloque(FilaBloque fila)
    {
        if (fila.Archivo is { } ruta)
            Process.Start("explorer.exe", $"/select,\"{ruta}\"");
    }

    // --- Generación --------------------------------------------------------------------------

    [RelayCommand]
    private Task GenerarAsync() => NarrarAsync(null, forzar: false);

    [RelayCommand]
    private Task RegenerarAsync(FilaBloque fila) => NarrarAsync(new HashSet<int> { fila.Indice }, forzar: true);

    [RelayCommand]
    private void Cancelar() => _cancelacion?.Cancel();

    private async Task NarrarAsync(IReadOnlySet<int>? solo, bool forzar)
    {
        if (_proyecto is null || Generando)
            return;
        Error = null;
        _demora.Stop();
        await GuardarYRefrescarAsync();
        if (await _narracion.VozAsync(VozId) is not { } voz)
        {
            Error = Voces.Count == 0 ? "Primero añade una voz en «Voces»." : "Elige la voz con la que narrar.";
            return;
        }
        if (await _narracion.ModeloAsync(MotorVoz) is not { } modelo)
        {
            Error = EsChatterbox
                ? "Falta Chatterbox: instálalo desde Componentes o elige Qwen3-TTS."
                : "Falta un modelo Base: instálalo desde Componentes.";
            return;
        }

        // Un audio que suena no se puede sobrescribir.
        Reproductor.Detener();
        Generando = true;
        Fraccion = 0;
        Mensaje = "Preparando…";
        _cancelacion = new CancellationTokenSource();
        var progreso = new Progress<EventoNarracion>(e =>
        {
            Fraccion = e.Fraccion;
            Mensaje = e.Mensaje;
            if (Bloques.FirstOrDefault(f => f.Indice == e.Bloque) is { } fila)
            {
                if (e.Tipo is TipoEventoNarracion.Bloque or TipoEventoNarracion.Frase)
                    fila.Estado = EstadoBloqueVista.Generando;
                else if (e.Tipo is TipoEventoNarracion.BloqueListo)
                    fila.Estado = EstadoBloqueVista.Generado;
            }
        });
        try
        {
            var narrador = await _narracion.NarradorAsync(voz, modelo);
            var r = await narrador.NarrarAsync(_proyecto, TextoGuion, solo, forzar, progreso, _cancelacion.Token);
            _proyecto = r.Proyecto;
            Mensaje = r.Generados == 0
                ? "No había nada que actualizar."
                : $"Listo: {r.Generados} bloque(s) generado(s)" + (r.SinCambios > 0 ? $", {r.SinCambios} sin cambios." : ".");
        }
        catch (OperationCanceledException)
        {
            Mensaje = "Cancelado. Lo ya generado se conserva.";
        }
        catch (Exception e)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Generando = false;
            _cancelacion.Dispose();
            _cancelacion = null;
            // Lo generado se guarda aunque falle: se relee el proyecto para mostrarlo.
            _proyecto = (await _narracion.AlmacenAsync()).Leer(_proyecto!.Id) ?? _proyecto;
            await RefrescarBloquesAsync();
        }
    }

    // --- Escuchar ----------------------------------------------------------------------------

    [RelayCommand]
    private void Escuchar(FilaBloque fila)
    {
        if (fila.Archivo is { } ruta)
            Alternar(ruta);
    }

    [RelayCommand]
    private void EscucharCompleto()
    {
        if (Completo is { } ruta)
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
    private void IrAVoces() => _navegacion.Navigate(typeof(PaginaVoces));

    private static string Recortar(string texto, int maximo) => texto.Length <= maximo ? texto : texto[..(maximo - 1)] + "…";

    private static string Segundos(double s) => $"{s.ToString("0.0", Es)} s";

    private static string Duracion(double s) =>
        s < 60 ? Segundos(s) : $"{(int)(s / 60)} min {(int)(s % 60)} s";
}
