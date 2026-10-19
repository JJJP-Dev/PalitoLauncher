# launcher/launcher.json: el tablón del launcher (1.0.5)

`launcher/launcher.json` es un archivo **opcional** de la carpeta `launcher/` del repo **JJJP-Dev/Coop2D** (rama `main`).
Con él cambias, sin compilar ni publicar el launcher, lo que ven todos los jugadores:

- **Anuncios** arriba del contenido (información, aviso o crítico).
- **Builds con problemas**: avisar a quien tenga instalada una build rota y marcarla en Versiones.
- **Enlaces de comunidad** (Discord, itch.io, Steam…).
- **Sesiones de prueba** con cuenta atrás, aviso de Windows 15 minutos antes y archivo de calendario.
- **Datos para hostear** una partida (puerto e indicaciones).
- El **webhook de «Reportar un problema»** (canal `#bugs`).

Solo lo leen los launchers **1.0.5 o posteriores**; los anteriores lo ignoran. Si el archivo no existe, todo funciona
igual que en la 1.0.4: no hay anuncios, ni sesiones, ni enlaces.

> Índice de toda la documentación: [docs/README.md](README.md). Pasos para publicarlo: [TAREAS-MANUALES.md, punto 3](TAREAS-MANUALES.md#3-publicar-launcherlauncherjson).
> Código: [`LauncherManifest.cs`](../src/PalitoLauncher.Core/Remote/LauncherManifest.cs) (modelo),
> [`ManifestProvider.cs`](../src/PalitoLauncher.Core/Remote/ManifestProvider.cs) (descarga y saneado),
> [`ManifestLogic.cs`](../src/PalitoLauncher.Core/Remote/ManifestLogic.cs) (reglas) y
> [`MainViewModel.Manifest.cs`](../src/PalitoLauncher/ViewModels/MainViewModel.Manifest.cs) (interfaz).

## Contenido

- [Cómo lo lee el launcher](#cómo-lo-lee-el-launcher)
- [Reglas generales del formato](#reglas-generales-del-formato)
- [Ejemplo completo](#ejemplo-completo)
- [announcements: anuncios](#announcements-anuncios)
- [blockedBuilds: builds con problemas](#blockedbuilds-builds-con-problemas)
- [links: enlaces de comunidad](#links-enlaces-de-comunidad)
- [playtests: sesiones de prueba](#playtests-sesiones-de-prueba)
- [hosting: hostear partida](#hosting-hostear-partida)
- [bugReportWebhookUrl: Reportar un problema](#bugreportwebhookurl-reportar-un-problema)
- [Probar sin publicar](#probar-sin-publicar)
- [Problemas típicos](#problemas-típicos)

## Cómo lo lee el launcher

1. Si existe **`%LOCALAPPDATA%\PalitoLauncher\launcher.local.json`** y es JSON válido, usa ese y no pregunta a GitHub
   (para tus pruebas; ver [Probar sin publicar](#probar-sin-publicar)).
2. Si no, descarga `https://raw.githubusercontent.com/JJJP-Dev/Coop2D/main/launcher/launcher.json`
   (`LauncherConfig.RemoteManifestUrl`) con caché y ETag. **No gasta el cupo** de 60 consultas/hora de la API de GitHub.
3. Lo vuelve a leer **cada 15 minutos** mientras está abierto (`LauncherConfig.ManifestRefreshInterval`). Cada 30
   segundos recalcula qué anuncios tocan, la cuenta atrás y el aviso de las sesiones.

Lo que **nunca** hace:

- Mostrar un error al jugador por culpa de este archivo. Los problemas van al log (`Manifiesto del launcher: …`).
- Sustituir una copia buena por una rota: si publicas un JSON inválido, sigue con la **última versión buena** guardada en
  caché (o con nada, si nunca tuvo una).
- Cambiar el estado del botón principal ni la auto-actualización: solo **avisa**.

## Reglas generales del formato

- Todo es opcional. `{}` es un archivo válido.
- Codificación **UTF-8** (el editor web de GitHub ya lo hace). Máximo **256 KB**.
- Se permiten **comentarios** (`// …`) y **comas finales**. Los nombres de campo no distinguen mayúsculas, pero escríbelos
  como aquí (camelCase).
- Un campo con un tipo incorrecto se ignora **solo ese campo**; un elemento que no cumple lo mínimo (p. ej. un anuncio sin
  `id`) se descarta **solo ese elemento**. Lo descartado se apunta en el log.
- **URLs**: solo `https://` absolutas y sin usuario/contraseña. Cualquier otra cosa se descarta.
- **Ids** (`id` de anuncios y sesiones): letras, números, `-`, `_` o `.`, de 1 a 64 caracteres, únicos (si se repite uno,
  se queda el primero). El launcher recuerda por id qué anuncios cerró cada jugador y de qué sesiones ya avisó: **cambia
  el id** si quieres que algo vuelva a salir a todos.
- **Fechas** ISO 8601 **con zona horaria**: `"2026-10-31T21:00:00+01:00"` o `"2026-10-31T20:00:00Z"`. También valen
  `"2026-10-31 21:00+01:00"` o sin segundos. Sin zona se interpreta como la hora local **de cada PC** (y se avisa en el
  log), así que pon siempre la zona. En España: `+01:00` en invierno y `+02:00` en verano.
- Los textos se recortan a su máximo y se les quitan caracteres de control. Son **texto plano** (sin Markdown).

## Ejemplo completo

```json
{
  // Anuncios: como mucho 3 visibles a la vez (más el de «tu versión tiene un problema»).
  "announcements": [
    {
      "id": "bienvenida-1",
      "level": "info",
      "title": "¡Bienvenido al playtest de Coop2D!",
      "message": "Si algo falla, usa «Reportar un problema» en el menú ⋯.",
      "url": "https://discord.gg/TU_INVITACION",
      "linkText": "Unirme al Discord",
      "dismissible": true
    },
    {
      "id": "mantenimiento-oct",
      "level": "warning",
      "title": "Esta noche no habrá servidor de pruebas",
      "startsUtc": "2026-10-30T18:00:00+01:00",
      "endsUtc": "2026-10-31T09:00:00+01:00"
    }
  ],

  // Builds rotas: no las borres de GitHub, márcalas aquí.
  "blockedBuilds": [
    { "match": "v0.0.2", "reason": "Se desconecta al entrar en la sala del jefe." }
  ],

  "links": [
    { "label": "Discord", "url": "https://discord.gg/TU_INVITACION", "icon": "discord" },
    { "label": "itch.io", "url": "https://TU_USUARIO.itch.io/coop2d", "icon": "itch" }
  ],

  "playtests": [
    {
      "id": "pre2-sesion-1",
      "title": "Prueba de diversión del Pre 2",
      "description": "Probamos las reacciones entre elementos. Entrad al canal de voz 5 minutos antes.",
      "startsUtc": "2026-10-31T21:00:00+01:00",
      "durationMinutes": 120,
      "requiredVersion": "2D_0.0.3_Prototype2.zip",
      "discordUrl": "https://discord.gg/TU_INVITACION"
    }
  ],

  "hosting": {
    "port": 7770,
    "notes": "Abre el puerto UDP 7770 en tu router o usad Radmin VPN."
  },

  "bugReportWebhookUrl": "https://discord.com/api/webhooks/PEGA_AQUI_LA_URL_DEL_WEBHOOK_DE_BUGS"
}
```

(Los comentarios `//` funcionan en el launcher, pero `ConvertFrom-Json` de PowerShell 5 no los acepta: si validas con
PowerShell, quítalos antes o valida con PowerShell 7.)

## announcements: anuncios

Franjas apiladas arriba del contenido de todas las páginas. Máximo **10** en el archivo; se muestran como mucho **3** a la
vez.

| Campo | Tipo | Obligatorio | Detalles |
|---|---|---|---|
| `id` | texto | sí | Ver reglas de ids. Sin id válido, el anuncio se descarta |
| `title` | texto | sí | Máx. 80. Sin título, se descarta |
| `level` | texto | no | `info` (azul, por defecto), `warning` (ámbar) o `critical` (rojo). También `aviso`, `crítico`, `información`. Un valor desconocido → `info` |
| `message` | texto | no | Máx. 400 |
| `url` | https | no | Muestra un botón con `linkText` |
| `linkText` | texto | no | Máx. 40. Por defecto «Más información» |
| `startsUtc` | fecha | no | Desde cuándo se muestra. Sin ella, ya |
| `endsUtc` | fecha | no | Hasta cuándo (no incluido). Sin ella, siempre. Si es anterior a `startsUtc`, el anuncio se descarta |
| `dismissible` | bool | no | Por defecto `true`: el jugador lo cierra con ✕ y no vuelve a verlo. Los `critical` **nunca** se pueden cerrar |

Orden: críticos, avisos e info; dentro del mismo nivel, el de `startsUtc` más reciente primero (los que no tienen fecha al
final) y luego el orden del archivo.

**Variantes útiles:**

- *Anuncio temporal*: pon `startsUtc` y `endsUtc` y olvídate; aparece y desaparece solo (se comprueba cada 30 s).
- *Que vuelva a salir a todos*: cambia su `id` (`bienvenida-1` → `bienvenida-2`).
- *Aviso que nadie puede ignorar*: `"level": "critical"`. Úsalo poco.

## blockedBuilds: builds con problemas

Para builds publicadas con un fallo grave. **No borres la release de GitHub**: publica una corrección con número mayor y
marca la rota aquí. Máximo **50**.

| Campo | Tipo | Obligatorio | Detalles |
|---|---|---|---|
| `match` | texto | sí | Qué build: el **tag** (`v0.0.4`, también `0.0.4`), el **nombre exacto del zip** (`2D_0.0.4_Prototype2.zip`) o la **versión** (`0.0.4-prototype.2`, también `0.0.4_Prototype2`). Sin distinguir mayúsculas ni espacios. Máx. 120 |
| `reason` | texto | no | Motivo para el jugador (máx. 300). Por defecto «Esta versión tiene un problema grave conocido.» |

Qué pasa en el launcher:

- **Quien la tiene instalada** ve un aviso rojo fijo «Tu versión (X) tiene un problema: motivo» con **Actualizar** (a la
  versión más nueva sin problemas) o, si no hay ninguna, **Ver versiones**. No se puede cerrar.
- Al pulsar **JUGAR** con esa build: «¿Jugar igualmente?». Puede seguir jugando: solo se avisa.
- En **Versiones**, esa build lleva la etiqueta **Con problemas**, y al instalarla se pide confirmación («Instalar
  igualmente»).

## links: enlaces de comunidad

Botones en **Inicio** y entradas en el menú **⋯**. Máximo **6**.

| Campo | Tipo | Obligatorio | Detalles |
|---|---|---|---|
| `url` | https | sí | Sin URL válida, se descarta |
| `label` | texto | no | Texto del botón (máx. 30). Si falta, el nombre del icono («Discord», «itch.io»…) |
| `icon` | texto | no | `discord`, `itch`, `steam`, `youtube`, `github` o `web` (por defecto; también para valores desconocidos) |

## playtests: sesiones de prueba

Tarjeta **«Próxima sesión de prueba»** en Inicio. Máximo **10** en el archivo; se muestra **una**: la que está en curso
(si hay varias, la que empezó antes) o, si no, la más cercana que empiece en los **próximos 30 días**. Las pasadas no se
muestran, así que puedes dejarlas o borrarlas.

| Campo | Tipo | Obligatorio | Detalles |
|---|---|---|---|
| `id` | texto | sí | Ver reglas de ids. Sirve para no avisar dos veces |
| `startsUtc` | fecha | sí | Inicio, **con zona horaria** |
| `title` | texto | no | Máx. 80. Por defecto «Sesión de prueba» |
| `description` | texto | no | Máx. 500: qué se prueba, cómo conectarse… |
| `durationMinutes` | número | no | 15 a 720 (se ajusta a ese rango). Por defecto 120 |
| `requiredVersion` | texto | no | Build que hay que tener (mismo formato que `match` de `blockedBuilds`) |
| `discordUrl` | https | no | Canal o evento de Discord |

Qué hace el launcher:

- **Cuenta atrás**: «Empieza en 3 h 12 min»; durante la sesión, «En curso · termina a las 23:00». Fecha y hora en la zona
  de cada jugador.
- **Aviso 15 minutos antes**: notificación de Windows y parpadeo de la ventana, **solo si el launcher está abierto** y el
  jugador no lo desactivó en **Ajustes › «Avisarme antes de las sesiones de prueba»**. Una vez por sesión (por `id`). Si
  abre el launcher hasta 5 minutos después del inicio, también avisa.
- **Añadir al calendario**: guarda un `.ics` (con recordatorio 15 min antes) que se abre con Outlook, Google Calendar, etc.
- **Versión necesaria** (si pones `requiredVersion`): ✓ «Tienes la versión correcta» o aviso ámbar «Necesitas …» con
  botón **Actualizar** / **Instalar esa versión**. Si esa versión no aparece en la lista del jugador (por ejemplo, es una
  pre-release y no tiene activadas las versiones de prueba), se le indica.

## hosting: hostear partida

Datos extra para el diálogo **⋯ › «Hostear partida: mi IP»**, que muestra las IP locales del jugador (la de su red LAN
destacada) y, solo si la pide, su IP pública (consulta `api.ipify.org` o `checkip.amazonaws.com`), con botones Copiar.
El diálogo funciona sin este bloque; esto solo añade información.

| Campo | Tipo | Obligatorio | Detalles |
|---|---|---|---|
| `port` | número | no | 1 a 65535. Se muestra «Puerto del juego: 7770». Fuera de rango, se ignora |
| `notes` | texto | no | Máx. 300. Indicaciones cortas (abrir puerto, VPN…) |

## bugReportWebhookUrl: Reportar un problema

Webhook de Discord al que llegan los reportes de **Reportar un problema** (menú ⋯, Ajustes, aviso de cierre inesperado del
juego y diálogos de error). Ponlo en un canal `#bugs` distinto de `#feedback`.

- Mismas reglas que el webhook de la encuesta: `https://discord.com/api/webhooks/<id>/<token>` (también
  `discordapp.com`, `canary.` o `ptb.`). Si no es válido se descarta (y se apunta en el log).
- **Orden de preferencia**: este webhook → el `discordWebhookUrl` de `launcher/feedback.json` → ninguno. Sin webhook,
  los reportes esperan en la bandeja de cada PC (`%LOCALAPPDATA%\PalitoLauncher\reports\outbox\`) y se envían solos
  cuando haya uno.
- Cada reporte lleva la descripción, los pasos, la categoría, versiones, y los registros `Player.log`,
  `Player-prev.log` y `launcher.log` (últimos 2 MB de cada uno, **anonimizados**) más un `summary.txt`. Si Discord
  rechaza el tamaño, se reenvía una vez solo con el resumen.
- El archivo es **público** (como `feedback.json`): cualquiera puede leer la URL del webhook. Si alguien hace spam,
  rótalo ([TAREAS-MANUALES.md, punto 9](TAREAS-MANUALES.md#9-renovar-o-rotar-un-webhook)).

Detalles del envío y de la bandeja: [FEEDBACK.md](FEEDBACK.md#reportar-un-problema-un-webhook-aparte-105) y la
documentación de `BugReportService`.

## Probar sin publicar

1. Guarda tu archivo como `%LOCALAPPDATA%\PalitoLauncher\launcher.local.json`. Tiene prioridad sobre el publicado, **solo
   en tu PC**.
2. Reinicia el launcher (o espera a la siguiente lectura, cada 15 min).
3. Busca `launcher.json:` y `Manifiesto del launcher` en el log (`%LOCALAPPDATA%\PalitoLauncher\logs`): dice qué se
   cargó y qué se descartó. El **informe para el desarrollador** también incluye un resumen (`launcher.json`, anuncios
   visibles, próxima sesión…).
4. Para probar una sesión, pon `startsUtc` dentro de 10 minutos: verás el aviso enseguida. Cambia el `id` para repetir.
5. Cuando esté bien, publícalo por PR como `launcher/launcher.json` y **borra el archivo local**.

Si `launcher.local.json` no es JSON válido, se ignora y se usa el publicado (y se avisa en el log).

## Problemas típicos

| Síntoma | Causa probable | Qué hacer |
|---|---|---|
| No sale nada | Launcher anterior a 1.0.5, archivo en otra ruta o JSON inválido sin copia buena | Comprueba la versión en Ajustes, la URL raw y el log (`Manifiesto del launcher`) |
| Publiqué un cambio y no se ve | Caché de raw.githubusercontent.com (≈ 5 min) + lectura cada 15 min | Espera o reinicia el launcher |
| Mi launcher ignora lo publicado | Tienes `launcher.local.json` | Bórralo |
| Falta un anuncio | Sin `id`/`title` válidos, fuera de su ventana de fechas, cerrado por ti, o hay más de 3 | Mira el log; cambia el `id` para que vuelva a salir |
| La sesión sale a otra hora | Fecha sin zona horaria | Añade `+01:00`/`+02:00` o `Z` |
| No llega el aviso de la sesión | Launcher cerrado, aviso desactivado en Ajustes, o ya se avisó de ese `id` | Normal; cambia el `id` para probar |
| «Con problemas» no aparece | `match` no coincide con tag, zip ni versión | Copia el nombre exacto del zip de la release |
| Los reportes no llegan a `#bugs` | `bugReportWebhookUrl` inválido → se usan el de la encuesta o la bandeja | Revisa la URL; en el log aparece «bugReportWebhookUrl: no es un webhook de Discord válido» |
