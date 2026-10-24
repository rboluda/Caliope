[Español](README.md) · **English**

<p align="center">
  <img src="recursos/caliope.png" width="140" alt="Calíope logo">
</p>

<h1 align="center">Calíope</h1>

<p align="center">
  <b>Your voice studio on your own computer: design voices, clone them and narrate full scripts.</b><br>
  Offline, no accounts and no paying by the minute. For Windows.
</p>

<p align="center">
  <a href="https://github.com/rboluda/Caliope/releases/latest"><b>⬇ Download the installer</b></a> ·
  <a href="#how-to-use-it">How to use it</a> ·
  <a href="#frequently-asked-questions">FAQ</a>
</p>

---

## What is it for?

> **Calíope is a suite that brings together open-source tools made by others** —[Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS), [Chatterbox](https://github.com/resemble-ai/chatterbox), [VoxCPM2](https://github.com/OpenBMB/VoxCPM), [Whisper](https://github.com/openai/whisper), [PyTorch](https://pytorch.org), [FFmpeg](https://ffmpeg.org) and more— and makes them easy to use: it installs them, coordinates them and gives them a simple interface. The voice engines and models are the work of their authors; Calíope doesn't include them, it downloads them from their official sources when you ask it to. See [credits](#credits-and-licenses).

Calíope turns text into speech with artificial intelligence models that **run on your own computer**. It's made for people who often need voice-overs: YouTube or TikTok videos, podcasts, audiobooks, stories, courses, presentations or video games. It's a local alternative to services like ElevenLabs.

- 🎨 **Design new voices** by describing them in words: “40-year-old man, deep and warm voice, slow pace, mysterious tone, Spanish from Spain”.
- 🎙️ **Clone a voice** from 10 to 20 seconds of recording: your own, or that of someone who has given you permission. You can record it with your microphone right in the app, and with Whisper the transcript writes itself.
- 📖 **Narrate long scripts** paragraph by paragraph, with pauses, whispers and changes of emotion marked in the text. If you change a sentence, only that paragraph is generated again.
- 🎬 **Dub videos from English into Spanish** keeping the speaker's voice: Calíope separates the voice from the music, transcribes, translates with an AI on your computer and says each sentence again with the original timbre and Spanish pronunciation, without an English accent. For now, videos with one person and up to 10 minutes long.
- 🎭 **Variants of the same voice**: whispering, tense, frightened, sad, cheerful or shouting.
- 🎚️ **Fine-tune the result**: expressiveness, speed and pitch (independent of each other), pauses and volume leveled for video or podcasts.
- 📤 **Get the audio as MP3 or WAV**, or drag it straight into your video editor.
- 🔒 **Everything stays private**: your texts and voices never leave your computer. The internet is only used to download the components and to check whether there's a new version of Calíope (you can turn that off in Settings).
- 🧩 **Installation with buttons**: a wizard checks your computer and suggests what to download. No need to touch Python or the command line.

The interface is available in Spanish and English: choose the language in *Settings*.

<p align="center">
  <img src="docs/capturas/narrar.png" width="860" alt="Narrate screen: script, voice, settings and generated blocks">
</p>

## Requirements

| | Minimum | Recommended |
|---|---|---|
| System | Windows 10 (1809) or 11, 64-bit | Windows 11 |
| Graphics card | None (it runs on the processor, much more slowly) | NVIDIA with 6 GB or more (RTX 3050 with 8 GB or better) |
| Memory | 8 GB of RAM | 16 GB or more |
| Disk | 10 GB free | 35 GB free (if you install all three engines) |

With a 4 GB NVIDIA card it works using the light model (0.6B). Of the optional engines, Fast Spanish (Chatterbox) needs 4.5 GB and doesn't work with the RTX 50 series, and Closest match (VoxCPM2) needs 8 GB.

## Installation

1. Download **`Caliope-x.y.z-Instalador.exe`** from [Releases](https://github.com/rboluda/Caliope/releases/latest).
2. Open it and follow the steps. It doesn't need administrator rights, and .NET is included.
   - If Windows SmartScreen warns that the program isn't recognized, click *More info → Run anyway*. The installer isn't digitally signed.
3. The first time you open Calíope, the **wizard** checks your computer and suggests what to install. Each component explains what it does, how much space it takes and whether it's essential.

The installer speaks Spanish or English, depending on your Windows language.

<p align="center">
  <img src="docs/capturas/inicio.png" width="420" alt="Computer check and recommendations">
  <img src="docs/capturas/componentes.png" width="420" alt="Installable components">
</p>

The installer is only about 50 MB. The engines and models are downloaded later, from the app:

| Component | Do I need it? | What for | Size |
|---|---|---|---|
| Basic tools (uv, Python and FFmpeg) | Essential | The base for everything else | 0.3 GB |
| Voice engine (Qwen3-TTS) | Essential | Generates the voice | 1.2–5 GB |
| High-quality voice (Qwen3-TTS 1.7B) **or** Light voice (Qwen3-TTS 0.6B) | Essential (one of the two) | Cloning voices and narrating | 4.3 / 2.4 GB |
| Voice designer (Qwen3-TTS VoiceDesign) | Recommended | Designing new voices | 4.3 GB |
| Automatic transcription (Whisper) | Optional | Transcribing recordings when cloning | 1.6 GB |
| Spanish engine (Chatterbox) | Optional | More speed and control over emotion; Spanish only | 4.9–8.4 GB |
| Closest-match engine (VoxCPM2) | Optional | The voice closest to the original; needs an 8 GB graphics card | 10.5 GB |
| Voice and music separator (Demucs) | For dubbing | Separates the voice from the video's background | 1.3–5 GB |
| AI translator (llama.cpp + Qwen3-4B) | For dubbing | Translates the script offline | 2.6 GB |

## How to use it

1. **Get a voice.** You can design one in “Design a voice”, clone one in “Clone a voice” or import a recording in “Voices”.
2. **Write the script** in “Narrate”, choose the voice and click “Generate narration”.
3. **Listen and touch up.** Each paragraph is a block: you can regenerate just the one you don't like.
4. **Get the audio out** with “Save as…”, by dragging it into your editor, or with “Export all…”.

<p align="center">
  <img src="docs/capturas/disenar.png" width="860" alt="Designing a voice from a description">
</p>

### Script tags

```text
# opening
It was Saturday night. [pause] It was pouring down.

# scare
Someone knocked at the door. [long pause]
[whispers] Don't open it. [normal] But she opened it.
[scared] Who's there?
```

| Tag | What it does |
|---|---|
| `[pause]` / `[long pause]` | Short or long pause (adjustable) |
| `[whispers]` | Whispers until the end of the line |
| `[scared]`, `[tense]`, `[happy]`… | Reads with that variant of the voice until the end of the line |
| `[voice: Name]` | Reads with another voice from your library until the end of the line |
| `[sfx: thunder]` | Plays a sound effect (thunder, heartbeat, clock, bell, gust or your own from “Mis sonidos”) |
| `[normal]` | Goes back to the normal voice |
| `# name` | Names the block (and its file) |
| `// note` | Comment, not read aloud |
| Blank line | Starts a new block |

Variant tags work in English or Spanish: `[tense]`/`[tenso]`, `[scared]`/`[asustado]`, `[sad]`/`[triste]`, `[happy]`/`[alegre]`, `[shout]`/`[grito]`, and `[whispers]` for whispering. In “Voices”, each variant shows its tag.

Inside the app there's a **Help** section with all of this, with the settings explained and with solutions to the most common problems.

## Voice engines

**The main engine is enough for everything.** The other two are optional and you don't need to try them: Calíope looks at your graphics card and tells you in “Components” whether either one suits you.

| | Main (Qwen3-TTS) | Fast Spanish (Chatterbox) | Closest match (VoxCPM2) |
|---|---|---|---|
| Author and license | Alibaba Qwen, Apache 2.0 | Resemble AI, MIT | OpenBMB, Apache 2.0 |
| What for | The most complete: designs voices and does whispers | Spanish-from-Spain accent, speed and control over emotion | The voice closest to the original and the best sound (48 kHz) |
| Speed (8 GB card) | Slow: about 3 times the length of the audio | The fastest: less than the length of the audio | Medium: twice the length of the audio |
| Similarity to the voice | Very high | Very high | The highest, especially with a transcript |
| Languages | 10 | Spanish only | 30 |
| Graphics card | Any (or none) | From 4.5 GB; not RTX 50 | From 8 GB; 10 GB is better |
| Watermark | No | Yes, inaudible | No |

All three read the text equally well: in a test with the same voice and the same text, none of them dropped or changed more than 3 words.

**Which one is right for you?** With no NVIDIA card or with less than 4.5 GB, the main one. Between 4.5 and 8 GB, the main one and, if you narrate in Spanish and want speed, Fast Spanish. With 8 GB, Closest match works, but only just. With 10 GB or more, Closest match for the final result.

In “Voices → Try” you can listen to the same voice with each installed engine.

## Frequently asked questions

**Do I need the internet?** Only to download the components the first time. After that it works offline. When it starts, Calíope checks GitHub for a new version (without sending anything of yours); you can turn that off in Settings.

**How long does it take?** With an RTX 3050, a few seconds per sentence. The first generation takes 20 to 60 seconds longer while the model loads. Without an NVIDIA card it works, but quite a bit more slowly.

**The cloned voice comes out cut off.** It's almost always because the transcript doesn't match the recording. Fix it in “Voices → Edit transcript”, or leave it empty to imitate only the timbre.

**Where are my voices and narrations saved?** In the folder you choose in the wizard (by default `%LOCALAPPDATA%\Caliope`), organized so it makes sense at a glance. The folders keep their Spanish names in every language:

```text
Mis narraciones\      your projects; each one's audio is in its "Audio" folder
Mis voces\            your voices and their variants
Mis sonidos\          sound effects and background ambience
Modelos de voz\       the voice models, which take up the most space
Programas internos\   engines and tools (don't touch)
LÉEME.txt             a short guide to the folder
```

In *Settings → Storage* you can see how much space each part takes and open it.

**What languages does it speak?** Spanish, English, French, German, Italian, Portuguese, Russian, Chinese, Japanese and Korean. You can choose the accent, for example Spanish from Spain or Latin American Spanish.

## Responsible use

Only clone your own voice or the voices of people who have given you **explicit permission**: Calíope asks you to confirm this before cloning. Don't use synthetic voices to impersonate, deceive or defame anyone, and say that a voice is synthetic whenever it could mislead the listener.

## For developers

Calíope is built in C# with .NET 10, WPF and [WPF-UI](https://github.com/lepoco/wpfui). The voice engines run in isolated Python processes, each with its own environment, which the app installs with [uv](https://github.com/astral-sh/uv).

```
dotnet build
dotnet test
dotnet run --project src/Constantino.App
pwsh instalador/crear-instalador.ps1   # builds the installer (needs Inno Setup 6)
```

There are more details in [docs/DESARROLLO.md](docs/DESARROLLO.md) (in Spanish: structure, engine protocol, history) and in [docs/DISENO.md](docs/DISENO.md) (in Spanish: full design). The source code, its comments and the internal names are in Spanish.

## Credits and licenses

Calíope exists thanks to the work of others. The main pieces:

| Tool | Author | License | What for |
|---|---|---|---|
| [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) | Alibaba (Qwen team) | Apache 2.0 | Main voice engine: cloning, designing and narrating |
| [Chatterbox Multilingual](https://github.com/resemble-ai/chatterbox) and its Spanish package | Resemble AI | MIT | Fast Spanish engine, with control over emotion |
| [VoxCPM2](https://github.com/OpenBMB/VoxCPM) | OpenBMB | Apache 2.0 | Closest-match engine |
| [Demucs](https://github.com/adefossez/demucs) | Meta | MIT | Separating the voice from the music when dubbing |
| [llama.cpp](https://github.com/ggml-org/llama.cpp) and [Qwen3-4B](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507) | ggml-org · Alibaba Qwen | MIT · Apache 2.0 | Translating videos |
| [Whisper](https://github.com/openai/whisper) | OpenAI | MIT | Automatic transcription and check |
| [PyTorch](https://pytorch.org) | PyTorch Foundation | BSD-3 | Running the models |
| [FFmpeg](https://ffmpeg.org) and [Rubber Band](https://breakfastquay.com/rubberband/) | FFmpeg · Breakfast Quay | GPL | Audio: volume, speed, pitch, mixing |
| [uv](https://github.com/astral-sh/uv) and [Python](https://www.python.org) | Astral · PSF | MIT/Apache 2.0 · PSF | Engine environments |
| [WPF-UI](https://github.com/lepoco/wpfui), [.NET](https://dotnet.microsoft.com), [NAudio](https://github.com/naudio/NAudio) | lepo.co · Microsoft · Mark Heath | MIT | The app |

The full list, with what each one does, is in [TERCEROS.en.md](TERCEROS.en.md) and inside the app, in *Settings → About Calíope*. Calíope doesn't redistribute the engines or the models: it downloads them from their official sources and shows you each license before installing.

Calíope's code, its logo and the sounds it creates are released under the [MIT license](LICENSE). Made by [rboluda](https://github.com/rboluda).
