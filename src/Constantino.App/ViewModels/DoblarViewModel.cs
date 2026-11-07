using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.Doblaje;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>Una frase del doblaje en la lista: quién la dice, el original, la traducción (editable) y si cabe en su tiempo.</summary>
public sealed partial class FilaFrase(FraseDoblaje f, string archivo, Brush color, Action<FilaFrase> alCambiar,
    Action<FilaFrase> alCambiarHablante) : ObservableObject
{
    public int Id { get; } = f.Id;
    public string Tiempo { get; } = TimeSpan.FromSeconds(f.Inicio).ToString(@"m\:ss");
    public string Original { get; } = f.Original;
    public string Archivo { get; } = archivo;

    [ObservableProperty] public partial string Traduccion { get; set; } = f.Traduccion;
    [ObservableProperty] public partial int Hablante { get; set; } = f.Hablante;
    [ObservableProperty] public partial Brush Color { get; set; } = color;
    [ObservableProperty] public partial string Estado { get; set; } = TextoEncaje(f);
    [ObservableProperty] public partial ControlAppearance Apariencia { get; set; } = AparienciaDe(f.Encaje);
    [ObservableProperty] public partial bool Generada { get; set; } = f.Encaje != EncajeFrase.SinGenerar && File.Exists(archivo);

    partial void OnTraduccionChanged(string value) => alCambiar(this);

    partial void OnHablanteChanged(int value) => alCambiarHablante(this);

    public void Actualizar(FraseDoblaje f)
    {
        Estado = TextoEncaje(f);
        Apariencia = AparienciaDe(f.Encaje);
        Generada = f.Encaje != EncajeFrase.SinGenerar && File.Exists(Archivo);
    }

    private static string TextoEncaje(FraseDoblaje f) => f.Encaje switch
    {
        EncajeFrase.Cabe => T("Cabe"),
        EncajeFrase.Justa => T("Justa ({0:0.00}×)", f.Tempo),
        EncajeFrase.NoCabe => T("No cabe"),
        _ => T("Sin doblar"),
    };

    private static ControlAppearance AparienciaDe(EncajeFrase e) => e switch
    {
        EncajeFrase.Cabe => ControlAppearance.Success,
        EncajeFrase.Justa => ControlAppearance.Caution,
        EncajeFrase.NoCabe => ControlAppearance.Danger,
        _ => ControlAppearance.Secondary,
    };
}

/// <summary>Una persona del vídeo: su nombre, su voz original, la voz que le da la pronunciación y «unir con…».</summary>
public sealed partial class FilaHablante(HablanteDoblaje h, string referencia, Brush color, Action<FilaHablante> alCambiar,
    Action<FilaHablante, FilaHablante> alUnir) : ObservableObject
{
    public int Id { get; } = h.Id;
    public string Referencia { get; } = referencia;
    public Brush Color { get; } = color;

    /// <summary>Las demás personas, para «unir con…».</summary>
    public ObservableCollection<FilaHablante> Otras { get; } = [];

    [ObservableProperty] public partial string Nombre { get; set; } = h.Nombre;
    [ObservableProperty] public partial string? VozPronunciacion { get; set; } = h.VozPronunciacion;
    [ObservableProperty] public partial string Detalle { get; set; } = "";
    [ObservableProperty] public partial FilaHablante? UnirCon { get; set; }

    partial void OnNombreChanged(string value) => alCambiar(this);

    partial void OnVozPronunciacionChanged(string? value) => alCambiar(this);

    partial void OnUnirConChanged(FilaHablante? value)
    {
        if (value is not null)
            alUnir(this, value);
    }
}

public sealed record OpcionDoblaje(string Id, string Nombre);

