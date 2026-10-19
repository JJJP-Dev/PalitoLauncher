<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/PalitoLauncher/Assets/logo_white.png">
    <img src="src/PalitoLauncher/Assets/logo.png" width="96" alt="Palito Launcher">
  </picture>
</p>

# Palito Launcher

Launcher para Windows que instala y mantiene al día **Coop2D**, el juego 2D cooperativo de PalitoGames, sin tener que
descargar ni descomprimir nada a mano. Las builds del juego salen de las
[releases de GitHub de JJJP-Dev/Coop2D](https://github.com/JJJP-Dev/Coop2D/releases).

- Instala el juego con un clic y lo actualiza cuando hay una versión nueva (o una revisión de la misma versión).
- Las descargas se pueden **pausar y continuar**, aunque cierres el launcher o se corte internet.
- Comprueba que el archivo descargado está íntegro (SHA-256) antes de instalarlo y nunca deja el juego a medio actualizar.
- Funciona sin conexión si el juego ya está instalado.
- Se actualiza solo.

Requisitos: Windows 10 u 11 de 64 bits. No hace falta instalar .NET ni nada más.

---

## Para amigos: cómo instalar y jugar

### 1. Descargar el launcher

1. Entra en **[github.com/JJJP-Dev/PalitoLauncher/releases/latest](https://github.com/JJJP-Dev/PalitoLauncher/releases/latest)**.
2. En la sección **Assets**, descarga **`PalitoLauncher.exe`**.
   No descargues «Source code (zip)» ni «Source code (tar.gz)»: eso es el código, no el programa.
3. Si el navegador dice que el archivo «no se descarga habitualmente», elige **Conservar**
   (en Edge: menú **⋯** del aviso → **Conservar** → **Mostrar más** → **Conservar de todas formas**).

### 2. Abrirlo por primera vez

- Windows puede mostrar **«Windows protegió su PC»** (SmartScreen). Pulsa **Más información → Ejecutar de todas formas**.
  Es normal: el exe no está firmado con un certificado de pago, como pasa con casi todos los programas pequeños.
- La primera vez, el launcher **se copia solo** a `%LOCALAPPDATA%\Programs\PalitoLauncher` y crea el acceso directo
  **Coop2D** en el menú Inicio. A partir de ahí ábrelo desde el menú Inicio (o desde el acceso del escritorio) y puedes
  borrar el archivo que descargaste.
  - Si prefieres que no se copie (por ejemplo, para llevarlo en un USB), ábrelo con el argumento `--portable`.

### 3. Instalar el juego

1. Pulsa **INSTALAR**. Se abre una ventana para elegir dónde instalarlo
   (por defecto `%LOCALAPPDATA%\PalitoGames\Coop2D`). Puedes cambiar la carpeta con **Cambiar…**.
2. La ventana te dice el espacio necesario (unas 4 veces lo que ocupa la descarga, ~285 MB ahora mismo) y el libre.
3. Deja marcada **Crear acceso directo en el escritorio** si lo quieres y pulsa **Instalar**.

Dentro de la carpeta elegida verás `game\` (el juego) y una carpeta oculta `.launcher\` (descargas y archivos
temporales del launcher). No hace falta tocar ninguna de las dos.

### 4. Jugar

Pulsa **JUGAR**. En **Ajustes → Juego** puedes elegir qué hace el launcher al abrir el juego (mantenerse abierto,
minimizarse o cerrarse) y añadir argumentos de lanzamiento.

### Actualizaciones

- Al abrir el launcher (y cada hora mientras está abierto) se comprueba si hay versión nueva. Si la hay, el botón
  principal cambia a **ACTUALIZAR**.
- En **Ajustes → Actualizaciones** puedes activar **Descargar e instalar automáticamente**.
- **Jugar sin actualizar** te deja jugar con la versión que tienes, pero recuerda que **para jugar en cooperativo todos
  necesitáis la misma versión**.
- Una **revisión** es la misma versión re-subida por el desarrollador con un arreglo: también conviene instalarla.
- El propio launcher también se actualiza: aparecerá un aviso arriba con **Actualizar**.

### Pausar y continuar

- Durante la descarga, **PAUSAR** la detiene y el botón pasa a **CONTINUAR (45 %)**.
- Si cierras el launcher o se apaga el PC, al volver a abrirlo podrás continuar desde donde se quedó.
- Si se corta internet, el launcher reintenta solo; si no lo consigue, guarda el progreso y te pide pulsar CONTINUAR.

### Primera partida en cooperativo: el firewall de Windows

La primera vez que juegues en cooperativo, Windows preguntará si permites que **Coop2D** use la red.
Marca **Redes privadas** y pulsa **Permitir**.

- Si le diste a Cancelar por error: **Seguridad de Windows → Firewall y protección de red → Permitir una aplicación a
  través del firewall → Cambiar la configuración**, busca Coop2D y marca **Privada**.
- Tu red de casa debe estar como **privada**: **Configuración → Red e Internet → (tu wifi o ethernet) → Tipo de perfil de
  red → Privada**.

### Dónde está cada cosa

| Qué | Dónde |
|---|---|
| El juego | La carpeta que elegiste, subcarpeta `game\` (por defecto `%LOCALAPPDATA%\PalitoGames\Coop2D\game`) |
| Partidas guardadas y `Player.log` del juego | `%USERPROFILE%\AppData\LocalLow\PalitoGames\Coop2D` |
| El launcher | `%LOCALAPPDATA%\Programs\PalitoLauncher\PalitoLauncher.exe` |
| Ajustes y caché del launcher | `%LOCALAPPDATA%\PalitoLauncher` |
| Logs del launcher | `%LOCALAPPDATA%\PalitoLauncher\logs` |

Para abrir una de estas carpetas, copia la ruta en la barra de direcciones del Explorador (o en **Win + R**).
El menú **⋯** del launcher tiene accesos directos a la carpeta del juego y a la de guardados.

Las partidas guardadas **no se tocan nunca**: ni al actualizar, ni al reparar, ni al desinstalar.

### Si algo falla

Pulsa **Crear informe** (en el mensaje de error, en el menú **⋯ → Crear informe para el desarrollador** o en
**Ajustes → Launcher**). Se crea un archivo `Coop2D-informe-….zip` en el Escritorio con los logs y datos necesarios:
envíaselo al desarrollador. No incluye tus partidas.

Problemas típicos:

- **«Límite de consultas a GitHub alcanzado»**: GitHub limita cuántas veces se le puede preguntar por hora, y el cupo
  se comparte entre todos los PCs de la misma casa. Espera a la hora que indica el mensaje. Si ya tienes el juego
  instalado, puedes seguir jugando.
- **«Sin conexión con GitHub»**: puedes jugar a la versión instalada; las actualizaciones llegarán cuando vuelva internet.
- **El antivirus bloqueó archivos del juego**: añade una exclusión para la carpeta del juego y usa **⋯ → Reparar**.
- **No hay espacio suficiente**: libera espacio o elige otra unidad (**Ajustes → Instalación → Mover…**).
- **«No se encuentra la unidad E:\»**: conecta el disco donde instalaste el juego y pulsa **REINTENTAR**, o pulsa
  **Elegir otra carpeta…** (también en **Ajustes → Instalación**, junto a **Olvidar esta carpeta**) para instalarlo en
  otro sitio.
- **El juego no abre o se cierra solo**: prueba **⋯ → Reparar (volver a instalar)**. Si sigue fallando, crea un informe.
- **El juego se quedó colgado**: usa el enlace **¿El juego no responde? Forzar cierre** del launcher.
- **«Palito Launcher ya está abierto pero no responde»**: acepta y el launcher cerrará la copia colgada.

### Desinstalar

- **El juego**: **Ajustes → Instalación → Desinstalar** (tus partidas se conservan).
- **El launcher**: borra las carpetas `%LOCALAPPDATA%\Programs\PalitoLauncher` y `%LOCALAPPDATA%\PalitoLauncher`, y los
  accesos directos **Coop2D** del menú Inicio y del escritorio.

---

## Para el desarrollador

### Resumen

- El launcher lee las releases públicas de **JJJP-Dev/Coop2D** con la API de GitHub (sin token) y elige como build del
  juego el asset que coincide con `^2D_.+\.(zip|rar|7z)$`.
- La **versión sale del nombre del asset** (`2D_0.0.3_Prototype2.zip` en pre-producción, `2D_0.1.0.zip` en
  producción); si no tiene, del tag y luego del título. Hay actualización si la versión sube; hay **revisión** si la
  versión es la misma pero cambia el SHA-256 del archivo (re-subiste el zip). Nunca se baja de versión automáticamente.
- Cómo nombrar y publicar las builds del juego: **[docs/PUBLICAR.md](docs/PUBLICAR.md)**.

### Fondo remoto

Si existe `launcher/background.png` en la rama `main` del repo del juego
(`https://raw.githubusercontent.com/JJJP-Dev/Coop2D/main/launcher/background.png`), el launcher lo usa como fondo.

- Formato PNG o JPG (guardado con el nombre `background.png`), idealmente 1920×1080 y menos de 2 MB (máximo 20 MB).
  Se recorta para llenar la ventana y se oscurece con un degradado, así que deja lo importante en el centro.
- Cambiarlo en GitHub cambia el fondo de todos los launchers sin publicar nada (se descarga con caché y no gasta cupo
  de la API). Si lo borras, los launchers vuelven al fondo por defecto.

### Publicar una versión del launcher

El launcher se publica en el repo público **JJJP-Dev/PalitoLauncher** con GitHub Actions
([.github/workflows/release.yml](.github/workflows/release.yml)):

```powershell
git tag v1.0.1
git push origin v1.0.1
```

El workflow ejecuta los tests, publica el exe único (win-x64, autocontenido) con la versión del tag y crea la release
`v1.0.1` con el asset **`PalitoLauncher.exe`**. También se puede lanzar a mano desde **Actions → Release → Run
workflow** indicando la versión.

- El asset debe llamarse **exactamente `PalitoLauncher.exe`**: es lo que busca la auto-actualización
  (`LauncherConfig.LauncherAssetPattern`). El workflow lo comprueba.
- Los launchers instalados consultan la última release **al arrancar y cada hora, como mucho una vez cada 2 h** (o al
  momento con **Ajustes → Buscar ahora**) y muestran un aviso
  («Nueva versión del launcher…») con Actualizar / Ver cambios / Omitir.
- Un tag con sufijo (`v1.1.0-beta.1`) se publica como **pre-release** y no llega a nadie automáticamente
  (`/releases/latest` ignora las pre-releases): útil para probar.
- La versión del tag manda en la release; actualiza también `<Version>` en `Directory.Build.props` para que las
  compilaciones locales lleven la misma.
- Si la creación de la release falla con un 403: **Settings → Actions → General → Workflow permissions → Read and write
  permissions**.

### Configuración

Todo lo que identifica al juego y a los repos está en
[`src/PalitoLauncher.Core/LauncherConfig.cs`](src/PalitoLauncher.Core/LauncherConfig.cs): repos de GitHub, nombre del
exe del juego, patrón de assets, nombre del acceso directo, URL del fondo e intervalos de comprobación. Cambia los
valores y recompila. La versión del launcher está en [`Directory.Build.props`](Directory.Build.props).

### Compilar en local

Requisitos: Windows 10/11 y el [SDK de .NET 10](https://dotnet.microsoft.com/download) (10.0.401 o superior).
Visual Studio 2026 o Rider son opcionales.

```powershell
dotnet build PalitoLauncher.sln
dotnet test PalitoLauncher.sln
dotnet run --project src/PalitoLauncher -- --portable   # --portable: no se copia a %LOCALAPPDATA%\Programs
pwsh scripts/publish.ps1                                # → publish\PalitoLauncher.exe (muestra tamaño y SHA-256)
pwsh scripts/publish.ps1 -Version 1.0.1 -RunTests
```

Tests de integración (descargan las builds reales de GitHub, ~110 MB; desactivados por defecto):

```powershell
$env:PALITO_RUN_INTEGRATION = "1"
dotnet test PalitoLauncher.sln --filter "FullyQualifiedName~IntegrationTests"
```

Iconos: `logo.png`, `logo_white.png` y `app.ico` se generan desde `Assets/logo_source.png` con
`powershell -ExecutionPolicy Bypass -File scripts\generate-icons.ps1`.

La CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) compila y ejecuta los tests en cada push y pull request a
`main`.

### Estructura del proyecto

```
PalitoLauncher.sln
Directory.Build.props          versión y propiedades comunes
src/
  PalitoLauncher.Core/         toda la lógica, sin WPF (testeable)
    GitHub/                    cliente de la API con caché, rate limit y modo offline; selección de releases
    Versioning/                GameVersion: versiones a partir de nombres de assets, tags y títulos
    Downloads/                 descargas reanudables con verificación SHA-256
    Install/                   instalación/actualización atómica, extracción (.zip/.rar/.7z), recuperación
    Launch/                    lanzar y vigilar el proceso del juego
    Update/                    auto-actualización y auto-instalación del launcher
    State/                     lógica de estados de la pantalla (qué hace el botón principal)
    Settings/ Logging/ Diagnostics/ Net/ Util/
  PalitoLauncher/              interfaz WPF (MVVM hecho a mano)
    Assets/ Themes/ Views/ ViewModels/ Services/ Helpers/ Converters/
tests/PalitoLauncher.Tests/    tests xUnit (sin red, salvo IntegrationTests)
tests/PalitoLauncher.UI.Tests/ tests de la capa WPF: ViewModels, MarkdownRenderer y errores de binding de todas las
                               vistas (hilo STA propio, sin mostrar ventanas, sin red, datos en %TEMP%)
scripts/                       publish.ps1, generate-icons.ps1
docs/PUBLICAR.md               guía para publicar builds del juego
.github/workflows/             ci.yml, release.yml
```

### Límites de la API de GitHub

Sin token, GitHub permite **60 peticiones por hora y por IP**, y las respuestas 304 (sin cambios) **también cuentan**.
El cupo lo comparten todos los PCs detrás de la misma IP (amigos en la misma casa, una LAN party…). Por eso el launcher:

- reutiliza respuestas de menos de 60 s sin volver a preguntar;
- guarda el estado del límite en disco y no pregunta hasta la hora de restablecimiento;
- hace comprobaciones automáticas cada 60 min y se las salta si quedan 10 peticiones o menos;
- guarda las releases en caché y funciona sin conexión con esos datos.

Descargar las builds (CDN de GitHub) y el fondo (raw.githubusercontent.com) no gasta cupo de la API. Un arranque normal
gasta 1 petición (más 1, como mucho cada 2 h, para buscar versión nueva del launcher).

### Solución de problemas

- **Una build nueva no aparece**: revisa que el asset se llame `2D_….zip` y que su versión sea mayor que la anterior
  (ver [docs/PUBLICAR.md](docs/PUBLICAR.md)), que la release no sea un borrador y que, si está marcada como pre-release,
  el amigo tenga activado «Incluir versiones de prueba». Luego **⋯ → Buscar actualizaciones** (las respuestas se
  reutilizan durante 60 s).
- **Límite de consultas mientras pruebas**: cada arranque y cada «Buscar actualizaciones» gasta cupo. Espera a la hora de
  restablecimiento o cambia de red.
- **Builds de depuración**: si junto al exe hay un `PalitoLauncher.dll` (compilación normal, no publicada), el launcher
  lo detecta como desarrollo y no se copia a `%LOCALAPPDATA%\Programs` ni se auto-actualiza. `--portable` fuerza lo
  mismo con el exe publicado.
- **El antivirus marca el exe**: los exe autocontenidos y comprimidos sin firmar a veces dan falsos positivos. Se puede
  enviar a revisión (Microsoft: <https://www.microsoft.com/wdsi/filesubmission>).
- **Logs**: `%LOCALAPPDATA%\PalitoLauncher\logs\launcher-AAAAMMDD.log`. El informe para el desarrollador incluye los de
  los últimos 7 días, los ajustes, el marcador de la instalación y el `Player.log` de Unity.
