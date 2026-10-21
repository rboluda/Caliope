"""Textos para el usuario en el idioma de la interfaz.

La aplicación pasa el idioma en CONSTANTINO_IDIOMA («es» o «en»). Los textos se escriben en español y ese mismo
texto es la clave de la traducción; si falta, se usa el español. Solo para mensajes que ve el usuario (errores y
progreso): nada de lo que lee la aplicación ni de los registros.
"""
import os

INGLES = {
    # Progreso
    "Cargando el modelo de voz": "Loading the voice model",
    "Cargando el modelo de transcripción": "Loading the transcription model",
    "Cargando Chatterbox": "Loading Chatterbox",
    "Cargando VoxCPM2": "Loading VoxCPM2",
    "Preparando la voz": "Preparing the voice",
    "Generando la voz": "Generating the voice",
    "Transcribiendo": "Transcribing",
    # Errores
    "Operación desconocida: {op}": "Unknown operation: {op}",
    "El modelo no está instalado.": "The model is not installed.",
    "No hay ningún modelo cargado.": "No model is loaded.",
    "No existe la grabación de referencia.": "The reference recording doesn't exist.",
    "No existe la grabación de referencia: {ruta}": "The reference recording doesn't exist: {ruta}",
    "Describe la voz que quieres crear.": "Describe the voice you want to create.",
    "No existe el audio.": "The audio doesn't exist.",
    "No existe el audio: {ruta}": "The audio doesn't exist: {ruta}",
    "El modelo cargado no sirve para {para}.": "The loaded model can't be used to {para}.",
    "transcribir": "transcribe",
    "clonar voces": "clone voices",
    "diseñar voces": "design voices",
    "No hay texto que leer.": "There is no text to read.",
    "Chatterbox solo genera voz.": "Chatterbox only generates voice.",
    "Falta parte de Chatterbox: repáralo desde Componentes.":
        "Part of Chatterbox is missing: repair it from Components.",
    "El modelo de Chatterbox no encaja: repáralo desde Componentes.":
        "The Chatterbox model doesn't fit together: repair it from Components.",
    "Este motor está afinado para castellano: con voces en otros idiomas usa el motor principal o VoxCPM2.":
        "This engine is tuned for Spanish (from Spain): for voices in other languages, use the main engine or VoxCPM2.",
    "Chatterbox no diseña voces: usa Qwen3-TTS VoiceDesign.": "Chatterbox doesn't design voices: use Qwen3-TTS VoiceDesign.",
    "Chatterbox no transcribe: usa Whisper.": "Chatterbox doesn't transcribe: use Whisper.",
    "VoxCPM2 solo genera voz.": "VoxCPM2 only generates voice.",
    "VoxCPM2 no diseña voces en Calíope: usa el diseñador de voces.":
        "VoxCPM2 doesn't design voices in Calíope: use the voice designer.",
    "VoxCPM2 no transcribe: usa la transcripción automática.":
        "VoxCPM2 doesn't transcribe: use automatic transcription.",
}


def t(texto, **valores):
    """El texto en el idioma de la interfaz, con los valores ya puestos."""
    if os.environ.get("CONSTANTINO_IDIOMA") == "en":
        texto = INGLES.get(texto, texto)
    return texto.format(**valores) if valores else texto
