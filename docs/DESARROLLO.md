# Calíope — notas de desarrollo

Nombre interno del código: **Constantino** (los proyectos, espacios de nombres y la carpeta de datos se llaman así; lo visible es Calíope). El diseño completo está en [DISENO.md](DISENO.md).

## Crear el instalador

Necesita el SDK de .NET 10 e Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```
pwsh instalador/crear-instalador.ps1
```

Publica la aplicación autocontenida (con .NET, sin los motores ni los modelos, que se descargan desde la propia aplicación) en `publish/app` y deja `publish/Caliope-<versión>-Instalador.exe`. La versión sale de `Directory.Build.props`. El instalador es por usuario (sin administrador), en español e inglés, y al desinstalar conserva la carpeta de datos.

### Firma de código

Cada versión sale firmada sola con el certificado personal de firma de código de rboluda: autofirmado, `CN=rboluda`, huella SHA1 `AFCC10CD1778706726F64437CE0AC9D35A8ED4E5`, válido hasta el 03/11/2031. No es el certificado de Azor: nunca se usa AzorCodeSign.

- El certificado está fuera del repositorio, en `%USERPROFILE%\.firma\`. Allí están `rboluda-firma-codigo.pfx` (clave privada), `rboluda-firma-codigo.clave` (su contraseña, que `firmar.ps1` lee al firmar) y `rboluda-firma-codigo.cer` (público). El .pfx y la .clave no se suben nunca al repositorio ni a un CI, y la contraseña no se escribe en ningún archivo del proyecto.
- `instalador/firmar.ps1 <archivo>` firma con el `signtool.exe` de versión más alta del Windows SDK (`Windows Kits\10\bin\*\x64`). Usa SHA-256 y el sello de tiempo de DigiCert, así que la firma sigue valiendo cuando caduque el certificado. Si no encuentra el .pfx o la .clave (otra persona compilando), avisa y sigue sin firmar.
- `crear-instalador.ps1` firma `publish\app\Caliope.exe` después de `dotnet publish`. Si está el certificado, también define en ISCC la herramienta `firma` y `Firmar`. Con eso, `Caliope.iss` activa `SignTool=firma` y `SignedUninstaller=yes`, e Inno firma el instalador y el desinstalador (`unins000.exe`).
- Solo se firman esos tres ejecutables, que son nuestros. El resto de `publish\app` (.NET, WPF-UI, NAudio, CommunityToolkit…) va tal como lo publican sus autores. `Caliope.dll` y `Constantino.Core.dll` tampoco se firman.
- El actualizador automático no comprueba ninguna huella: solo descarga el instalador de la última versión de GitHub.
- Para comprobarlo: `Get-AuthenticodeSignature <exe>` debe mostrar `CN=rboluda` y el sello de DigiCert. El estado «UnknownError» es normal: la firma es correcta, pero el certificado autofirmado no es de confianza en ese equipo.

## Historia (hitos del diseño)

- **H0**: ventana principal con navegación, catálogo de componentes y análisis del equipo con recomendaciones.
- **H1**: asistente de primer arranque (6 pasos) e instalador real del núcleo (uv, Python 3.11, ffmpeg), el motor Qwen3-TTS y sus modelos. Descargas verificadas por SHA-256 o revisión exacta, reanudables y con reintentos; progreso con GB, velocidad y tiempo restante; reparar y desinstalar desde Componentes.

- **H2**: motor Qwen3-TTS persistente (proceso Python con protocolo JSON por líneas, en `motores/constantino_motor`) y biblioteca de voces: importar una grabación con su transcripción (o en modo «solo timbre»), escuchar, probar con cualquier texto, renombrar, corregir la transcripción, elegir la predeterminada y borrar.

- **H3**: pantalla Narrar. Proyectos con guion (bloques por párrafos, `# nombre`, comentarios `//`, etiquetas `[pause]`, `[long pause]` y `[whispers]`), ajustes de voz, expresividad, pausas, susurro, volumen (LUFS), semilla y formato. Genera bloque a bloque, solo rehace los que cambian (huella por bloque), normaliza con ffmpeg (ganancia + limitador), monta el audio completo y escribe `manifiesto.json` con la duración y el inicio de cada bloque.

