using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Constantino.App.Paginas;

public sealed record InfoProximamente(
    SymbolRegular Icono,
    string Titulo,
    string Descripcion,
    IReadOnlyList<string> Funciones,
    string Necesita,
    string Llegada);

/// <summary>Página provisional para las secciones que llegarán en hitos posteriores.</summary>
public partial class PaginaProximamente : Page
{
    public PaginaProximamente(InfoProximamente info)
    {
        DataContext = info;
        Title = info.Titulo;
        InitializeComponent();
    }
}

public sealed class PaginaDisenar() : PaginaProximamente(new(
    SymbolRegular.Sparkle24,
    "Diseñar voz",
    "Crea una voz nueva describiéndola con palabras: edad, timbre, ritmo, tono y acento.",
    [
        "Escribir la descripción con plantillas y ejemplos",
        "Generar varias versiones y escucharlas",
        "Guardar la que más te guste en tu biblioteca",
    ],
    "el modelo VoiceDesign (además del motor Qwen3-TTS y un modelo Base para narrar con ella).",
    "Llegará en el hito H4."));

public sealed class PaginaClonar() : PaginaProximamente(new(
    SymbolRegular.Mic24,
    "Clonar voz",
    "Crea una voz a partir de una grabación de 10–20 segundos: la tuya o la de alguien que te haya dado permiso.",
    [
        "Importar un audio o grabar con el micrófono",
        "Recortar el fragmento sobre la forma de onda",
        "Transcripción automática (con Whisper) o a mano",
        "Probar el clon y guardarlo en tu biblioteca",
    ],
    "el motor Qwen3-TTS y un modelo Base. Whisper es opcional.",
    "Llegará en el hito H5."));

public sealed class PaginaNarrar() : PaginaProximamente(new(
    SymbolRegular.BookOpen24,
    "Narrar",
    "Convierte tus guiones en audio con cualquiera de tus voces.",
    [
        "Editor con botones para pausas, pausas largas y susurros",
        "Ajustar velocidad, tono, expresividad y volumen",
        "Escuchar y regenerar cada bloque por separado",
        "Exportar el MP3 completo, los bloques sueltos y un manifiesto para el montaje",
    ],
    "el motor Qwen3-TTS y un modelo Base.",
    "Llegará en el hito H3."));
