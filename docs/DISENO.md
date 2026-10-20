# Constantino — documento de diseño

Caja de herramientas de voz con interfaz gráfica: instala con botones los motores de síntesis de voz que el usuario elija y permite usarlos para diseñar voces, clonarlas, ajustarlas y crear narraciones, sin tocar la línea de comandos.

Estado: **borrador v0.1** (20/10/2026). Nace de las pruebas de `voz-narrador` (Qwen3-TTS, Chatterbox), pero empieza de cero.

---

## 1. Decisiones tomadas

| Tema | Decisión | Motivo |
|---|---|---|
| Interfaz | **.NET 10 + WPF + WPF-UI** (aspecto Fluent de Windows 11), patrón MVVM con CommunityToolkit.Mvvm | Un `.exe` autocontenido arranca en un Windows vacío y puede instalar todo lo demás; WPF es lo más maduro y estable en Windows |
| Usuarios | **Yo y otras personas** | Debe funcionar en PCs distintos (otra GPU, poca VRAM o sin GPU), explicar todo con claridad y respetar licencias |
| Motores | **Ninguno viene incluido.** Un asistente de configuración inicial (y después la pantalla de Componentes) deja elegir qué instalar, explicando qué hace cada cosa, para qué sirve y si es imprescindible | El usuario decide cuánto espacio y qué funciones quiere |
| Punto de partida | **De cero**: no reutiliza entornos, modelos ni voces de `voz-narrador` | Así se prueba el instalador como lo vivirá cualquier usuario |
| Plataforma | Windows 10/11 x64 | Los motores necesitan CUDA (NVIDIA) para ir rápidos; sin GPU funcionan en CPU, mucho más lento |

---

## 2. Arquitectura

```
┌──────────────────────────────────────────────────────────────┐
│ Constantino.exe  (.NET 10, WPF + WPF-UI, autocontenido)       │
│                                                              │
│  Vistas ─ ViewModels ─ Servicios                              │
│   · Asistente / Componentes  → InstaladorService (uv, descargas)│
│   · Voces / Diseñar / Clonar → MotorService (procesos Python)  │
│   · Narrar                   → NarracionService + AudioService │
│   · Ajustes                  → ConfigService                   │
└──────────┬───────────────────────────────────┬───────────────┘
           │ JSON por líneas (stdin/stdout)    │ procesos
           ▼                                   ▼
  Motor Qwen3-TTS  (python, entorno propio)   ffmpeg (velocidad, tono,
  Motor Whisper    (python, entorno propio)          volumen, MP3)
  Motor Chatterbox (python, entorno propio)
```

### 2.1 Por qué estas piezas

- **uv** (ejecutable único, licencia MIT/Apache) instala Python, crea entornos y paquetes muy rápido y sin depender de nada del sistema. Constantino lo descarga en el primer arranque.
- **Un entorno Python por motor**, como en las pruebas: las dependencias de cada uno chocan entre sí (Chatterbox fija `torch 2.6` y `transformers 5.2`; Qwen pide `transformers 4.57`).
- **Motores como procesos persistentes**: el modelo se carga una vez en la GPU y queda listo. La aplicación los arranca al necesitarlos y los para al cerrar o al pedir memoria para otro motor (en 8 GB de VRAM no caben dos a la vez).
- **Comunicación por stdin/stdout** con mensajes JSON de una línea: sin puertos de red, sin avisos del cortafuegos y el proceso muere con la aplicación.
- **ffmpeg** se descarga al instalar el núcleo (no se redistribuye con la app; las compilaciones completas son GPL). Hace falta una compilación con el filtro `rubberband` para cambiar tono y velocidad con calidad.

### 2.2 Protocolo aplicación ↔ motor

Cada mensaje es un objeto JSON en una línea, en UTF-8.

```jsonc
// petición (app → motor)
{"id": "7f3a", "op": "sintetizar", "params": {"voz": "narrador", "texto": "…", "semilla": 1234, …}}
// eventos (motor → app), tantos como haga falta
{"id": "7f3a", "tipo": "progreso", "valor": 0.42, "mensaje": "Generando frase 3 de 7"}
{"id": "7f3a", "tipo": "log", "nivel": "aviso", "mensaje": "Duración anómala en la frase 5"}
// final: exactamente uno de los dos
{"id": "7f3a", "tipo": "resultado", "datos": {"wav": "C:\\…\\tmp\\7f3a.wav", "duracion": 12.4, "vram_gib": 4.6}}
{"id": "7f3a", "tipo": "error", "codigo": "sin_memoria", "mensaje": "No hay VRAM suficiente…"}
```

