using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Constantino.App.Paginas;
using Constantino.App.Servicios;
using Constantino.Core.IA;
using Constantino.Core.Motores;
using Constantino.Core.Voces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using static Constantino.Core.Idioma;

namespace Constantino.App.ViewModels;

public sealed record OpcionSexo(SexoVoz Valor, string Nombre);

public sealed record EjemploDiseno(string Nombre, DescripcionGuiada Descripcion);

/// <summary>De dónde sale la pronunciación de España: una voz de la biblioteca o (Ruta vacía) la de serie del motor castellano.</summary>
public sealed record OpcionPronunciacion(string Ruta, string Nombre);

/// <summary>Un botón de «Más así» en una variante concreta.</summary>
public sealed record CambioDeVariante(VarianteVoz Variante, OpcionCambio Opcion)
{
    public string Nombre => Opcion.Nombre;
}

/// <summary>Una voz candidata: el mismo texto leído con otra semilla.</summary>
/// <param name="etiqueta">De dónde sale, si viene de «Más así» («de la 2: más grave, un poco»).</param>
public sealed partial class VarianteVoz(int numero, int semilla, string ruta, double duracion, string instruccion, string idioma, string texto,
    string? etiqueta = null) : ObservableObject
{
    public int Numero { get; } = numero;
    public int Semilla { get; } = semilla;
    /// <summary>La grabación de la voz: la del diseñador o, con acento de España, la que sale del motor castellano.</summary>
    public string Ruta { get; set; } = ruta;

    /// <summary>La grabación tal como la inventó el diseñador (con su seseo).</summary>
    public string RutaDiseno { get; } = ruta;

    /// <summary>Ha pasado por el motor castellano: tiene acento de España.</summary>
    public bool AcentoEspana { get; set; }
    public double Duracion { get; } = duracion;
    public string Instruccion { get; } = instruccion;
    public string Idioma { get; } = idioma;
    public string Texto { get; } = texto;
    public string Titulo => etiqueta is null ? T("Variante {0}", Numero) : T("Variante {0} · {1}", Numero, etiqueta);
    public string Detalle => T("Semilla {0} · {1:0.0} s", Semilla, Duracion);

    /// <summary>Tono esperado según lo pedido (null: no se comprueba).</summary>
    public (double Min, double Max)? Rango { get; init; }

    /// <summary>Lo que ha medido el motor (null si no se ha revisado).</summary>
    public MedidasVoz? Medidas { get; set; }

    public IReadOnlyList<CambioDeVariante> Cambios => _cambios ??= RevisionDiseno.Cambios.Select(c => new CambioDeVariante(this, c)).ToList();
    private IReadOnlyList<CambioDeVariante>? _cambios;

    /// <summary>«Tono 112 Hz · 160 palabras por minuto · lee el texto exacto».</summary>
    [ObservableProperty] public partial string? Revision { get; set; }

    /// <summary>Lo que no encaja, si algo no encaja.</summary>
    [ObservableProperty] public partial string? Avisos { get; set; }

    /// <summary>El párrafo de narración leído con la voz ya clonada (null hasta que se pide).</summary>
    [ObservableProperty] public partial string? Narracion { get; set; }

    [ObservableProperty] public partial bool Narrando { get; set; }
    [ObservableProperty] public partial bool MostrarCambios { get; set; }
    [ObservableProperty] public partial bool Guardando { get; set; }
    [ObservableProperty] public partial string Nombre { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Guardada { get; set; }
}

public sealed partial class DisenarViewModel : ObservableObject
{
    private readonly ServicioMotor _motor;
    private readonly ServicioVoces _voces;
    private readonly ServicioInstalacion _instalacion;
    private readonly INavigationService _navegacion;
    private string _ultimaCompuesta = "";
    private CancellationTokenSource? _cancelacion;
    private int _contador;

    /// <summary>Cuántas veces se rehace una variante que no encaja antes de dejarla con su aviso.</summary>
    private const int Reintentos = 2;

    /// <summary>El componente con la IA local (llama.cpp y Qwen3-4B), el mismo que traduce en Doblar.</summary>
    private const string ComponenteIA = "traductor";

    /// <summary>Lo que se pide al generar una variante.</summary>
    private sealed record Encargo(string Instruccion, int Semilla, string? Etiqueta, (double Min, double Max)? Rango,
        (CambioDiseno Cambio, MedidasVoz Partida)? Cambio = null);

    public DisenarViewModel(ServicioMotor motor, ServicioVoces voces, ServicioReproductor reproductor, ServicioInstalacion instalacion,
        INavigationService navegacion)
    {
        _motor = motor;
        _voces = voces;
        _instalacion = instalacion;
        _navegacion = navegacion;
        Reproductor = reproductor;
        Ejemplo = Ejemplos[0]; // aplica sus opciones y compone la descripción
        TextoMuestra = TextosDeReferencia.Para(Idioma);
    }

    public ServicioReproductor Reproductor { get; }
    public ServicioMotor Motor => _motor;

    public IReadOnlyList<EjemploDiseno> Ejemplos { get; } = CatalogoDiseno.Ejemplos.Select(e => new EjemploDiseno(e.Nombre, e.Descripcion)).ToList();
    public IReadOnlyList<OpcionSexo> Sexos { get; } = [new(SexoVoz.Hombre, T("Hombre")), new(SexoVoz.Mujer, T("Mujer")), new(SexoVoz.Indiferente, T("Indiferente"))];
    public IReadOnlyList<OpcionDiseno> Timbres => CatalogoDiseno.Timbres;
    public IReadOnlyList<OpcionDiseno> Texturas => CatalogoDiseno.Texturas;
    public IReadOnlyList<OpcionDiseno> Ritmos => CatalogoDiseno.Ritmos;
    public IReadOnlyList<OpcionDiseno> Tonos => CatalogoDiseno.Tonos;
    public IReadOnlyList<OpcionDiseno> Acentos => CatalogoDiseno.Acentos;
    public IReadOnlyList<OpcionIdioma> IdiomasDisponibles { get; } = Idiomas.Todos.Select(i => new OpcionIdioma(i.Clave, i.Nombre)).ToList();
    public ObservableCollection<VarianteVoz> Variantes { get; } = [];

    [ObservableProperty] public partial bool VoiceDesignInstalado { get; set; } = true;

    [ObservableProperty] public partial EjemploDiseno? Ejemplo { get; set; }
    [ObservableProperty] public partial SexoVoz Sexo { get; set; }
    [ObservableProperty] public partial double Edad { get; set; }
    [ObservableProperty] public partial string Timbre { get; set; } = "";
    [ObservableProperty] public partial string Textura { get; set; } = "";
    [ObservableProperty] public partial string Ritmo { get; set; } = "";
    [ObservableProperty] public partial string Tono { get; set; } = "";
    [ObservableProperty] public partial string? Acento { get; set; }
    [ObservableProperty] public partial string Extra { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsEspanol), nameof(PuedeAcentoEspana))]
    public partial string Idioma { get; set; } = "Spanish";

    public bool EsEspanol => Idioma == "Spanish";

    /// <summary>
    /// El diseñador sesea siempre (medido: ninguna de 12 voces con tres descripciones distintas tuvo acento de España).
    /// Con «castellano», cada variante pasa por el motor castellano: pronunciación de España y el timbre de la variante.
    /// </summary>
    public bool PuedeAcentoEspana => EsEspanol && Acento == "castellano";

    [ObservableProperty] public partial bool AcentoEspana { get; set; } = true;
    [ObservableProperty] public partial bool MotorCastellanoInstalado { get; set; } = true;
    public ObservableCollection<OpcionPronunciacion> Pronunciaciones { get; } = [];
    [ObservableProperty] public partial OpcionPronunciacion? Pronunciacion { get; set; }

    [ObservableProperty] public partial string Instruccion { get; set; } = "";
    [ObservableProperty] public partial string TextoMuestra { get; set; } = "";
    [ObservableProperty] public partial double NumeroVariantes { get; set; } = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Generando { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    public partial bool Describiendo { get; set; }

    public bool Libre => !Generando && !Describiendo;

    /// <summary>Descripción libre en español, para que la IA rellene las opciones.</summary>
    [ObservableProperty] public partial string DescripcionLibre { get; set; } = "";

    /// <summary>Revisar cada variante (tono y lectura) y rehacer las que no encajen.</summary>
    [ObservableProperty] public partial bool RehacerSolas { get; set; } = true;

    [ObservableProperty] public partial string Mensaje { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }

    public async Task CargarAsync()
    {
        VoiceDesignInstalado = (await _instalacion.InstaladorAsync()).EstaInstalado(ServicioMotor.ModeloVoiceDesign);
        MotorCastellanoInstalado = await _motor.HayMotorCastellanoAsync();
        // Pronunciación de España: la de serie del motor castellano o una voz en español de la biblioteca.
        var elegida = Pronunciacion?.Ruta;
        var biblioteca = await _voces.BibliotecaAsync();
        Pronunciaciones.Clear();
        Pronunciaciones.Add(new OpcionPronunciacion("", T("La de serie del motor castellano")));
        foreach (var v in biblioteca.Listar().Where(v => v.Idioma == "Spanish"))
            Pronunciaciones.Add(new OpcionPronunciacion(biblioteca.ReferenciaDe(v.Id), v.Nombre));
        Pronunciacion = Pronunciaciones.FirstOrDefault(p => p.Ruta == elegida) ?? Pronunciaciones[0];
    }

    // --- Descripción guiada ------------------------------------------------------------------

    partial void OnEjemploChanged(EjemploDiseno? value)
    {
        if (value is not null)
            Aplicar(value);
    }

    private void Aplicar(EjemploDiseno ejemplo) => Aplicar(ejemplo.Descripcion);

    private void Aplicar(DescripcionGuiada d)
    {
        Sexo = d.Sexo;
        Edad = d.Edad;
        Timbre = d.Timbre;
        Textura = d.Textura;
        Ritmo = d.Ritmo;
        Tono = d.Tono;
        Acento = d.Acento;
        Extra = d.Extra ?? "";
        Componer(forzar: true);
    }

    partial void OnSexoChanged(SexoVoz value) => Componer();
    partial void OnEdadChanged(double value) => Componer();
    partial void OnTimbreChanged(string value) => Componer();
    partial void OnTexturaChanged(string value) => Componer();
    partial void OnRitmoChanged(string value) => Componer();
    partial void OnTonoChanged(string value) => Componer();
    partial void OnAcentoChanged(string? value)
    {
        OnPropertyChanged(nameof(PuedeAcentoEspana));
        Componer();
    }
    partial void OnExtraChanged(string value) => Componer();

    partial void OnIdiomaChanged(string oldValue, string newValue)
    {
        // Si el texto de muestra era el de ejemplo, se cambia por el del nuevo idioma.
        if (TextoMuestra == TextosDeReferencia.Para(oldValue))
            TextoMuestra = TextosDeReferencia.Para(newValue);
        Componer();
    }

    /// <summary>Rehace la descripción desde las opciones, salvo que el usuario la haya retocado a mano.</summary>
    private void Componer(bool forzar = false)
    {
        if (!forzar && Instruccion != _ultimaCompuesta)
            return;
        _ultimaCompuesta = new DescripcionGuiada(Sexo, (int)Edad, Timbre, Textura, Ritmo, Tono, Acento, Extra).Componer(Idioma);
        Instruccion = _ultimaCompuesta;
    }

    [RelayCommand]
    private void RehacerDescripcion() => Componer(forzar: true);

    [RelayCommand]
    private void RestaurarTexto() => TextoMuestra = TextosDeReferencia.Para(Idioma);

    // --- Generar variantes -------------------------------------------------------------------

    [RelayCommand]
    private async Task GenerarAsync()
    {
        if (string.IsNullOrWhiteSpace(Instruccion) || string.IsNullOrWhiteSpace(TextoMuestra))
        {
            Error = T("Hace falta una descripción y un texto de muestra.");
            return;
        }
        Reproductor.Detener();
        Variantes.Clear();
        _contador = 0;
        // El tono solo se comprueba si la descripción sale de las opciones (retocada a mano, no se sabe qué se pidió).
        var rango = Instruccion == _ultimaCompuesta ? RevisionDiseno.RangoTono(Sexo, Timbre, (int)Edad) : null;
        var aleatorio = new Random();
        var encargos = Enumerable.Range(0, (int)Math.Clamp(NumeroVariantes, 1, 6))
            .Select(_ => new Encargo(Instruccion, aleatorio.Next(1, 1_000_000), null, rango))
            .ToList();
        await CrearAsync(encargos, T("Escucha las variantes y guarda la que más te guste."));
    }

    /// <summary>
    /// «Más así»: la misma semilla con la descripción retocada (un poco y bastante). Salen voces parecidas, no
    /// idénticas: el diseñador no garantiza la misma voz con la misma semilla si cambia la descripción.
    /// </summary>
    [RelayCommand]
    private async Task MasAsiAsync(CambioDeVariante c)
    {
        var v = c.Variante;
        v.MostrarCambios = false;
        var cambio = c.Opcion.Cambio;
        List<Encargo> encargos;
        if (cambio == CambioDiseno.Igual)
        {
            var aleatorio = new Random();
            encargos = [.. Enumerable.Range(0, 2).Select(_ =>
                new Encargo(v.Instruccion, aleatorio.Next(1, 1_000_000), T("parecida a la {0}", v.Numero), v.Rango))];
        }
        else
        {
            var partida = v.Medidas;
            try
            {
                partida ??= v.Medidas = await _motor.AnalizarVozAsync(v.Ruta);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Error = ServicioMotor.Explicar(e);
                return;
            }
            var rango = RevisionDiseno.CambiaElTono(cambio) ? null : v.Rango;
            var nombre = c.Nombre.ToLowerInvariant();
            encargos =
            [
                new(RevisionDiseno.Instruccion(v.Instruccion, cambio, fuerte: false), v.Semilla,
                    T("de la {0}: {1}, un poco", v.Numero, nombre), rango, (cambio, partida)),
                new(RevisionDiseno.Instruccion(v.Instruccion, cambio, fuerte: true), v.Semilla,
                    T("de la {0}: {1}, bastante", v.Numero, nombre), rango, (cambio, partida)),
            ];
        }
        await CrearAsync(encargos, T("Las nuevas están al final de la lista: parecidas a la {0}, no idénticas.", v.Numero));
    }

    /// <summary>
    /// Genera las variantes y, si está activado, las revisa: el motor mide su tono y Whisper (si está instalado) comprueba
    /// que leen exactamente el texto. Las que no encajan se rehacen con otra semilla, hasta <see cref="Reintentos"/> veces;
    /// si aun así no encajan, se quedan con su aviso.
    /// </summary>
    private async Task CrearAsync(List<Encargo> encargos, string mensajeFinal)
    {
        Error = null;
        Generando = true;
        _cancelacion = new CancellationTokenSource();
        var ct = _cancelacion.Token;
        var texto = TextoMuestra.Trim();
        var idioma = Idioma;
        var revisar = RehacerSolas;
        var aleatorio = new Random();
        var nuevas = new HashSet<int>();
        try
        {
            var whisper = revisar && await _motor.HayWhisperAsync();
            var espana = AcentoEspana && PuedeAcentoEspana && await _motor.HayMotorCastellanoAsync();
            var pendientes = encargos.Select(e => (Encargo: e, Puesto: -1)).ToList();
            // Con acento de España se escucha la primera cuando ya lo tiene, no la del diseñador.
            var primera = Variantes.Count == 0 && !espana;
            for (var ronda = 0; ronda <= Reintentos && pendientes.Count > 0; ronda++)
            {
                var hechas = new List<(Encargo Encargo, VarianteVoz Variante)>();
                foreach (var (encargo, puestoAnterior) in pendientes)
                {
                    var numero = puestoAnterior >= 0 ? Variantes[puestoAnterior].Numero : ++_contador;
                    Mensaje = ronda == 0 ? T("Creando la variante {0}…", numero) : T("Rehaciendo la variante {0}, que no encajaba…", numero);
                    var salida = await _voces.ArchivoTemporalAsync($"diseno-{numero}-{ronda}");
                    var r = await _motor.DisenarAsync(encargo.Instruccion, idioma, texto, salida, encargo.Semilla, ct: ct);
                    var v = new VarianteVoz(numero, encargo.Semilla, r.Salida, r.Duracion, encargo.Instruccion, idioma, texto, encargo.Etiqueta)
                    {
                        Nombre = Ejemplo?.Nombre ?? T("Voz diseñada"),
                        Rango = encargo.Rango,
                    };
                    if (revisar)
                        v.Medidas = await _motor.AnalizarVozAsync(r.Salida, ct);
                    if (puestoAnterior >= 0)
                        Variantes[puestoAnterior] = v;
                    else
                        Variantes.Add(v);
                    if (primera)
                    {
                        Reproductor.Reproducir(r.Salida);
                        primera = false;
                    }
                    hechas.Add((encargo, v));
                    nuevas.Add(numero);
                }
                if (!revisar)
                    break;

                pendientes = [];
                foreach (var (encargo, v) in hechas)
                {
                    string? oido = null;
                    if (whisper)
                    {
                        Mensaje = T("Comprobando que la variante {0} lee bien el texto…", v.Numero);
                        oido = (await _motor.TranscribirAsync(v.Ruta, idioma, ct)).Texto;
                    }
                    var problemas = RevisionDiseno.Problemas(v.Medidas!, oido, texto, idioma, encargo.Rango, encargo.Cambio);
                    var leeBien = whisper && RevisionDiseno.LeeElTexto(texto, oido!, idioma);
                    v.Revision = RevisionDiseno.Resumen(v.Medidas!, texto) + (leeBien ? " · " + T("lee el texto exacto") : "");
                    v.Avisos = problemas.Count == 0 ? null : string.Join(" ", problemas);
                    if (problemas.Count > 0 && ronda < Reintentos)
                        pendientes.Add((encargo with { Semilla = aleatorio.Next(1, 1_000_000) }, Variantes.IndexOf(v)));
                }
            }
            if (espana)
                await DarAcentoEspanaAsync([.. Variantes.Where(v => nuevas.Contains(v.Numero))], whisper, ct);
            Mensaje = mensajeFinal;
        }
        catch (OperationCanceledException)
        {
            Mensaje = T("Cancelado.");
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
        }
    }

    /// <summary>
    /// Pasa las variantes por el motor castellano: lee el texto de muestra con la pronunciación de España (la de serie o
    /// la voz elegida) y el timbre de la variante. Probado: mismo timbre (parecido 0,99) y sin seseo. Si Whisper está,
    /// se comprueba que lee el texto exacto y, si no, se repite con otra semilla.
    /// </summary>
    private async Task DarAcentoEspanaAsync(List<VarianteVoz> variantes, bool whisper, CancellationToken ct)
    {
        var pronunciacion = Pronunciacion?.Ruta ?? "";
        var pendientes = variantes;
        for (var ronda = 0; ronda <= Reintentos && pendientes.Count > 0; ronda++)
        {
            var hechas = new List<(VarianteVoz Variante, string Salida)>();
            foreach (var v in pendientes)
            {
                Mensaje = T("Dando acento de España a la variante {0}…", v.Numero);
                var salida = await _voces.ArchivoTemporalAsync($"espana-{v.Numero}-{ronda}");
                await _motor.SintetizarDoblajeAsync(v.Texto, pronunciacion, v.RutaDiseno, salida, v.Semilla + ronda, ct);
                hechas.Add((v, salida));
            }
            pendientes = [];
            foreach (var (v, salida) in hechas)
            {
                var leeBien = true;
                if (whisper)
                {
                    Mensaje = T("Comprobando que la variante {0} lee bien el texto…", v.Numero);
                    leeBien = RevisionDiseno.LeeElTexto(v.Texto, (await _motor.TranscribirAsync(salida, v.Idioma, ct)).Texto, v.Idioma);
                }
                if (!leeBien && ronda < Reintentos)
                {
                    pendientes.Add(v);
                    continue;
                }
                v.Ruta = salida;
                v.AcentoEspana = true;
                v.Narracion = null;
                v.Revision = string.Join(" · ", new[] { v.Revision, T("acento de España") }.Where(x => !string.IsNullOrEmpty(x)));
                if (!leeBien)
                    v.Avisos = string.Join(" ", new[] { v.Avisos, T("Con el acento de España se salta o cambia palabras.") }.Where(x => !string.IsNullOrEmpty(x)));
                if (v == variantes[0] && Reproductor.Sonando is null)
                    Reproductor.Reproducir(v.Ruta);
            }
        }
    }

    [RelayCommand]
    private void AlternarCambios(VarianteVoz v) => v.MostrarCambios = !v.MostrarCambios;

    /// <summary>Lee un párrafo de narración con la voz ya clonada de la variante: lo que saldrá de verdad en Narrar.</summary>
    [RelayCommand]
    private async Task NarrarAsync(VarianteVoz v)
    {
        if (v.Narracion is { } hecha && File.Exists(hecha))
        {
            Escuchar(hecha);
            return;
        }
        v.Error = null;
        v.Narrando = true;
        try
        {
            var voz = new Voz("prueba-diseno", v.Nombre, v.Idioma, OrigenVoz.Disenada, v.Texto, v.Duracion, DateTimeOffset.Now, true);
            var salida = await _voces.ArchivoTemporalAsync($"narracion-{v.Numero}-{v.Semilla}");
            var r = await _motor.SintetizarAsync(voz, v.Ruta, TextosDeNarracion.Para(v.Idioma), salida);
            v.Narracion = r.Salida;
            Reproductor.Reproducir(r.Salida);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            v.Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            v.Narrando = false;
        }
    }

    /// <summary>La IA local lee la descripción en español y rellena las opciones (y el «algo más», en inglés).</summary>
    [RelayCommand]
    private async Task DescribirAsync()
    {
        if (string.IsNullOrWhiteSpace(DescripcionLibre))
            return;
        var instalador = await _instalacion.InstaladorAsync();
        if (!instalador.EstaInstalado(ComponenteIA))
        {
            Error = T("Para describir la voz con tus palabras hace falta el traductor con IA: instálalo desde Componentes.");
            return;
        }
        Error = null;
        Describiendo = true;
        Mensaje = T("La IA está leyendo tu descripción (la primera vez tarda unos segundos)…");
        try
        {
            // La IA y el diseñador no caben a la vez en la tarjeta: se apaga el motor de voz mientras tanto.
            await _motor.ApagarAsync();
            var rutas = instalador.Rutas;
            await using var ia = new ServidorIA(Path.Combine(rutas.Herramientas, "llama", "llama-server.exe"),
                Path.Combine(rutas.Modelos, "traductor", ServicioDoblaje.Modelo), rutas.UsaGpu, contexto: 4096);
            var d = await DescriptorVoz.ConvertirAsync(ia, DescripcionLibre, CancellationToken.None);
            Ejemplo = null;
            Aplicar(d);
            Mensaje = T("Listo: revisa las opciones (puedes cambiarlas) y genera las variantes.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = ServicioMotor.Explicar(e);
        }
        finally
        {
            Describiendo = false;
        }
    }

    [RelayCommand]
    private void Cancelar() => _cancelacion?.Cancel();

    [RelayCommand]
    private void Escuchar(VarianteVoz v) => Escuchar(v.Ruta);

    private void Escuchar(string ruta)
    {
        if (Reproductor.Sonando == ruta)
            Reproductor.Detener();
        else
            Reproductor.Reproducir(ruta);
    }

    /// <summary>La grabación de la variante pasa a ser la referencia de la voz; el texto de muestra, su transcripción exacta.</summary>
    [RelayCommand]
    private async Task GuardarAsync(VarianteVoz v)
    {
        v.Error = null;
        v.Guardando = true;
        try
        {
            var biblioteca = await _voces.BibliotecaAsync();
            var r = await biblioteca.ImportarAsync(new SolicitudImportacion(
                v.Ruta, v.Nombre, v.Idioma, v.Texto, Consentimiento: true, OrigenVoz.Disenada, v.Instruccion, v.Semilla,
                AcentoEspana: v.AcentoEspana));
            if (_voces.Predeterminada is null)
                _voces.MarcarPredeterminada(r.Voz.Id);
            else
                _voces.Notificar();
            v.Guardada = T("Guardada como «{0}».", r.Voz.Nombre) + (r.Aviso is null ? "" : " " + r.Aviso);
        }
        catch (VozInvalidaException e)
        {
            v.Error = e.Message;
        }
        finally
        {
            v.Guardando = false;
        }
    }

    [RelayCommand]
    private void IrAVoces() => _navegacion.Navigate(typeof(PaginaVoces));

    [RelayCommand]
    private void IrAComponentes() => _navegacion.Navigate(typeof(PaginaComponentes));
}