public sealed partial class DoblarViewModel(ServicioDoblaje doblaje, ServicioVoces voces, ServicioReproductor reproductor,
    INavigationService navegacion) : ObservableObject
{
    /// <summary>Un color por persona, en la lista de frases y en el panel de personas.</summary>
    private static readonly Brush[] Colores = new[] { "#2B88D8", "#E3008C", "#10893E", "#F7630C", "#8764B8", "#C19C00" }
        .Select(c => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return (Brush)b; })
        .ToArray();

    private ProyectoDoblaje? _proyecto;
    private AlmacenDoblajes? _almacen;
    private CancellationTokenSource? _cancelacion;
    private bool _cargando;

    public ServicioReproductor Reproductor { get; } = reproductor;
    public ObservableCollection<OpcionDoblaje> Proyectos { get; } = [];
    public ObservableCollection<OpcionVoz> Voces { get; } = [];
    public ObservableCollection<FilaFrase> Frases { get; } = [];
    public ObservableCollection<FilaHablante> Hablantes { get; } = [];

    [ObservableProperty] public partial string? Faltan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayProyecto))]
    public partial OpcionDoblaje? ProyectoSeleccionado { get; set; }

    public bool HayProyecto => ProyectoSeleccionado is not null;

    [ObservableProperty] public partial bool VozOriginalDeFondo { get; set; }
    [ObservableProperty] public partial string? ResumenHablantes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Ocupado { get; set; }

    public bool Libre => !Ocupado;

    [ObservableProperty] public partial string? Mensaje { get; set; }
    [ObservableProperty] public partial double Fraccion { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Resumen { get; set; }
    [ObservableProperty] public partial string? VideoExportado { get; set; }

    /// <summary>La voz de «Mis voces» que da la pronunciación a las personas nuevas: la predeterminada.</summary>
    private string? VozPorDefecto => voces.Predeterminada ?? Voces.FirstOrDefault()?.Id;

    public async Task CargarAsync()
    {
        var faltan = await doblaje.FaltanAsync();
        Faltan = faltan.Count == 0 ? null : T("Para doblar vídeos falta instalar: {0}.", string.Join(", ", faltan));
        _almacen = await doblaje.AlmacenAsync();

        var biblioteca = await voces.BibliotecaAsync();
        Voces.Clear();
        foreach (var v in biblioteca.Listar().Where(v => v.Idioma == "Spanish"))
            Voces.Add(new OpcionVoz(v.Id, v.Nombre));

        var actual = ProyectoSeleccionado?.Id;
        Proyectos.Clear();
        foreach (var p in _almacen.Listar())
            Proyectos.Add(new OpcionDoblaje(p.Id, p.Nombre));
        ProyectoSeleccionado = Proyectos.FirstOrDefault(p => p.Id == actual) ?? Proyectos.FirstOrDefault();
    }

    partial void OnProyectoSeleccionadoChanged(OpcionDoblaje? value) => Mostrar(value is null ? null : _almacen?.Abrir(value.Id));

    private static Brush ColorDe(int hablante) => Colores[(Math.Max(hablante, 1) - 1) % Colores.Length];

    private void Mostrar(ProyectoDoblaje? p)
    {
        _cargando = true;
        _proyecto = p;
        Frases.Clear();
        Hablantes.Clear();
        // El botón «Ver el vídeo doblado» aparece siempre que exista, también al volver a abrir el proyecto.
        VideoExportado = p is not null && _almacen is not null && File.Exists(_almacen.Exportado(p, "mp4")) ? _almacen.Exportado(p, "mp4") : null;
        if (p is not null && _almacen is not null)
        {
            foreach (var h in p.ListaHablantes)
            {
                Hablantes.Add(new FilaHablante(h, Path.Combine(_almacen.Trabajo(p.Id), $"referencia_{h.Id}.wav"), ColorDe(h.Id),
                    HablanteCambiado, (origen, destino) => _ = UnirAsync(origen, destino)));
            }
            foreach (var h in Hablantes)
            {
                foreach (var otra in Hablantes.Where(o => o != h))
                    h.Otras.Add(otra);
            }
            foreach (var f in p.ListaFrases)
            {
                Frases.Add(new FilaFrase(f, Path.Combine(_almacen.Frases(p.Id), $"{f.Id:000}.wav"), ColorDe(f.Hablante),
                    TraduccionCambiada, HablanteDeFraseCambiado));
            }
            VozOriginalDeFondo = p.VozOriginalDeFondo;
        }
        ActualizarResumen();
        _cargando = false;
    }

    private void ActualizarResumen()
    {
        if (_proyecto is not { } p)
        {
            Resumen = ResumenHablantes = null;
            return;
        }
        foreach (var h in Hablantes)
        {
            var suyas = p.ListaFrases.Where(f => f.Hablante == h.Id).ToList();
            h.Detalle = T("{0} frases · {1} de voz", suyas.Count, TimeSpan.FromSeconds(suyas.Sum(f => f.Hueco)).ToString(@"m\:ss"));
        }
        ResumenHablantes = Hablantes.Count switch
        {
            0 => null,
            1 => T("Calíope ha encontrado una persona en el vídeo. Si hablan varias y no las ha distinguido, pulsa «Volver a distinguir a las personas»."),
            _ => T("Calíope ha distinguido {0} personas: cada una se dobla con su propia voz. Ponles nombre, escucha su voz original y, si dos son la misma, únelas.", Hablantes.Count),
        };
        if (p.ListaFrases.Count == 0)
        {
            Resumen = T("Vídeo de {0}. Pulsa «Doblar vídeo» para empezar.", TimeSpan.FromSeconds(p.Duracion).ToString(@"m\:ss"));
            return;
        }
        var f = p.ListaFrases;
        Resumen = T("{0} frases · {1} caben · {2} justas · {3} no caben · {4} sin doblar", f.Count,
            f.Count(x => x.Encaje == EncajeFrase.Cabe), f.Count(x => x.Encaje == EncajeFrase.Justa),
            f.Count(x => x.Encaje == EncajeFrase.NoCabe), f.Count(x => x.Encaje == EncajeFrase.SinGenerar));
    }

    private void TraduccionCambiada(FilaFrase fila)
    {
        if (_cargando || _proyecto is null || _almacen is null)
            return;
        var f = _proyecto.ListaFrases.First(x => x.Id == fila.Id);
        if (f.Traduccion == fila.Traduccion)
            return;
        // Cambiar el texto deja la frase pendiente de volver a doblar (su huella ya no coincide).
        _proyecto = _almacen.Guardar(_proyecto.ConFrase(f with { Traduccion = fila.Traduccion, Encaje = EncajeFrase.SinGenerar }));
        fila.Actualizar(_proyecto.ListaFrases.First(x => x.Id == fila.Id));
        ActualizarResumen();
    }

    /// <summary>La frase pasa a otra persona: se doblará con su voz.</summary>
    private void HablanteDeFraseCambiado(FilaFrase fila)
    {
        if (_cargando || _proyecto is null || _almacen is null)
            return;
        var f = _proyecto.ListaFrases.First(x => x.Id == fila.Id);
        if (f.Hablante == fila.Hablante)
            return;
        _proyecto = _almacen.Guardar(_proyecto.ConFrase(f with { Hablante = fila.Hablante, Encaje = EncajeFrase.SinGenerar }));
        fila.Color = ColorDe(fila.Hablante);
        fila.Actualizar(_proyecto.ListaFrases.First(x => x.Id == fila.Id));
        ActualizarResumen();
    }

    /// <summary>Nombre o voz de pronunciación de una persona. Otra voz deja sus frases pendientes de volver a doblar.</summary>
    private void HablanteCambiado(FilaHablante fila)
    {
        if (_cargando || _proyecto is null || _almacen is null)
            return;
        var antes = _proyecto.ListaHablantes.First(h => h.Id == fila.Id);
        var ahora = antes with { Nombre = string.IsNullOrWhiteSpace(fila.Nombre) ? antes.Nombre : fila.Nombre.Trim(), VozPronunciacion = fila.VozPronunciacion };
        var p = _proyecto with { Hablantes = _proyecto.ListaHablantes.Select(h => h.Id == fila.Id ? ahora : h).ToList() };
        if (ahora.VozPronunciacion != antes.VozPronunciacion)
        {
            p = p with { Frases = p.ListaFrases.Select(f => f.Hablante == fila.Id ? f with { Encaje = EncajeFrase.SinGenerar } : f).ToList() };
            foreach (var f in Frases.Where(x => x.Hablante == fila.Id))
                f.Actualizar(p.ListaFrases.First(x => x.Id == f.Id));
        }
        _proyecto = _almacen.Guardar(p);
        // Los desplegables de las frases muestran el nombre: se refrescan.
        if (ahora.Nombre != antes.Nombre)
            Mostrar(_proyecto);
        else
            ActualizarResumen();
    }

    private async Task UnirAsync(FilaHablante origen, FilaHablante destino)
    {
        if (_cargando || _proyecto is null || Ocupado)
            return;
        var caja = new Wpf.Ui.Controls.MessageBox
        {
            Title = T("Unir personas"),
            Content = T("Las frases de «{0}» pasarán a «{1}» y se doblarán con su voz. ¿Son la misma persona?", origen.Nombre, destino.Nombre),
            PrimaryButtonText = T("Unir"),
            CloseButtonText = T("Cancelar"),
        };
        if (await caja.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            origen.UnirCon = null;
            return;
        }
        await using var sesion = await doblaje.AbrirAsync();
        Mostrar(sesion.Doblador.UnirHablantes(_proyecto, origen.Id, destino.Id));
        Mensaje = T("Unidas. Pulsa «Doblar vídeo» para rehacer sus frases con la misma voz.");
    }

    partial void OnVozOriginalDeFondoChanged(bool value)
    {
        if (!_cargando && _proyecto is not null && _almacen is not null)
            _proyecto = _almacen.Guardar(_proyecto with { VozOriginalDeFondo = value });
    }

    [RelayCommand]
    private void IrAComponentes() => navegacion.Navigate(typeof(PaginaComponentes));

    [RelayCommand]
    private async Task NuevoAsync()
    {
        var dialogo = new OpenFileDialog
        {
            Title = T("Elige el vídeo que quieres doblar"),
            Filter = T("Vídeos") + "|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v",
        };
        if (dialogo.ShowDialog() != true || _almacen is null)
            return;
        Error = null;
        try
        {
            var duracion = await doblaje.DuracionAsync(dialogo.FileName);
            var p = _almacen.Crear(dialogo.FileName, duracion);
            Proyectos.Insert(0, new OpcionDoblaje(p.Id, p.Nombre));
            ProyectoSeleccionado = Proyectos[0];
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Todo de una vez: guion, frases (solo las que falten o hayan cambiado) y exportación.</summary>
    [RelayCommand]
    private Task DoblarAsync() => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var p = await sesion.Doblador.PrepararAsync(_proyecto!, VozPorDefecto, progreso, ct);
        EnPantalla(() => Mostrar(p));
        p = await sesion.Doblador.GenerarAsync(p, RutaVoz, null, progreso, ct);
        EnPantalla(() => Mostrar(p));
        var r = await sesion.Doblador.ExportarAsync(p, progreso, ct);
        EnPantalla(() =>
        {
            VideoExportado = r.Video;
            Mensaje = T("Listo: el vídeo doblado está en la carpeta «Audio» del proyecto.");
        });
    });

    /// <summary>Solo hasta el guion traducido, para revisarlo antes de generar las voces.</summary>
    [RelayCommand]
    private Task PrepararAsync() => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var p = await sesion.Doblador.PrepararAsync(_proyecto!, VozPorDefecto, progreso, ct);
        EnPantalla(() =>
        {
            Mostrar(p);
            Mensaje = T("Guion listo: revisa la traducción y las personas, y pulsa «Doblar vídeo».");
        });
    });

    /// <summary>Vuelve a distinguir quién habla (las traducciones se conservan).</summary>
    [RelayCommand]
    private Task DistinguirPersonasAsync() => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var p = await sesion.Doblador.DetectarHablantesAsync(_proyecto!, VozPorDefecto, progreso, ct);
        EnPantalla(() =>
        {
            Mostrar(p);
            Mensaje = p.ListaHablantes.Count == 1
                ? T("Solo se oye a una persona.")
                : T("Distinguidas {0} personas. Revisa sus nombres y pulsa «Doblar vídeo».", p.ListaHablantes.Count);
        });
    });

    [RelayCommand]
    private Task RegenerarAsync(FilaFrase fila) => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var p = await sesion.Doblador.GenerarAsync(_proyecto!, RutaVoz, [fila.Id], progreso, ct);
        EnPantalla(() =>
        {
            Mostrar(p);
            Mensaje = T("Frase rehecha. Pulsa «Doblar vídeo» para volver a exportar.");
        });
    });

    [RelayCommand]
    private void Escuchar(FilaFrase fila) => Alternar(fila.Archivo);

    /// <summary>La voz original de una persona (el tramo del que se clona su timbre).</summary>
    [RelayCommand]
    private void EscucharHablante(FilaHablante fila) => Alternar(fila.Referencia);

    private void Alternar(string archivo)
    {
        if (Reproductor.Sonando == archivo)
            Reproductor.Detener();
        else if (File.Exists(archivo))
            Reproductor.Reproducir(archivo);
    }

    [RelayCommand]
    private void Cancelar() => _cancelacion?.Cancel();

    [RelayCommand]
    private void AbrirCarpeta()
    {
        if (_proyecto is not null && _almacen is not null)
        {
            var carpeta = Directory.Exists(_almacen.Salida(_proyecto.Id)) ? _almacen.Salida(_proyecto.Id) : _almacen.CarpetaDe(_proyecto.Id);
            Process.Start(new ProcessStartInfo(carpeta) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void AbrirVideo()
    {
        if (VideoExportado is not null && File.Exists(VideoExportado))
            Process.Start(new ProcessStartInfo(VideoExportado) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task BorrarAsync()
    {
        if (_proyecto is null || _almacen is null)
            return;
        var caja = new Wpf.Ui.Controls.MessageBox
        {
            Title = T("Borrar el doblaje"),
            Content = T("Se borrará el proyecto «{0}» con sus frases dobladas y lo exportado. El vídeo original no se toca.", _proyecto.Nombre),
            PrimaryButtonText = T("Borrar"),
            CloseButtonText = T("Cancelar"),
        };
        if (await caja.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;
        Reproductor.Detener();
        _almacen.Borrar(_proyecto.Id);
        await CargarAsync();
    }

    /// <summary>El trabajo va en segundo plano; la lista de frases solo se toca desde la ventana.</summary>
    private static void EnPantalla(Action accion) => System.Windows.Application.Current.Dispatcher.Invoke(accion);

    private string RutaVoz(string? id) => doblaje.RutaVozAsync(id).GetAwaiter().GetResult();

    private async Task TrabajarAsync(Func<ServicioDoblaje.Sesion, IProgress<EventoDoblaje>, CancellationToken, Task> trabajo)
    {
        if (_proyecto is null || Ocupado)
            return;
        if (Faltan is not null)
        {
            Error = Faltan;
            return;
        }
        Reproductor.Detener();
        Error = null;
        Ocupado = true;
        Fraccion = 0;
        _cancelacion = new CancellationTokenSource();
        var progreso = new Progress<EventoDoblaje>(e =>
        {
            Mensaje = e.Mensaje;
            if (e.Fraccion is { } f)
                Fraccion = f;
        });
        try
        {
            await using var sesion = await doblaje.AbrirAsync();
            await Task.Run(() => trabajo(sesion, progreso, _cancelacion.Token));
        }
        catch (OperationCanceledException)
        {
            Mensaje = T("Cancelado. Lo hecho se conserva: puedes seguir cuando quieras.");
        }
        catch (Exception e)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Ocupado = false;
            _cancelacion.Dispose();
            _cancelacion = null;
            if (_proyecto is not null && _almacen?.Abrir(_proyecto.Id) is { } actual)
                Mostrar(actual);
        }
    }
}
