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
using Constantino.Core.Recomendaciones;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using static Constantino.Core.Idioma;

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

    // --- Otras tomas ---
    public ObservableCollection<FilaToma> Tomas { get; } = [];
    [ObservableProperty] public partial bool MostrarTomas { get; set; }
    [ObservableProperty] public partial bool GenerandoTomas { get; set; }
    [ObservableProperty] public partial string? MensajeTomas { get; set; }

    public string Etiqueta => Estado switch
    {
        EstadoBloqueVista.Generado => T("Generado"),
        EstadoBloqueVista.Desactualizado => T("Por actualizar"),
        EstadoBloqueVista.Generando => T("Generando…"),
        _ => T("Pendiente"),
    };

    public ControlAppearance Apariencia => Estado switch
    {
        EstadoBloqueVista.Generado => ControlAppearance.Success,
        EstadoBloqueVista.Desactualizado => ControlAppearance.Caution,
        EstadoBloqueVista.Generando => ControlAppearance.Primary,
        _ => ControlAppearance.Secondary,
    };
}

/// <summary>Una versión alternativa de un bloque, para escucharla y quedarse con ella.</summary>
public sealed record FilaToma(int Indice, int Semilla, string Archivo, string Duracion, string Titulo);

/// <summary>Una palabra del diccionario de pronunciación, editable.</summary>
public sealed partial class FilaPronunciacion : ObservableObject
{
    [ObservableProperty] public partial string Escrito { get; set; } = "";
    [ObservableProperty] public partial string Dicho { get; set; } = "";
}

public sealed record OpcionProyecto(string Id, string Nombre);

public sealed record OpcionVoz(string Id, string Nombre);

public sealed record OpcionFormato(FormatoSalida Valor, string Nombre);

public sealed record OpcionMotor(string Id, string Nombre)
{
    public static OpcionMotor De(string motor) => new(motor, MotoresDeVoz.NombreVisible(motor));

    /// <summary>Deja en <paramref name="lista"/> los motores instalados, en orden, sin vaciarla (no se pierde la selección).</summary>
    public static void Sincronizar(ObservableCollection<OpcionMotor> lista, IReadOnlyList<string> instalados)
    {
        foreach (var sobra in lista.Where(o => !instalados.Contains(o.Id)).ToList())
            lista.Remove(sobra);
        for (var i = 0; i < instalados.Count; i++)
            if (i >= lista.Count || lista[i].Id != instalados[i])
                lista.Insert(i, De(instalados[i]));
    }
}

/// <summary>Un sonido de fondo elegible; Clave vacía = ninguno.</summary>
public sealed record OpcionSonido(string Clave, string Nombre);

/// <summary>Una etiqueta de variante de la voz, para insertarla en el guion.</summary>
public sealed record EtiquetaVariante(string Nombre, string Etiqueta);

public sealed partial class NarrarViewModel : ObservableObject
{
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
        [new(FormatoSalida.Mp3, "MP3 (192 kbps)"), new(FormatoSalida.Wav, T("WAV (sin pérdida)"))];

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

    public string TextoVelocidad => Math.Abs(Velocidad - 1) < 0.001 ? T("normal") : $"{Velocidad.ToString("0.00", Idioma.Cultura)}×";