- **H4**: pantalla Diseñar voz con VoiceDesign. Descripción guiada (sexo, edad, timbre, textura, ritmo, tono, idioma y acento) que se compone en inglés, ejemplos de partida y variantes con semillas distintas. La variante elegida se guarda tal cual como grabación de referencia, con el texto de muestra como transcripción exacta. El motor cambia solo entre los modelos Base y VoiceDesign según la tarea.

- **H5**: pantalla Clonar voz. Grabar con el micrófono (NAudio) leyendo un texto propuesto, o importar un audio; forma de onda con selección del tramo; análisis de la grabación (voz, saturación, volumen bajo, ruido de fondo, duración); transcripción con Whisper large-v3-turbo (instalable desde Componentes, usa el entorno del motor Qwen), con el texto leído o a mano; consentimiento obligatorio; probar y guardar.

- **H6**: velocidad (0,8–1,2×) y tono (±3 semitonos) en Narrar, aplicados con rubberband conservando los formantes, sin cambiar el tono al cambiar la velocidad ni al revés. Variantes de voz (susurro, tenso, asustado, triste, alegre, grito): grabaciones de la misma voz hablando de otra forma, añadidas desde un audio o, en las voces diseñadas, generadas con VoiceDesign con la misma descripción y semilla. En el guion se usan con su etiqueta (`[asustado]`… hasta `[normal]` o fin de línea); `[whispers]` usa la variante susurro si existe y si no baja el volumen. Los bloques generados antes siguen valiendo mientras no se toquen los ajustes nuevos. Sacar los audios: «Guardar como…», copiar al portapapeles o arrastrar la narración completa o cualquier bloque al editor de vídeo, y «Exportar todo…» a una carpeta.

- **H7**: Chatterbox Multilingual (Resemble AI) como motor alternativo, en su propio entorno (chatterbox-tts 0.1.7, torch 2.6.0 cu124, 80 dependencias fijadas, sin gradio) y descargando solo los 6 archivos del modelo multilingüe (3,2 GB de 12). Narrar tiene un selector de motor y, con Chatterbox, «intensidad emocional»; en Voces → Probar se puede escuchar la misma voz con cada motor. Solo hay un motor vivo a la vez en la tarjeta: cambiar de motor cierra el otro. La huella solo incluye el motor cuando no es Qwen.

- **1.1**: tres motores. Chatterbox pasa a la v3 con el paquete de castellano de España (código de GitHub 5de7a54; del repositorio general solo `ve.pt` y `conds.pt`, el resto de `Chatterbox-Multilingual-es-es`; el adaptador monta T3 y S3Gen a mano porque `from_pretrained` no conoce el paquete) y llega VoxCPM2 (OpenBMB, torch 2.11 cu128, 66 dependencias fijadas e instaladas con `--no-deps`, sin gradio, modelscope, funasr ni datasets; sin limpiador de ruido ni `torch.compile`). `GuiaMotores` compara los tres y da un consejo según la memoria de la tarjeta, en «Componentes» y en el motivo de cada recomendación. La huella del modelo junta las revisiones de todos sus pasos `modelo`, así que pasar de Chatterbox v2 a v3 regenera lo hecho con él. Con VoxCPM2 no cuentan ni la intensidad ni la expresividad.

- **1.1 · idiomas**: interfaz en español e inglés. Los textos se escriben en español y ese texto es la clave: `T("…")` en C# (`using static Constantino.Core.Idioma`), `{l:T '…'}` en el XAML, y el catálogo se traduce al cargarse. Las traducciones están en `src/Constantino.Core/Idiomas/en.json` (recurso incrustado); `IdiomaTests` falla si un texto no tiene traducción o pierde un hueco `{0}`. Los motores de Python reciben `CONSTANTINO_IDIOMA` y traducen sus mensajes con `textos.py`. El guion admite también etiquetas en inglés (`[voice: …]`, `[scared]`, `[sfx: thunder]`…). Las carpetas de datos conservan sus nombres en español. Cambiar de idioma pide reiniciar.

