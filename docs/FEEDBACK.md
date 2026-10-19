# Encuesta y opiniones (feedback)

Guía para el desarrollador: cómo recibir en Discord las opiniones de los jugadores, cómo cambiar las preguntas sin
publicar una versión del launcher y cómo probarlo todo antes de que lo vea nadie.

## Qué es

Desde la 1.0.3, el launcher puede pedir una opinión rápida a quien juega:

- **Al cerrar el juego**, si la partida duró lo suficiente, aparece un aviso discreto (no bloquea nada):
  «¿Qué tal la partida de 34 min? Tu opinión ayuda a mejorar Coop2D.» con 5 estrellas, **Dar mi opinión**, **Ahora no**
  y **No volver a preguntar**. Pulsar una estrella abre la encuesta con esa valoración ya puesta.
- **Cuando quiera**, con **Enviar opinión** (en Inicio y en el menú **⋯**). Funciona aunque el juego no esté instalado.
- Si el juego se cerró con un error, en lugar de la invitación sale el aviso de cierre inesperado.

La encuesta es un diálogo dentro del launcher (≈ 1 minuto). Las respuestas llegan a un **canal de Discord** a través de
un **webhook**, junto con la versión del juego, la del launcher y la de Windows. Las preguntas y el webhook se definen en
un archivo **`launcher/feedback.json`** del repo **Coop2D**: cambiarlo cambia la encuesta de todos los launchers sin
sacar versión nueva.

## Resumen rápido

