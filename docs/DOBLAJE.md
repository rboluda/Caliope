# Calíope · Módulo «Doblar» (diseño)

> Estado: **diseño aprobado, pendiente de empezar**. No hay nada desarrollado. Decisiones del usuario (22/10/2026):
> traducción con un **modelo de IA local grande**, hablantes **detectados automáticamente y corregibles**, solo a
> **castellano**, vídeos de hasta **10 minutos**, voz original de fondo **opcional** (ver §8).

## 1. Qué hace

Dobla un vídeo de un idioma a otro conservando las voces originales: un vídeo con dos personas hablando en inglés
sale en castellano con las mismas dos voces (clonadas), la misma música y los mismos efectos de fondo.

Va como apartado nuevo **«Doblar»** en el menú de la izquierda, **debajo de «Narrar» y separado por una línea**.

## 2. El proceso, paso a paso

| # | Paso | Cómo | Ya lo tiene Calíope |
|---|---|---|---|
| 1 | Abrir el vídeo | Arrastrar MP4/MOV/MKV; idioma de origen (detectado) y de destino | FFmpeg |
| 2 | Separar voz y fondo | Demucs: «voces» y «resto» (música + efectos) | — |
| 3 | Transcribir con tiempos | Whisper sobre la pista de voces: frases con inicio y fin | Whisper |
| 4 | Distinguir hablantes | Huella de voz de cada frase y agrupación; de cada hablante, el mejor tramo limpio de 5–15 s para clonarlo | Parecido (prueba del motor castellano) |
| 5 | Traducir | Modelo de lenguaje local con todo el guion como contexto; cada frase con su «presupuesto» de tiempo | — |
| 6 | Revisar | Lista de frases editable (ver §4) | Patrón de bloques de Narrar |
| 7 | Generar voces | Cada frase con la voz clonada de su hablante, en el idioma de destino (clonación entre idiomas) | Motores de voz |
| 8 | Encajar en el tiempo | Si sobra un poco, acelerar hasta 1,25× sin cambiar el tono; si sobra más, la IA propone una frase más corta y se regenera (hasta 2 intentos); si aun así no cabe, se marca | rubberband |
| 9 | Montar y exportar | Fondo + voces dobladas en su sitio; vídeo con la pista nueva (sin recodificar la imagen), audio suelto y subtítulos SRT | FFmpeg, montaje de Narrar |

Lo hecho se guarda por frase (huellas, como en Narrar): cambiar una traducción solo regenera esa frase.

## 3. Componentes nuevos (grupo «Doblaje de vídeo» en Componentes, todos opcionales)

| Componente | Qué es | Licencia | Tamaño aprox. | Notas |
|---|---|---|---|---|
| Separador de voz | Demucs (htdemucs) en su propio entorno | MIT | 1 GB con entorno | Hay que fijar versiones compatibles (riesgo: Demucs fija versiones antiguas de torchaudio) |
| Traductor local | llama.cpp (servidor, binario para Windows con CUDA) + modelo GGUF cuantizado a 4 bits | MIT + Apache 2.0 | ~5,5 GB | Candidatos: **Qwen3-8B** (mejor) y **Qwen3-4B** (tarjetas de 4–6 GB). Sin Python: menos cosas que puedan fallar al instalar |
| Huellas de voz | Codificador de hablante ligero | MIT / Apache 2.0 | < 0,1 GB | Opción A: VoiceEncoder de Resemble (6 MB, ya probado); opción B: ECAPA de SpeechBrain (más preciso). Se elige en la fase 0 |
| Transcripción | Whisper | MIT | ya está | Imprescindible |
| Voz | Motor principal (Qwen3-TTS) o VoxCPM2 | Apache 2.0 | ya están | Chatterbox castellano no sirve: solo acepta voces en español |

**Descartados:** NLLB (traductor no comercial, CC-BY-NC); pyannote (obliga a crear cuenta en Hugging Face y aceptar
condiciones); traductores en internet (cuenta, clave y el texto sale del equipo).

**Por qué un modelo de IA y no un traductor clásico:** traduce con el contexto de todo el vídeo (nombres, tono,
tú/usted), respeta un glosario y, sobre todo, puede **acortar una frase para que quepa** en el tiempo del original,
que es el problema principal del doblaje (el castellano ocupa un 15–25 % más que el inglés).

**Memoria de la tarjeta:** los pasos van de uno en uno y cada modelo se descarga de la tarjeta al terminar; así cabe
en 8 GB. Con 4–6 GB se propone el traductor de 4B. Sin tarjeta NVIDIA funciona, pero muy despacio (se avisa).

## 4. Pantallas