- **1.3 · varias personas y actas**: «Doblar» distingue hasta 4 personas y dobla a cada una con su timbre (ver DOBLAJE.md §10). Módulo nuevo «Actas» (`Constantino.Core.Actas`): `Secretario` transcribe con Whisper (`transcribir_tramos` con `unir_cortos`: junta los trozos de menos de 1 s y quita lo que Whisper inventa), distingue a las personas con la huella x-vector del modelo Base de Qwen3-TTS (`hablantes_tramos`: ventanas de 3 s, k-medias, silueta ≥ 0,4 y cada persona ≥ 10 % del tiempo, o el número que diga el usuario), la IA propone los nombres y redacta el acta con la fecha de la reunión, el glosario y el minuto de cada punto; las reuniones de más de 60 000 caracteres se resumen por partes. `ServidorIA` (llama-server) lo comparten el traductor y las actas (32k de contexto con caché en 8 bits). `DocumentoWord` escribe el .docx a mano (zip con cinco XML). Datos en «Mis actas».

- **1.3.1 · susurro fabricado**: comparación a ciegas con ElevenLabs v4 (mismo texto, 5 rondas, notas del usuario). El motor principal con una referencia expresiva y una variante de susurro grabada se acerca en narración tranquila, tensión y final; VoxCPM2 (también con indicaciones de estilo), Chatterbox y una ecualización «de brillo» no mejoran al oído. El motor copia el tono de la referencia (consejo nuevo al clonar y en la Ayuda) y un susurro de referencia de menos de 3 s no sale susurrado. Variante «susurro» fabricada sin grabarla: `retoque.py` analiza la voz con WORLD (pyworld 0.3.5, MIT), pone f0 = 0 y aperiodicidad total, sube los formantes un 6 % y quita lo de debajo de 300 Hz (op `susurrar`, sin modelo). Suena peor que un susurro grabado (4/4/4 frente a 4/4/5 en el final), pero mucho mejor que bajar el volumen. Un primer intento con LPC + ruido sonaba «horrible» al clonarlo: el motor copia los defectos de la referencia.

