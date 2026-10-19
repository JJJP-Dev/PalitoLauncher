# Documentación de Palito Launcher

Aquí está todo lo necesario para **mantener, ampliar y operar el launcher sin depender de nadie**. Las guías están
escritas para ti, el desarrollador; lo que leen los amigos que usan el launcher está en el
[README principal](../README.md).

## Por dónde empezar

Si vas a tocar el código después de un tiempo sin verlo, este orden funciona bien:

1. **[ARQUITECTURA.md](codigo/ARQUITECTURA.md)**: el mapa. Qué hace cada parte, cómo encajan (con diagramas), qué se guarda en
   disco, la máquina de estados del botón principal y por qué se decidió cada cosa. Repasa al menos las
   «reglas de oro» y las «reglas que no hay que romper».
2. **[GUIA-CODIGO.md](codigo/GUIA-CODIGO.md)**: el recorrido. En qué orden leer los archivos, los conceptos de C#/.NET/WPF que
   usa el proyecto explicados con su propio código, cómo depurar, cómo funcionan los tests y recetas paso a paso.
3. El documento de la tarea concreta que vayas a hacer (tabla de abajo).

## ¿Qué quieres hacer?

| Quiero… | Lee |
|---|---|
| Entender cómo está hecho el launcher | [ARQUITECTURA.md](codigo/ARQUITECTURA.md) |
| Aprender a leer el código y los conceptos que usa | [GUIA-CODIGO.md](codigo/GUIA-CODIGO.md), secciones 2 y 3 |
| Saber por qué el botón principal muestra lo que muestra | [ARQUITECTURA.md, sección 10](codigo/ARQUITECTURA.md#10-máquina-de-estados-del-botón-principal) |
| Entender qué gasta el cupo de GitHub | [ARQUITECTURA.md, sección 8](codigo/ARQUITECTURA.md#8-github-caché-y-límite-de-consultas) |
| Saber dónde guarda cada cosa el launcher | [ARQUITECTURA.md, sección 9](codigo/ARQUITECTURA.md#9-qué-se-guarda-en-disco-y-dónde) |
| Depurar un problema o leer los logs | [GUIA-CODIGO.md, sección 4](codigo/GUIA-CODIGO.md#4-cómo-depurar) |
| Ejecutar o escribir tests | [GUIA-CODIGO.md, sección 5](codigo/GUIA-CODIGO.md#5-tests) |
| Añadir un ajuste nuevo | [GUIA-CODIGO.md, receta 6.1](codigo/GUIA-CODIGO.md#61-añadir-un-ajuste-nuevo) |
| Añadir una página a la navegación | [GUIA-CODIGO.md, receta 6.2](codigo/GUIA-CODIGO.md#62-añadir-una-página-nueva-a-la-navegación) |
| Añadir un campo a `feedback.json` o `launcher.json` | [GUIA-CODIGO.md, receta 6.3](codigo/GUIA-CODIGO.md#63-añadir-un-campo-a-feedbackjson-o-a-launcherjson) |
| Cambiar un texto | [GUIA-CODIGO.md, receta 6.4](codigo/GUIA-CODIGO.md#64-añadir-o-cambiar-un-texto-o-traducir) |
| Apuntar el launcher a otro repo o a otro juego | [GUIA-CODIGO.md, receta 6.5](codigo/GUIA-CODIGO.md#65-cambiar-el-repo-o-el-juego-en-launcherconfig) |
| Publicar una versión nueva del **launcher** | [GUIA-CODIGO.md, receta 6.6](codigo/GUIA-CODIGO.md#66-publicar-una-versión-del-launcher) y el [README principal](../README.md) |
| Publicar una build nueva del **juego** (nombres, canales, revisiones) | [PUBLICAR.md](operacion/PUBLICAR.md) |
| Que las opiniones de la encuesta lleguen a un canal de Discord | [FEEDBACK.md](remoto/FEEDBACK.md) |
| Cambiar las preguntas de la encuesta | [FEEDBACK.md](remoto/FEEDBACK.md) y el ejemplo [feedback.example.json](ejemplos/feedback.example.json) |
| Entender o cambiar la página Roadmap (milestones de GitHub) | [ROADMAP.md](remoto/ROADMAP.md) |
| Publicar anuncios, sesiones de prueba, enlaces de comunidad, bloquear una build con problemas o configurar «Reportar un problema» (1.0.5) | [LAUNCHER-JSON.md](remoto/LAUNCHER-JSON.md) |
| Preparar misiones de prueba para un playtest (1.0.6) | [MISIONES.md](remoto/MISIONES.md) y el ejemplo [missions.example.json](ejemplos/missions.example.json) |
| Saber qué eventos tiene que escribir el juego en su log para las misiones | [EVENTOS-JUEGO.md](juego/EVENTOS-JUEGO.md) |
| Ver qué cosas solo puedo hacer yo a mano (webhooks, PRs al repo del juego, publicar, si falla GitHub Actions…) | [TAREAS-MANUALES.md](operacion/TAREAS-MANUALES.md) |

## Cómo está organizada esta carpeta

```
docs/
├── README.md              ← este índice
├── codigo/                Entender y cambiar el código del launcher
│   ├── ARQUITECTURA.md
│   └── GUIA-CODIGO.md
├── operacion/             Lo que haces tú a mano: publicar, Discord, playtests
│   ├── TAREAS-MANUALES.md
│   └── PUBLICAR.md
├── remoto/                Lo que se cambia desde el repo del juego (launcher/*.json, milestones), sin publicar el launcher
│   ├── FEEDBACK.md
│   ├── LAUNCHER-JSON.md
│   ├── MISIONES.md
│   └── ROADMAP.md
├── juego/                 Lo que tiene que hacer Coop2D para el launcher
│   └── EVENTOS-JUEGO.md
└── ejemplos/              JSON listos para copiar al repo del juego
    ├── feedback.example.json
    ├── launcher.example.json
    └── missions.example.json
```

### codigo/: entender y cambiar el launcher

| Documento | Contenido |
|---|---|
| [ARQUITECTURA.md](codigo/ARQUITECTURA.md) | Capas, mapa de carpetas, diagramas (arranque, instalación, auto-actualización, caché de GitHub), datos en disco, estados, decisiones de diseño |
| [GUIA-CODIGO.md](codigo/GUIA-CODIGO.md) | Orden de lectura, conceptos de C#/.NET/WPF con ejemplos del proyecto, depuración, tests, recetas paso a paso, glosario |

### operacion/: lo que solo puedes hacer tú

| Documento | Contenido |
|---|---|
| [TAREAS-MANUALES.md](operacion/TAREAS-MANUALES.md) | Checklist con pasos numerados: webhooks de Discord, publicar los JSON, publicar el launcher, antes y después de un playtest, si falla GitHub Actions |
| [PUBLICAR.md](operacion/PUBLICAR.md) | Cómo nombrar y publicar las builds de Coop2D para que el launcher las detecte |

### remoto/: configuración desde el repo del juego

Todo esto vive en el repo **JJJP-Dev/Coop2D** (carpeta `launcher/` y milestones) y se cambia sin compilar ni publicar el
launcher. Los webhooks van siempre **ofuscados** (`pw1:…`): ver [FEEDBACK.md, «Seguridad del webhook»](remoto/FEEDBACK.md#seguridad-del-webhook-y-cómo-rotarlo).

| Documento | Archivo del repo del juego | Contenido |
|---|---|---|
| [FEEDBACK.md](remoto/FEEDBACK.md) | `launcher/feedback.json` | Encuesta de opiniones: webhook de Discord paso a paso, qué llega a Discord, cómo ampliarlo |
| [LAUNCHER-JSON.md](remoto/LAUNCHER-JSON.md) | `launcher/launcher.json` | Anuncios, builds con problemas, enlaces, sesiones de prueba, hosting y webhook de reportes (1.0.5) |
| [MISIONES.md](remoto/MISIONES.md) | `launcher/missions.json` | Misiones de prueba (1.0.6): objetivos, lectura del log, tarjeta, envío a Discord y escalabilidad |
| [ROADMAP.md](remoto/ROADMAP.md) | Milestones de GitHub | Cómo se construye la página Roadmap y cómo nombrar los milestones |

### juego/: lo que tiene que hacer Coop2D

| Documento | Contenido |
|---|---|
| [EVENTOS-JUEGO.md](juego/EVENTOS-JUEGO.md) | Contrato con Coop2D: formato y catálogo de los eventos que el juego escribe en `Player.log` (para las misiones) |

### ejemplos/: para copiar

| Archivo | Para |
|---|---|
| [feedback.example.json](ejemplos/feedback.example.json) | `launcher/feedback.json`: encuesta del Pre 2 |
| [launcher.example.json](ejemplos/launcher.example.json) | `launcher/launcher.json`: un poco de cada sección |
| [missions.example.json](ejemplos/missions.example.json) | `launcher/missions.json`: misiones de un playtest |

Los tres los validan los tests (`Parse_DocsExampleFile…`): si los cambias y dejan de ser válidos, la CI falla.

Fuera de esta carpeta: el [README principal](../README.md), con la guía para los amigos que usan el launcher y el resumen para
el desarrollador (compilar, publicar el launcher, estructura del proyecto).

## Otras fuentes de verdad

- **La documentación XML del código** (`/// <summary>` encima de cada clase y método) es la especificación detallada
  de cada pieza: qué hace en cada caso, qué lanza y qué no. Estas guías explican el porqué y el conjunto; el detalle
  fino está allí.
- **Los tests** son ejemplos ejecutables de cómo se comporta cada cosa. Si dudas de qué pasa en un caso raro, busca su
  test (`tests/PalitoLauncher.Tests/<Clase>Tests.cs`).
- **El log** del launcher (`%LOCALAPPDATA%\PalitoLauncher\logs`) cuenta qué hizo y por qué en cada sesión.

## Mantener la documentación al día

- Las guías de arquitectura y de código describen la versión **1.0.4**; las novedades de la 1.0.5 están en
  [LAUNCHER-JSON.md](remoto/LAUNCHER-JSON.md), [FEEDBACK.md](remoto/FEEDBACK.md) y [TAREAS-MANUALES.md](operacion/TAREAS-MANUALES.md), y las de la
  1.0.6, en [MISIONES.md](remoto/MISIONES.md) y [EVENTOS-JUEGO.md](juego/EVENTOS-JUEGO.md).
- Cuando cambies algo que se describe aquí (un flujo, una carpeta, un intervalo, un texto que se cita), actualiza el
  documento en el mismo commit.
- Los diagramas son [Mermaid](https://mermaid.js.org/) dentro del Markdown: GitHub los dibuja solo. Para editarlos con
  vista previa, usa <https://mermaid.live>.
- **Dónde va un documento nuevo:** en la carpeta de arriba que corresponda a quién lo usa y para qué. Si cambias uno
  de carpeta, actualiza sus enlaces (y los que apuntan a él) en el mismo commit.
- Cita archivos con enlaces relativos y nombres reales de clases y métodos; evita números de línea (cambian con cada
  edición).
