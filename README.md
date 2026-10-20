# Constantino

Caja de herramientas de voz para Windows: instala con botones los motores de síntesis de voz que elijas (Qwen3-TTS, Whisper, Chatterbox) y te deja diseñar voces, clonarlas y crear narraciones sin tocar la línea de comandos.

El diseño completo está en [docs/DISENO.md](docs/DISENO.md).

## Estado

- **H0**: ventana principal con navegación, catálogo de componentes y análisis del equipo con recomendaciones.
- **H1**: asistente de primer arranque (6 pasos) e instalador real del núcleo (uv, Python 3.11, ffmpeg), el motor Qwen3-TTS y sus modelos. Descargas verificadas por SHA-256 o revisión exacta, reanudables y con reintentos; progreso con GB, velocidad y tiempo restante; reparar y desinstalar desde Componentes.

- **H2**: motor Qwen3-TTS persistente (proceso Python con protocolo JSON por líneas, en `motores/constantino_motor`) y biblioteca de voces: importar una grabación con su transcripción (o en modo «solo timbre»), escuchar, probar con cualquier texto, renombrar, corregir la transcripción, elegir la predeterminada y borrar.

Whisper y Chatterbox aparecen en el catálogo pero aún no se pueden instalar (H5 y H7).

### El motor de voz

La app lanza `python -m constantino_motor` con el Python del entorno del motor y le envía una petición JSON por línea (`info`, `cargar`, `descargar`, `sintetizar`, `salir`); el motor responde con eventos `progreso`, `resultado` o `error`. stdout queda reservado al protocolo y todo lo demás va a stderr. Con `CONSTANTINO_MOTOR_FALSO=1` usa un motor falso sin torch, que es el que prueban los tests (`CONSTANTINO_EXIGIR_PYTHON=1` hace que falten Python sea un fallo y no se salte la prueba).

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
Constantino.exe --captura salida.png --pagina PaginaComponentes
Constantino.exe --captura salida.png --pagina PaginaAsistente --paso 3
```

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/Constantino.App` | Interfaz WPF (WPF-UI, MVVM con CommunityToolkit) |
| `src/Constantino.Core` | Catálogo, análisis del equipo, recomendaciones e instalador, sin dependencias de la interfaz |
| `catalogo/catalogo.json` | Componentes instalables: qué son, para qué sirven, tamaño, requisitos, licencias y pasos de instalación |
| `tests/Constantino.Core.Tests` | Pruebas de xUnit |
| `docs/` | Diseño y capturas |