- **1.6 · voz en directo (beta)** (componente `voz-en-directo`, motor `endirecto.py`, `ServicioEnDirecto`, `PaginaEnDirecto`):
  - Fase 0 (17/11/2026, RTX 3050, lectura con acento de España, conversión simulada bloque a bloque):
    - MeanVC2 120 ms (Apache 2.0) en un núcleo del procesador: 0,33 del tiempo real; Whisper la entiende entera; conserva la zeta (−12/−13 dB, como el original); parecido 0,955.
    - Seed-VC tiny (fork seed-vc-realtime, GPL-3.0) en la tarjeta: con bloques de 0,18 s va justo (179 ms por bloque); con 0,25 s y 6 pasos, 130 ms. Parecido 0,966; la zeta se debilita (−6 dB). En la escucha a ciegas gustó más.
    - Por eso hay dos modos: «calidad» (Seed-VC) y «ligero» (MeanVC2).
  - Su propio proceso y servicio (no `ServicioMotor`): el audio corre sin parar, no puede esperar turno ni apagarse por reposo. El proceso abre el micrófono (o lee un archivo, «Probar con una grabación») con sounddevice por MME y escribe en una o dos salidas a la vez (auriculares y cable). Operaciones: `dispositivos`, `cargar`, `iniciar`, `estado` (la aplicación pregunta cada 0,5 s), `parar`.
  - Seed-VC se usa sin modificar: el adaptador sustituye su `load_custom_model_from_hf` (modelos sin conexión en la revisión del catálogo), carga FSMN-VAD desde su copia en Hugging Face (no desde ModelScope) y llama a `_process_block` bloque a bloque. MeanVC2: se cambian las rutas de `run_rt` y se usa `VCRunner.process_chunk`. La configuración de WavLM-Large va escrita en el adaptador (el modelo base ya no está en GitHub y solo hacía falta su cfg); los pesos del codificador de hablante salen del enlace oficial de UniSpeech en Google Drive, con SHA-256.
  - Salida: un bloque de margen; si se vacía, calla y vuelve a juntar el margen (MeanVC2 entrega a saltos de 120 ms).
  - «Salida de Calíope» (`SalidaCaliope`): la licencia que acompaña a VB-CABLE no permite meterlo en el instalador de otro programa sin permiso del autor, así que Calíope lo descarga de la web oficial (Pack46, SHA-256 fijo) cuando el usuario lo pide y abre el instalador de VB-Audio en un proceso propio con permisos de administrador (`Caliope.exe --preparar-salida`). Después renombra los extremos con la API de sonido de Windows (`PKEY_Device_DeviceDesc`, como el panel de sonido): «Salida de Calíope» (el que ve Teams) y «Calíope (uso interno)».
  - Sin probar aún en un equipo con micrófono ni con VB-CABLE instalado (el de desarrollo no tiene ninguno de los dos).
  - 1.6.1, tras la primera prueba en un portátil con micrófono (17/11/2026). La cadena funciona: micrófono → Calíope → «Salida de Calíope» → OBS o la Grabadora de sonido. Se vieron dos problemas:
    - Al instalar VB-CABLE, Windows lo dejó como dispositivo predeterminado de grabación y de reproducción, y no se oía nada. Se arregla a mano en Sonido › Predeterminar.
    - Los números de dispositivo de MME cambian cuando Windows cambia el predeterminado, porque el predeterminado va el primero. Con la lista vieja, la salida al cable acabó sonando por los altavoces y se acopló. Ahora `iniciar` recibe también los nombres (`entrada_nombre`, `salidas_nombres`), vuelve a leer la lista y busca cada dispositivo por su nombre. Si dos nombres coinciden (MME los corta a 31 letras), decide el número. Si el nombre ya no está, avisa en vez de abrir otro dispositivo.
  - 1.7.1: VB-CABLE instala dos entradas en Windows de 64 bits: «CABLE Input» y «CABLE In 16 Ch». `SalidaCaliope` renombra las dos como «Calíope (uso interno)». Si la de 16 canales salía primero en la lista de MME, no se abría y daba «Device unavailable [PaErrorCode -9985]». Ahora cada dispositivo llega como una lista de candidatos con el mismo nombre: primero van los que pasan `check_*_settings`, y al abrir se prueban en orden. Si falla una salida, las ya abiertas se cierran. El error dice qué dispositivos se intentaron.

- **1.7.3 · comprobar actualizaciones**: si «Buscar ahora» no podía consultar GitHub (sin red, límite de 60 consultas por hora por red, error), decía «Tienes la última versión» aunque fuera mentira. Pasó con una 1.2.3. Ahora `ServicioActualizaciones.ComprobarAhoraAsync` devuelve el motivo y Ajustes lo muestra. Al arrancar, un fallo sigue sin avisarse.

- **Limpiar la grabación al clonar** (`LimpiezaGrabacion` en Core, `ClonarViewModel.LimpiarAsync`):
  - Se limpia la grabación entera, siempre a partir del original, y se puede deshacer o escuchar el mismo tramo del original.
  - Pasos, en este orden:
    1. Reparar la distorsión: FFmpeg `volume=-6dB,adeclip`, para que los picos reconstruidos quepan.
    2. Quitar la música: el separador de voz, Demucs `op_separar`, el mismo de Doblar. Tarda 2 s con 30 s de audio en la RTX 3050.
    3. Quitar el ruido: FFmpeg `afftdn=nr=10:nf=…:tn=1`, suave. Tras Demucs usa nf=-50; sin Demucs, el ruido medido por `AnalisisGrabacion`.
    4. Igualar el pico a −1 dBFS.
  - Medido con voz real + música + ruido rosa + recorte: saturación del 0,058 % al 0,001 %, fondo de −7 a −54 dB respecto a la voz.
  - Suave a propósito: el clon copia los defectos de un procesado agresivo.