Operaciones comunes a todos los motores: `info` (versión, dispositivo, VRAM), `cargar`, `descargar`, `cancelar`. Las específicas se listan en el §4. El audio viaja como **ruta a un WAV temporal**, no dentro del JSON.

El código Python de los motores (`constantino_motor`) vive en el repositorio y la app lo copia al entorno al instalar o actualizar, así que cada versión de la app lleva su motor emparejado.

---

## 3. Componentes instalables

Un **catálogo** (`catalogo.json`, incluido en la app) describe cada componente: qué es, para qué sirve, si es imprescindible, cuánto ocupa, qué requisitos tiene y de qué otros depende. El asistente y la pantalla de Componentes se generan a partir de él, así que añadir un motor nuevo es sobre todo añadir una entrada.

| Componente | ¿Imprescindible? | Para qué sirve (texto para el usuario) | Ocupa | Depende de |
|---|---|---|---|---|
| **Núcleo** (uv, Python 3.11, ffmpeg) | **Sí** | Las herramientas básicas sobre las que funciona todo lo demás | ~0,3 GB | — |
| **Motor Qwen3-TTS** (entorno + PyTorch) | **Sí, para generar voz** | El programa que convierte texto en voz. Sin él, Constantino no puede hablar | ~5 GB (CUDA) / ~1 GB (CPU) | Núcleo |
| Modelo **Base 1.7B** | Uno de los dos Base | Clonar voces y narrar. Mejor calidad. Recomendado con 6 GB de VRAM o más | ~4,5 GB | Motor Qwen |
| Modelo **Base 0.6B** | Uno de los dos Base | Lo mismo con menos calidad, para GPU de 4 GB o para CPU | ~2,5 GB | Motor Qwen |
| Modelo **VoiceDesign 1.7B** | No | Crear voces nuevas describiéndolas con palabras. Solo existe en 1.7B | ~4,5 GB | Motor Qwen |
| **Whisper** (entorno + modelo) | No | Transcribe solo tus grabaciones al clonar una voz. Sin él, la transcripción se escribe a mano | ~1,5 GB | Núcleo |
| **Chatterbox** (entorno + modelo) | No | Motor alternativo, más rápido y con control de intensidad emocional; hace menos pausas | ~8 GB | Núcleo |

Comportamiento del instalador:

- **Detecta el equipo antes de recomendar**: GPU NVIDIA y modelo, VRAM total y libre, driver, RAM y espacio libre en el disco de destino. Con eso propone una selección (por ejemplo: «Tu RTX 3050 tiene 8 GB: recomendamos Base 1.7B; VoiceDesign cabe»). Sin NVIDIA instala PyTorch para CPU y lo explica.
- **Cada paso es reanudable**: si se corta la conexión, continúa donde iba. Las descargas de modelos usan `huggingface_hub` con `HF_HOME` dentro de la carpeta de datos de Constantino.
- **Reparar** reinstala un componente sin tocar las voces; **Desinstalar** libera espacio y avisa si otra pieza depende de él.
- **Muestra la licencia de cada modelo** antes de instalarlo (Qwen3-TTS: Apache 2.0; Chatterbox: MIT; Whisper: MIT).
- **Nunca instala en OneDrive**: si la carpeta elegida está sincronizada, avisa y propone otra.

---

## 4. Pantallas

### 4.1 Asistente de primer arranque
1. Bienvenida: qué es Constantino y qué se va a instalar.
2. Análisis del equipo, con la recomendación.
3. Carpeta de datos, con aviso si está en OneDrive o si falta espacio.
4. Selección de componentes, en tarjetas con explicación, tamaño y una etiqueta de «Imprescindible», «Recomendado» u «Opcional». Muestra el total a descargar.
5. Instalación, con progreso por componente y registro desplegable.
6. Final: «Ya puedes crear tu primera voz» y accesos directos.

