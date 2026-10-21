using Constantino.Core.Catalogo;
using Constantino.Core.Equipo;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Recomendaciones;

/// <summary>Lo que Constantino aconseja para un componente en este equipo concreto.</summary>
public enum NivelRecomendacion
{
    Imprescindible,
    Recomendado,
    Opcional,
    NoRecomendado,
}

public enum SeveridadAviso
{
    Informacion,
    Atencion,
    Problema,
}

public sealed record Recomendacion(NivelRecomendacion Nivel, string Motivo);

public sealed record Aviso(SeveridadAviso Severidad, string Titulo, string Mensaje);

public sealed record InformeRecomendacion(
    bool UsaGpu,
    string ResumenDispositivo,
    IReadOnlyDictionary<string, Recomendacion> PorComponente,
    IReadOnlyList<Aviso> Avisos,
    double DescargaPropuestaGb)
{
    public Recomendacion Para(string id) => PorComponente[id];

    /// <summary>Los componentes que se instalarían con la selección propuesta.</summary>
    public IEnumerable<string> Propuestos => PorComponente
        .Where(p => p.Value.Nivel is NivelRecomendacion.Imprescindible or NivelRecomendacion.Recomendado)
        .Select(p => p.Key);
}

/// <summary>Cruza el catálogo con el análisis del equipo y propone qué instalar.</summary>
public static class Recomendador
{
    /// <summary>Por debajo de esta VRAM no merece la pena usar la GPU: ni el modelo más ligero cabe con holgura.</summary>
    public const double VramMinimaParaGpuGb = 3.5;

    /// <summary>Driver mínimo de NVIDIA para PyTorch con CUDA 12.8.</summary>
    public const int DriverMinimoNvidia = 570;

    public static InformeRecomendacion Recomendar(CatalogoComponentes catalogo, InformeEquipo equipo)
    {
        var gpu = equipo.GpuPrincipal;
        var vram = gpu?.VramTotalGb ?? 0;
        var usaGpu = gpu is not null && vram >= VramMinimaParaGpuGb;
        var avisos = new List<Aviso>();
        var resultado = new Dictionary<string, Recomendacion>(StringComparer.Ordinal);

        foreach (var c in catalogo.Componentes.Where(c => c.GrupoExclusivo is null))
            resultado[c.Id] = RecomendarIndividual(c, usaGpu, vram, gpu?.Nombre);

        foreach (var grupo in catalogo.Componentes.Where(c => c.GrupoExclusivo is not null).GroupBy(c => c.GrupoExclusivo!))
        {
            foreach (var (id, r) in RecomendarGrupo(grupo.ToList(), usaGpu, vram))
                resultado[id] = r;
        }

        var descarga = catalogo.Componentes
            .Where(c => resultado[c.Id].Nivel is NivelRecomendacion.Imprescindible or NivelRecomendacion.Recomendado)
            .Sum(c => c.Tamano.Para(usaGpu));

        AvisosDeGpu(equipo, gpu, usaGpu, avisos);
        AvisosDeDisco(equipo.Disco, descarga, avisos);
        if (equipo.RamGb is > 0 and < 12)
            avisos.Add(new Aviso(SeveridadAviso.Atencion, T("Poca memoria RAM"),
                T("Tu equipo tiene {0} de RAM. Cargar los modelos puede ir lento; se recomiendan 16 GB.", Formato.Gb(equipo.RamGb))));

        var resumen = usaGpu
            ? T("Se usará tu tarjeta {0} ({1} de memoria) para generar la voz.", gpu!.Nombre, Formato.Gb(vram))
            : T("No hay una tarjeta NVIDIA utilizable: la voz se generará con el procesador, bastante más despacio.");

        return new InformeRecomendacion(usaGpu, resumen, resultado, avisos, descarga);
    }

    private static Recomendacion RecomendarIndividual(Componente c, bool usaGpu, double vram, string? gpu)
    {
        if (usaGpu && c.Requisitos.SinRtx50 && GuiaMotores.EsRtx50(gpu))
            return new(NivelRecomendacion.NoRecomendado, T("Todavía no funciona con las tarjetas RTX 50 como la tuya."));
        var minima = c.Requisitos.VramMinimaGb;
        if (usaGpu && minima is { } min && vram < min)
        {
            return c.Requisitos.FuncionaEnCpu
                ? new(NivelRecomendacion.NoRecomendado,
                    T("Necesita {0} de memoria en la tarjeta y la tuya tiene {1}. Funcionaría con el procesador, pero muy despacio.", Formato.Gb(min), Formato.Gb(vram)))
                : new(NivelRecomendacion.NoRecomendado,
                    T("Necesita {0} de memoria en la tarjeta y la tuya tiene {1}.", Formato.Gb(min), Formato.Gb(vram)));
        }
        if (!usaGpu && minima is not null && !c.Requisitos.FuncionaEnCpu)
            return new(NivelRecomendacion.NoRecomendado, T("Necesita una tarjeta NVIDIA y este equipo no tiene una utilizable."));

        var nivel = c.Necesidad switch
        {
            Necesidad.Imprescindible => NivelRecomendacion.Imprescindible,
            Necesidad.Recomendado => NivelRecomendacion.Recomendado,
            _ => NivelRecomendacion.Opcional,
        };
        var motivo = c.Necesidad switch
        {
            Necesidad.Imprescindible => T("Sin esto, Calíope no puede funcionar."),
            Necesidad.Recomendado => T("Recomendado: amplía mucho lo que puedes hacer."),
            _ => GuiaMotores.MotivoOpcional(c.Id, vram, usaGpu) ?? T("Opcional: instálalo solo si lo vas a usar."),
        };
        if (!usaGpu && minima is not null)
        {
            if (nivel == NivelRecomendacion.Recomendado)
                nivel = NivelRecomendacion.Opcional;
            motivo += " " + T("Sin tarjeta NVIDIA irá lento.");
        }
        return new(nivel, motivo);
    }

