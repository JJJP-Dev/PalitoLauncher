# Documentación de Palito Launcher

Aquí está todo lo necesario para **mantener, ampliar y operar el launcher sin depender de nadie**. Las guías están
escritas para ti, el desarrollador; lo que leen los amigos que usan el launcher está en el
[README principal](../README.md).

## Por dónde empezar

Si vas a tocar el código después de un tiempo sin verlo, este orden funciona bien:

1. **[ARQUITECTURA.md](ARQUITECTURA.md)**: el mapa. Qué hace cada parte, cómo encajan (con diagramas), qué se guarda en
   disco, la máquina de estados del botón principal y por qué se decidió cada cosa. Repasa al menos las
   «reglas de oro» y las «reglas que no hay que romper».
2. **[GUIA-CODIGO.md](GUIA-CODIGO.md)**: el recorrido. En qué orden leer los archivos, los conceptos de C#/.NET/WPF que
   usa el proyecto explicados con su propio código, cómo depurar, cómo funcionan los tests y recetas paso a paso.
3. El documento de la tarea concreta que vayas a hacer (tabla de abajo).

## ¿Qué quieres hacer?

| Quiero… | Lee |
|---|---|
| Entender cómo está hecho el launcher | [ARQUITECTURA.md](ARQUITECTURA.md) |
| Aprender a leer el código y los conceptos que usa | [GUIA-CODIGO.md](GUIA-CODIGO.md), secciones 2 y 3 |
| Saber por qué el botón principal muestra lo que muestra | [ARQUITECTURA.md, sección 10](ARQUITECTURA.md#10-máquina-de-estados-del-botón-principal) |
| Entender qué gasta el cupo de GitHub | [ARQUITECTURA.md, sección 8](ARQUITECTURA.md#8-github-caché-y-límite-de-consultas) |
| Saber dónde guarda cada cosa el launcher | [ARQUITECTURA.md, sección 9](ARQUITECTURA.md#9-qué-se-guarda-en-disco-y-dónde) |
| Depurar un problema o leer los logs | [GUIA-CODIGO.md, sección 4](GUIA-CODIGO.md#4-cómo-depurar) |
| Ejecutar o escribir tests | [GUIA-CODIGO.md, sección 5](GUIA-CODIGO.md#5-tests) |
| Añadir un ajuste nuevo | [GUIA-CODIGO.md, receta 6.1](GUIA-CODIGO.md#61-añadir-un-ajuste-nuevo) |
| Añadir una página a la navegación | [GUIA-CODIGO.md, receta 6.2](GUIA-CODIGO.md#62-añadir-una-página-nueva-a-la-navegación) |
| Añadir un campo a `feedback.json` o `launcher.json` | [GUIA-CODIGO.md, receta 6.3](GUIA-CODIGO.md#63-añadir-un-campo-a-feedbackjson-o-a-launcherjson) |
| Cambiar un texto | [GUIA-CODIGO.md, receta 6.4](GUIA-CODIGO.md#64-añadir-o-cambiar-un-texto-o-traducir) |
| Apuntar el launcher a otro repo o a otro juego | [GUIA-CODIGO.md, receta 6.5](GUIA-CODIGO.md#65-cambiar-el-repo-o-el-juego-en-launcherconfig) |
| Publicar una versión nueva del **launcher** | [GUIA-CODIGO.md, receta 6.6](GUIA-CODIGO.md#66-publicar-una-versión-del-launcher) y el [README principal](../README.md) |
| Publicar una build nueva del **juego** (nombres, canales, revisiones) | [PUBLICAR.md](PUBLICAR.md) |
| Que las opiniones de la encuesta lleguen a un canal de Discord | [FEEDBACK.md](FEEDBACK.md) |
| Cambiar las preguntas de la encuesta | [FEEDBACK.md](FEEDBACK.md) y el ejemplo [feedback.example.json](feedback.example.json) |
| Entender o cambiar la página Roadmap (milestones de GitHub) | [ROADMAP.md](ROADMAP.md) |
| Publicar anuncios, sesiones de prueba, enlaces de comunidad, bloquear una build con problemas o configurar «Reportar un problema» (1.0.5) | [LAUNCHER-JSON.md](LAUNCHER-JSON.md) |
| Ver qué cosas solo puedo hacer yo a mano (webhooks, PRs al repo del juego, publicar, si falla GitHub Actions…) | [TAREAS-MANUALES.md](TAREAS-MANUALES.md) |

## Todos los documentos

| Documento | Contenido |
|---|---|
| [README principal](../README.md) | Para amigos: instalar y usar el launcher. Para el desarrollador: resumen, compilar, publicar el launcher, estructura |
| [ARQUITECTURA.md](ARQUITECTURA.md) | Capas, mapa de carpetas, diagramas (arranque, instalación, auto-actualización, caché de GitHub), datos en disco, estados, decisiones de diseño |
| [GUIA-CODIGO.md](GUIA-CODIGO.md) | Orden de lectura, conceptos de C#/.NET/WPF con ejemplos del proyecto, depuración, tests, recetas paso a paso, glosario |
| [PUBLICAR.md](PUBLICAR.md) | Cómo nombrar y publicar las builds de Coop2D para que el launcher las detecte |
| [FEEDBACK.md](FEEDBACK.md) | Encuesta de opiniones: webhook de Discord paso a paso, `launcher/feedback.json`, qué llega a Discord, cómo ampliarlo |
| [feedback.example.json](feedback.example.json) | Encuesta de ejemplo lista para copiar |
| [ROADMAP.md](ROADMAP.md) | Cómo se construye la página Roadmap a partir de los milestones y cómo nombrarlos en GitHub |
| [LAUNCHER-JSON.md](LAUNCHER-JSON.md) | Referencia de `launcher/launcher.json` (1.0.5): anuncios, builds bloqueadas, enlaces, sesiones de prueba, hosting y webhook de reportes |
| [TAREAS-MANUALES.md](TAREAS-MANUALES.md) | Checklist de lo que solo puedes hacer tú, con pasos numerados |

## Otras fuentes de verdad

- **La documentación XML del código** (`/// <summary>` encima de cada clase y método) es la especificación detallada
  de cada pieza: qué hace en cada caso, qué lanza y qué no. Estas guías explican el porqué y el conjunto; el detalle
  fino está allí.
- **Los tests** son ejemplos ejecutables de cómo se comporta cada cosa. Si dudas de qué pasa en un caso raro, busca su
  test (`tests/PalitoLauncher.Tests/<Clase>Tests.cs`).
- **El log** del launcher (`%LOCALAPPDATA%\PalitoLauncher\logs`) cuenta qué hizo y por qué en cada sesión.

## Mantener la documentación al día

- Las guías de arquitectura y de código describen la versión **1.0.4**; las novedades de la 1.0.5 están en
  [LAUNCHER-JSON.md](LAUNCHER-JSON.md), [FEEDBACK.md](FEEDBACK.md) y [TAREAS-MANUALES.md](TAREAS-MANUALES.md).
- Cuando cambies algo que se describe aquí (un flujo, una carpeta, un intervalo, un texto que se cita), actualiza el
  documento en el mismo commit.
- Los diagramas son [Mermaid](https://mermaid.js.org/) dentro del Markdown: GitHub los dibuja solo. Para editarlos con
  vista previa, usa <https://mermaid.live>.
- Cita archivos con enlaces relativos y nombres reales de clases y métodos; evita números de línea (cambian con cada
  edición).
