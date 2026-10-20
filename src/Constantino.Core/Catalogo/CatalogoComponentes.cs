using System.Text.Json;
using System.Text.Json.Serialization;

namespace Constantino.Core.Catalogo;

/// <summary>Catálogo de componentes instalables, leído de catalogo.json.</summary>
public sealed class CatalogoComponentes
{
    public const string NombreArchivo = "catalogo.json";

    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // «tipo» de cada paso puede ir en cualquier posición del objeto.
        AllowOutOfOrderMetadataProperties = true,
    };

    private readonly Dictionary<string, Componente> _porId;

    public int Version { get; }
    public IReadOnlyList<Componente> Componentes { get; }

    private CatalogoComponentes(int version, IReadOnlyList<Componente> componentes)
    {
        Version = version;
        Componentes = componentes;
        // TryAdd para que un id repetido llegue a Validar() y se explique, en vez de romper aquí.
        _porId = new Dictionary<string, Componente>(StringComparer.Ordinal);
        foreach (var c in componentes)
            _porId.TryAdd(c.Id ?? "", c);
    }

    public Componente this[string id] => _porId[id];

    public bool Contiene(string id) => _porId.ContainsKey(id);

    /// <summary>Grupos en el orden en que aparecen en el catálogo.</summary>
    public IEnumerable<string> Grupos => Componentes.Select(c => c.Grupo).Distinct();

    public static CatalogoComponentes Cargar(string ruta) => Leer(File.ReadAllText(ruta));

    /// <summary>Busca catalogo.json junto al ejecutable.</summary>
    public static CatalogoComponentes CargarPredeterminado() =>
        Cargar(Path.Combine(AppContext.BaseDirectory, NombreArchivo));

    public static CatalogoComponentes Leer(string json)
    {
        var dto = JsonSerializer.Deserialize<CatalogoDto>(json, Opciones)
                  ?? throw new CatalogoInvalidoException("El catálogo está vacío.");
        // Las listas que falten en el JSON llegan como null: se normalizan a vacías.
        var componentes = (dto.Componentes ?? []).Select(c => c with
        {
            Habilita = c.Habilita ?? [],
            DependeDe = c.DependeDe ?? [],
            Licencias = c.Licencias ?? [],
        }).ToList();
        var catalogo = new CatalogoComponentes(dto.Version, componentes);
        catalogo.Validar();
        return catalogo;
    }

    /// <summary>Todos los componentes de los que depende <paramref name="id"/>, directa o indirectamente.</summary>
    public IReadOnlyList<Componente> DependenciasDe(string id)
    {
        var resultado = new List<Componente>();
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        void Visitar(string actual)
        {
            foreach (var dep in this[actual].DependeDe)
            {
                if (vistos.Add(dep))
                {
                    Visitar(dep);
                    resultado.Add(this[dep]);
                }
            }
        }
        Visitar(id);
        return resultado;
    }

    private void Validar()
    {
        var errores = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Componentes)
        {
            if (string.IsNullOrWhiteSpace(c.Id))
                errores.Add("Hay un componente sin id.");
            else if (!ids.Add(c.Id))
                errores.Add($"Id repetido: {c.Id}.");
            if (string.IsNullOrWhiteSpace(c.Nombre) || string.IsNullOrWhiteSpace(c.Resumen))
                errores.Add($"{c.Id}: falta el nombre o el resumen.");
            if (c.Tamano is null || c.Requisitos is null)
                errores.Add($"{c.Id}: falta el tamaño o los requisitos.");
            if (c.Instalacion is { } inst)
            {
                if (inst.Pasos is not { Count: > 0 })
                    errores.Add($"{c.Id}: la instalación no tiene pasos.");
                if (inst.Comprobar is not { Count: > 0 } || inst.Rutas is not { Count: > 0 })
                    errores.Add($"{c.Id}: la instalación debe indicar qué comprobar y qué rutas ocupa.");
            }
        }
        foreach (var c in Componentes)
        {
            foreach (var dep in c.DependeDe)
            {
                if (!ids.Contains(dep))
                    errores.Add($"{c.Id} depende de «{dep}», que no existe.");
            }
        }
        if (errores.Count == 0)
        {
            foreach (var c in Componentes)
            {
                if (TieneCiclo(c.Id))
                    errores.Add($"{c.Id} forma parte de un ciclo de dependencias.");
            }
        }
        foreach (var grupo in Componentes.Where(c => c.GrupoExclusivo is not null).GroupBy(c => c.GrupoExclusivo))
        {
            if (grupo.Count() < 2)
                errores.Add($"El grupo exclusivo «{grupo.Key}» solo tiene un componente.");
        }
        if (errores.Count > 0)
            throw new CatalogoInvalidoException(string.Join(Environment.NewLine, errores));
    }

    private bool TieneCiclo(string origen)
    {
        var pendientes = new Stack<string>(this[origen].DependeDe);
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        while (pendientes.Count > 0)
        {
            var actual = pendientes.Pop();
            if (actual == origen)
                return true;
            if (vistos.Add(actual))
            {
                foreach (var dep in this[actual].DependeDe)
                    pendientes.Push(dep);
            }
        }
        return false;
    }

    private sealed record CatalogoDto(int Version, List<Componente>? Componentes);
}

public sealed class CatalogoInvalidoException(string mensaje) : Exception(mensaje);