- **1.7.2 · narración completa**: ffmpeg escribía el `_completo` directamente encima del anterior. Si estaba abierto en otro programa, fallaba con «Permission denied», se quedaba el viejo (hasta de otra voz) y Narrar lo seguía mostrando como si fuera el nuevo. Ahora se codifica en la carpeta temporal y se mueve encima (`Narrador.SustituirAsync`, 10 intentos cada 300 ms; si sigue abierto, un error que dice qué cerrar). Narrar no muestra un completo más antiguo que el bloque más nuevo.

- **Espacio** (página `PaginaEspacio`, `Espacio` en Core, `ServicioEspacio`):
  - uv instala los paquetes en su caché y los entornos usan esos mismos archivos (enlaces duros). PyTorch, por ejemplo, es un único archivo aunque aparezca en tres entornos y en la caché. Quitar un entorno no libera nada mientras otro nombre del archivo siga existiendo.
  - Por eso `Espacio.Medir` identifica cada archivo de 64 KB o más por su número en el disco (`GetFileInformationByHandle`) y cuenta cuántos nombres tiene y dónde. Un archivo se da por liberado solo si todos sus nombres están en lo que se borra. Los archivos pequeños se cuentan sin identificar: los de la caché como no liberables y los demás como de su carpeta. Con 48 GB tarda 4 s en vez de 20, con 0,15 GB de error como mucho por componente.
  - `Desinstalar` ya no deja atrás los modelos de Hugging Face del componente (antes se quedaban los de «Convertir voz» y «Voz en directo»), salvo los que use otro componente instalado, y vacía la caché de uv: los demás entornos conservan sus enlaces. No se puede desinstalar durante una instalación.
  - Limpieza sin perder nada: la caché, los restos (modelos y entornos sin componente instalado y la carpeta `modelscope`), las descargas a medias, los temporales de más de una hora y los registros.
  - Lo del usuario (narraciones, doblajes y actas) va a la papelera de Windows. Las voces se quitan desde «Mis voces».

- **1.5 · conversión de voz** (componente `conversion-voz`, idea vista en VoiceStudio, sin su código):
  - CosyVoice 3 (`inference_vc`, Apache 2.0) en su propio entorno con PyTorch 2.6 cu124, el del motor castellano, que también funciona aunque ellos fijen la 2.3.1.
  - El código se descarga de GitHub en commits fijos (CosyVoice 074ca6d, Matcha-TTS dd9105b) con SHA-256. Del modelo solo se bajan las piezas necesarias (5,4 GB de 9,75).
  - Su normalizador de texto (wetext) se descarga de ModelScope, que llegó a responder 403. La conversión no lee textos, así que el adaptador pone un normalizador vacío y no hace falta conexión.
  - Las grabaciones largas se trocean por silencios (25 s como mucho) y cada trozo convertido vuelve a su sitio.
  - Medido: parecido 0,982 con la voz destino, mismo ritmo, acento de España conservado; 8,5 s por 14 s de audio y 3,4 GB de VRAM.
- **1.5 · doblaje**:
  - **«Prosody Mirror»** (op `prosodia`, WORLD): volumen, tono y movimiento de la entonación de cada frase original. `Doblador.Intensidades` lo compara con la mediana de esa persona → intensidad de Chatterbox = 0,5 + 0,04·Δvolumen(dB) + 0,04·Δmovimiento(st), entre 0,3 y 0,85, con 0,3 si casi no tiene tono (susurro).
    - Medido en la leyenda: el grito da unos 0,71, los susurros 0,3 y las frases normales 0,5.
    - La intensidad entra en la huella solo si no es 0,5.
  - **Reanudar:** las frases dobladas ya se guardaban una a una. La traducción ahora también guarda tras cada tanda.
