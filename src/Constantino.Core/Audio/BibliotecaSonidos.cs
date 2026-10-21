using Constantino.Core.Instalaciones;
using Constantino.Core.Narracion;

namespace Constantino.Core.Audio;

/// <summary>Un sonido de «Mis sonidos»: nombre para el guion y archivo.</summary>
public sealed record Sonido(string Nombre, string Clave, string Ruta, bool Incluido);

/// <summary>
/// La carpeta «Mis sonidos»: efectos para [sfx: …], ambientes y música de fondo. Calíope trae unos cuantos
/// generados por él mismo (sin grabaciones con derechos de otros); se puede añadir cualquier audio a cada carpeta.
/// </summary>
public sealed class BibliotecaSonidos(string carpeta) : ISonidos
{
    public const string Efectos_ = "Efectos", Ambientes_ = "Ambientes", Musica_ = "Música";
    private static readonly string[] Extensiones = [".wav", ".mp3", ".ogg", ".flac", ".m4a"];

    public string Carpeta => carpeta;
    public string CarpetaEfectos => Path.Combine(carpeta, Efectos_);
    public string CarpetaAmbientes => Path.Combine(carpeta, Ambientes_);
    public string CarpetaMusica => Path.Combine(carpeta, Musica_);

    public IReadOnlyList<Sonido> ListaEfectos => Listar(CarpetaEfectos);
    public IReadOnlyList<Sonido> ListaAmbientes => Listar(CarpetaAmbientes);
    public IReadOnlyList<Sonido> ListaMusica => Listar(CarpetaMusica);

    IReadOnlyCollection<string> ISonidos.Efectos => ListaEfectos.Select(s => s.Clave).ToList();

    public string? RutaEfecto(string nombre) => Buscar(ListaEfectos, nombre);
    public string? RutaAmbiente(string nombre) => Buscar(ListaAmbientes, nombre);
    public string? RutaMusica(string nombre) => Buscar(ListaMusica, nombre);
    string? ISonidos.RutaAmbiente(string nombre) => RutaAmbiente(nombre);
    string? ISonidos.RutaMusica(string nombre) => RutaMusica(nombre);

    private static string? Buscar(IReadOnlyList<Sonido> lista, string nombre)
    {
        var clave = Textos.Slug(nombre, "");
        return lista.FirstOrDefault(s => s.Clave == clave)?.Ruta;
    }

    private static IReadOnlyList<Sonido> Listar(string carpeta)
    {
        if (!Directory.Exists(carpeta))
            return [];
        return Directory.EnumerateFiles(carpeta)
            .Where(f => Extensiones.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => new Sonido(Path.GetFileNameWithoutExtension(f), Textos.Slug(Path.GetFileNameWithoutExtension(f), "sonido"), f,
                Incluidos.ContainsKey(Path.GetFileNameWithoutExtension(f))))
            .GroupBy(s => s.Clave)
            .Select(g => g.First())
            .OrderBy(s => s.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Los sonidos que trae Calíope: (carpeta, filtro de ffmpeg que los sintetiza).</summary>
    private static readonly Dictionary<string, (string Carpeta, string Filtro)> Incluidos = new()
    {
        // Efectos (cortos).
        ["trueno"] = (Efectos_, "anoisesrc=color=brown:duration=5:amplitude=1,lowpass=f=260,afade=t=in:d=0.06,afade=t=out:st=0.4:d=4.6,volume=5,alimiter=limit=0.9"),
        ["latido"] = (Efectos_, "aevalsrc='0.9*sin(2*PI*55*t)*(exp(-28*mod(t,0.85))+0.7*gte(mod(t,0.85),0.22)*exp(-28*(mod(t,0.85)-0.22)))':d=3.4,lowpass=f=180,volume=2,alimiter=limit=0.9"),
        ["reloj"] = (Efectos_, "aevalsrc='0.7*sin(2*PI*2600*t)*exp(-450*mod(t,0.5))+0.4*sin(2*PI*1900*t)*exp(-450*mod(t+0.25,0.5))':d=4"),
        ["campana"] = (Efectos_, "aevalsrc='0.35*(sin(2*PI*523*t)+0.6*sin(2*PI*1046*t)+0.3*sin(2*PI*1569*t)+0.2*sin(2*PI*2093*t))*exp(-1.1*t)':d=5"),
        ["rafaga"] = (Efectos_, "anoisesrc=color=pink:duration=4:amplitude=0.8,bandpass=f=550:width_type=h:w=450,volume='0.15+0.85*sin(PI*t/4)':eval=frame,afade=t=out:st=3.2:d=0.8"),
        // Ambientes (para el fondo, se repiten si hace falta).
        ["lluvia"] = (Ambientes_, "anoisesrc=color=pink:duration=60:amplitude=0.5,highpass=f=350,lowpass=f=7000,volume=0.8"),
        ["viento"] = (Ambientes_, "anoisesrc=color=brown:duration=60:amplitude=0.9,bandpass=f=420:width_type=h:w=320,volume='0.35+0.65*(0.5+0.5*sin(2*PI*t/11))':eval=frame,volume=3"),
        ["hoguera"] = (Ambientes_, "anoisesrc=color=brown:duration=60:amplitude=0.12,lowpass=f=450[base];aevalsrc='0.8*gt(random(0),0.99993)':d=60,highpass=f=1200,lowpass=f=6000[chispas];[base][chispas]amix=inputs=2:normalize=0,volume=2"),
        ["noche"] = (Ambientes_, "aevalsrc='0.12*sin(2*PI*4400*t)*gt(sin(2*PI*28*t),0)*gt(sin(2*PI*0.7*t),0.4)':d=60[grillos];anoisesrc=color=brown:duration=60:amplitude=0.05,lowpass=f=300[fondo];[grillos][fondo]amix=inputs=2:normalize=0"),
    };

    /// <summary>Crea las carpetas y los sonidos incluidos que falten (con ffmpeg; unos segundos la primera vez).</summary>
    public async Task PrepararAsync(string ffmpeg, IEjecutorProcesos ejecutor, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CarpetaEfectos);
        Directory.CreateDirectory(CarpetaAmbientes);
        Directory.CreateDirectory(CarpetaMusica);
        // Restos de una generación interrumpida (la app se cerró a medias).
        foreach (var resto in Directory.EnumerateFiles(carpeta, "*.parcial", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(carpeta, "*.tmp.wav", SearchOption.AllDirectories)))
        {
            try { File.Delete(resto); }
            catch (IOException) { }
        }
        if (!File.Exists(ffmpeg))
            return;
        foreach (var (nombre, (sub, filtro)) in Incluidos)
        {
            var destino = Path.Combine(carpeta, sub, nombre + ".wav");
            if (File.Exists(destino))
                continue;
            // Con una extensión que no se lista como sonido mientras se escribe.
            var temporal = destino + ".parcial";
            // Todos al mismo nivel: los efectos como una voz, los ambientes algo por debajo.
            var nivel = sub == Efectos_ ? "loudnorm=I=-20:TP=-2:LRA=11" : "loudnorm=I=-24:TP=-3:LRA=11";
            var r = await ejecutor.EjecutarAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-filter_complex", filtro + "," + nivel, "-ar", "44100", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", temporal],
                new Dictionary<string, string>(), _ => { }, ct).ConfigureAwait(false);
            if (r.CodigoSalida == 0 && File.Exists(temporal))
                File.Move(temporal, destino, overwrite: true);
            else if (File.Exists(temporal))
                File.Delete(temporal);
        }
    }
}
