using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Narracion;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Generando { get; set; }

    public bool Libre => !Generando;

    [ObservableProperty] public partial double Fraccion { get; set; }
    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string Resumen { get; set; } = "";
    [ObservableProperty] public partial string? Completo { get; set; }
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
        var bloques = Guion.Leer(TextoGuion, ajustes.Pausa, ajustes.PausaLarga);
        var voz = await _narracion.VozAsync(VozId);
        var modelo = await _narracion.ModeloAsync();
        var salida = (await _narracion.AlmacenAsync()).CarpetaSalida(_proyecto.Id);
        var estados = _proyecto.Bloques.ToDictionary(b => b.Indice);

        Bloques.Clear();
        foreach (var b in bloques)
        {
            var fila = new FilaBloque(b.Indice, b.Nombre, b.Vacio ? "(solo silencio)" : Recortar(b.TextoLeido, 160));
            if (estados.TryGetValue(b.Indice, out var e) && File.Exists(Path.Combine(salida, e.Archivo)))
            {
                var huella = voz is { } v && modelo is not null
                    ? Huellas.DeBloque(b, ajustes, SintetizadorMotor.Firma(v.Voz, v.Referencia), modelo)
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
        if (await _narracion.ModeloAsync() is not { } modelo)
        {
            Error = "Falta un modelo Base: instálalo desde Componentes.";
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
            var narrador = await _narracion.NarradorAsync(voz.Voz, voz.Referencia, modelo);
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