- **1.5 · narración (ideas de VoiceStudio, sin su código)**:
  - **Etiquetas finas:** `[pause 2s]`, `[lento]`/`[rápido]`/`[velocidad 1.2]` (rubberband por línea; la velocidad entra en la huella solo si no es 1), `[emoción: X]` (la variante de la voz) y `[[escrito|dicho]]`.
  - **Ruido de sala** (`RuidoDeSala`): el 15 % de tramos de 20 ms más tranquilos de las tomas, unidos con fundidos, rellenan las pausas. Desactivado por defecto: así no cambia la huella de lo ya generado.
  - **Subtítulos:** op `palabras` (Whisper con tiempo por palabra). `Subtitulos.Alinear` empareja por formas normalizadas (cifras a palabras) con distancia de edición y reparte las palabras sin pareja. Salen un SRT por frases equilibradas y un ASS karaoke a 1080×1920.
  - **Liberar la GPU:** el motor se apaga tras 5 minutos sin uso; si se queda sin memoria, se cierra y se reintenta una vez.
- **1.5 · mezclar voces y «Más así» con varios cambios**:
  - **Mezclador** (op `mezclar` del motor castellano): T3 de la voz de la «forma de hablar» y S3Gen de la del timbre. Con «acercar», la huella de S3Gen se mezcla con la de la otra voz (de 0 a 0,5).
  - Medido con dos voces muy distintas:
    - el ritmo y el acento siguen exactamente a T3 (12,2 s frente a 14,1 s; −8,8 frente a +0,1 dB);
    - el timbre sigue a S3Gen (parecido 0,98);
    - el tono sale a medias (139 Hz entre 98 y 181);
    - mezclar huellas de S3Gen es un efecto suave y asimétrico; mezclar la de T3 no cambia nada.
  - Después, el motor principal clona el resultado: parecido 0,985 y −12,8 dB.
  - **«Más así»**:
    - Se pueden marcar varios cambios a la vez; los contrarios se excluyen.
    - Fuerzas «clearly», «much» y «very much». Si solo falla que un cambio no se nota, se sube la fuerza con la misma semilla; si falla otra cosa, se cambia de semilla.
    - El diseñador responde mal: «más pausada» salió más rápida en 6 de 6 pruebas y el tono cambia de forma errática. Por eso se mide (umbral: un semitono, un 8 % de duración, un 12 % de movimiento de la entonación) y se avisa.

- **1.4 · diseñar voces**: el diseñador (Qwen3-TTS VoiceDesign) no obedece del todo. Pidiendo «hombre de sesenta años» salieron voces de 200–280 Hz; con «voz grave», de 75 a 114 Hz según la semilla. Y **siempre sesea**: 0 de 12 voces tuvieron acento de España, con tres descripciones distintas, incluida «actor de doblaje de Madrid» explicando la /θ/. Lo medí con un detector de seseo: el pico de 4–11 kHz en palabras con z/ce/ci frente a palabras con s da −9/−10 dB en España y unos 0 dB con seseo. Mejoras:
  - `RevisionDiseno`:
    - El motor mide el tono (op `analizar_voz`, WORLD) y comprueba que encaja con el sexo, el timbre y la edad pedidos.
    - Whisper comprueba que lee el texto de muestra. Se compara letra a letra: Whisper parte mal palabras como «guardabosques encendió».
    - Las variantes que fallan se rehacen hasta 2 veces.
  - «Así sonará al narrar»: un párrafo leído con el clon (modelo Base).
  - `DescriptorVoz`: la IA local (Qwen3-4B, la del traductor) pasa una descripción en español a las opciones guiadas, más una frase en inglés.
  - «Más así»:
    - Misma semilla y la descripción retocada, «un poco» y «bastante», comprobando que el cambio se cumple.
    - La misma semilla da voces algo más parecidas (huella 0,983 frente a 0,975), no idénticas.
  - **Acento de España de doblaje.** En el motor principal, la huella (timbre) y las muestras de la referencia (acento, ritmo) no se pueden combinar de dos voces: mandan las muestras. Tampoco sirve promediar huellas: da un timbre intermedio, pero con seseo. Lo que funciona:
    1. El motor castellano lee el texto de muestra con la pronunciación de España (su voz de serie, `conds.pt`, o una voz de la biblioteca) y el timbre de la variante.
    2. El motor principal clona esa grabación.
    3. Resultado: parecido 0,99 con la voz inventada y −12/−16 dB. En escucha a ciegas, acento 1 → 5 con la misma naturalidad (4).
    4. La voz guarda `AcentoEspana`, y sus variantes ya no se generan con el diseñador.

