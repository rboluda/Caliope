# Calíope — notas de desarrollo

Nombre interno del código: **Constantino** (los proyectos, espacios de nombres y la carpeta de datos se llaman así; lo visible es Calíope). El diseño completo está en [DISENO.md](DISENO.md).

## Crear el instalador

Necesita el SDK de .NET 10 e Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```
pwsh instalador/crear-instalador.ps1
```

Publica la aplicación autocontenida (con .NET, sin los motores ni los modelos, que se descargan desde la propia aplicación) en `publish/app` y deja `publish/Caliope-<versión>-Instalador.exe`. La versión sale de `Directory.Build.props`. El instalador es por usuario (sin administrador), en español e inglés, y al desinstalar conserva la carpeta de datos.

## Historia (hitos del diseño)

- **H0**: ventana principal con navegación, catálogo de componentes y análisis del equipo con recomendaciones.
- **H1**: asistente de primer arranque (6 pasos) e instalador real del núcleo (uv, Python 3.11, ffmpeg), el motor Qwen3-TTS y sus modelos. Descargas verificadas por SHA-256 o revisión exacta, reanudables y con reintentos; progreso con GB, velocidad y tiempo restante; reparar y desinstalar desde Componentes.

- **H2**: motor Qwen3-TTS persistente (proceso Python con protocolo JSON por líneas, en `motores/constantino_motor`) y biblioteca de voces: importar una grabación con su transcripción (o en modo «solo timbre»), escuchar, probar con cualquier texto, renombrar, corregir la transcripción, elegir la predeterminada y borrar.

- **H3**: pantalla Narrar. Proyectos con guion (bloques por párrafos, `# nombre`, comentarios `//`, etiquetas `[pause]`, `[long pause]` y `[whispers]`), ajustes de voz, expresividad, pausas, susurro, volumen (LUFS), semilla y formato. Genera bloque a bloque, solo rehace los que cambian (huella por bloque), normaliza con ffmpeg (ganancia + limitador), monta el audio completo y escribe `manifiesto.json` con la duración y el inicio de cada bloque.

- **H4**: pantalla Diseñar voz con VoiceDesign. Descripción guiada (sexo, edad, timbre, textura, ritmo, tono, idioma y acento) que se compone en inglés, ejemplos de partida y variantes con semillas distintas. La variante elegida se guarda tal cual como grabación de referencia, con el texto de muestra como transcripción exacta. El motor cambia solo entre los modelos Base y VoiceDesign según la tarea.

- **H5**: pantalla Clonar voz. Grabar con el micrófono (NAudio) leyendo un texto propuesto, o importar un audio; forma de onda con selección del tramo; análisis de la grabación (voz, saturación, volumen bajo, ruido de fondo, duración); transcripción con Whisper large-v3-turbo (instalable desde Componentes, usa el entorno del motor Qwen), con el texto leído o a mano; consentimiento obligatorio; probar y guardar.

- **H6**: velocidad (0,8–1,2×) y tono (±3 semitonos) en Narrar, aplicados con rubberband conservando los formantes, sin cambiar el tono al cambiar la velocidad ni al revés. Variantes de voz (susurro, tenso, asustado, triste, alegre, grito): grabaciones de la misma voz hablando de otra forma, añadidas desde un audio o, en las voces diseñadas, generadas con VoiceDesign con la misma descripción y semilla. En el guion se usan con su etiqueta (`[asustado]`… hasta `[normal]` o fin de línea); `[whispers]` usa la variante susurro si existe y si no baja el volumen. Los bloques generados antes siguen valiendo mientras no se toquen los ajustes nuevos. Sacar los audios: «Guardar como…», copiar al portapapeles o arrastrar la narración completa o cualquier bloque al editor de vídeo, y «Exportar todo…» a una carpeta.

- **H7**: Chatterbox Multilingual (Resemble AI) como motor alternativo, en su propio entorno (chatterbox-tts 0.1.7, torch 2.6.0 cu124, 80 dependencias fijadas, sin gradio) y descargando solo los 6 archivos del modelo multilingüe (3,2 GB de 12). Narrar tiene un selector de motor y, con Chatterbox, «intensidad emocional»; en Voces → Probar se puede escuchar la misma voz con cada motor. Solo hay un motor vivo a la vez en la tarjeta: cambiar de motor cierra el otro. La huella solo incluye el motor cuando no es Qwen.

### Proyectos de narración

`<datos>\proyectos\<id>\`: `proyecto.json` (ajustes y estado de cada bloque generado), `guion.txt` y `salida\` con `NN_nombre.mp3`, `<id>_completo.mp3` y `manifiesto.json`.

### El motor de voz

La app lanza `python -m constantino_motor` con el Python del entorno de cada motor (`CONSTANTINO_MOTOR=chatterbox` elige el adaptador de Chatterbox; sin ella, Qwen) y le envía una petición JSON por línea (`info`, `cargar`, `descargar`, `sintetizar`, `disenar`, `transcribir`, `salir`); el motor responde con eventos `progreso`, `resultado` o `error`. stdout queda reservado al protocolo y todo lo demás va a stderr. Con `CONSTANTINO_MOTOR_FALSO=1` usa un motor falso sin torch, que es el que prueban los tests (`CONSTANTINO_EXIGIR_PYTHON=1` hace que falten Python sea un fallo y no se salte la prueba).

### Dónde instala

En la carpeta de datos elegida en el asistente (por defecto `%LOCALAPPDATA%\Constantino`): `herramientas`, `python`, `entornos`, `modelos`, `cache`, `registros` y `estado.json`. Las preferencias van aparte, en `%LOCALAPPDATA%\Constantino\ajustes.json`. Los pasos de cada componente están en `catalogo/catalogo.json`.

## Compilar y ejecutar

Necesita el SDK de .NET 10.

```
dotnet build
dotnet test
dotnet run --project src/Constantino.App
```

### Capturas de la interfaz

La aplicación puede dibujarse a sí misma en un PNG y cerrarse, útil para revisar la interfaz o documentarla:

```
Caliope.exe --captura salida.png --pagina PaginaComponentes
Caliope.exe --captura salida.png --pagina PaginaAsistente --paso 3
```

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/Constantino.App` | Interfaz WPF (WPF-UI, MVVM con CommunityToolkit) |
| `src/Constantino.Core` | Catálogo, análisis del equipo, recomendaciones e instalador, sin dependencias de la interfaz |
| `catalogo/catalogo.json` | Componentes instalables: qué son, para qué sirven, tamaño, requisitos, licencias y pasos de instalación |
| `motores/constantino_motor` | Motor de voz en Python: protocolo, adaptadores de Qwen3-TTS (con Whisper) y Chatterbox, y un motor falso para las pruebas |
| `instalador/` | Script de Inno Setup, imágenes del asistente y `crear-instalador.ps1` |
| `recursos/` | Logotipo e icono |
| `tests/Constantino.Core.Tests` | Pruebas de xUnit |
| `docs/` | Diseño, estas notas y capturas |