    public string TextoTono => Math.Abs(TonoSemitonos) < 0.001
        ? T("natural")
        : T("{0} semitonos", $"{(TonoSemitonos > 0 ? "+" : "")}{TonoSemitonos.ToString("0.#", Idioma.Cultura)}");

    // --- Motor de voz ---
    public ObservableCollection<OpcionMotor> Motores { get; } = [OpcionMotor.De(MotoresDeVoz.Qwen)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneIntensidad), nameof(TieneExpresividad), nameof(AyudaMotor))]
    public partial string MotorVoz { get; set; } = MotoresDeVoz.Qwen;

    public bool TieneIntensidad => MotoresDeVoz.TieneIntensidad(MotorVoz);

    public bool TieneExpresividad => MotoresDeVoz.TieneExpresividad(MotorVoz);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoIntensidad))]
    public partial double Intensidad { get; set; } = 0.5;

    public string TextoIntensidad => Intensidad switch
    {
        < 0.4 => T("contenida"),
        < 0.65 => T("natural"),
        < 0.85 => T("intensa"),
        _ => T("exagerada"),
    };

    /// <summary>Explica el motor elegido y, si solo está el principal, que hay otros opcionales.</summary>
    public string AyudaMotor => GuiaMotores.Resumen(MotorVoz) +
                                (Motores.Count == 1 ? " " + T("Hay otros dos motores opcionales en «Componentes»; la guía de allí te dice si te conviene alguno.") : "");

    /// <summary>Otras voces de la biblioteca, para insertar [voz: …].</summary>
    public ObservableCollection<EtiquetaVariante> OtrasVoces { get; } = [];

    /// <summary>Efectos de «Mis sonidos», para insertar [sfx: …].</summary>
    public ObservableCollection<EtiquetaVariante> Efectos { get; } = [];

    // --- Revisión, duración y fondo ---
    [ObservableProperty] public partial bool Revisar { get; set; } = true;
    [ObservableProperty] public partial bool RevisionDisponible { get; set; }
    [ObservableProperty] public partial double DuracionMaxima { get; set; }
    public ObservableCollection<OpcionSonido> Ambientes { get; } = [];
    public ObservableCollection<OpcionSonido> Musicas { get; } = [];
    [ObservableProperty] public partial string Ambiente { get; set; } = "";
    [ObservableProperty] public partial double VolumenAmbiente { get; set; } = -20;
    [ObservableProperty] public partial string Musica { get; set; } = "";
    [ObservableProperty] public partial double VolumenMusica { get; set; } = -22;
    [ObservableProperty] public partial string? AvisosResultado { get; set; }

    // --- Diccionario de pronunciación ---
    public ObservableCollection<FilaPronunciacion> Diccionario { get; } = [];
    [ObservableProperty] public partial bool MostrarDiccionario { get; set; }
    private DispatcherTimer? _demoraDiccionario;

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
        < 0.7 => T("Estable y contenida"),
        < 1.0 => T("Natural"),
        _ => T("Muy expresiva (más riesgo de fallos)"),
    };

    // --- Carga ---------------------------------------------------------------------------------

    public async Task CargarAsync()
    {
        var biblioteca = await _voces.BibliotecaAsync();
        Voces.Clear();
        foreach (var v in biblioteca.Listar())
            Voces.Add(new OpcionVoz(v.Id, v.Nombre));
        SinVoces = Voces.Count == 0;

        OpcionMotor.Sincronizar(Motores, await _narracion.MotoresInstaladosAsync());
        OnPropertyChanged(nameof(AyudaMotor));

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
        Revisar = a.Revisar;
        DuracionMaxima = a.DuracionMaxima ?? 0;
        Ambiente = a.Ambiente ?? "";
        VolumenAmbiente = a.VolumenAmbiente;
        Musica = a.Musica ?? "";
        VolumenMusica = a.VolumenMusica;
        AvisosResultado = null;
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
    partial void OnRevisarChanged(bool value) => Programar();
    partial void OnDuracionMaximaChanged(double value) => Programar();
    partial void OnAmbienteChanged(string value) => Programar();
    partial void OnVolumenAmbienteChanged(double value) => Programar();
    partial void OnMusicaChanged(string value) => Programar();
    partial void OnVolumenMusicaChanged(double value) => Programar();
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
        Revisar = Revisar,
        DuracionMaxima = DuracionMaxima > 0 ? Math.Round(DuracionMaxima, 1) : null,
        Ambiente = string.IsNullOrEmpty(Ambiente) ? null : Ambiente,
        VolumenAmbiente = Math.Round(VolumenAmbiente),
        Musica = string.IsNullOrEmpty(Musica) ? null : Musica,
        VolumenMusica = Math.Round(VolumenMusica),
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
        var proyecto = _proyecto with { Ajustes = ajustes };
        var voz = await _narracion.VozAsync(VozId);
        ActualizarEtiquetas(voz);
        var modelo = await _narracion.ModeloAsync(ajustes.Motor);
        Narrador? narrador = null;
        if (voz is not null && modelo is not null)
        {
            try { narrador = await _narracion.NarradorAsync(voz, modelo, esperarSonidos: false); }
            catch (MotorNoDisponibleException) { }
        }
        var bloques = narrador?.Leer(proyecto, TextoGuion) ?? Guion.Leer(TextoGuion, ajustes.Pausa, ajustes.PausaLarga, voz?.IdsVariantes);
        var salida = (await _narracion.AlmacenAsync()).CarpetaSalida(_proyecto.Id);
        Salida = salida;
        var estados = _proyecto.Bloques.ToDictionary(b => b.Indice);

        Bloques.Clear();
        foreach (var b in bloques)
        {
            var fila = new FilaBloque(b.Indice, b.Nombre, b.Vacio ? T("(solo silencio)") : Recortar(b.TextoLeido, 160));
            if (estados.TryGetValue(b.Indice, out var e) && File.Exists(Path.Combine(salida, e.Archivo)))
            {
                var huella = narrador?.Huella(proyecto, b);
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

        await ActualizarInsertablesAsync(voz);
        var completo = Path.Combine(salida, Narrador.ArchivoCompleto(_proyecto with { Ajustes = ajustes }));
        Completo = File.Exists(completo) ? completo : null;
        var generados = Bloques.Count(f => f.Estado == EstadoBloqueVista.Generado);
        var porActualizar = Bloques.Count(f => f.Estado == EstadoBloqueVista.Desactualizado);
        var duracion = _proyecto.Bloques.Where(e => e.Indice <= bloques.Count).Sum(e => e.Duracion);
        Resumen = Bloques.Count == 0
            ? T("El guion está vacío.")
            : T("{0} bloques · {1} generados", Bloques.Count, generados) +
              (porActualizar > 0 ? " · " + T("{0} por actualizar", porActualizar) : "") +
              (duracion > 0 ? " · " + T("{0} de audio", Duracion(duracion)) : "");
    }

    private async Task ActualizarInsertablesAsync(VozParaNarrar? voz)
    {
        OtrasVoces.Clear();
        foreach (var v in await _narracion.OtrasVocesAsync(voz?.Voz.Id))
            OtrasVoces.Add(new EtiquetaVariante(v.Nombre, Idioma.EsIngles ? $"[voice: {v.Nombre}] " : $"[voz: {v.Nombre}] "));
        var sonidos = await _narracion.SonidosAsync(esperar: false);
        Efectos.Clear();
        foreach (var e in sonidos.ListaEfectos)
            Efectos.Add(new EtiquetaVariante(e.Nombre, $"[sfx: {e.Nombre}] "));
        // Las listas de fondo se rellenan sin perder lo elegido (el ComboBox lo borraría al vaciarse).
        var (ambiente, musica) = (Ambiente, Musica);
        _cargando = true;
        Rellenar(Ambientes, T("Sin ambiente"), sonidos.ListaAmbientes);
        Rellenar(Musicas, T("Sin música"), sonidos.ListaMusica);
        (Ambiente, Musica) = (ambiente, musica);
        _cargando = false;
        RevisionDisponible = await _narracion.RevisionDisponibleAsync();
    }

    private static void Rellenar(ObservableCollection<OpcionSonido> lista, string ninguno, IEnumerable<Constantino.Core.Audio.Sonido> sonidos)
    {
        var nuevas = new List<OpcionSonido> { new("", ninguno) };
        nuevas.AddRange(sonidos.Select(s => new OpcionSonido(s.Nombre, s.Nombre)));
        if (lista.SequenceEqual(nuevas))
            return;
        lista.Clear();
        foreach (var o in nuevas)
            lista.Add(o);
    }

    [RelayCommand]
    private async Task AbrirCarpetaMusicaAsync()
    {
        var carpeta = (await _narracion.SonidosAsync(esperar: false)).CarpetaMusica;
        Directory.CreateDirectory(carpeta);
        Process.Start(new ProcessStartInfo(carpeta) { UseShellExecute = true });
    }

    // --- Diccionario de pronunciación ------------------------------------------------------------

    [RelayCommand]
    private async Task AlternarDiccionarioAsync()
    {
        MostrarDiccionario = !MostrarDiccionario;
        if (!MostrarDiccionario)
            return;
        foreach (var f in Diccionario)
            f.PropertyChanged -= AlCambiarPronunciacion;
        Diccionario.Clear();
        foreach (var p in await _narracion.DiccionarioAsync())
            AnadirFila(new FilaPronunciacion { Escrito = p.Escrito, Dicho = p.Dicho });
        if (Diccionario.Count == 0)
            AnadirFila(new FilaPronunciacion());
    }

    [RelayCommand]
    private void AnadirPronunciacion() => AnadirFila(new FilaPronunciacion());

    [RelayCommand]
    private void QuitarPronunciacion(FilaPronunciacion fila)
    {
        fila.PropertyChanged -= AlCambiarPronunciacion;
        Diccionario.Remove(fila);
        ProgramarDiccionario();
    }

    private void AnadirFila(FilaPronunciacion fila)
    {
        fila.PropertyChanged += AlCambiarPronunciacion;
        Diccionario.Add(fila);
    }

    private void AlCambiarPronunciacion(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ProgramarDiccionario();

    /// <summary>Se guarda solo, poco después de dejar de escribir; los bloques afectados pasan a «Por actualizar».</summary>
    private void ProgramarDiccionario()
    {
        if (_demoraDiccionario is null)
        {
            _demoraDiccionario = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _demoraDiccionario.Tick += async (_, _) =>
            {
                _demoraDiccionario.Stop();
                await _narracion.GuardarDiccionarioAsync(Diccionario.Select(f => new Pronunciacion(f.Escrito, f.Dicho)));
                await RefrescarBloquesAsync();
            };
        }
        _demoraDiccionario.Stop();
        _demoraDiccionario.Start();
    }

    // --- Otras tomas -------------------------------------------------------------------------------

    private async Task<Narrador?> NarradorActualAsync()
    {
        if (await _narracion.VozAsync(VozId) is not { } voz || await _narracion.ModeloAsync(MotorVoz) is not { } modelo)
            return null;
        return await _narracion.NarradorAsync(voz, modelo);
    }

    [RelayCommand]
    private async Task OtrasTomasAsync(FilaBloque fila)
    {
        fila.MostrarTomas = !fila.MostrarTomas;
        if (fila.MostrarTomas && _proyecto is not null && await NarradorActualAsync() is { } narrador)
            CargarTomas(fila, narrador.TomasDe(_proyecto, TextoGuion, fila.Indice));
    }

    private static void CargarTomas(FilaBloque fila, IEnumerable<Toma> tomas)
    {
        fila.Tomas.Clear();
        var n = 1;
        foreach (var t in tomas)
            fila.Tomas.Add(new FilaToma(fila.Indice, t.Semilla, t.Archivo, Segundos(t.Duracion), T("Toma {0}", n++)));
        fila.MensajeTomas = fila.Tomas.Count == 0 ? T("Aún no hay otras tomas de este bloque.") : null;
    }

    [RelayCommand]
    private async Task GenerarTomasAsync(FilaBloque fila)
    {
        if (_proyecto is null || Generando)
            return;
        Error = null;
        await GuardarYRefrescarAsync();
        var actual = Bloques.FirstOrDefault(f => f.Indice == fila.Indice) ?? fila;
        actual.MostrarTomas = true;
        if (await NarradorActualAsync() is not { } narrador)
        {
            Error = T("Elige una voz y comprueba que el motor está instalado.");
            return;
        }
        Reproductor.Detener();
        Generando = true;
        actual.GenerandoTomas = true;
        actual.MensajeTomas = T("Generando dos tomas nuevas…");
        _cancelacion = new CancellationTokenSource();
        try
        {
            var progreso = new Progress<EventoNarracion>(e => { Mensaje = e.Mensaje; Fraccion = e.Fraccion; });
            await narrador.GenerarTomasAsync(_proyecto, TextoGuion, actual.Indice, 2, progreso, _cancelacion.Token);
            CargarTomas(actual, narrador.TomasDe(_proyecto, TextoGuion, actual.Indice));
            Mensaje = T("Escucha las tomas y quédate con la que mejor suene.");
        }
        catch (OperationCanceledException)
        {
            actual.MensajeTomas = T("Cancelado.");
        }
        catch (Exception e)
        {
            Error = ServicioMotor.Explicar(e);
            actual.MensajeTomas = null;
        }
        finally
        {
            actual.GenerandoTomas = false;
            Generando = false;
            _cancelacion?.Dispose();
            _cancelacion = null;
        }
    }

    [RelayCommand]
    private void EscucharToma(FilaToma toma) => Alternar(toma.Archivo);

    /// <summary>La toma pasa a ser el audio del bloque (la anterior queda como otra toma) y se vuelve a montar el completo.</summary>
    [RelayCommand]
    private async Task UsarTomaAsync(FilaToma toma)
    {
        if (_proyecto is null || Generando || await NarradorActualAsync() is not { } narrador)
            return;
        Reproductor.Detener();
        Generando = true;
        Mensaje = T("Usando la toma elegida…");
        try
        {
            _proyecto = await narrador.ElegirTomaAsync(_proyecto, TextoGuion, toma.Indice, toma.Semilla, CancellationToken.None);
            var r = await narrador.NarrarAsync(_proyecto, TextoGuion, null, false, new Progress<EventoNarracion>(_ => { }), CancellationToken.None);
            _proyecto = r.Proyecto;
            Mensaje = T("Listo: el bloque usa la toma elegida.");
        }
        catch (Exception e)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Generando = false;
            await RefrescarBloquesAsync();
            if (Bloques.FirstOrDefault(f => f.Indice == toma.Indice) is { } fila)
            {
                fila.MostrarTomas = true;
                CargarTomas(fila, narrador.TomasDe(_proyecto!, TextoGuion, toma.Indice));
            }
        }
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
        var lista = string.Join(", ", voz.Variantes.Select(v => $"{v.Variante.Etiqueta} {v.Variante.Nombre.ToLowerInvariant()}"));
        AyudaVariantes = voz.Variantes.Count == 0
            ? T("Esta voz no tiene variantes: [whispers] se simula bajando el volumen. Puedes añadirle variantes en «Voces».")
            : tieneSusurro
                ? T("Variantes de esta voz: {0}.", lista)
                : T("Variantes de esta voz: {0}. [whispers] se simula bajando el volumen.", lista);
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
            Title = T("Borrar «{0}»", _proyecto.Nombre),
            Content = T("Se borrarán el guion y todos los audios generados del proyecto. No se puede deshacer."),
            PrimaryButtonText = T("Borrar"),
            CloseButtonText = T("Cancelar"),
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
            Title = T("Guardar el audio"),
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
            AvisoSalida = T("Guardado en {0}", dialogo.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AvisoSalida = T("No se ha podido guardar: {0}", e.Message);
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
            Title = T("Carpeta donde copiar los audios"),
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
            AvisoSalida = T("{0} audios copiados a {1}", archivos.Count, destino);
            Process.Start(new ProcessStartInfo(destino) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AvisoSalida = T("No se ha podido copiar: {0}", e.Message);
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
        AvisoSalida = T("«{0}» copiado: pégalo en una carpeta o en tu editor de vídeo.", Path.GetFileName(ruta));
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
            Error = Voces.Count == 0 ? T("Primero añade una voz en «Voces».") : T("Elige la voz con la que narrar.");
            return;
        }
        if (await _narracion.ModeloAsync(MotorVoz) is not { } modelo)
        {
            Error = MotorVoz == MotoresDeVoz.Qwen
                ? T("Falta una voz: instala la «Voz de alta calidad» desde Componentes.")
                : T("Falta el motor «{0}»: instálalo desde Componentes o elige el principal.", MotoresDeVoz.NombreVisible(MotorVoz));
            return;
        }

        // Un audio que suena no se puede sobrescribir.
        Reproductor.Detener();
        Generando = true;
        Fraccion = 0;
        Mensaje = T("Preparando…");
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
                ? (r.Completo is null ? T("No había nada que actualizar.") : T("Listo: audio completo actualizado."))
                : r.SinCambios > 0
                    ? T("Listo: {0} bloque(s) generado(s), {1} sin cambios.", r.Generados, r.SinCambios)
                    : T("Listo: {0} bloque(s) generado(s).", r.Generados);
            AvisosResultado = r.Avisos is { Count: > 0 } avisos ? string.Join("\n", avisos) : null;
        }
        catch (OperationCanceledException)
        {
            Mensaje = T("Cancelado. Lo ya generado se conserva.");
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

    private static string Segundos(double s) => $"{s.ToString("0.0", Idioma.Cultura)} s";

    private static string Duracion(double s) =>
        s < 60 ? Segundos(s) : $"{(int)(s / 60)} min {(int)(s % 60)} s";
}