**Inicio del doblaje.** Zona para arrastrar el vídeo; vista previa; idioma de origen (detectado) y de destino
(los 10 del motor principal); motor de voz; «Doblar». Aviso de consentimiento como al clonar: solo vídeos y voces
que se tenga derecho a usar.

**Análisis.** Barra de progreso por fases (separar, transcribir, hablantes, traducir) con tiempo restante.
Se puede cerrar Calíope: al volver, sigue donde iba.

**Revisión** (el corazón del módulo):
- **Panel de hablantes:** cada uno con color, nombre editable, botón para escuchar su voz original, «unir con…»,
  y elegir con qué voz se dobla: la clonada del vídeo (por defecto) o una de «Mis voces».
- **Lista de frases:** tiempo, hablante (cambiable), texto original, traducción editable, indicador de encaje
  («cabe» / «justa» / «no cabe», con la velocidad necesaria), escuchar original / escuchar doblada, regenerar,
  «acortar con IA», varias tomas (como en Narrar).
- **Glosario:** palabras que no se traducen o se traducen siempre igual (nombres, marcas).
- **Reproductor** con el vídeo y la pista doblada sincronizados para comprobar el resultado.

**Exportar.** Vídeo doblado (MP4), solo el audio (WAV/MP3), subtítulos SRT del idioma de destino, y opción de
dejar la voz original muy baja por debajo (estilo documental).

**Datos:** carpeta nueva **«Mis doblajes»** (un proyecto por vídeo, con su audio y sus subtítulos; el vídeo original
no se copia, se enlaza).

## 5. Tiempos estimados (RTX 3050 8 GB, vídeo de 5 minutos con habla continua)

Separar ~1 min · transcribir ~1 min · hablantes < 1 min · traducir ~2–4 min · voces ~15 min con el motor principal
(~10 con VoxCPM2) · montaje < 1 min. **Total: unos 20–25 minutos.** Son estimaciones: se miden en la fase 0.

## 6. Fases propuestas

0. **Pruebas técnicas (antes de escribir la app).** Con 2–3 vídeos de prueba del usuario:
   - clonación entre idiomas inglés → castellano con Qwen3-TTS y VoxCPM2 (¿se mantiene la voz? ¿acento?);
   - Qwen3-8B frente a 4B: calidad, velocidad, memoria y si sabe ajustarse a una longitud;
   - precisión de la separación de hablantes (VoiceEncoder frente a ECAPA);
   - instalar Demucs con versiones compatibles; calidad de los tiempos de Whisper.
1. **Un hablante de punta a punta:** vídeo → separar → transcribir → traducir → voces → encajar → vídeo doblado,
   con la lista de frases editable y regenerar por frase.
2. **Varios hablantes:** detección automática, panel de hablantes, corregir y unir.
3. **Ajuste fino:** acortar con IA automático, glosario, tomas, voz original de fondo, subtítulos, reanudar.
4. **Pulido y publicación:** guía en Componentes, Ayuda, traducción al inglés de la interfaz, pruebas y versión 1.2.0.

Cada fase termina con algo que el usuario puede probar.

## 6 bis. Resultados de la fase 0 (22/10/2026, RTX 3050 8 GB)

Pruebas en `C:\ConstantinoRecursos\pruebas\doblaje` (fuera de Calíope), con voces de LibriVox y los dos vídeos del
usuario (`VIDEOSMUESTRA`: una persona, 7:44; dos personas, 3:31).

- **Voz en otro idioma: funciona.** Parecido con la voz original (0–1; personas distintas ≈ 0,67): motor principal
  0,92 en inglés → 0,90 en castellano; VoxCPM2 0,95 → 0,93. El motor principal pronuncia bien el castellano;
  VoxCPM2 arrastra acento inglés («joguera» en 3 de 4 voces). **Motor recomendado para doblar: el principal.**
- **Separar voz y fondo: Demucs 4.1.0** (MIT) se instala sin conflictos con PyTorch 2.11 (ya no fija torchaudio).
  7:44 de audio en 22 s, 0,5 GB de tarjeta, modelo de 81 MB.
- **Transcribir con tiempos:** Whisper con marcas de tiempo pierde los tiempos en audios largos (devolvió un bloque
  de 448 s). **Cortar antes la voz separada por sus pausas** y transcribir cada trozo da frases de 1–14 s con
  inicio y fin exactos (122 frases en el vídeo de 7:44, 38 s).
