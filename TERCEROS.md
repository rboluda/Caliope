**Español** · [English](TERCEROS.en.md)

# Herramientas de terceros que usa Calíope

**Calíope es una suite que reúne herramientas de código abierto de terceros y las hace fáciles de usar**: las instala, las coordina y les pone una interfaz sencilla. Los motores y los modelos de voz no son obra de Calíope. Pertenecen a sus autores, que merecen todo el reconocimiento, y se usan según sus licencias.

Calíope **no redistribuye** los motores ni los modelos: los descarga de sus fuentes oficiales (PyPI, el índice de PyTorch, Hugging Face, GitHub) cuando el usuario lo pide, y le muestra cada licencia antes de instalar. Las versiones exactas están en [`catalogo/catalogo.json`](catalogo/catalogo.json).

## Motores y modelos de voz

| Herramienta | Autor | Licencia | Para qué la usa Calíope |
|---|---|---|---|
| [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS): modelos Base 1.7B / 0.6B y VoiceDesign 1.7B | Alibaba (equipo Qwen) | Apache 2.0 | El motor de voz principal: clonar voces, diseñarlas a partir de una descripción y leer los guiones. |
| [Chatterbox Multilingual](https://github.com/resemble-ai/chatterbox) v3 y su [paquete de castellano de España](https://huggingface.co/ResembleAI/Chatterbox-Multilingual-es-es) | Resemble AI | MIT | El motor castellano rápido, con control de la intensidad emocional. |
| [VoxCPM2](https://github.com/OpenBMB/VoxCPM) | OpenBMB | Apache 2.0 | El motor de máximo parecido: imita la voz con más fidelidad y genera audio a 48 kHz. |
| [Demucs](https://github.com/adefossez/demucs) con el modelo htdemucs | Meta (Alexandre Défossez) | MIT | Separa la voz de la música y los efectos al doblar un vídeo. |
| [llama.cpp](https://github.com/ggml-org/llama.cpp) | ggml-org | MIT | Ejecuta en tu equipo el modelo que traduce los vídeos. |
| [Qwen3-4B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507) (GGUF de [unsloth](https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF)) | Alibaba (equipo Qwen) | Apache 2.0 | Traduce el guion de los vídeos al castellano. |
| [Perth](https://github.com/resemble-ai/perth) | Resemble AI | MIT | La marca de agua inaudible que Chatterbox añade a todo lo que genera. |
| [Whisper large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) | OpenAI | MIT | La transcripción automática y la revisión de que la voz lee bien. |

## Herramientas que los hacen funcionar

| Herramienta | Autor | Licencia | Para qué la usa Calíope |
|---|---|---|---|
| [PyTorch](https://pytorch.org) | PyTorch Foundation | BSD-3 | Ejecuta los modelos en la tarjeta gráfica o en el procesador. |
| [Transformers](https://github.com/huggingface/transformers) y [Hugging Face Hub](https://github.com/huggingface/huggingface_hub) | Hugging Face | Apache 2.0 | Descarga y carga de los modelos. |
| [FFmpeg](https://ffmpeg.org) | Proyecto FFmpeg | GPL | Convierte el audio, iguala el volumen, mezcla el fondo y crea los sonidos incluidos. Se descarga una compilación oficial ([gyan.dev](https://www.gyan.dev/ffmpeg/builds/)). |
| [Rubber Band](https://breakfastquay.com/rubberband/) | Breakfast Quay | GPL | Cambia la velocidad sin tocar el tono y el tono sin tocar la velocidad (incluido en FFmpeg). |
| [uv](https://github.com/astral-sh/uv) | Astral | MIT / Apache 2.0 | Instala cada motor en su propio entorno de Python. |
| [Python](https://www.python.org) | Python Software Foundation | PSF | El lenguaje de los motores. |

## La aplicación

| Herramienta | Autor | Licencia | Para qué la usa Calíope |
|---|---|---|---|
| [.NET](https://dotnet.microsoft.com) y WPF | Microsoft | MIT | La base de la aplicación (va incluido en el instalador). |
| [WPF-UI](https://github.com/lepoco/wpfui) | lepo.co | MIT | El aspecto de las ventanas y los controles. |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | .NET Foundation | MIT | La organización interna de las pantallas. |
| [NAudio](https://github.com/naudio/NAudio) | Mark Heath | MIT | La grabación con el micrófono. |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) | Microsoft | MIT | El arranque y los servicios de la aplicación. |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | Jordan Russell y Martijn Laan | Inno Setup License | El instalador de Calíope. |

## Lo que es de Calíope

El código de la aplicación, el logotipo y los efectos y ambientes de «Mis sonidos» son de Calíope y se publican con la [licencia MIT](LICENSE). Calíope sintetiza esos sonidos con FFmpeg: no son grabaciones de terceros.