1. Crea un webhook en un canal privado de tu servidor de Discord ([paso 1](#1-crear-el-webhook-de-discord)).
2. Copia [`feedback.example.json`](feedback.example.json), ajusta las preguntas y pega la URL del webhook.
3. Pruébalo en tu PC guardándolo como `%LOCALAPPDATA%\PalitoLauncher\feedback.local.json`
   ([paso 2](#2-probar-sin-publicar-feedbacklocaljson)).
4. Publícalo como `launcher/feedback.json` en la rama `main` de Coop2D con una pull request
   ([paso 3](#3-publicar-launcherfeedbackjson-en-coop2d)).
5. Los launchers lo usan la próxima vez que se abran y envían también las respuestas que tuvieran pendientes.

> **Sin `feedback.json` no se pierde nada.** El launcher usa una [encuesta por defecto](#encuesta-por-defecto) y, como
> no tiene webhook, guarda las respuestas en la [bandeja de salida](#7-la-bandeja-de-salida) de cada PC. Se envían
> solas en cuanto publiques un `feedback.json` con webhook.

## Cómo elige el launcher la encuesta

Al arrancar (en segundo plano, después de comprobar las releases) el launcher busca la encuesta en este orden:

1. **`%LOCALAPPDATA%\PalitoLauncher\feedback.local.json`**: solo en tu PC, para probar.
2. **`launcher/feedback.json`** de la rama `main` de Coop2D
   (`https://raw.githubusercontent.com/JJJP-Dev/Coop2D/main/launcher/feedback.json`, configurable en
   `LauncherConfig.RemoteFeedbackConfigUrl`). Se descarga con caché y ETag desde raw.githubusercontent.com, así que
   **no gasta cupo de la API de GitHub**, y funciona sin conexión con la última copia buena.
3. La **encuesta por defecto**, si no hay ninguna de las anteriores o no es válida.

Detalles:

- El archivo se valida y se **sanea** (ver [esquema](#4-esquema-del-json)): lo que no cumple las reglas se recorta o
  se descarta en silencio y queda una línea de aviso en el log. Un archivo que no es JSON válido, o con preguntas pero
  ninguna válida, no se usa.
- Si publicas un JSON así, los launchers que ya tenían una versión buena **siguen con ella**; los que no, usan la
  encuesta por defecto.
- Si borras el archivo del repo, los launchers vuelven a la encuesta por defecto, sin webhook.
- raw.githubusercontent.com cachea los archivos unos 5 minutos: un cambio recién publicado puede tardar un poco en
  llegar, y cada launcher lo recoge la próxima vez que se abre.

### Cuándo se invita a responder

La invitación al cerrar el juego sale solo si se cumplen **todas** estas condiciones:

- El jugador tiene activado **Ajustes → Juego → Pedirme opinión al cerrar el juego** (activado por defecto; el enlace
  **No volver a preguntar** lo desactiva).
- La encuesta tiene `askAfterSession: true`.
- La partida duró al menos `minSessionMinutes` minutos (cuenta desde que el launcher abre el juego o se engancha a uno ya
  abierto).
- Han pasado al menos `minHoursBetweenPrompts` horas desde la última invitación en ese PC.

El botón **Enviar opinión** funciona siempre, con cualquier configuración. Para evitar dobles envíos, cada PC puede
enviar como mucho una opinión cada 30 segundos.

### Encuesta por defecto

Es la que ven todos mientras no haya `feedback.json` (Id `default`, sin webhook). Sirve como punto de partida: usa los
mismos Ids si quieres comparar respuestas.

| Id | Tipo | Pregunta |
|---|---|---|
| `overall` | rating, obligatoria | En general, ¿qué tal la partida? |
| `again` | choice, obligatoria | ¿Te apetece volver a jugar? (Sí, otra partida ya / Sí, otro día / No mucho) |
| `difficulty` | scale 1..5 | ¿Qué tal la dificultad? (Muy fácil … Muy difícil) |
| `players` | choice | ¿Cuántos jugabais? (Solo / 2 / 3 / 4) |
| `issues` | multi | ¿Tuviste algún problema? (Lag o desconexiones / El juego se cerró / Va lento o a tirones / Fallos visuales / Controles incómodos / Ninguno) |
| `best` | text | ¿Qué fue lo mejor? |
| `improve` | text | ¿Qué cambiarías o mejorarías? |

## 1. Crear el webhook de Discord

Un webhook es una URL que permite publicar mensajes en **un** canal concreto. Necesitas el permiso **Gestionar
webhooks** en el servidor (el dueño y los administradores lo tienen).

1. Crea un **canal de texto privado** para las opiniones, por ejemplo `#feedback-coop2d`, visible solo para ti (o para
   el equipo). Usa un canal de texto normal, no un canal de foro (en un foro, Discord rechaza los mensajes de webhook
   que no indican un hilo; ver la nota de `thread_id` más abajo).
2. Pasa el ratón por encima del canal y pulsa la rueda dentada (**Editar canal**, los ajustes del canal).
3. **Integraciones → Webhooks → Nuevo webhook**.
4. Pulsa el webhook que se acaba de crear (Discord le pone un nombre gracioso). Si quieres, cámbiale el nombre y la
   imagen (por ejemplo el logo de Coop2D); los mensajes del launcher se publican con el nombre «Coop2D · Feedback»
   pero usan esa imagen.
5. **Copiar URL de webhook**. Tiene esta forma:
   `https://discord.com/api/webhooks/123456789012345678/AbCdEf…` (un número y un token largo).
6. **Guardar cambios**.

Pega la URL **tal cual** en el campo `discordWebhookUrl`. Se aceptan solo URLs `https` de `discord.com`,
`discordapp.com`, `ptb.discord.com` o `canary.discord.com` con la ruta `/api/webhooks/{id}/{token}`; cualquier otra
cosa se ignora y las respuestas se quedan en la bandeja de salida. El único añadido que se respeta es
`?thread_id=<número>`, para publicar dentro de un hilo concreto (clic derecho en el hilo → **Copiar ID del hilo**, con el
modo desarrollador de Discord activado); así también sirve un canal de foro.

## 2. Probar sin publicar (feedback.local.json)

1. Abre la carpeta del launcher: **Win + R** → `%LOCALAPPDATA%\PalitoLauncher` → Aceptar.
2. Guarda tu encuesta ahí con el nombre **`feedback.local.json`** (UTF-8; el Bloc de notas y VS Code lo guardan así
   por defecto). Tiene prioridad sobre la publicada, **solo en tu PC**.
3. **Cierra el launcher del todo y vuelve a abrirlo** (la encuesta se lee al arrancar).
4. Pulsa **Enviar opinión**: deberías ver tu título y tus preguntas. Envía una respuesta y mira el canal de Discord.

Para probar también la invitación al cerrar el juego sin esperar, pon temporalmente en el archivo local
`"minSessionMinutes": 1` y `"minHoursBetweenPrompts": 0`, abre el juego desde el launcher, juega más de un minuto y
ciérralo. **Antes de publicar, vuelve a poner los valores de verdad.**

Si en lugar de tu encuesta aparece otra (la publicada o la [encuesta por defecto](#encuesta-por-defecto)), el JSON
tiene algún error o ninguna pregunta válida. Mira el log del día, `%LOCALAPPDATA%\PalitoLauncher\logs\launcher-AAAAMMDD.log`,
y busca `Encuesta de feedback`: cuando todo va bien dice «se usa el archivo local feedback.local.json («pre2-fun-test», 8
preguntas, webhook: …)»; si no, explica el motivo y qué preguntas se descartaron. Para comprobar que el JSON está bien
formado:

```powershell
Get-Content .\feedback.json -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null   # sin salida = JSON correcto
```

> **Borra `feedback.local.json` cuando termines.** Mientras exista, tu launcher lo usa e ignora el que publiques en el
> repo.

## 3. Publicar launcher/feedback.json en Coop2D

El archivo va en la carpeta **`launcher/`** de la raíz del repo Coop2D (la misma del fondo remoto `background.png`),
fuera de `Assets/`, así que Unity no lo ve. Como `main` está protegida, se publica con una **pull request**.

**Desde la web de GitHub**

1. Repo **Coop2D**, rama `main` → **Add file → Create new file** (o abre `launcher/feedback.json` y pulsa el lápiz si
   ya existe).
2. Nombre: `launcher/feedback.json` (al escribir la `/` se crea la carpeta).
3. Pega el contenido.
4. **Commit changes…** → elige **Create a new branch for this commit and start a pull request**, por ejemplo con la
   rama `feedback/pre2-fun-test` → **Propose changes** → **Create pull request**.
5. Revisa el diff y pulsa **Merge pull request**.
6. Comprueba que está publicado abriendo
   <https://raw.githubusercontent.com/JJJP-Dev/Coop2D/main/launcher/feedback.json> (puede tardar unos minutos en
   mostrar la versión nueva).

**Con git y GitHub CLI** (desde una copia del repo Coop2D)

```powershell
git switch main
git pull
git switch -c feedback/pre2-fun-test
New-Item -ItemType Directory -Force launcher | Out-Null
Copy-Item C:\ruta\a\tu\feedback.json launcher\feedback.json   # la que probaste, sin los valores de prueba
git add launcher/feedback.json
git commit -m "Feedback: encuesta del Pre 2 (prueba de diversión)"
git push -u origin feedback/pre2-fun-test
gh pr create --repo JJJP-Dev/Coop2D --base main --title "Feedback: encuesta del Pre 2" --body "Encuesta de la prueba de diversión."
gh pr merge --squash --delete-branch
```

Para dejar de invitar al cerrar el juego sin quitar la encuesta, publica el mismo archivo con `"askAfterSession": false`
(el botón **Enviar opinión** sigue funcionando).

Si de momento solo quieres recibir las respuestas de la encuesta por defecto, basta con un `feedback.json` que tenga
únicamente el webhook:

```json
{
  "discordWebhookUrl": "https://discord.com/api/webhooks/123456789012345678/AbCdEf…"
}
```

## 4. Esquema del JSON

JSON en UTF-8 con nombres de campo en **camelCase**, como en el [ejemplo](#5-ejemplo-completo-pre-2) (el launcher no
distingue mayúsculas en los nombres). También tolera comentarios `//` y comas finales, pero mejor no usarlos: así
cualquier editor o validador acepta el archivo. Los campos desconocidos se ignoran y el archivo puede ocupar como mucho
256 KB (de sobra).

### Encuesta

| Campo | Tipo | Por defecto | Reglas |
|---|---|---|---|
| `id` | texto | `"default"` | Identifica la encuesta en Discord (p. ej. `"pre2-fun-test"`). Letras, números, `-`, `_` y `.`, máx. 64 (el resto de caracteres se quita). **Cámbialo cuando cambies las preguntas.** |
| `title` | texto | `"Tu opinión sobre Coop2D"` | Título del diálogo, en una línea. Máx. 120 caracteres. Vacío → el valor por defecto. |
| `intro` | texto o `null` | `null` | Texto bajo el título. Máx. 300 caracteres. Admite saltos de línea (`\n`). |
| `askAfterSession` | `true` / `false` | `true` | `false` = no invitar al cerrar el juego (el botón manual sigue funcionando). |
| `minSessionMinutes` | número | `5` | Minutos mínimos de partida para invitar. Se limita a 1..120. |
| `minHoursBetweenPrompts` | número | `12` | Horas mínimas entre dos invitaciones en el mismo PC. Se limita a 0..168. |
| `discordWebhookUrl` | texto o `null` | `null` | URL del webhook ([paso 1](#1-crear-el-webhook-de-discord)). Si falta o no es válida, las respuestas esperan en la bandeja de salida. |
| `questions` | lista | — | De 1 a 12 preguntas; a partir de la 12.ª se ignoran. |

### Pregunta

| Campo | Tipo | Para | Por defecto | Reglas |
|---|---|---|---|---|
| `id` | texto | todas | — | **Obligatorio.** Solo letras, números, `-` y `_`, máx. 64 (p. ej. `"overall"`, `"combo-fun"`). Único dentro de la encuesta (sin distinguir mayúsculas). |
| `type` | texto | todas | — | **Obligatorio.** `"rating"`, `"scale"`, `"choice"`, `"multi"` o `"text"` (sin distinguir mayúsculas). |
| `text` | texto | todas | — | **Obligatorio.** La pregunta, en una línea. Máx. 200 caracteres. |
| `hint` | texto o `null` | todas | `null` | Ayuda en gris bajo la pregunta. Máx. 200 caracteres. |
| `required` | `true` / `false` | todas | `false` | Obligatoria: **Enviar** no se activa hasta que esté respondida. |
| `options` | lista de textos | choice, multi | `[]` | De 2 a 10 opciones, cada una de máx. 80 caracteres. Las vacías y las repetidas (sin distinguir mayúsculas) se quitan. |
| `min` / `max` | número | scale | `1` / `5` | Rango 0..10 con `min` < `max` (si están al revés se intercambian; si son iguales, 1..5). |
| `minLabel` / `maxLabel` | texto o `null` | scale | `null` | Etiquetas bajo los extremos (p. ej. «Muy fácil» / «Muy difícil»). Máx. 60 caracteres. |
| `maxLength` | número | text | `1000` | Caracteres máximos de la respuesta. Se limita a 1..1000 (límite de Discord). |
| `placeholder` | texto o `null` | text | `null` | Texto de ejemplo en gris dentro de la caja vacía. Máx. 200 caracteres. |

Los campos que no corresponden al tipo (por ejemplo `options` en una `rating`) se ignoran.

### Tipos de pregunta

| `type` | Cómo se ve | Cómo llega a Discord |
|---|---|---|
| `rating` | 5 estrellas grandes (ratón, teclado ← → o 1-5) | `★★★★☆ (4/5)` |
| `scale` | Una fila de botones de `min` a `max` con las etiquetas debajo de los extremos | `4 / 5 (Muy difícil)` (la etiqueta solo si es un extremo) |
| `choice` | Botones «píldora»: se elige **una** opción | La opción |
| `multi` | Píldoras: se eligen **varias**. Una opción llamada exactamente **«Ninguno»** o **«Ninguna»** es exclusiva: al marcarla se desmarcan las demás, y al revés | Opciones separadas por ` · ` |
| `text` | Caja de texto de 3-4 líneas con contador «123 / 1000» | El texto |

La **primera pregunta `rating`** es especial: las estrellas de la invitación la rellenan y su valor da color al mensaje
de Discord. Ponla la primera y obligatoria.

### Qué se descarta o recorta al sanear

- **Se descarta la pregunta** si: su `id` no es válido o está repetido, no tiene `text`, su `type` no existe, o es
  `choice`/`multi` con menos de 2 opciones válidas.
- **Se recorta**: textos más largos que su máximo (terminan en «…»), opciones de más (quedan 10), preguntas de más
  (quedan 12). Los saltos de línea solo se respetan en `intro`.
- **Se ajusta al rango**: `min`/`max` de las escalas, `maxLength`, `minSessionMinutes` y `minHoursBetweenPrompts`.
- **Se ignora** `discordWebhookUrl` si no es un webhook de Discord válido.

## 5. Ejemplo completo (Pre 2)

El milestone **Pre 2 · Gameplay prototype** pregunta «¿es divertido combinar elementos con un compañero?», así que la
encuesta de la prueba de diversión se centra en eso. Está lista para copiar en
[`docs/feedback.example.json`](feedback.example.json) (cambia el placeholder del webhook por tu URL; tal cual, se
ignora y las respuestas se quedan en la bandeja):

```json
{
  "id": "pre2-fun-test",
  "title": "Prueba de diversión · Prototipo 2",
  "intro": "Estamos comprobando si combinar elementos con tus compañeros es divertido. Responde con sinceridad: si algo no divierte, es justo lo que necesitamos saber.",
  "askAfterSession": true,
  "minSessionMinutes": 5,
  "minHoursBetweenPrompts": 12,
  "discordWebhookUrl": "https://discord.com/api/webhooks/PEGA_AQUI_LA_URL_DE_TU_WEBHOOK",
  "questions": [
    {
      "id": "overall",
      "type": "rating",
      "text": "En general, ¿qué tal la partida?",
      "required": true
    },
    {
      "id": "combo-fun",
      "type": "scale",
      "text": "¿Fue divertido combinar elementos con tus compañeros?",
      "required": true,
      "min": 1,
      "max": 5,
      "minLabel": "Nada divertido",
      "maxLabel": "Muy divertido"
    },
    {
      "id": "detonate-clear",
      "type": "choice",
      "text": "¿Entendiste cuándo detonar una reacción?",
      "hint": "Según lo que notaste jugando, no lo que te hayan explicado.",
      "required": true,
      "options": [
        "Sí, lo tuve claro",
        "Más o menos",
        "No, las reacciones salían por casualidad"
      ]
    },
    {
      "id": "favorite-reaction",
      "type": "choice",
      "text": "¿Qué reacción te gustó más?",
      "options": [
        "Electrocución",
        "Vapor",
        "Sobrecarga",
        "No me fijé"
      ]
    },
    {
      "id": "again",
      "type": "choice",
      "text": "¿Te apetece volver a jugar?",
      "options": [
        "Sí, otra partida ya",
        "Sí, otro día",
        "No mucho"
      ]
    },
    {
      "id": "players",
      "type": "choice",
      "text": "¿Cuántos jugabais?",
      "options": [
        "Solo",
        "2",
        "3",
        "4"
      ]
    },
    {
      "id": "issues",
      "type": "multi",
      "text": "¿Tuviste algún problema?",
      "hint": "Marca todos los que quieras.",
      "options": [
        "Lag o desconexiones",
        "El juego se cerró",
        "Va lento o a tirones",
        "Fallos visuales",
        "Controles incómodos",
        "Ninguno"
      ]
    },
    {
      "id": "improve",
      "type": "text",
      "text": "¿Qué cambiarías para que combinar elementos fuera más divertido?",
      "maxLength": 1000,
      "placeholder": "Lo que se te ocurra: qué sobraba, qué faltaba, qué te frustró…"
    }
  ]
}
```

Por qué así:

- **3 obligatorias** (`overall`, `combo-fun`, `detonate-clear`): son las que responden a la pregunta del milestone. El
  resto es opcional para que nadie abandone a medias.
- `combo-fun` es una escala con etiquetas para medir **cuánto** divierte; `detonate-clear` comprueba si las reglas de
  aura y detonante se entienden **sin explicarlas** (por eso la ayuda no da pistas).
- `again`, `players` e `issues` usan **los mismos Ids y opciones que la encuesta por defecto**: así se pueden comparar
  con las respuestas de antes y de después del Pre 2.
- Una sola pregunta de texto, opcional y concreta, al final.

## 6. Qué llega a Discord

Cada respuesta es un mensaje del webhook con el nombre **Coop2D · Feedback** y una tarjeta (embed) así:

```
Opinión · Coop2D 0.0.3 · Prototipo 2
  En general, ¿qué tal la partida?                         ★★★★☆ (4/5)
  ¿Fue divertido combinar elementos con tus compañeros?    5 / 5 (Muy divertido)
  ¿Entendiste cuándo detonar una reacción?                 Más o menos
  ¿Tuviste algún problema?                                 Lag o desconexiones · Fallos visuales
  ¿Qué cambiarías para que combinar elementos fuera…?      Que el vapor dure un poco más…
  Jugador: Marta     Partida: 34 min · salió bien     Encuesta: pre2-fun-test
Launcher 1.0.3 · Windows 10.0.26100 · hoy a las 21:14
```

- **Título**: la versión instalada del juego («versión desconocida» si no lo tiene instalado).
- **Color** de la barra lateral según la primera valoración `rating`: 1-2 rojo, 3 ámbar, 4-5 verde, sin valoración azul.
  Así se ve de un vistazo cómo fue la sesión.
- **Un campo por pregunta respondida**, en el orden de la encuesta. Las que se dejaron en blanco no aparecen.
- **Jugador**: el nombre o apodo que escribió (opcional, se recuerda para la próxima vez) o «Anónimo».
- **Partida**: duración y cómo terminó («34 min · salió bien», o «código X» si el juego se cerró mal) cuando la opinión
  viene de la invitación al cerrar el juego, o «Enviada desde «Enviar opinión»» si la mandó a mano.
- **Encuesta**: el `id` de la encuesta, para saber qué versión de las preguntas respondió.
- **Pie**: versión del launcher y de Windows, y la fecha de la respuesta (la de cuando se escribió, aunque llegue más
  tarde desde la bandeja de salida).
- Las menciones (`@everyone`, `@usuario`) que alguien escriba no avisan a nadie, y los textos muy largos se recortan
  con «…» para cumplir los límites de Discord.

## 7. La bandeja de salida

Cada respuesta se guarda **siempre** primero en disco y se borra solo cuando Discord confirma que la ha recibido, así
que no se pierde aunque no haya internet, falte el webhook o Discord falle.

- Carpeta: `%LOCALAPPDATA%\PalitoLauncher\feedback\outbox\`, un archivo JSON por respuesta
  (`AAAAMMDDhhmmss-<id>.json`) con las preguntas tal como se mostraron y las respuestas, así que se formatean bien
  aunque cambies la encuesta después.
- Se reintenta **al abrir el launcher** (hasta 10 respuestas cada vez, de la más antigua a la más nueva) y con
  **Ajustes → Juego → Reintentar envío**, que aparece junto a «N opiniones pendientes de enviar» cuando hay alguna.

| Qué pasa al enviar | Qué hace el launcher |
|---|---|
| Discord la acepta | La borra de la bandeja |
| Sin internet o Discord caído (5xx) | La deja en la bandeja y lo reintenta más tarde |
| Demasiados mensajes (429) | Espera lo que diga Discord y la deja en la bandeja |
| Webhook borrado o no válido (401/403/404), o sin webhook | La deja en la bandeja hasta que haya un webhook válido en el JSON |
| Discord rechaza el mensaje (400) | La mueve a `outbox\failed\` para no reintentar sin fin (el motivo queda en el log) |
| Archivo de la bandeja dañado | Lo mueve a `outbox\failed\` |

Los archivos de `failed\` se pueden abrir con cualquier editor para leer la respuesta a mano. Si arreglas la causa,
devolverlos a `outbox\` hace que se reintenten.

## 8. Privacidad y seguridad del webhook

**Qué se envía**: las respuestas, el apodo si lo escribe, la versión del juego, la del launcher, la de Windows (p. ej.
«Microsoft Windows 10.0.26100»), la duración de la partida y el código de salida del juego si viene de la invitación, y
la fecha. **No** se envían el nombre de usuario de Windows, el nombre del PC, rutas, archivos ni identificadores del
equipo. Como en cualquier conexión a internet, Discord ve la IP desde la que llega el mensaje, pero no la muestra en el
canal. El diálogo de la encuesta lo explica en una línea al pie.

**La URL del webhook es pública.** El repo Coop2D es público y cualquiera puede leer `launcher/feedback.json`. Con la
URL se puede publicar en **ese** canal (y cambiar o borrar el propio webhook), pero no leer el canal ni tocar nada más
del servidor. Por eso:

- Usa un **canal privado dedicado** solo a las opiniones, sin nadie más dentro.
- No pongas el webhook en el código del launcher ni en otro sitio: solo en `feedback.json`, para poder cambiarlo.

**Si alguien lo usa para spam:**

1. En Discord: **Editar canal → Integraciones → Webhooks**, abre el webhook y pulsa **Eliminar webhook**. El spam se
   corta al momento.
2. Crea un webhook nuevo ([paso 1](#1-crear-el-webhook-de-discord)).
3. Actualiza `discordWebhookUrl` en `launcher/feedback.json` con una pull request ([paso 3](#3-publicar-launcherfeedbackjson-en-coop2d)).

**No hace falta sacar versión del launcher.** Mientras tanto, las respuestas de tus amigos esperan en su bandeja de
salida (el webhook borrado responde 404) y llegan solas cuando su launcher recoge el JSON nuevo.

## 9. Consejos para una buena encuesta

- **Pocas preguntas**: 5-8 (≈ 1 minuto). Se admiten 12, pero cada pregunta de más hace que menos gente termine.
- **Empieza por una valoración `rating` obligatoria**: la rellenan las estrellas de la invitación y colorea el mensaje.
- **2-3 obligatorias como mucho**, y las de texto siempre opcionales.
- **Pregunta por la duda del milestone.** Cada milestone de Coop2D tiene su «Pregunta:» (Pre 2: «¿es divertido combinar
  elementos con un compañero?»; Pre 3: «¿una partida entera tiene ritmo?»): la encuesta debería ayudarte a responderla.
- **Una idea por pregunta y redacción neutra**: «¿Fue divertido…?» con una escala, no «¿Verdad que fue divertido?». No
  des la respuesta en la ayuda.
- **Opciones que no se solapen** y una salida para quien no sabe o no tuvo problemas («No me fijé», «Ninguno»).
- **Cambia el `id` de la encuesta cuando cambien las preguntas** (`pre2-fun-test` → `pre2-fun-test-2`, `pre3-pacing`…):
  en Discord sabrás qué versión respondió cada uno.
- **Mantén los mismos Ids de pregunta** (`overall`, `again`, `players`, `issues`…) mientras la pregunta signifique lo
  mismo, para comparar entre versiones del juego. Si cambias el sentido de una pregunta, dale un Id nuevo; nunca
  reutilices un Id para otra cosa.
- **Escalas siempre en la misma dirección** (el número alto = «más»), con etiquetas en los extremos.
- **Ajusta los tiempos a la sesión**: `minSessionMinutes` algo menor que una partida normal (para no preguntar tras una
  prueba de 30 segundos) y `minHoursBetweenPrompts` para preguntar una vez por sesión de juego, no tras cada partida.
- **Publica la encuesta antes de la sesión de playtest** (y pruébala en local): los launchers la recogen al abrirse.
- **Cierra el círculo**: cuenta en las notas de la siguiente release qué cambiaste gracias a las respuestas. Es lo que
  hace que la gente siga respondiendo.