- **Hablantes:** huella de voz (VoiceEncoder de Resemble, MIT, 6 MB) cada 0,5 s con ventanas de 1,5 s y
  k-medias; se acepta el mayor número de hablantes (hasta 4) cuyos centros se parezcan menos de **0,87** (una
  persona partida a la fuerza: 0,97; dos personas: 0,83). Resultado: 1 hablante en el primer vídeo y 2 en el
  segundo. Las frases se cortan por pausas y solo se parten donde cambia el hablante durante ≥ 1 s.
- **Traductor:** llama.cpp b11819 + **Qwen3-4B-Instruct-2507 Q4_K_M** (Apache 2.0, 2,5 GB): mejor que Qwen3-8B
  (más rápido, 3,7 GB frente a 5,8 GB de tarjeta, y respeta mejor los límites). Con reglas de castellano de
  España y una segunda pasada de revisión, la calidad es de borrador aceptable (vosotros, sin calcos); quedan
  fallos sueltos (persona verbal, números en cifras: se resolverán con el normalizador de números propio).
  CUDA (726 MB) y Vulkan (99 MB) generan igual de rápido; Vulkan tarda más en procesar el texto de entrada.
- **Prototipo:** primer minuto de cada vídeo doblado de punta a punta (`videos\doblado`): 20 de 24 frases caben
  en su hueco (acelerando hasta 1,25×); 4 se pasan ligeramente. Voz generada a ~2,3× el tiempo real
  (60 s de audio en ~135 s), mejor de lo estimado: un vídeo de 10 minutos rondaría los 25–30 minutos en total.
- **Acento (problema grave del primer prototipo):** clonando con grabación + transcripción, el motor «continúa» el
  audio inglés y copia también la pronunciación: el usuario lo oyó con un acento inglés fortísimo, casi
  incomprensible (Whisper creía oír inglés el 28 % del tiempo). **Clonar solo el timbre** lo resuelve
  (7 frases de los dos vídeos):

  | Manera | Parecido | Suena a castellano | Palabras mal |
  |---|---|---|---|
  | Imitar la grabación entera (prototipo) | 0,90 | 61 % | 5/105 |
  | Motor principal, solo timbre | 0,85 | 97 % | 2/105 |
  | VoxCPM2, solo timbre | 0,89 | 92 % | 6/105 |
  | **Motor castellano (Chatterbox es-es)** | **0,88** | **97 %** | **2/105** |

  **Pero al oído las cuatro seguían con acento** (el usuario: «sigue teniendo el mismo problema»): Whisper entiende
  demasiado bien el castellano con acento para servir de juez. **Solución validada por el usuario («ahora sí, está
  bien»):** el motor castellano tiene dos etapas y se condicionan por separado:
  - **T3** (qué se dice y cómo: pronunciación, ritmo, entonación) ← una **voz castellana nativa** («Mi voz»).
  - **S3Gen** (timbre) ← la **voz del vídeo** de cada hablante.
  La grabación inglesa no toca la pronunciación, así que el acento desaparece; el timbre sigue siendo el del
  vídeo (parecido medido 0,86). Prototipo en `videos\doblado_mezcla`; el minuto se genera en ~50 s.

  **Consecuencias para el diseño:**
  - El doblaje usa el **motor castellano** (componente obligatorio para «Doblar»). Problema: no funciona en las
    RTX 50; para ellas habrá que buscar otra vía (conversión de voz con otro modelo) más adelante.
  - Hace falta una **voz de pronunciación nativa** por hablante: por defecto una de «Mis voces» (la
    predeterminada) y, mejor, una masculina y otra femenina incluidas con Calíope (grabaciones de dominio público
    o diseñadas), elegidas según el tono del hablante. El usuario podrá cambiarla en el panel de hablantes.
  - El adaptador del motor castellano necesita una operación nueva: sintetizar con dos referencias
    (pronunciación y timbre).
- **Red:** en la red de la oficina, uv necesitó `UV_NATIVE_TLS=1` (TLS de Windows) para descargar paquetes.
  Conviene que Calíope lo use si la conexión normal se corta.

## 7. Riesgos y límites conocidos

- **Clonación entre idiomas:** la voz puede perder parecido o arrastrar acento inglés. Es lo primero que se mide.
- **Sincronía de labios:** no se intenta; se respetan los tiempos de cada frase, no el movimiento de la boca.
- **Voces solapadas, gritos o música muy alta:** la separación y la detección de hablantes fallan más.
- **Demucs** y sus versiones antiguas de dependencias: puede obligar a un entorno propio.
- **Legal:** doblar vídeos ajenos o clonar voces de otras personas requiere permiso; se avisa como al clonar.

## 8. Decisiones del usuario (22/10/2026)

1. **Idioma de destino:** solo **castellano** en la primera versión (el de origen, el que detecte Whisper). Los demás
   idiomas del motor principal, más adelante.