### 4.2 Componentes
Lo mismo que el paso 4 del asistente pero en cualquier momento: estado, versión, espacio ocupado y botones de instalar, reparar, actualizar y desinstalar.

### 4.3 Voces (biblioteca)
Lista de voces con reproductor de su muestra, origen (diseñada, clonada o importada), idioma y fecha. Permite renombrar, duplicar, borrar, exportar e importar (un `.zip` con la carpeta de la voz), marcar como predeterminada y gestionar sus **variantes** (§4.7).

### 4.4 Diseñar voz *(requiere VoiceDesign)*
- Descripción en lenguaje natural, con plantillas de ayuda (edad, sexo, timbre, ritmo, tono, acento) y ejemplos.
- Idioma y número de variantes (semillas) a generar.
- Texto de muestra para escucharlas, editable.
- Generar → lista de variantes con reproductor → **«Guardar como voz»**.
- Al guardar se genera la **referencia** leyendo un texto fijo, fonéticamente variado y de unos 15–20 s, en el idioma de la voz. Así la transcripción es exacta sin recortar a mano.

### 4.5 Clonar voz *(requiere un modelo Base)*
- Importar un audio o **grabar con el micrófono**, con medidor de nivel y consejos de grabación.
- Forma de onda con selección del tramo (recomendado 10–20 s) y aviso si hay ruido, saturación o silencio.
- Transcripción: automática con Whisper si está instalado, o a mano. Siempre editable.
- **Casilla obligatoria de consentimiento**: «Esta voz es mía o tengo permiso de su dueño para clonarla».
- Prueba de clonado con un texto → guardar como voz.

### 4.6 Narrar
- Proyecto de narración: guion, voz, ajustes y resultados (§5.2).
- Editor del guion con botones para insertar `[pause]`, `[long pause]` y `[whispers]` (y las etiquetas de variantes, §4.7), resaltado de etiquetas y división en bloques por líneas en blanco.
- Panel de ajustes: **voz**, **velocidad** (0,8–1,2), **tono** (±3 semitonos), **expresividad** (temperatura), **semilla**, duración de las pausas, intensidad del susurro, volumen objetivo (LUFS), formato y calidad de salida.
- Lista de bloques: estado, duración, reproducir y regenerar solo ese. Los bloques no modificados no se vuelven a generar.
- Exportar MP3 o WAV completo, bloques sueltos y un `manifiesto.json` para el montaje de vídeo.
- Estimación de tiempo antes de generar y botón de cancelar.

### 4.7 Variantes de una voz
Una voz puede tener variantes de la misma persona en otro tono («susurrando», «tenso», «alegre»), cada una con su propia referencia, creada diseñándola o grabándola. En el guion, `[whispers]` usa la variante «susurro» si existe y, si no, baja el volumen (como en las pruebas). Se podrán añadir etiquetas propias que apunten a otras variantes.

### 4.8 Ajustes
Carpeta de datos, dispositivo (GPU o CPU), liberar la VRAM al terminar, idioma de la interfaz (español primero, preparado para traducir), valores por defecto de narración y registro de diagnóstico.

---

## 5. Datos en disco

### 5.1 Carpeta de datos (elegida en el asistente)
```
<datos>\
  herramientas\   uv.exe, ffmpeg.exe
  python\         intérpretes gestionados por uv
  entornos\       qwen3tts\, whisper\, chatterbox\
  modelos\        caché de Hugging Face (HF_HOME)
  voces\          una carpeta por voz (§5.3)
  proyectos\      proyectos de narración (§5.2)
  tmp\            WAV intermedios (se limpia sola)
  registros\
  estado.json     qué componentes hay instalados y en qué versión
```

### 5.2 Proyecto de narración
```
proyectos\la_cazadora\
  proyecto.json   voz, ajustes y huella de cada bloque
  guion.txt
  salida\         01_bloque.mp3 …, la_cazadora_completo.mp3, manifiesto.json
```

### 5.3 Voz
```
voces\narrador\
  voz.json        nombre, idioma, origen, motor y modelo, descripción, semilla, transcripción, fecha, consentimiento
  referencia.wav
  muestra.mp3     para escucharla en la biblioteca
  variantes\susurro\ (referencia.wav + variante.json)
```