- **1.3.2 · cifras con palabras**: `NormalizadorEspanol` pasa a palabras cifras (punto de miles y coma decimal, como en España), fechas, horas, monedas, porcentajes, ordinales, unidades, números romanos y abreviaturas, con concordancia por la palabra siguiente (heurística por terminación más listas de excepciones; los años nunca se apocopan). Se aplica en `Guion` después del diccionario de pronunciación (así el diccionario manda) y solo si la voz es española (`ISintetizador.Idioma`), en las frases de Doblar y en «Probar» de Voces. La revisión con Whisper pasa también a palabras lo que Whisper escribe con cifras antes de comparar. Prueba real: sin normalizar, el motor leyó «1.250 €» como «1.150 oro», «3.º piso» como «tristupiso» y «-5 °C» como «5 de Grau Ceci»; normalizado, todo bien. Menú de la izquierda por secciones (Voces, Módulos y Configuración).

- **1.1.1–1.1.2 · instalación más robusta y rápida**: uv a veces no puede crear la unión «cpython-3.11-…» (os error 448); el paso lo tolera con `toleraError` y se quitan las uniones inaccesibles. uv no deja nada fuera de la carpeta de datos (`UV_PYTHON_INSTALL_BIN=0`, sin registro). El `Descargador` corta y reanuda una conexión que pasa 30 s sin datos (antes se quedaba colgado para siempre), y si el servidor admite rangos baja cada archivo grande por tramos con hasta 8 conexiones. Cuando a un componente solo le quedan modelos, esas descargas siguen en segundo plano (hasta 3 a la vez, `Instalador.ModelosALaVez`) mientras se instala el siguiente; cada fila de la lista muestra su propia barra. Xet sigue desactivado: en una prueba iba igual de rápido, tardaba en empezar a informar del avance y duplicaba datos en `xet/`.

### Proyectos de narración

`<datos>\proyectos\<id>\`: `proyecto.json` (ajustes y estado de cada bloque generado), `guion.txt` y `salida\` con `NN_nombre.mp3`, `<id>_completo.mp3` y `manifiesto.json`.

### El motor de voz

La app lanza `python -m constantino_motor` con el Python del entorno de cada motor (`CONSTANTINO_MOTOR=chatterbox` o `voxcpm` elige el adaptador de Chatterbox o de VoxCPM2; sin ella, Qwen) y le envía una petición JSON por línea (`info`, `cargar`, `descargar`, `sintetizar`, `disenar`, `transcribir`, `salir`); el motor responde con eventos `progreso`, `resultado` o `error`. stdout queda reservado al protocolo y todo lo demás va a stderr. Con `CONSTANTINO_MOTOR_FALSO=1` usa un motor falso sin torch, que es el que prueban los tests (`CONSTANTINO_EXIGIR_PYTHON=1` hace que falten Python sea un fallo y no se salte la prueba).

### Dónde instala

En la carpeta de datos elegida en el asistente (por defecto `%LOCALAPPDATA%\Caliope`), con la estructura de `EstructuraDatos`: `Mis narraciones`, `Mis voces`, `Modelos de voz` (HF_HOME) y `Programas internos` (`herramientas`, `python`, `entornos`, `cache`, `descargas`, `registros`, `tmp`, `estado.json`), más un `LÉEME.txt`. Las preferencias van aparte, en `%LOCALAPPDATA%\Caliope\ajustes.json`. Los pasos de cada componente están en `catalogo/catalogo.json`.

Las carpetas de versiones anteriores (todo suelto en la raíz) se migran solas al arrancar, moviendo carpetas en el mismo disco. Los entornos de uv guardan la ruta absoluta del Python base en tres sitios y la migración corrige los tres: `pyvenv.cfg` (`home`), la unión de versión menor de `python\` (`cpython-3.11-…` → `cpython-3.11.17-…`) y el propio `Scripts\python.exe`, un lanzador de uv con la ruta grabada como recurso, que se sustituye por el de un entorno temporal recién creado. Si algo falla al mover, se deshace y se sigue con la estructura antigua.

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