2. **Duración máxima:** **10 minutos** de vídeo en la primera versión; con más, se avisa y no se empieza.
3. **Voz original de fondo:** **sí, como opción** al exportar (por defecto, quitada).
4. **Vídeos de prueba:** los busca el usuario (uno con una persona y otro con dos o más). La fase 0 empieza cuando
   los tenga y lo diga.

## 9. Plan de la fase 1 (listo para empezar cuando el usuario diga «Adelante»)

Objetivo: doblar en Calíope un vídeo de **un hablante** de punta a punta, con el método validado en la fase 0
(pronunciación de voz nativa + timbre del vídeo). Los scripts del prototipo, con el flujo que funcionó, están en
`C:\ConstantinoRecursos\pruebas\doblaje\scripts` (ver su LÉEME); se reescriben dentro de Calíope, no se copian tal cual.

**1. Componentes (catálogo, grupo nuevo «Doblaje de vídeo»).**
- `separador-voz`: entorno propio `demucs`, Python 3.11, torch 2.11 (`{indiceTorch}`), `demucs==4.1.0`, numpy,
  soundfile, versiones fijadas; modelo htdemucs descargado a «Modelos de voz» (81 MB). Comprobar la descarga del
  peso (Demucs 4.1 usa huggingface_hub) y fijar su revisión.
- `traductor`: llama.cpp b11819 (zip **Vulkan**, 99 MB, recomendado por tamaño; CUDA 726 MB va algo más rápido
  procesando el texto) + `unsloth/Qwen3-4B-Instruct-2507-GGUF` rev `a06e946bb6b655725eafa393f4a9745d460374c9`,
  archivo `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` (2,5 GB). Pasos `descargar` + `extraer` + `modelo`/`descargar`.
- Dependencias: Whisper y el motor castellano (Chatterbox) obligatorios para «Doblar».
- Red: si uv falla con «os error 10054», reintentar con `UV_NATIVE_TLS=1` (visto en la red de la oficina).

**2. Motores (Python, `motores/constantino_motor`).**
- `chatterbox.py`: operación nueva `sintetizar_doblaje` con `referencia_pronunciacion` (voz nativa → T3) y
  `referencia_timbre` (voz del vídeo → S3Gen), con caché de condiciones por referencia.
- Nuevo `doblaje.py` (entorno qwen3tts, Whisper): `trocear_y_transcribir` (cortar por pausas: top_db 38, pausa
  mínima 0,35 s, máximo 15 s; luego Whisper por lotes) y elegir la referencia del hablante (tramo de 6–12 s).
- Nuevo proceso para Demucs (`separar`) en su entorno.
- Traductor: Calíope arranca `llama-server` (puerto libre, `--jinja`, `-ngl 99`, `-c 16384`) y le habla por HTTP;
  prompts de `doblaje_traductor2.py` (reglas de castellano de España + revisión + acortar).

**3. Core (`src/Constantino.Core/Doblaje/`).**
- `ProyectoDoblaje` en «Mis doblajes/<id>/proyecto.json»: vídeo (ruta, no copia), duración, frases
  {id, hablante, inicio, fin, en, es, estado, huella} y hablantes {id, nombre, tramo de referencia, voz de
  pronunciación}. Límite 10 minutos.
- `Doblador`: extraer audio (FFmpeg) → separar → transcribir → traducir → sintetizar → encajar (rubberband hasta
  1,25×; si no cabe, «acortar con IA» y regenerar, hasta 2 intentos; si aún no cabe, marcar) → montar (fondo +
  frases con `adelay`; voz original opcional al 12 %) → MP4 (vídeo copiado), WAV/MP3 y SRT. Reanudable y por
  huellas: cambiar una frase solo regenera esa.
- Pruebas: encaje en el hueco, SRT, guardado del proyecto, plan de pasos, textos en `en.json` (IdiomaTests).

**4. App.**
- Menú: «Doblar» debajo de «Narrar», separado por una línea (`VentanaPrincipal.xaml`).
- `PaginaDoblar` + `DoblarViewModel`: arrastrar vídeo → progreso por fases → lista de frases (original,
  traducción editable, encaje cabe/justa/no cabe, escuchar, regenerar, acortar con IA) → exportar (con o sin voz
  original de fondo). Voz de pronunciación: por defecto la predeterminada de «Mis voces».
- Ayuda, Componentes (guía) y traducción al inglés de todos los textos nuevos.

**Fuera de la fase 1:** varios hablantes (fase 2: VoiceEncoder + k-medias, umbral 0,87, ya probado), glosario,
tomas, voces de pronunciación incluidas (hombre/mujer), RTX 50.