---

## 6. Qué se puede ajustar y cómo (límites honestos)

| Ajuste | Cómo se consigue | Límite |
|---|---|---|
| Timbre, edad, acento, ritmo y tono de base | Al **crear** la voz (descripción) o eligiendo la grabación | El acento con VoiceDesign es irregular; se prueban varias semillas |
| Velocidad | Postproceso con `rubberband` (no cambia el tono) | Bien con ±15 %; con más se nota |
| Tono | Postproceso con `rubberband` (no cambia la velocidad) | Bien con ±2–3 semitonos |
| Entonación | Puntuación del guion; expresividad (temperatura) | No hay control frase a frase |
| Emoción por frase | Variantes de la voz (§4.7) | El modelo Base no acepta instrucciones de estilo |
| Idioma | Parámetro del motor (10 idiomas en Qwen) | Puede quedar acento del idioma de la referencia |
| Pausas y susurro | Etiquetas del guion | — |

---

## 7. Estructura de la solución

```
Constantino\
  docs\                     este documento y guías
  src\
    Constantino.App\        WPF: vistas, ViewModels, recursos
    Constantino.Core\       modelos de datos, catálogo, servicios (sin WPF)
    Constantino.Motores\    cliente del protocolo JSON y gestión de procesos
  motores\
    constantino_motor\      paquete Python: protocolo + qwen.py, whisper.py, chatterbox.py
  catalogo\catalogo.json
  tests\
    Constantino.Core.Tests\
    motores\                pruebas de Python del protocolo y del lector de guiones
```

El lector de etiquetas y bloques del guion se implementa en C# (`Core`), y el motor recibe el texto ya troceado: así la interfaz puede mostrar los bloques y avisos sin arrancar Python.

---

## 8. Plan de entregas

| Hito | Contenido | Se puede probar… |
|---|---|---|
| **H0** | Solución .NET 10, ventana principal con navegación Fluent, catálogo y detección del equipo | Abrir la app y ver el análisis del PC |
| **H1** | Asistente e instalador del núcleo y del motor Qwen con un modelo Base | Instalar desde cero con botones |
| **H2** | Motor Qwen persistente con su protocolo; biblioteca de voces; importar una voz desde un WAV con su transcripción | Escuchar una frase con una voz |
| **H3** | Pantalla Narrar completa: bloques, etiquetas, regenerar, exportar MP3 y manifiesto | Narrar una historia entera |
| **H4** | Diseñar voz (VoiceDesign) con variantes y referencia automática | Crear una voz desde una descripción |
| **H5** | Clonar voz: grabación, recorte, Whisper, consentimiento | Clonar tu propia voz |
| **H6** | Velocidad y tono, variantes de voz, ajustes avanzados | Afinar el narrador |
| **H7** | Chatterbox como motor alternativo | Comparar motores |
| **H8** | Empaquetado e instalador para otras personas (Velopack o Inno Setup), actualizaciones, pruebas en un PC limpio | Pasárselo a otra persona |

---

## 9. Riesgos y preguntas abiertas

- **VRAM compartida con el escritorio**: en las pruebas, navegadores y Teams ocupaban ~4 GB de los 8. La app debe medir la VRAM libre antes de cargar un modelo y avisar o proponer el modelo 0.6B.
- **Rendimiento en CPU**: sin GPU, generar puede ser decenas de veces más lento que el tiempo real. Hay que medirlo y decirlo claramente en el asistente.
- **Windows sin Visual C++ Redistributable**: algunos paquetes lo necesitan; el núcleo debe comprobarlo e instalarlo si falta, pidiendo permiso.
- **Compilaciones de ffmpeg con rubberband**: elegir una fuente de descarga estable (p. ej. gyan.dev) y verificarla con su suma de comprobación.
- **Firma del ejecutable**: sin firma, SmartScreen avisará al instalar en otros PCs. Hay que decidir si se firma.
- **Actualizaciones de los motores**: fijar versiones exactas en el catálogo (p. ej. `qwen-tts==0.1.1`) y probarlas antes de cambiarlas.
- **Uso responsable**: consentimiento al clonar; recordar a los usuarios las normas de YouTube sobre contenido sintético.
