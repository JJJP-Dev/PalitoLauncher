# Publicar una build de Coop2D

Guía para el desarrollador: cómo subir una versión del juego a las
[releases de JJJP-Dev/Coop2D](https://github.com/JJJP-Dev/Coop2D/releases) para que el launcher de tus amigos la
detecte, la descargue y la instale sola.

## Resumen rápido

1. Compila en Unity (Windows x64) con el ejecutable llamado **`Coop2D.exe`**.
2. Comprime la carpeta de la build en **`2D_X.Y.Z.zip`** (o `2D_X.Y.Z_ProtypeN.zip` para prototipos), con una
   versión **mayor** que la anterior.
3. En GitHub: **Releases → Draft a new release**, tag (p. ej. `v0.0.3`), título, notas, adjunta el zip (**uno solo**),
   espera a que termine de subir y pulsa **Publish release**.
4. Los launchers lo detectan al abrirse, cada hora, antes de pulsar JUGAR (si la última comprobación tiene más de
   10 minutos) o al pulsar **Buscar actualizaciones**.

## 1. El nombre del archivo: de ahí sale la versión

El launcher deduce la versión **del nombre del asset** (si no tiene, del tag y si no, del título de la release).
El tag y el título pueden ser lo que quieras, pero conviene que sean coherentes (`v0.0.3`, «0.0.3 — Nuevo mapa»).

Formatos:

| Archivo | Versión | Se muestra como |
|---|---|---|
| `2D_0.0.1_Protype1.zip` | 0.0.1, prototipo 1 | 0.0.1 · Prototipo 1 |
| `2D_0.0.2_Protype2.zip` | 0.0.2, prototipo 2 | 0.0.2 · Prototipo 2 |
| `2D_0.0.2.zip` | 0.0.2 (estable) | 0.0.2 |
| `2D_0.1.0.zip` | 0.1.0 | 0.1.0 |
| `2D_1.0.zip` | 1.0.0 | 1.0.0 |
| `2D_1.0.0_beta2.zip` | 1.0.0, beta 2 | 1.0.0 · Beta 2 |
| `2D_0.1.0_win64.zip` | 0.1.0 (estable: «win» se ignora) | 0.1.0 |

Reglas:

- **La versión del nombre SIEMPRE debe subir.** El launcher solo ofrece actualizar si la versión de la última release
  es mayor que la instalada (nunca baja de versión solo).
- Orden: `0.0.1 Prototipo 1` < `0.0.2 Prototipo 2` < `0.0.2 Prototipo 3` < `0.0.2 Prototipo 4` < **`0.0.2`** <
  `0.0.3` < `0.1.0`. Es decir, un prototipo va **antes** que la versión final con el mismo número.
- Por tanto, **después de los prototipos de 0.0.2 vale `2D_0.0.2.zip` o mayor, NUNCA `2D_0.0.1.zip`** (sería más
  vieja y nadie la recibiría).
- Hacen falta al menos dos números: `2D_1.zip` **no** tiene versión. Usa `2D_1.0.zip` o `2D_1.0.0.zip`.
- Etiquetas que entiende (sin distinguir mayúsculas), de menor a mayor: `dev` < cualquier palabra que empiece por
  `prot` (`Protype`, `Prototype`, `Prototipo`…) < `alpha` < `beta` < `preview` < `rc` < versión final.
- Palabras que se ignoran (la versión sigue siendo final): `release`, `final`, `stable`, `build`, `full`, `setup`,
  `client`, `server`, `win`, `windows`, `x64`, `linux`, `mac`, `steam`, `itch`, `game`, `coop`, `launcher`.
- Cualquier otra palabra justo después del número (p. ej. `2D_0.3.0_test2.zip`) se trata como versión de prueba
  (mismo rango que un prototipo). Mejor evitarlo.
- El nombre debe empezar por `2D_` y terminar en `.zip`, `.rar` o `.7z` (patrón `^2D_.+\.(zip|rar|7z)$`, configurable en
  `LauncherConfig.GameAssetPattern`).

## 2. Un único archivo de juego por release

- Sube **un solo** archivo del juego por release. **`.zip` es lo recomendado**; `.rar` y `.7z` también valen.
- Si hubiera varios, el launcher elige el que coincide con el patrón, prefiriendo `.zip` > `.7z` > `.rar` y luego el
  más grande. Otros adjuntos (un `.txt`, capturas…) se ignoran, pero no subas varios `2D_….zip` a la misma release.
- Límite de GitHub: 2 GiB por archivo. Tus amigos necesitan libres unas **4 veces** el tamaño del zip mientras se
  instala (descarga + extracción + intercambio).

## 3. Qué va dentro del zip

- La build completa de Unity: `Coop2D.exe`, `Coop2D_Data\`, `UnityPlayer.dll`, `UnityCrashHandler64.exe`,
  `MonoBleedingEdge\`, `D3D12\`, etc.
- **Se permite una carpeta raíz** dentro del zip (es lo que pasa al comprimir la carpeta con el Explorador:
  `2D_0.0.3\Coop2D.exe`). El launcher busca el exe hasta 3 niveles de profundidad.
- **El ejecutable debe llamarse `Coop2D.exe`** (elige ese nombre al hacer Build en Unity; la carpeta `Coop2D_Data`
  sale con el mismo nombre). Si no lo encuentra, usa el único `.exe` de la carpeta que no sea `UnityCrashHandler`; si no
  hay ninguno, la instalación falla con un error claro y el juego anterior queda intacto.
- No incluyas partidas guardadas ni archivos de configuración personales.
- Cómo comprimir: clic derecho en la carpeta de la build → **Comprimir en archivo ZIP** (o «Enviar a → Carpeta
  comprimida») y renómbralo a `2D_0.0.3.zip`. 7-Zip también vale.

## 4. Revisiones: re-subir el zip en la misma release

Si encuentras un fallo justo después de publicar, puedes **re-subir el zip en la misma release** (editar la release →
borrar el adjunto antiguo → adjuntar el nuevo, con el mismo nombre → **Update release**).

- Los amigos que ya tenían esa versión verán **«Hay una revisión nueva de 0.0.2 · Prototipo 2 (subida el dd/MM
  HH:mm)»** y podrán actualizar. Se detecta por el SHA-256 del archivo, que GitHub calcula solo.
- Cambiar solo las notas, el título o el tag **no** genera ninguna actualización.
- Si alguien estaba descargando justo en ese momento, el launcher detecta el cambio, vuelve a consultar y reintenta
  con el archivo nuevo.
- Para cambios grandes es mejor una versión nueva (`2D_0.0.4.zip`): queda más claro quién tiene qué.

## 5. Pre-releases y borradores

- **«Set as a pre-release»**: solo la verán los amigos que activen **Ajustes → Actualizaciones → Incluir versiones de
  prueba (pre-release)**. Útil para probar algo con un par de personas. En el launcher aparece con la etiqueta «De
  prueba» (que depende solo de esa casilla, no de que el nombre diga «Prototipo»).
- **Los borradores (drafts) son invisibles** para el launcher hasta que los publicas.
- «Set as the latest release» da igual: el launcher ordena por versión, no por fecha.

## 6. No borres releases que la gente pueda tener instaladas

- Quien tenga una versión instalada puede seguir jugando aunque borres su release, pero **Reparar** necesitará la
  release original (si no existe, instalará la última) y la página **Versiones** solo muestra las que siguen publicadas
  (sirve para volver a una versión anterior si la nueva falla).
- Si una build está rota, publica una corrección con una versión mayor o una revisión, en lugar de borrarla.

## 7. Partidas guardadas: usa `Application.persistentDataPath`

**La carpeta del juego se reemplaza entera en cada actualización** (y al reparar). Todo lo que el juego escriba junto
al exe se pierde.

- Guarda partidas, ajustes y cualquier dato del jugador en **`Application.persistentDataPath`**, que en Windows es
  `%USERPROFILE%\AppData\LocalLow\PalitoGames\Coop2D`. El launcher nunca toca esa carpeta (tampoco al desinstalar).
- `PlayerPrefs` (registro de Windows) también se conserva.
- No cambies **Company Name** (`PalitoGames`) ni **Product Name** (`Coop2D`) en *Player Settings*: cambiarían esa ruta
  y parecería que se han perdido las partidas. El launcher también la usa para «Abrir guardados y logs del juego» y
  para incluir el `Player.log` en los informes.

## 8. Notas de la release

El launcher muestra las notas en la página de inicio («Novedades de …») y en **Versiones**. Entiende un Markdown
sencillo: títulos `#`, `##`, `###`; listas con `-` o `*`; `**negrita**`, `*cursiva*`, `` `código` ``;
enlaces `[texto](https://…)` y URLs sueltas; una línea en blanco separa párrafos. **Evita tablas e imágenes** (no se
ven en el launcher).

Plantilla:

```markdown
## Novedades
- Nuevo nivel: la cueva.
- Ahora se puede jugar con mando.

## Arreglos
- El segundo jugador ya no se queda atascado al reaparecer.

## Problemas conocidos
- A veces el chat no se ve en pantalla completa.

## Para jugar en cooperativo
Todos necesitáis la versión **0.0.3**. Si el launcher muestra ACTUALIZAR, actualiza antes de jugar.
```

## 9. Paso a paso

**Desde la web de GitHub**

1. Build de Unity para Windows x64 en una carpeta (p. ej. `2D_0.0.3\`) con el exe `Coop2D.exe`.
2. Comprime la carpeta → `2D_0.0.3.zip`.
3. GitHub → repo **Coop2D** → **Releases** → **Draft a new release**.
4. **Choose a tag** → escribe `v0.0.3` → «Create new tag on publish».
5. Título (p. ej. «0.0.3 — La cueva») y notas (plantilla de arriba).
6. Arrastra `2D_0.0.3.zip` a la zona de adjuntos y **espera a que termine de subir**.
7. Deja sin marcar «Set as a pre-release» (salvo que sea solo para probadores) y pulsa **Publish release**.
8. Comprueba en tu launcher: **Versiones → Actualizar lista** (las respuestas de GitHub se reutilizan durante 60 s).

**Con GitHub CLI**

```powershell
gh release create v0.0.3 2D_0.0.3.zip --repo JJJP-Dev/Coop2D --title "0.0.3 — La cueva" --notes-file notas.md
# Revisión del mismo zip en la misma release:
gh release upload v0.0.3 2D_0.0.3.zip --repo JJJP-Dev/Coop2D --clobber
```

## Comprobaciones antes de publicar

- [ ] El nombre empieza por `2D_` y la versión es **mayor** que la de la última release.
- [ ] Dentro del zip está `Coop2D.exe` (en la raíz o en una sola carpeta) junto con `Coop2D_Data\`.
- [ ] La build arranca bien al descomprimirla en otra carpeta.
- [ ] Solo hay un archivo del juego en la release.
- [ ] Las notas dicen qué versión necesitan todos para jugar juntos.
