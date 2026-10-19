# Publicar una build de Coop2D

Guía para el desarrollador: cómo subir una versión del juego a las
[releases de JJJP-Dev/Coop2D](https://github.com/JJJP-Dev/Coop2D/releases) para que el launcher de tus amigos la
detecte, la descargue y la instale sola.

## Resumen rápido

1. Pon la versión en Unity (*Player Settings → Version*, p. ej. `0.0.3-prototype.2`) y compila para Windows x64 con el
   ejecutable llamado **`Coop2D.exe`**.
2. Comprime la carpeta de la build siguiendo el [esquema de versiones](#1-esquema-de-versiones-del-proyecto): durante
   la pre-producción **`2D_0.0.N_PrototypeP.zip`** (P = número del Pre); en producción **`2D_X.Y.Z.zip`**. El número
   siempre **mayor** que el de la build anterior.
3. En GitHub: **Releases → Draft a new release**, tag `v0.0.N`, título, notas, adjunta el zip (**uno solo**), espera
   a que termine de subir y pulsa **Publish release** (sin marcar «pre-release» si es para todo el grupo).
4. Los launchers lo detectan al abrirse, cada hora, antes de pulsar JUGAR (si la última comprobación tiene más de
   10 minutos) o al pulsar **Buscar actualizaciones**.

## 1. Esquema de versiones del proyecto

Sigue la regla del documento de diseño: **0.0.x durante toda la pre-producción; en producción, el número del milestone
(0.1, 0.2…) y un tercer número para las builds intermedias** (como 0.5.1 para un arreglo durante la CBT 1).

| Fase | Milestone | Nombre del asset | El launcher muestra |
|---|---|---|---|
| Pre-producción | Pre 1 · Tech prototype | `2D_0.0.1_Protype1.zip` *(publicado)* | 0.0.1 · Prototipo 1 |
| | Pre 2 · Gameplay prototype | `2D_0.0.2_Protype2.zip` *(publicado)*, después `2D_0.0.3_Prototype2.zip`, `2D_0.0.4_Prototype2.zip`… | 0.0.3 · Prototipo 2 |
| | Pre 3 · First playable | `2D_0.0.N_Prototype3.zip` | 0.0.N · Prototipo 3 |
| | Pre 4 · Vertical slice | `2D_0.0.N_Prototype4.zip` | 0.0.N · Prototipo 4 |
| Producción | 0.1 · Runs and rooms | `2D_0.1.0.zip`; arreglos: `2D_0.1.1.zip`, `2D_0.1.2.zip`… | 0.1.0 |
| | 0.2 · Meta and hub … 0.4 · Alpha | `2D_0.2.0.zip` … `2D_0.4.0.zip` | 0.4.0 |
| Betas | 0.5 · Beta · CBT 1 … 0.9 · Private beta | `2D_0.5.0.zip` … `2D_0.9.0.zip` | 0.5.0 |
| Lanzamiento | 1.0 · Launch | `2D_1.0.0.zip` | 1.0.0 |

Reglas:

- **En pre-producción, el tercer número sube con CADA build** y la etiqueta `PrototypeP` solo dice a qué Pre
  pertenece. Así se pueden sacar todas las builds que haga falta dentro de un mismo Pre (iterar el Pre 2 hasta que
  divierta) sin trucos. Las dos releases ya publicadas encajan tal cual: no hay que renombrar nada.
- **En producción el archivo lleva solo el número.** El número ya dice la fase según el roadmap; el nombre del
  milestone («0.4 · Alpha», «0.5 · Beta · CBT 1») va en el **título** de la release, no en el archivo.
- **No hace falta marcar como «pre» ninguna versión 0.x**: el «0.» ya indica que no es la versión final.
- **El número nunca baja ni se repite con una etiqueta detrás.** Las etiquetas cuentan como *anteriores* a su número:
  después de `2D_0.1.0.zip`, un `2D_0.1.0_Beta1.zip` sería «más viejo» y nadie lo recibiría. Si quieres una build de
  prueba antes de una versión importante, ponla **antes**: `2D_0.5.0_RC1.zip` (se muestra «0.5.0 · RC 1») y luego
  `2D_0.5.0.zip`.
- **Escribe `Prototype` y no `Pre`** en el archivo: `2D_0.0.3_Pre2.zip` se mostraría como «0.0.3 · Preview 2». La
  errata `Protype` de las dos primeras también se entiende, pero mejor escribirlo bien a partir de ahora.
- **A partir de la 0.5 entra Steam** (Playtest en la CBT 1, demo en la 0.7, lanzamiento en la 1.0). Para la gente de
  fuera, las ramas de Steam sustituyen al launcher; el launcher sigue siendo la herramienta del equipo y de los amigos.

> El launcher tiene su **propia** numeración (`v1.0.2`…, en el repo PalitoLauncher). No tiene nada que ver con la del
> juego.

## 2. El nombre del archivo: cómo lo lee el launcher

El launcher deduce la versión **del nombre del asset** (si no tiene, del tag y si no, del título de la release).

| Archivo | Versión | Se muestra como |
|---|---|---|
| `2D_0.0.1_Protype1.zip` | 0.0.1, prototipo 1 | 0.0.1 · Prototipo 1 |
| `2D_0.0.3_Prototype2.zip` | 0.0.3, prototipo 2 | 0.0.3 · Prototipo 2 |
| `2D_0.1.0.zip` | 0.1.0 | 0.1.0 |
| `2D_0.5.0_RC1.zip` | 0.5.0, candidata 1 | 0.5.0 · RC 1 |
| `2D_1.0.zip` | 1.0.0 | 1.0.0 |
| `2D_0.1.0_win64.zip` | 0.1.0 (final: «win» se ignora) | 0.1.0 |

Detalles:

- **Orden** (de más vieja a más nueva): `0.0.1 · Prototipo 1` < `0.0.2 · Prototipo 2` < `0.0.3 · Prototipo 2` <
  `0.0.5 · Prototipo 3` < `0.1.0` < `0.1.1` < `0.4.0` < `0.5.0 · RC 1` < `0.5.0` < `1.0.0`.
  Con el mismo número, cualquier etiqueta va **antes** que la versión sin etiqueta.
- El launcher solo ofrece actualizar si la versión de la última release es **mayor** que la instalada (nunca baja
  de versión solo). Volver a una anterior se hace a mano desde **Versiones**.
- Hacen falta al menos dos números: `2D_1.zip` **no** tiene versión. Usa `2D_1.0.zip` o `2D_1.0.0.zip`.
- Etiquetas que entiende (sin distinguir mayúsculas), de menor a mayor: `dev` < cualquier palabra que empiece por
  `prot` (`Protype`, `Prototype`, `Prototipo`…) < `alpha` < `beta` < `preview`/`pre` < `rc` < versión final.
- Palabras que se ignoran (la versión sigue siendo final): `release`, `final`, `stable`, `build`, `full`, `setup`,
  `client`, `server`, `win`, `windows`, `x64`, `linux`, `mac`, `steam`, `itch`, `game`, `coop`, `launcher`.
- Cualquier otra palabra justo después del número (p. ej. `2D_0.3.0_test2.zip`) se trata como versión de prueba
  (mismo rango que un prototipo). Mejor evitarlo.
- El nombre debe empezar por `2D_` y terminar en `.zip`, `.rar` o `.7z` (patrón `^2D_.+\.(zip|rar|7z)$`, configurable en
  `LauncherConfig.GameAssetPattern`).

## 3. Canales: normal y de pruebas

La casilla **«Set as a pre-release»** de GitHub **no** significa «build en desarrollo» (todas lo son ahora mismo):
en el launcher funciona como **canal de pruebas**.

| Canal | Cómo se publica | Quién la recibe |
|---|---|---|
| **Normal** | Release normal (casilla sin marcar) | Todos los launchers |
| **De pruebas** | Release con **«Set as a pre-release»** marcado | Solo quien active **Ajustes → Actualizaciones → Incluir versiones de prueba (pre-release)**. En **Versiones** aparece con la etiqueta «De prueba» |

Cuándo usar cada uno:

- **Builds hechas desde `main`** (lo que el grupo va a jugar, incluidos todos los prototipos) → **canal normal**.
- **Builds de una rama `feature/...` o `fix/...`, experimentos, o probar tú la build antes de la sesión** → **canal de
  pruebas**. Ponle ya el número siguiente (p. ej. `2D_0.0.4_Prototype2.zip`).
- **Promocionar** una build de pruebas que salió bien: edita la release y **desmarca «Set as a pre-release»**. Todo el
  grupo la recibe sin volver a subir nada.
- **No marques todas como pre-release.** Un amigo con los ajustes por defecto que aún no tenga el juego vería «SIN
  VERSIONES», y quien ya lo tenga dejaría de recibir actualizaciones. Las dos primeras releases están bien sin marcar.

Otros:

- **Los borradores (drafts) son invisibles** para el launcher hasta que los publicas.
- «Set as the latest release» da igual: el launcher ordena por versión, no por fecha.

## 4. Versión dentro del juego y tags

- **Unity → Project Settings → Player → Version**: pon la misma versión que el asset en formato
  `MAYOR.MENOR.PARCHE[-etiqueta.número]`, por ejemplo `0.0.3-prototype.2` o `0.1.0`. Es lo que devuelve
  `Application.version`, así que la versión del menú (#37) y el rechazo de versiones distintas al conectar (#24)
  dirán lo mismo que el launcher y nadie entrará a una partida con otra build.
- **Tag de la release**: `v` + la versión sin etiqueta, p. ej. `v0.0.3`, `v0.1.0`, `v0.5.1`. El launcher no lo
  necesita (manda el nombre del asset), pero un tag uniforme ordena el historial de GitHub y los enlaces de
  comparación de las notas automáticas. Los tags antiguos (`0.0.1-Protype1`, `0.0.2-Prototype-2`) pueden quedarse
  como están.
- **Título de la release**: el milestone y la versión, p. ej. «Pre 2 · Gameplay prototype — 0.0.3» o
  «0.5 · Beta · CBT 1».

## 5. Un único archivo de juego por release

- Sube **un solo** archivo del juego por release. **`.zip` es lo recomendado**; `.rar` y `.7z` también valen.
- Si hubiera varios, el launcher elige el que coincide con el patrón, prefiriendo `.zip` > `.7z` > `.rar` y luego el
  más grande. Otros adjuntos (un `.txt`, capturas…) se ignoran, pero no subas varios `2D_….zip` a la misma release.
- Límite de GitHub: 2 GiB por archivo. Tus amigos necesitan libres unas **4 veces** el tamaño del zip mientras se
  instala (descarga + extracción + intercambio).

## 6. Qué va dentro del zip

- La build completa de Unity: `Coop2D.exe`, `Coop2D_Data\`, `UnityPlayer.dll`, `UnityCrashHandler64.exe`,
  `MonoBleedingEdge\`, `D3D12\`, etc.
- **Se permite una carpeta raíz** dentro del zip (es lo que pasa al comprimir la carpeta con el Explorador:
  `2D_0.0.3_Prototype2\Coop2D.exe`). El launcher busca el exe hasta 3 niveles de profundidad.
- **El ejecutable debe llamarse `Coop2D.exe`** (elige ese nombre al hacer Build en Unity; la carpeta `Coop2D_Data`
  sale con el mismo nombre). Si no lo encuentra, usa el único `.exe` de la carpeta que no sea `UnityCrashHandler`; si no
  hay ninguno, la instalación falla con un error claro y el juego anterior queda intacto.
- No incluyas partidas guardadas ni archivos de configuración personales. La carpeta `Sandbox` no entra en las builds.
- Cómo comprimir: clic derecho en la carpeta de la build → **Comprimir en archivo ZIP** (o «Enviar a → Carpeta
  comprimida») y renómbralo, p. ej. `2D_0.0.3_Prototype2.zip`. 7-Zip también vale.

## 7. Revisiones: re-subir el zip en la misma release

Si encuentras un fallo justo después de publicar, puedes **re-subir el zip en la misma release** (editar la release →
borrar el adjunto antiguo → adjuntar el nuevo, con el mismo nombre → **Update release**).

- Los amigos que ya tenían esa versión verán **«Hay una revisión nueva de 0.0.3 · Prototipo 2 (subida el dd/MM
  HH:mm)»** y podrán actualizar. Se detecta por el SHA-256 del archivo, que GitHub calcula solo.
- Cambiar solo las notas, el título o el tag **no** genera ninguna actualización.
- Si alguien estaba descargando justo en ese momento, el launcher detecta el cambio, vuelve a consultar y reintenta
  con el archivo nuevo.
- Para cambios grandes es mejor una build nueva con el número siguiente (`2D_0.0.4_Prototype2.zip`): queda más claro
  quién tiene qué. En una revisión el número no cambia, así que la versión de Unity tampoco.

## 8. No borres releases que la gente pueda tener instaladas

- Quien tenga una versión instalada puede seguir jugando aunque borres su release, pero **Reparar** necesitará la
  release original (si no existe, instalará la última) y la página **Versiones** solo muestra las que siguen publicadas
  (sirve para volver a una versión anterior si la nueva falla).
- Si una build está rota, publica una corrección con una versión mayor o una revisión, en lugar de borrarla.

## 9. Partidas guardadas: usa `Application.persistentDataPath`

**La carpeta del juego se reemplaza entera en cada actualización** (y al reparar). Todo lo que el juego escriba junto
al exe se pierde.

- Guarda partidas, ajustes y cualquier dato del jugador en **`Application.persistentDataPath`**, que en Windows es
  `%USERPROFILE%\AppData\LocalLow\PalitoGames\Coop2D`. El launcher nunca toca esa carpeta (tampoco al desinstalar).
- `PlayerPrefs` (registro de Windows) también se conserva.
- No cambies **Company Name** (`PalitoGames`) ni **Product Name** (`Coop2D`) en *Player Settings*: cambiarían esa ruta
  y parecería que se han perdido las partidas. El launcher también la usa para «Abrir guardados y logs del juego» y
  para incluir el `Player.log` en los informes.

## 10. Notas de la release

El launcher muestra las notas en la página de inicio («Novedades de …») y en **Versiones**. Entiende un Markdown
sencillo: títulos `#`, `##`, `###`; listas con `-` o `*`; `**negrita**`, `*cursiva*`, `` `código` ``;
enlaces `[texto](https://…)` y URLs sueltas; una línea en blanco separa párrafos. **Evita tablas e imágenes** (no se
ven en el launcher).

Plantilla:

```markdown
## Novedades
- Tres reacciones: Electrocución, Vapor y Sobrecarga.
- Iconos de aura sobre los enemigos.

## Arreglos
- El segundo jugador ya no se queda atascado al reaparecer.

## Problemas conocidos
- El texto flotante tiembla un poco con la cámara pixel perfect.

## Para jugar en cooperativo
Todos necesitáis la versión **0.0.3 · Prototipo 2**. Si el launcher muestra ACTUALIZAR, actualiza antes de jugar.
```

## 11. Paso a paso

**Desde la web de GitHub** (ejemplo: tercera build, dentro del Pre 2)

1. Unity → *Player Settings → Version* = `0.0.3-prototype.2`. Build para Windows x64 en una carpeta
   (`2D_0.0.3_Prototype2\`) con el exe `Coop2D.exe`.
2. Comprime la carpeta → `2D_0.0.3_Prototype2.zip`.
3. GitHub → repo **Coop2D** → **Releases** → **Draft a new release**.
4. **Choose a tag** → escribe `v0.0.3` → «Create new tag on publish».
5. Título «Pre 2 · Gameplay prototype — 0.0.3» y notas (plantilla de arriba).
6. Arrastra el zip a la zona de adjuntos y **espera a que termine de subir**.
7. «Set as a pre-release»: **sin marcar** si es para todo el grupo; **marcado** si es una build de pruebas
   ([canales](#3-canales-normal-y-de-pruebas)). Pulsa **Publish release**.
8. Comprueba en tu launcher: **Versiones → Actualizar lista** (las respuestas de GitHub se reutilizan durante 60 s).

**Con GitHub CLI**

```powershell
gh release create v0.0.3 2D_0.0.3_Prototype2.zip --repo JJJP-Dev/Coop2D --title "Pre 2 · Gameplay prototype — 0.0.3" --notes-file notas.md
# Build de pruebas (canal de pruebas):
gh release create v0.0.4 2D_0.0.4_Prototype2.zip --repo JJJP-Dev/Coop2D --title "Pre 2 — 0.0.4 (pruebas)" --notes-file notas.md --prerelease
# Promocionarla al canal normal cuando esté bien:
gh release edit v0.0.4 --repo JJJP-Dev/Coop2D --prerelease=false
# Revisión del mismo zip en la misma release:
gh release upload v0.0.3 2D_0.0.3_Prototype2.zip --repo JJJP-Dev/Coop2D --clobber
```

## Comprobaciones antes de publicar

- [ ] El nombre sigue el esquema (`2D_0.0.N_PrototypeP.zip` en pre-producción, `2D_X.Y.Z.zip` en producción) y la
      versión es **mayor** que la de la última release.
- [ ] La versión de Unity (*Player Settings → Version*) coincide con la del archivo.
- [ ] Dentro del zip está `Coop2D.exe` (en la raíz o en una sola carpeta) junto con `Coop2D_Data\`.
- [ ] La build arranca bien al descomprimirla en otra carpeta.
- [ ] Solo hay un archivo del juego en la release.
- [ ] «Set as a pre-release» marcado **solo** si es para el canal de pruebas.
- [ ] Las notas dicen qué versión necesitan todos para jugar juntos.
