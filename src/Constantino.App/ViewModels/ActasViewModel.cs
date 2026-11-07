using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core;
using Constantino.Core.Actas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

/// <summary>Una persona de la reunión: su nombre en el acta, su voz para reconocerla y «unir con…».</summary>
public sealed partial class FilaParticipante(ParticipanteActa p, Brush color, Action<FilaParticipante> alCambiar,
    Action<FilaParticipante, FilaParticipante> alUnir) : ObservableObject
{
    public int Id { get; } = p.Id;
    public double MuestraInicio { get; } = p.MuestraInicio;
    public double MuestraFin { get; } = p.MuestraFin;
    public Brush Color { get; } = color;
    public ObservableCollection<FilaParticipante> Otras { get; } = [];

    [ObservableProperty] public partial string Nombre { get; set; } = p.Nombre;
    [ObservableProperty] public partial string Detalle { get; set; } = "";
    [ObservableProperty] public partial FilaParticipante? UnirCon { get; set; }

    partial void OnNombreChanged(string value) => alCambiar(this);

    partial void OnUnirConChanged(FilaParticipante? value)
    {
        if (value is not null)
            alUnir(this, value);
    }
}

/// <summary>Lo que dice una persona seguido: minuto, quién (cambiable) y el texto (corregible).</summary>
public sealed partial class FilaIntervencion(IntervencionActa i, Brush color, Action<FilaIntervencion> alCambiar) : ObservableObject
{
    public int Id { get; } = i.Id;
    public double Inicio { get; } = i.Inicio;
    public double Fin { get; } = i.Fin;
    public string Tiempo { get; } = Secretario.Reloj(i.Inicio);

    [ObservableProperty] public partial int Hablante { get; set; } = i.Hablante;
    [ObservableProperty] public partial string Texto { get; set; } = i.Texto;
    [ObservableProperty] public partial Brush Color { get; set; } = color;

    partial void OnHablanteChanged(int value) => alCambiar(this);

    partial void OnTextoChanged(string value) => alCambiar(this);
}

public sealed record OpcionActa(string Id, string Titulo);

/// <param name="Numero">Cuántas personas hablan; null: que lo decida Calíope.</param>
public sealed record OpcionPersonas(int? Numero, string Texto);

