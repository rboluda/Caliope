using System.Diagnostics;
using System.Text;

namespace Constantino.Core.Instalaciones;

public sealed record ResultadoProceso(int CodigoSalida, string Salida);

public interface IEjecutorProcesos
{
    /// <summary>Ejecuta y espera. Cada línea de stdout y stderr se pasa a <paramref name="alEscribir"/> según llega.</summary>
    Task<ResultadoProceso> EjecutarAsync(
        string programa,
        IReadOnlyList<string> argumentos,
        IReadOnlyDictionary<string, string> entorno,
        Action<string> alEscribir,
        CancellationToken ct);
}

public sealed class EjecutorProcesos : IEjecutorProcesos
{
    public async Task<ResultadoProceso> EjecutarAsync(
        string programa,
        IReadOnlyList<string> argumentos,
        IReadOnlyDictionary<string, string> entorno,
        Action<string> alEscribir,
        CancellationToken ct)
    {
        var inicio = new ProcessStartInfo(programa)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(programa) is { Length: > 0 } dir && Directory.Exists(dir)
                ? dir
                : Environment.CurrentDirectory,
        };
        foreach (var a in argumentos)
            inicio.ArgumentList.Add(a);
        foreach (var (clave, valor) in entorno)
            inicio.Environment[clave] = valor;

        var salida = new StringBuilder();
        var cerrojo = new object();
        void Linea(string? texto)
        {
            if (texto is null)
                return;
            lock (cerrojo)
                salida.AppendLine(texto);
            alEscribir(texto);
        }

        using var proceso = new Process { StartInfo = inicio };
        proceso.OutputDataReceived += (_, e) => Linea(e.Data);
        proceso.ErrorDataReceived += (_, e) => Linea(e.Data);
        proceso.Start();
        proceso.BeginOutputReadLine();
        proceso.BeginErrorReadLine();
        try
        {
            await proceso.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelar debe parar también los hijos (uv lanza otros procesos).
            try { proceso.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        // Tras WaitForExitAsync con lectura asíncrona, esta llamada vacía los últimos eventos de salida.
        proceso.WaitForExit();
        lock (cerrojo)
            return new ResultadoProceso(proceso.ExitCode, salida.ToString());
    }
}