    /// <summary>
    /// De un grupo de alternativas se propone una sola: con GPU, la de mayor calidad que quepa con holgura;
    /// sin GPU, la más ligera.
    /// </summary>
    private static IEnumerable<(string, Recomendacion)> RecomendarGrupo(List<Componente> alternativas, bool usaGpu, double vram)
    {
        Componente elegida;
        string motivoElegida;
        if (usaGpu)
        {
            var holgadas = alternativas.Where(c => vram >= (c.Requisitos.VramRecomendadaGb ?? 0)).ToList();
            var caben = alternativas.Where(c => vram >= (c.Requisitos.VramMinimaGb ?? 0)).ToList();
            var candidatas = holgadas.Count > 0 ? holgadas : caben.Count > 0 ? caben : alternativas;
            elegida = candidatas.MaxBy(c => c.Requisitos.VramRecomendadaGb ?? 0)!;
            motivoElegida = holgadas.Contains(elegida)
                ? T("La mejor opción que cabe con holgura en tu tarjeta ({0}).", Formato.Gb(vram))
                : T("La opción que mejor encaja en tu tarjeta ({0}), aunque irá algo justa.", Formato.Gb(vram));
        }
        else
        {
            elegida = alternativas.MinBy(c => c.Requisitos.VramMinimaGb ?? 0)!;
            motivoElegida = T("La opción más ligera, la adecuada para generar con el procesador.");
        }

        foreach (var c in alternativas)
        {
            if (c == elegida)
            {
                yield return (c.Id, new(NivelRecomendacion.Imprescindible, motivoElegida));
                continue;
            }
            var cabe = !usaGpu || vram >= (c.Requisitos.VramMinimaGb ?? 0);
            yield return (c.Id, cabe
                ? new(NivelRecomendacion.Opcional, T("Alternativa a «{0}». Solo hace falta una de las dos.", elegida.Nombre))
                : new(NivelRecomendacion.NoRecomendado,
                    T("Necesita {0} de memoria en la tarjeta y la tuya tiene {1}.", Formato.Gb(c.Requisitos.VramMinimaGb ?? 0), Formato.Gb(vram))));
        }
    }

    private static void AvisosDeGpu(InformeEquipo equipo, Gpu? gpu, bool usaGpu, List<Aviso> avisos)
    {
        if (gpu is null)
        {
            var nvidiaSinDriver = equipo.Gpus.FirstOrDefault(g => g.Nombre.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            avisos.Add(nvidiaSinDriver is not null
                ? new Aviso(SeveridadAviso.Problema, T("Falta el driver de NVIDIA"),
                    T("Hay una {0}, pero no responde. Instala el driver más reciente desde nvidia.com para que Calíope pueda usarla.", nvidiaSinDriver.Nombre))
                : new Aviso(SeveridadAviso.Atencion, T("Sin tarjeta NVIDIA"),
                    T("La voz se generará con el procesador. Funciona, pero puede tardar varias veces más que la duración del audio.")));
            return;
        }
        if (!usaGpu)
        {
            avisos.Add(new Aviso(SeveridadAviso.Atencion, T("Tarjeta con poca memoria"),
                T("Tu {0} tiene {1} de memoria, por debajo de lo necesario. Se usará el procesador.", gpu.Nombre, Formato.Gb(gpu.VramTotalGb ?? 0))));
        }
        if (int.TryParse(gpu.Driver?.Split('.')[0], out var mayor) && mayor < DriverMinimoNvidia)
        {
            avisos.Add(new Aviso(SeveridadAviso.Problema, T("Driver de NVIDIA antiguo"),
                T("Tienes la versión {0}. Hace falta la {1} o posterior: actualízalo desde nvidia.com.", gpu.Driver, DriverMinimoNvidia)));
        }
        if (usaGpu && gpu.VramUsadaGb is > 1.5)
        {
            avisos.Add(new Aviso(SeveridadAviso.Informacion, T("Otros programas usan la tarjeta"),
                T("Ahora mismo hay {0} de memoria de la tarjeta ocupados (navegadores, Teams…). Ciérralos antes de generar voz para que el modelo quepa.", Formato.Gb(gpu.VramUsadaGb.Value))));
        }
    }

    private static void AvisosDeDisco(InfoDisco? disco, double descargaGb, List<Aviso> avisos)
    {
        if (disco is null)
            return;
        if (disco.EnOneDrive)
        {
            avisos.Add(new Aviso(SeveridadAviso.Problema, T("La carpeta está en OneDrive"),
                T("OneDrive intentaría subir a la nube varios gigas de modelos. Elige una carpeta fuera de OneDrive.")));
        }
        const double margenGb = 5;
        if (disco.LibreGb < descargaGb + margenGb)
        {
            avisos.Add(new Aviso(SeveridadAviso.Problema, T("Poco espacio en disco"),
                T("La selección propuesta ocupa unos {0} y en {1} quedan {2}. Libera espacio o elige otra unidad.", Formato.Gb(descargaGb), disco.Unidad, Formato.Gb(disco.LibreGb))));
        }
    }
}
