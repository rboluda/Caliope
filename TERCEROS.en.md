[Español](TERCEROS.md) · **English**

# Third-party tools used by Calíope

**Calíope is a suite that brings together open-source tools made by others and makes them easy to use**: it installs them, coordinates them and gives them a simple interface. The voice engines and models are not Calíope's work. They belong to their authors, who deserve all the credit, and they are used under their licenses.

Calíope **does not redistribute** the engines or the models: it downloads them from their official sources (PyPI, the PyTorch index, Hugging Face, GitHub) when the user asks for them, and shows each license before installing. The exact versions are listed in [`catalogo/catalogo.json`](catalogo/catalogo.json).

## Voice engines and models

| Tool | Author | License | What Calíope uses it for |
|---|---|---|---|
| [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS): Base 1.7B / 0.6B and VoiceDesign 1.7B models | Alibaba (Qwen team) | Apache 2.0 | The main voice engine: cloning voices, designing them from a description and reading the scripts. |
| [Chatterbox Multilingual](https://github.com/resemble-ai/chatterbox) v3 and its [Spanish (from Spain) package](https://huggingface.co/ResembleAI/Chatterbox-Multilingual-es-es) | Resemble AI | MIT | The fast Spanish engine, with control over emotional intensity. |
| [VoxCPM2](https://github.com/OpenBMB/VoxCPM) | OpenBMB | Apache 2.0 | The closest-match engine: it imitates the voice most faithfully and produces 48 kHz audio. |
| [Perth](https://github.com/resemble-ai/perth) | Resemble AI | MIT | The inaudible watermark that Chatterbox adds to everything it generates. |
| [Whisper large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) | OpenAI | MIT | Automatic transcription and the check that the voice reads the text correctly. |

## Tools that make them work

| Tool | Author | License | What Calíope uses it for |
|---|---|---|---|
| [PyTorch](https://pytorch.org) | PyTorch Foundation | BSD-3 | Runs the models on the graphics card or the processor. |
| [Transformers](https://github.com/huggingface/transformers) and [Hugging Face Hub](https://github.com/huggingface/huggingface_hub) | Hugging Face | Apache 2.0 | Downloading and loading the models. |
| [FFmpeg](https://ffmpeg.org) | FFmpeg project | GPL | Converts the audio, levels the volume, mixes in the background and creates the included sounds. An official build is downloaded ([gyan.dev](https://www.gyan.dev/ffmpeg/builds/)). |
| [Rubber Band](https://breakfastquay.com/rubberband/) | Breakfast Quay | GPL | Changes the speed without touching the pitch, and the pitch without touching the speed (included in FFmpeg). |
| [uv](https://github.com/astral-sh/uv) | Astral | MIT / Apache 2.0 | Installs each engine in its own Python environment. |
| [Python](https://www.python.org) | Python Software Foundation | PSF | The language the engines are written in. |

## The app

| Tool | Author | License | What Calíope uses it for |
|---|---|---|---|
| [.NET](https://dotnet.microsoft.com) and WPF | Microsoft | MIT | The foundation of the app (included in the installer). |
| [WPF-UI](https://github.com/lepoco/wpfui) | lepo.co | MIT | The look of the windows and controls. |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | .NET Foundation | MIT | The internal organization of the screens. |
| [NAudio](https://github.com/naudio/NAudio) | Mark Heath | MIT | Recording with the microphone. |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) | Microsoft | MIT | Starting the app and its services. |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | Jordan Russell and Martijn Laan | Inno Setup License | Calíope's installer. |

## What belongs to Calíope

The app's code, the logo, and the sound effects and ambiences in “Mis sonidos” belong to Calíope and are released under the [MIT license](LICENSE). Calíope synthesizes those sounds with FFmpeg: they are not third-party recordings.
