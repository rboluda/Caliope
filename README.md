# Constantino

Caja de herramientas de voz para Windows: instala con botones los motores de síntesis de voz que elijas (Qwen3-TTS, Whisper, Chatterbox) y te deja diseñar voces, clonarlas y crear narraciones sin tocar la línea de comandos.

El diseño completo está en [docs/DISENO.md](docs/DISENO.md).

## Estado

**Hito H0**: ventana principal con navegación, catálogo de componentes y análisis del equipo con recomendaciones. Todavía no instala nada.

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
```

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/Constantino.App` | Interfaz WPF (WPF-UI, MVVM con CommunityToolkit) |
| `src/Constantino.Core` | Catálogo, análisis del equipo y recomendaciones, sin dependencias de la interfaz |
| `catalogo/catalogo.json` | Componentes instalables: qué son, para qué sirven, tamaño, requisitos y licencias |
| `tests/Constantino.Core.Tests` | Pruebas de xUnit |
| `docs/` | Diseño y capturas |