public sealed partial class ActasViewModel(ServicioActas actas, ServicioReproductor reproductor, INavigationService navegacion) : ObservableObject
{
    private static readonly Brush[] Colores = new[] { "#2B88D8", "#E3008C", "#10893E", "#F7630C", "#8764B8", "#C19C00", "#038387", "#CA5010" }
        .Select(c => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return (Brush)b; })
        .ToArray();

    private ProyectoActa? _proyecto;
    private AlmacenActas? _almacen;
    private CancellationTokenSource? _cancelacion;
    private bool _cargando;

    public ServicioReproductor Reproductor { get; } = reproductor;
    public ObservableCollection<OpcionActa> Proyectos { get; } = [];
    public ObservableCollection<FilaParticipante> Participantes { get; } = [];
    public ObservableCollection<FilaIntervencion> Intervenciones { get; } = [];

    public IReadOnlyList<OpcionPersonas> OpcionesPersonas { get; } =
        [new(null, T("Que lo averigüe Calíope")), .. Enumerable.Range(2, 7).Select(n => new OpcionPersonas(n, T("{0} personas", n)))];

    [ObservableProperty] public partial string? Faltan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayProyecto))]
    public partial OpcionActa? ProyectoSeleccionado { get; set; }

    public bool HayProyecto => ProyectoSeleccionado is not null;

    [ObservableProperty] public partial string Titulo { get; set; } = "";
    [ObservableProperty] public partial DateTime? Fecha { get; set; }
    [ObservableProperty] public partial OpcionPersonas? Personas { get; set; }
    [ObservableProperty] public partial string Glosario { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayActa))]
    public partial string? Acta { get; set; }

    public bool HayActa => !string.IsNullOrWhiteSpace(Acta);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayTranscripcion))]
    public partial int NumeroIntervenciones { get; set; }

    public bool HayTranscripcion => NumeroIntervenciones > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Ocupado { get; set; }

    public bool Libre => !Ocupado;

    [ObservableProperty] public partial string? Mensaje { get; set; }
    [ObservableProperty] public partial double Fraccion { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Resumen { get; set; }

    public async Task CargarAsync()
    {
        var faltan = await actas.FaltanAsync();
        Faltan = faltan.Count == 0 ? null : T("Para hacer actas falta instalar: {0}.", string.Join(", ", faltan));
        _almacen = await actas.AlmacenAsync();
        var actual = ProyectoSeleccionado?.Id;
        Proyectos.Clear();
        foreach (var p in _almacen.Listar())
            Proyectos.Add(new OpcionActa(p.Id, p.Titulo));
        ProyectoSeleccionado = Proyectos.FirstOrDefault(p => p.Id == actual) ?? Proyectos.FirstOrDefault();
    }

    partial void OnProyectoSeleccionadoChanged(OpcionActa? value) => Mostrar(value is null ? null : _almacen?.Abrir(value.Id));

    private static Brush ColorDe(int persona) => Colores[(Math.Max(persona, 1) - 1) % Colores.Length];

    private void Mostrar(ProyectoActa? p)
    {
        _cargando = true;
        _proyecto = p;
        Participantes.Clear();
        Intervenciones.Clear();
        if (p is not null && _almacen is not null)
        {
            Titulo = p.Titulo;
            Fecha = p.Fecha.ToDateTime(TimeOnly.MinValue);
            Personas = OpcionesPersonas.FirstOrDefault(o => o.Numero == p.Personas) ?? OpcionesPersonas[0];
            Glosario = p.Glosario;
            foreach (var x in p.ListaParticipantes)
                Participantes.Add(new FilaParticipante(x, ColorDe(x.Id), ParticipanteCambiado, (origen, destino) => _ = UnirAsync(origen, destino)));
            foreach (var f in Participantes)
            {
                foreach (var otra in Participantes.Where(o => o != f))
                    f.Otras.Add(otra);
            }
            foreach (var i in p.ListaIntervenciones)
                Intervenciones.Add(new FilaIntervencion(i, ColorDe(i.Hablante), IntervencionCambiada));
            Acta = _almacen.LeerActa(p.Id);
        }
        else
        {
            Acta = null;
        }
        NumeroIntervenciones = Intervenciones.Count;
        ActualizarResumen();
        _cargando = false;
    }

    private void ActualizarResumen()
    {
        if (_proyecto is not { } p)
        {
            Resumen = null;
            return;
        }
        foreach (var f in Participantes)
        {
            var suyas = p.ListaIntervenciones.Where(i => i.Hablante == f.Id).ToList();
            f.Detalle = T("{0} intervenciones · {1}", suyas.Count, TimeSpan.FromSeconds(suyas.Sum(i => i.Fin - i.Inicio)).ToString(@"m\:ss"));
        }
        Resumen = p.Fase switch
        {
            FaseActa.Nueva => T("Grabación de {0}. Revisa la fecha y, si quieres, añade un glosario; después pulsa «Transcribir».",
                TimeSpan.FromSeconds(p.Duracion).ToString(@"h\:mm\:ss")),
            FaseActa.Transcrita => T("Transcrita: {0} personas y {1} intervenciones. Revisa quién es cada persona y pulsa «Redactar el acta».",
                p.ListaParticipantes.Count, p.ListaIntervenciones.Count),
            _ => T("Acta redactada. Puedes corregirla aquí mismo y guardarla en Word."),
        };
    }

    partial void OnTituloChanged(string value) => GuardarDatos();
    partial void OnFechaChanged(DateTime? value) => GuardarDatos();
    partial void OnPersonasChanged(OpcionPersonas? value) => GuardarDatos();
    partial void OnGlosarioChanged(string value) => GuardarDatos();

    private void GuardarDatos()
    {
        if (_cargando || _proyecto is null || _almacen is null)
            return;
        _proyecto = _almacen.Guardar(_proyecto with
        {
            Titulo = string.IsNullOrWhiteSpace(Titulo) ? _proyecto.Titulo : Titulo.Trim(),
            Fecha = Fecha is { } f ? DateOnly.FromDateTime(f) : _proyecto.Fecha,
            Personas = Personas?.Numero,
            Glosario = Glosario ?? "",
        });
        if (ProyectoSeleccionado is { } sel && sel.Titulo != _proyecto.Titulo)
        {
            var i = Proyectos.IndexOf(sel);
            _cargando = true;
            Proyectos[i] = new OpcionActa(_proyecto.Id, _proyecto.Titulo);
            ProyectoSeleccionado = Proyectos[i];
            _cargando = false;
        }
    }

    partial void OnActaChanged(string? value)
    {
        if (!_cargando && _proyecto is not null && _almacen is not null && value is not null)
            _almacen.GuardarActa(_proyecto.Id, value);
    }

    private void ParticipanteCambiado(FilaParticipante fila)
    {
        if (_cargando || _proyecto is null || _almacen is null || string.IsNullOrWhiteSpace(fila.Nombre))
            return;
        _proyecto = _almacen.Guardar(_proyecto with
        {
            Participantes = _proyecto.ListaParticipantes.Select(x => x.Id == fila.Id ? x with { Nombre = fila.Nombre.Trim() } : x).ToList(),
        });
        // Los desplegables de las intervenciones muestran el nombre: se refrescan.
        Mostrar(_proyecto);
    }

    private void IntervencionCambiada(FilaIntervencion fila)
    {
        if (_cargando || _proyecto is null || _almacen is null)
            return;
        var i = _proyecto.ListaIntervenciones.First(x => x.Id == fila.Id);
        _proyecto = _almacen.Guardar(_proyecto.ConIntervencion(i with { Hablante = fila.Hablante, Texto = fila.Texto.Trim() }));
        fila.Color = ColorDe(fila.Hablante);
        ActualizarResumen();
    }

    private async Task UnirAsync(FilaParticipante origen, FilaParticipante destino)
    {
        if (_cargando || _proyecto is null || Ocupado)
            return;
        var caja = new Wpf.Ui.Controls.MessageBox
        {
            Title = T("Unir personas"),
            Content = T("Las intervenciones de «{0}» pasarán a «{1}». ¿Son la misma persona?", origen.Nombre, destino.Nombre),
            PrimaryButtonText = T("Unir"),
            CloseButtonText = T("Cancelar"),
        };
        if (await caja.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            origen.UnirCon = null;
            return;
        }
        await using var sesion = await actas.AbrirAsync();
        Mostrar(sesion.Secretario.UnirParticipantes(_proyecto, origen.Id, destino.Id));
    }

    [RelayCommand]
    private void IrAComponentes() => navegacion.Navigate(typeof(PaginaComponentes));

    [RelayCommand]
    private async Task NuevaAsync()
    {
        var dialogo = new OpenFileDialog
        {
            Title = T("Elige la grabación de la reunión"),
            Filter = T("Audio y vídeo") + "|*.mp3;*.m4a;*.wav;*.ogg;*.opus;*.flac;*.aac;*.wma;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v",
        };
        if (dialogo.ShowDialog() != true || _almacen is null)
            return;
        Error = null;
        try
        {
            var duracion = await actas.DuracionAsync(dialogo.FileName);
            // La fecha de la reunión, la de la grabación (se puede cambiar).
            var fecha = DateOnly.FromDateTime(File.GetLastWriteTime(dialogo.FileName));
            var p = _almacen.Crear(dialogo.FileName, duracion, fecha);
            Proyectos.Insert(0, new OpcionActa(p.Id, p.Titulo));
            ProyectoSeleccionado = Proyectos[0];
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Pasa la reunión a texto, separa a las personas y propone sus nombres.</summary>
    [RelayCommand]
    private async Task TranscribirAsync()
    {
        if (_proyecto is null)
            return;
        if (_proyecto.ListaIntervenciones.Count > 0)
        {
            var caja = new Wpf.Ui.Controls.MessageBox
            {
                Title = T("Volver a transcribir"),
                Content = T("Se rehará la transcripción y se perderán los nombres y las correcciones. El acta ya redactada se conserva hasta que la vuelvas a redactar."),
                PrimaryButtonText = T("Transcribir"),
                CloseButtonText = T("Cancelar"),
            };
            if (await caja.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
                return;
        }
        await TrabajarAsync(async (sesion, progreso, ct) =>
        {
            var p = await sesion.Secretario.TranscribirAsync(_proyecto!, progreso, ct);
            EnPantalla(() => Mostrar(p));
            p = await sesion.Secretario.ProponerNombresAsync(p, progreso, ct);
            EnPantalla(() =>
            {
                Mostrar(p);
                Mensaje = T("Transcrita. Revisa los nombres que ha propuesto la IA (puede equivocarse) y pulsa «Redactar el acta».");
            });
        });
    }

    [RelayCommand]
    private Task RedactarAsync() => TrabajarAsync(async (sesion, progreso, ct) =>
    {
        var (p, _) = await sesion.Secretario.RedactarAsync(_proyecto!, progreso, ct);
        EnPantalla(() =>
        {
            Mostrar(p);
            Mensaje = T("Acta lista. Revísala: cada punto lleva entre corchetes el minuto en que se dijo, para comprobarlo en la transcripción.");
        });
    });

    [RelayCommand]
    private void Cancelar() => _cancelacion?.Cancel();

    [RelayCommand]
    private async Task EscucharParticipanteAsync(FilaParticipante fila) => await EscucharAsync(fila.MuestraInicio, fila.MuestraFin);

    [RelayCommand]
    private async Task EscucharIntervencionAsync(FilaIntervencion fila) => await EscucharAsync(fila.Inicio, fila.Fin);

    private async Task EscucharAsync(double inicio, double fin)
    {
        if (_proyecto is null || _almacen is null)
            return;
        if (await actas.TrozoAsync(_almacen, _proyecto, inicio, fin) is not { } trozo)
            return;
        if (Reproductor.Sonando == trozo)
            Reproductor.Detener();
        else
            Reproductor.Reproducir(trozo);
    }

    /// <summary>Guarda el acta en Word, Markdown o texto.</summary>
    [RelayCommand]
    private void GuardarActa()
    {
        if (_proyecto is null || string.IsNullOrWhiteSpace(Acta))
            return;
        var dialogo = new SaveFileDialog
        {
            Title = T("Guardar el acta"),
            FileName = Textos.NombreDeArchivo($"{T("Acta")} {_proyecto.Titulo}", "acta"),
            Filter = T("Documento de Word") + " (*.docx)|*.docx|Markdown (*.md)|*.md|" + T("Texto") + " (*.txt)|*.txt",
        };
        if (dialogo.ShowDialog() != true)
            return;
        try
        {
            switch (Path.GetExtension(dialogo.FileName).ToLowerInvariant())
            {
                case ".docx":
                    DocumentoWord.Guardar(Acta, dialogo.FileName);
                    break;
                case ".md":
                    File.WriteAllText(dialogo.FileName, Acta);
                    break;
                default:
                    File.WriteAllText(dialogo.FileName, ATexto(Acta));
                    break;
            }
            Mensaje = T("Acta guardada en {0}", dialogo.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Guarda la transcripción completa (minuto, quién y qué dijo) en un texto.</summary>
    [RelayCommand]
    private void GuardarTranscripcion()
    {
        if (_proyecto is null || _proyecto.ListaIntervenciones.Count == 0)
            return;
        var dialogo = new SaveFileDialog
        {
            Title = T("Guardar la transcripción"),
            FileName = Textos.NombreDeArchivo($"{T("Transcripción")} {_proyecto.Titulo}", "transcripcion"),
            Filter = T("Texto") + " (*.txt)|*.txt",
        };
        if (dialogo.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dialogo.FileName, Secretario.Transcripcion(_proyecto) + "\n");
            Mensaje = T("Transcripción guardada en {0}", dialogo.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
    }

    [RelayCommand]
    private void CopiarActa()
    {
        if (!string.IsNullOrWhiteSpace(Acta))
        {
            Clipboard.SetText(Acta);
            Mensaje = T("Acta copiada: puedes pegarla en un correo o en un documento.");
        }
    }

    /// <summary>El acta sin las marcas de Markdown, para un texto sencillo.</summary>
    private static string ATexto(string markdown) =>
        Regex.Replace(Regex.Replace(markdown, @"^#{1,6}\s+", "", RegexOptions.Multiline), @"\*\*(.+?)\*\*", "$1");

    [RelayCommand]
    private void AbrirCarpeta()
    {
        if (_proyecto is not null && _almacen is not null)
            Process.Start(new ProcessStartInfo(_almacen.CarpetaDe(_proyecto.Id)) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task BorrarAsync()
    {
        if (_proyecto is null || _almacen is null)
            return;
        var caja = new Wpf.Ui.Controls.MessageBox
        {
            Title = T("Borrar el acta"),
            Content = T("Se borrará «{0}» con su transcripción y su acta. La grabación original no se toca.", _proyecto.Titulo),
            PrimaryButtonText = T("Borrar"),
            CloseButtonText = T("Cancelar"),
        };
        if (await caja.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;
        Reproductor.Detener();
        _almacen.Borrar(_proyecto.Id);
        await CargarAsync();
    }

    private static void EnPantalla(Action accion) => Application.Current.Dispatcher.Invoke(accion);

    private async Task TrabajarAsync(Func<ServicioActas.Sesion, IProgress<EventoActa>, CancellationToken, Task> trabajo)
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
        var progreso = new Progress<EventoActa>(e =>
        {
            Mensaje = e.Mensaje;
            if (e.Fraccion is { } f)
                Fraccion = f;
        });
        try
        {
            await using var sesion = await actas.AbrirAsync();
            await Task.Run(() => trabajo(sesion, progreso, _cancelacion.Token));
        }
        catch (OperationCanceledException)
        {
            Mensaje = T("Cancelado. Lo hecho se conserva.");
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
