using Constantino.Core.Equipo;
using Constantino.Core.Motores;
using static Constantino.Core.Idioma;

namespace Constantino.Core.Recomendaciones;

/// <summary>Una fila de la comparativa de motores, en lenguaje llano.</summary>
public sealed record FichaMotor(
    string Motor,
    string Nombre,
    string ParaQue,
    string Velocidad,
    string Parecido,
    string Idiomas,
    string Tarjeta);

/// <summary>
/// Qué motor conviene a cada equipo. Los datos salen de una comparativa con la misma voz y el mismo texto en una
/// tarjeta de 8 GB: los tres leen el texto igual de bien (Whisper encontró de 0 a 3 palabras distintas en cada uno);
/// VoxCPM2 es el que más se parece a la voz original y Chatterbox el más rápido.
/// </summary>
public static class GuiaMotores
{
    public const double VramCastellanoGb = 4.5;
    public const double VramParecidoGb = 7.5;
    public const double VramParecidoHolgadoGb = 10;

    public static IReadOnlyList<FichaMotor> Fichas { get; } =
    [
        new(MotoresDeVoz.Qwen, T("Principal (Qwen3-TTS)"),
            T("El de serie y el más completo: diseña voces y hace susurros. Basta para todo."),
            T("Lento: unas 3 veces lo que dura el audio"),
            T("Muy alto"),
            T("10 idiomas"),
            T("Funciona con cualquier equipo; con tarjeta, mucho más rápido")),
        new(MotoresDeVoz.Chatterbox, T("Castellano rápido (Chatterbox)"),
            T("Acento de España, rapidez y control de la emoción."),
            T("El más rápido: tarda menos de lo que dura el audio"),
            T("Muy alto"),
            T("Solo castellano"),
            T("Desde {0} (no sirve para las RTX 50)", Formato.Gb(VramCastellanoGb))),
        new(MotoresDeVoz.VoxCpm, T("Máximo parecido (VoxCPM2)"),
            T("La voz más parecida a la original y el sonido de más calidad."),
            T("Medio: el doble de lo que dura el audio con 8 GB; más rápido con 10 GB o más"),
            T("El más alto (sobre todo si la voz tiene transcripción)"),
            T("30 idiomas"),
            T("Desde 8 GB; mejor {0} o más", Formato.Gb(VramParecidoHolgadoGb))),
    ];

    /// <summary>Una línea que explica el motor, para mostrarla junto al selector.</summary>
    public static string Resumen(string motor) => motor switch
    {
        MotoresDeVoz.Chatterbox => T("Solo castellano. El más rápido y deja ajustar la emoción."),
        MotoresDeVoz.VoxCpm => T("El que más se parece a la voz original. Más lento y pide una tarjeta potente."),
        _ => T("El motor principal: natural y fiel al texto. Si te convence, no necesitas otro."),
    };

    /// <summary>Si la tarjeta es de las que el motor castellano aún no admite (PyTorch 2.6 no tiene Blackwell).</summary>
    public static bool EsRtx50(string? gpu) =>
        gpu is not null && (gpu.Contains("RTX 50", StringComparison.OrdinalIgnoreCase)
                            || gpu.Contains("Blackwell", StringComparison.OrdinalIgnoreCase));

    /// <summary>El consejo personalizado: qué motor usar en este equipo y si merece la pena instalar otro.</summary>
    public static string Consejo(InformeEquipo equipo)
    {
        var gpu = equipo.GpuPrincipal;
        var vram = gpu?.VramTotalGb ?? 0;
        var basta = T("No hace falta probarlos todos: si el principal te convence, no instales nada más.");
        if (gpu is null || vram < Recomendador.VramMinimaParaGpuGb)
            return T("Quédate con el motor principal: sin una tarjeta NVIDIA con memoria suficiente, es el único que funciona bien con el procesador. " +
                     "Los otros dos no te aportarían nada.");
        var castellano = !EsRtx50(gpu.Nombre) && vram >= VramCastellanoGb;
        var tarjeta = T("tu {0} ({1})", gpu.Nombre, Formato.Gb(vram));
        if (vram < VramParecidoGb)
            return castellano
                ? T("Usa el motor principal. Si narras en castellano y quieres ir más rápido o ajustar la emoción, añade el castellano rápido: cabe en {0}. " +
                    "El de máximo parecido no cabe.", tarjeta) + " " + basta
                : T("Usa el motor principal: los otros no caben en {0}.", tarjeta);
        if (vram < VramParecidoHolgadoGb)
            return T("Usa el motor principal.") + " " +
                   (castellano ? T("Si quieres rapidez, el castellano rápido va unas 3 veces más deprisa.") + " " : "") +
                   T("El de máximo parecido funciona en {0}, pero justo: va más lento y la tarjeta se queda llena.", tarjeta) + " " +
                   T("Instálalo solo si lo que más te importa es que la voz se parezca al máximo.") + " " + basta;
        return (castellano
                   ? T("Con {0} puedes usar los tres. Para el resultado final con la mejor calidad, el de máximo parecido; " +
                       "para pruebas rápidas en castellano, el castellano rápido.", tarjeta)
                   : T("Con {0} puedes usar el principal y el de máximo parecido. Para el resultado final con la mejor calidad, " +
                       "el de máximo parecido.", tarjeta)) + " " +
               T("El principal sigue siendo el más completo (diseña voces y hace susurros).") + " " + basta;
    }

    /// <summary>
    /// El motivo de la recomendación para un motor opcional que cabe en el equipo, o null si no es un motor.
    /// </summary>
    public static string? MotivoOpcional(string componente, double vram, bool usaGpu) =>
        MotoresDeVoz.DeComponente(componente) switch
        {
            MotoresDeVoz.Chatterbox => usaGpu
                ? T("Opcional: va unas 3 veces más rápido que el principal y deja ajustar la emoción. Solo lee en castellano.")
                : T("Opcional: solo castellano y, sin tarjeta NVIDIA, lento. Con el principal te basta."),
            MotoresDeVoz.VoxCpm => vram >= VramParecidoHolgadoGb
                ? T("Opcional: tu tarjeta puede con él. Es el que más se parece a la voz original, con la mejor calidad de sonido.")
                : T("Opcional: cabe justo en tu tarjeta y va más despacio. Solo si buscas el máximo parecido con la voz original."),
            _ => null,
        };
}
