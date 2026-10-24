# Eventos del juego en el log (contrato v1)

Especificación para **Coop2D**: qué líneas tiene que escribir el juego en su `Player.log` para que Palito Launcher
detecte las **misiones de prueba** (diseño del lado del launcher: [MISIONES.md](../remoto/MISIONES.md)).

**La idea:** el juego cuenta **hechos** («he provocado un Vapor en dúo», «me he unido a una partida empezada») y el
launcher decide qué misiones cumplen esos hechos. El juego **no sabe nada de misiones**. Así se pueden crear misiones
nuevas para cada playtest sin sacar otra build, siempre que usen eventos que ya existan.

Este documento es un **contrato**: si el juego lo cumple, el launcher funciona. Cópialo al repo de Coop2D (por ejemplo,
`Docs/EVENTOS-LAUNCHER.md`) y mantén las dos copias iguales.

## Contenido

1. [Principios](#1-principios)
2. [Formato de una línea](#2-formato-de-una-línea)
3. [Campos comunes a todos los eventos](#3-campos-comunes-a-todos-los-eventos)
4. [Valores fijos (enumeraciones)](#4-valores-fijos-enumeraciones)
5. [Catálogo de eventos](#5-catálogo-de-eventos)
6. [Argumentos que pasa el launcher](#6-argumentos-que-pasa-el-launcher)
7. [Compatibilidad entre versiones](#7-compatibilidad-entre-versiones)
8. [Ejemplo de una sesión](#8-ejemplo-de-una-sesión)
9. [Implementación de referencia (C#)](#9-implementación-de-referencia-c)
10. [Cómo comprobar que está bien](#10-cómo-comprobar-que-está-bien)

## 1. Principios

- **Punto de vista del jugador local.** Cada PC escribe en su propio `Player.log`, y el launcher de ese PC solo lee
  ese log. Los eventos dicen qué ha visto o hecho **este** jugador (campo `me`, `by=me`…), no lo que pasa en el host.
- **Hechos, no interpretaciones.** «Ha habido una reacción `vapor_fire_water` con dúo en la que yo detoné», no
  «misión cumplida».
- **Nada personal ni texto libre.** Ni IPs, ni nombres, ni rutas, ni mensajes de error completos. Solo identificadores
  de la lista de valores fijos y números.
- **En todas las builds que reciben los testers**, de desarrollo o no. No envuelvas la emisión en
  `#if DEVELOPMENT_BUILD` ni la hagas depender de `Debug.isDebugBuild`.
- **Barato.** Un `Debug.Log` por evento. Ninguno se emite por fotograma; los que podrían repetirse mucho se agrupan
  (ver cada evento).

## 2. Formato de una línea

```
[PalitoEvent] v=1 seq=42 t=318.412 ev=reaction variant=vapor_fire_water duo=true me=detonator targets=3
```

| Regla | Detalle |
|---|---|
| Prefijo | La línea empieza **exactamente** por `[PalitoEvent] ` (corchetes, sin espacios dentro, y un espacio después). Es lo único que busca el launcher |
| Una línea | Un evento = un `Debug.Log` = una línea. Sin `\n` ni `\r` dentro |
| Pares | Después del prefijo, pares `clave=valor` separados por **un espacio** |
| Orden | Primero `v`, `seq`, `t` y `ev`, en ese orden. Los demás campos, en cualquier orden |
| Claves | `^[a-z][a-z0-9_]{0,31}$` (minúsculas, números y `_`) |
| Valores | `^[A-Za-z0-9_.:+-]{1,64}$`: sin espacios, comillas ni `=`. Si hace falta un valor con otros caracteres, es que no debería ir en un evento |
| Booleanos | `true` o `false`, en minúsculas |
| Enteros | En decimal, sin separador de miles: `1500` |
| Decimales | Con **punto** y como mucho 3 decimales: `318.412`. **Siempre con `CultureInfo.InvariantCulture`**: en un Windows en español, `ToString()` escribe `318,412` y el launcher lo descarta |
| Longitud | Máximo **512** caracteres por línea |
| Nivel | `Debug.Log` (nivel *Log*), nunca `LogWarning` ni `LogError`: así no ensucian la consola limpia de la lista de pruebas |

El launcher lee el log con estas dos expresiones regulares, así que lo que no encaje se ignora:

```
Línea:  ^\[PalitoEvent\] (.+)$
Par:    ^([a-z][a-z0-9_]{0,31})=([A-Za-z0-9_.:+\-]{1,64})$
```

Una línea con un par mal formado se descarta **entera**, y una clave repetida invalida la línea. Las claves que el
launcher no conoce se ignoran (sirven para añadir datos sin romper nada).

**Trazas de pila.** En las builds, Unity añade la traza de pila debajo de cada `Debug.Log` (según *Player Settings ›
Stack Trace Logging*). El launcher las ignora, pero el log queda mucho más limpio si el emisor desactiva la traza solo
para estos mensajes (ver la [implementación de referencia](#9-implementación-de-referencia-c)) o pones *Log* a *None*
en *Player Settings*.

## 3. Campos comunes a todos los eventos

| Campo | Tipo | Descripción |
|---|---|---|
| `v` | entero | Versión del formato. Ahora **`1`**. Solo cambia si se rompe la compatibilidad ([sección 7](#7-compatibilidad-entre-versiones)) |
| `seq` | entero | Contador del proceso: `1` en `session_start` y +1 en cada evento. Nunca se repite ni retrocede dentro de una ejecución. Al launcher le sirve para detectar líneas perdidas o duplicadas |
| `t` | decimal | Segundos desde que arrancó el proceso (`Time.realtimeSinceStartupAsDouble`), con 3 decimales |
| `ev` | id | Nombre del evento ([sección 5](#5-catálogo-de-eventos)) |

## 4. Valores fijos (enumeraciones)

Usa siempre estos identificadores, en minúsculas. Para algo nuevo (un personaje, un enemigo, una reacción), **añade un
id nuevo** en esta tabla; no reutilices uno existente para otra cosa.

| Nombre | Valores | Notas |
|---|---|---|
| `role` | `host`, `client` | |
| `slot` | `1`, `2`, `3`, `4` | Hueco del jugador (J1…J4) |
| `element` | `fire`, `water`, `electric` | |
| `character` | `ember`, `tide`, `volt` | Brasa, Marea y Voltio (#162). Los ids del catálogo de progresión; Filo será `blade` |
| `ability` | `basic`, `burst`, `mark`, `ember_flare`, `ember_firewall`, `ember_drizzle`, `tide_wave`, `tide_bubble`, `tide_spark`, `volt_bolt`, `volt_coil`, `volt_short` | Estallido = `burst`, Marca = `mark` (de prueba). Las demás, con los ids del catálogo (#162): Llamarada, Muro ígneo, Llovizna, Ola, Burbuja prisión, Chispa, Rayo, Bobina y Cortocircuito. Cada habilidad nueva añade el suyo |
| `variant` | `vapor_fire_water`, `vapor_water_fire`, `electrocution_water_electric`, `electrocution_electric_water`, `overload_electric_fire`, `overload_fire_electric` | Los mismos ids que ya usa `CombatEventLogger` |
| `me` | `aura`, `detonator`, `both`, `none` | Papel del jugador local en una reacción. `both` = reacción en solitario propia |
| `enemy` | `chaser`, `arc`, `dummy` | `arc` es el dron |
| `by` | `me`, `ally`, `none` | Quién lo hizo: el jugador local, otro jugador o nadie (truco, oleada, entorno) |
| `source` | `basic`, `ability`, `reaction`, `cheat`, `other` | Qué lo causó |
| `target` | `loopback`, `lan`, `vpn`, `public`, `invalid` | Tipo de dirección a la que se intenta unir (ver `join_attempt`) |
| `upgrade` | `basic_twin_shot`, `basic_long_barrel`, `basic_tempo`, `stat_aura_hunter`, `aura_lingering`, `aura_wide_skill`, `coop_relay`, `coop_assist`, `def_vitality`, `mov_tailwind`, `ember_wall_detonate`, `ember_eternal_flame`, `tide_whirlpool`, `tide_tsunami_heart`, `volt_surge`, `volt_coil_detonator`, `econ_magnet`, `mov_swift`, `def_thick_skin`, `econ_breather` | Los ids del catálogo de progresión (#78). `econ_breather` es Respiro. Cada mejora nueva añade el suyo |
| `rarity` | `common`, `rare`, `epic`, `legendary`, `duo`, `evolution` | Rareza de una mejora |
| `action` | `reroll`, `banish`, `skip` | Qué hizo el jugador con una oferta en vez de elegir (#78) |
| `cheat` | `spawn_chaser`, `spawn_chaser_10`, `spawn_arc`, `spawn_arc_10`, `kill_all`, `invulnerable`, `damage_all`, `heal_all`, `next_wave`, `reset_waves`, `give_xp`, `give_reroll`, `give_skill`, `test_skills`, `element_cycle`, `reaction_lab` | Un id por botón del panel F1; `element_cycle` es la tecla F6; `reaction_lab`, cualquier botón del panel del laboratorio de reacciones |

## 5. Catálogo de eventos

**Obligatorio** = lo necesita la primera versión de las misiones. **Recomendado** = mejora el diagnóstico (tirones,
errores, ping) y conviene tenerlo, pero el sistema funciona sin él.

### 5.1 Sesión

#### `session_start` · Obligatorio

Una vez, al arrancar, **antes que cualquier otro evento** (`seq=1`). Lo antes posible: en el `Awake` del primer objeto
de `Bootstrap` o con `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`.

| Campo | Tipo | Valor |
|---|---|---|
| `game_version` | texto | `Application.version` (p. ej. `0.0.4-prototype.2`) |
| `dev_build` | bool | `Debug.isDebugBuild` |
| `launcher_session` | texto | El valor de `-palitoSession` ([sección 6](#6-argumentos-que-pasa-el-launcher)) o `none` si no viene |
| `utc` | texto | `DateTime.UtcNow` en formato `yyyy-MM-ddTHH:mm:ssZ` |

```
[PalitoEvent] v=1 seq=1 t=0.412 ev=session_start game_version=0.0.4-prototype.2 dev_build=true launcher_session=9f2c4e7a1b3d4c5e8f9a0b1c2d3e4f50 utc=2026-10-31T20:02:11Z
```

#### `session_end` · Obligatorio

En `Application.quitting`, al cerrar el juego con normalidad. Si el juego se cuelga o se cierra de golpe no se escribe,
y así el launcher sabe que no terminó bien.

| Campo | Tipo | Valor |
|---|---|---|
| `duration` | entero | Segundos desde el arranque |

### 5.2 Conexión

#### `host_started` · Obligatorio

Al pulsar Host, cuando se sabe el resultado.

| Campo | Tipo | Valor |
|---|---|---|
| `result` | id | `ok`, `port_in_use` o `error` |

#### `join_attempt` · Obligatorio

Al pulsar Join o Enter, **una vez por intento real** (si Enter repetido no lanza otro intento, tampoco otro evento).

| Campo | Tipo | Valor |
|---|---|---|
| `target` | id | Tipo de la dirección escrita (**nunca la IP**): `loopback` (127.0.0.0/8), `lan` (10/8, 172.16/12, 192.168/16), `vpn` (25.0.0.0/8 de Hamachi, 26.0.0.0/8 de Radmin, 100.64.0.0/10 de Tailscale/ZeroTier), `public` (cualquier otra IPv4 válida), `invalid` (no es una IPv4) |
| `via` | id | `button` o `enter` |

#### `join_result` · Obligatorio

Cuando el intento termina, con éxito o no. Uno por cada `join_attempt`.

| Campo | Tipo | Valor |
|---|---|---|
| `result` | id | `ok`, `invalid_ip`, `not_found` (no responde a los 10 s), `rejected` o `error` |
| `target` | id | El mismo `target` de su `join_attempt` (así una misión «únete por internet» se comprueba con un solo evento) |
| `ms` | entero | Milisegundos desde el intento hasta el resultado |

### 5.3 Partida

Una **partida** va de `match_joined` a `match_left` (o al final del log). El launcher agrupa los eventos por partida,
por ejemplo para saber si se usaron trucos en ella.

#### `match_joined` · Obligatorio

Cuando el jugador local aparece en la Arena, en el host y en los clientes.

| Campo | Tipo | Valor |
|---|---|---|
| `role` | id | `host` o `client` |
| `slot` | entero | Hueco del jugador local |
| `players` | entero | Jugadores en la partida contándose a sí mismo |
| `match_time` | entero | Segundos que lleva la partida (desde que el host la creó). El host escribe `0` |
| `late` | bool | `true` si la partida llevaba al menos **10 s** en marcha al entrar |
| `seed` | id | Semilla de la partida (#75): 8 caracteres hexadecimales en mayúsculas, como `3A9F01C2`, iguales en todas las ventanas. `none` si la escena no tiene semilla. Llega con la primera build del Pre 3 |
| `character` | id | Personaje del jugador local (#162): `ember`, `tide` o `volt`. Llega con la primera build del Pre 3 |

#### `player_joined` / `player_left` · Obligatorio

Cuando **otro** jugador entra o sale (no por uno mismo), en todas las ventanas.

| Campo | Tipo | Valor |
|---|---|---|
| `slot` | entero | Hueco del otro jugador |
| `players` | entero | Total después del cambio |

#### `match_left` · Obligatorio

Cuando el jugador local vuelve al menú.

| Campo | Tipo | Valor |
|---|---|---|
| `reason` | id | `quit` (pulsó salir), `host_closed`, `disconnected`, `error` o `restart` (el host reinició la partida tras perderla, #52; justo después llega un `match_joined` nuevo con la semilla nueva) |
| `duration` | entero | Segundos dentro de la partida |

#### `wave_started` · Obligatorio

Al empezar cada oleada, en todas las ventanas (también al saltar con trucos).

| Campo | Tipo | Valor |
|---|---|---|
| `index` | entero | Número de oleada, desde 1 |
| `match_time` | entero | Segundos de partida |

#### `waves_finished` · Obligatorio

Una vez por partida, cuando ya no quedan oleadas por salir (pasados los 10 minutos del horario).

| Campo | Tipo | Valor |
|---|---|---|
| `match_time` | entero | Segundos de partida |
| `alive` | bool | Si el jugador local seguía en pie: `false` si estaba derribado (#51) |

#### `level_up` · Obligatorio

Cada vez que el grupo sube de nivel (#76), en todas las ventanas. La experiencia es compartida: todos suben a la vez. Si un orbe o un truco da varios niveles de golpe, una línea por nivel.

| Campo | Tipo | Valor |
|---|---|---|
| `level` | entero | Nivel nuevo del grupo, desde 2 |
| `match_time` | entero | Segundos de partida |

#### `upgrade_picked` · Obligatorio

Cuando el jugador local elige una mejora de su oferta y el host lo acepta (#78). Solo en su ventana: cada jugador elige la suya. Respiro también cuenta.

| Campo | Tipo | Valor |
|---|---|---|
| `upgrade` | id | La mejora elegida |
| `rarity` | id | Su rareza |
| `level` | entero | Nivel de la subida de la que salió la oferta (puede ser anterior al actual si tenía ofertas en cola) |
| `match_time` | entero | Segundos de partida |

#### `offer_action` · Recomendado

Cuando el jugador local vuelve a tirar, descarta una opción o salta una oferta y el host lo acepta (#78). Solo en su ventana.

| Campo | Tipo | Valor |
|---|---|---|
| `action` | id | `reroll`, `banish` o `skip` |
| `upgrade` | id | La mejora descartada con `banish`; `none` con `reroll` y `skip` |
| `level` | entero | Nivel de la subida de la oferta |
| `match_time` | entero | Segundos de partida |
| `ability` | id | Con `banish` de una habilidad (#162), la habilidad descartada; `none` en los demás casos (y entonces `upgrade` dice qué mejora fue) |

#### `skill_gained` · Obligatorio

Cuando el jugador local coge de su oferta una habilidad nueva o un nivel de una que tiene, y el host lo acepta (#162). Solo en su ventana. La habilidad inicial con la que empieza cada partida no lo escribe.

| Campo | Tipo | Valor |
|---|---|---|
| `ability` | id | La habilidad (ver [`ability`](#4-valores-fijos-enumeraciones)) |
| `ability_level` | entero | `1` si es nueva; `2` o `3` si es un nivel |
| `level` | entero | Nivel de la subida de la que salió la oferta |
| `match_time` | entero | Segundos de partida |

#### `match_over` · Obligatorio

Una vez por partida, cuando termina (#52), en todas las ventanas: derrota (todos los jugadores derribados) o victoria (acaba el horario de oleadas y muere su último enemigo).

| Campo | Tipo | Valor |
|---|---|---|
| `result` | id | `defeat` o `victory` |
| `match_time` | entero | Segundos que duró la partida, iguales en todas las ventanas |
| `waves` | entero | Oleadas que habían empezado |

#### `player_downed` · Obligatorio

Cuando un jugador cae a 0 de vida y queda derribado (#51), en todas las ventanas.

| Campo | Tipo | Valor |
|---|---|---|
| `slot` | entero | Hueco del jugador derribado |
| `me` | bool | `true` si es el jugador local |

#### `player_revived` · Obligatorio

Cuando un jugador derribado se levanta (#51), en todas las ventanas.

| Campo | Tipo | Valor |
|---|---|---|
| `slot` | entero | Hueco del jugador que se levanta |
| `me` | bool | `true` si es el jugador local |
| `by` | id | Quién lo reanimó: `me` (el jugador local), `ally` (otro jugador) o `none` (un truco, como «Curar a todos») |
| `left` | entero | Reanimaciones que le quedan al grupo después de esta (#79). Los trucos no gastan. `none` si la escena no las limita |

### 5.4 Combate

#### `element_set` · Obligatorio

Cada vez que cambia el elemento del jugador local, incluida la asignación inicial.

| Campo | Tipo | Valor |
|---|---|---|
| `element` | id | Elemento nuevo |
| `source` | id | `character` (el elemento de su personaje, desde el Pre 3), `debug_key` (F6) o `slot` (por su hueco; solo en builds anteriores al Pre 3) |

#### `ability_cast` · Obligatorio

Cuando el jugador local lanza una habilidad que sí sale (pasada su recarga). El básico **no** emite este evento.

| Campo | Tipo | Valor |
|---|---|---|
| `ability` | id | Ver [`ability`](#4-valores-fijos-enumeraciones): `burst`, `tide_wave`… |
| `element` | id | Elemento con el que se lanza |

#### `reaction` · Obligatorio

Cuando ocurre una reacción **que ve este jugador**, en todas las ventanas. **Agrupado:** un evento por cada
**detonación y variante**. Si un Estallido provoca tres Vapor iguales, es una sola línea con `targets=3`; si provoca dos
variantes distintas, son dos líneas.

| Campo | Tipo | Valor |
|---|---|---|
| `variant` | id | Variante de la reacción |
| `duo` | bool | Si el aura y la detonación son de jugadores distintos |
| `me` | id | Papel del jugador local: `aura`, `detonator`, `both` o `none` |
| `targets` | entero | Enemigos afectados por esa variante en esa detonación |

#### `enemy_killed` · Obligatorio

Cuando muere un enemigo, en todas las ventanas. Los muñecos que reviven también cuentan (`enemy=dummy`).

| Campo | Tipo | Valor |
|---|---|---|
| `enemy` | id | `chaser`, `arc`, `dummy` |
| `by` | id | `me`, `ally` o `none` (por ejemplo, «Matar a todos») |
| `source` | id | Qué dio el golpe final: `basic`, `ability`, `reaction` o `cheat` |

> Si en una partida se esperan cientos de muertes, este evento puede ser el más frecuente. Está bien (son pocos KB),
> pero no añadas un evento por **golpe**: para eso están los resúmenes.

#### `cheat_used` · Obligatorio

Cuando se usa un truco del panel F1 o la tecla F6.

| Campo | Tipo | Valor |
|---|---|---|
| `cheat` | id | Ver [`cheat`](#4-valores-fijos-enumeraciones) |
| `by` | id | `me` o `ally` |

Mínimo: lo escribe quien lo pulsa (`by=me`) y el host (`by=ally` para los de los demás). **Recomendado:** que el host
avise a todos los clientes y cada uno lo escriba, porque el launcher descarta en cada PC las misiones conseguidas en una
partida con trucos (salvo las que los permiten).

#### `player_died` · No se emite

Reservado. Desde el #51 los jugadores no mueren: quedan derribados (`player_downed`) y se levantan (`player_revived`), los
dos en la [sección 5.3](#53-partida). Se deja aquí para que nadie use este nombre para otra cosa.

### 5.5 Diagnóstico · Recomendado

No se usan para cumplir misiones, sino para el resumen que el launcher manda con los resultados (tirones, errores,
ping). Ninguno debe salir más de una vez por segundo.

#### `hitch`

Cuando un fotograma tarda más de **100 ms** (el mismo criterio que `[Perf] Hitch`). Si hay varios en el mismo segundo,
uno solo con el peor.

| Campo | Tipo | Valor |
|---|---|---|
| `ms` | entero | Duración del fotograma |
| `enemies` | entero | Enemigos vivos |

#### `perf`

Cada **60 s** mientras se está en partida.

| Campo | Tipo | Valor |
|---|---|---|
| `fps_avg` | decimal | FPS medios del último minuto |
| `frame_worst_ms` | entero | Peor fotograma del último minuto |
| `enemies` | entero | Enemigos vivos |

#### `net`

Cada **30 s** en los clientes, mientras se está en partida.

| Campo | Tipo | Valor |
|---|---|---|
| `rtt_ms` | entero | Ping al host (el del panel de red) |

#### `log_error`

La **primera vez** que aparece cada error distinto en el log (con `Application.logMessageReceivedThreaded`, tipos
*Error*, *Exception* y *Assert*). Sirve para que el launcher detecte que algo falló cerca de una misión.

| Campo | Tipo | Valor |
|---|---|---|
| `type` | id | `error`, `exception` o `assert` |
| `sig` | texto | Firma: 8 caracteres hexadecimales de un hash (FNV-1a de 32 bits) de la primera línea del mensaje. Así se agrupan sin escribir el texto |
| `count` | entero | `1` la primera vez. Opcional: en `session_end`, una línea por firma con el total |

## 6. Argumentos que pasa el launcher

El launcher abre el juego con argumentos extra. Unity ignora los que no conoce, así que no rompen nada.

| Argumento | Qué hace el juego |
|---|---|
| `-palitoSession <32 hex>` | Lo lee con `Environment.GetCommandLineArgs()` y lo copia tal cual en `launcher_session` de `session_start`. Así el launcher sabe que ese log es de **esa** ejecución. Si no viene (el juego se abrió fuera del launcher), escribe `none` |
| `-logFile <ruta>` | Solo en las instancias extra del modo desarrollador. Es un argumento estándar de Unity: el juego **no** debe cambiar la ruta del log por su cuenta |

## 7. Compatibilidad entre versiones

- **Nunca** renombres ni quites un evento o un campo, ni cambies lo que significa. Las misiones publicadas dependen de
  ellos.
- **Sí** puedes añadir eventos, campos y valores nuevos cuando quieras: el launcher ignora lo que no conoce, y una misión
  puede usar un evento nuevo sin actualizar el launcher (basta con publicar `missions.json`).
- Si algo tiene que cambiar de forma incompatible, se emite como evento nuevo (`reaction2`) o se sube `v` a `2`. Un
  launcher que no conoce esa `v` ignora esas líneas.
- Apunta en el changelog de cada build qué eventos nuevos trae. El launcher solo cuenta para una misión las sesiones con
  `game_version` igual o mayor que la `minGameVersion` de su conjunto.

## 8. Ejemplo de una sesión

Un cliente que se une por internet a una partida ya empezada, provoca un Vapor en dúo, ve una Sobrecarga de otros y
sale:

```
[PalitoEvent] v=1 seq=1 t=0.412 ev=session_start game_version=0.0.4-prototype.2 dev_build=true launcher_session=9f2c4e7a1b3d4c5e8f9a0b1c2d3e4f50 utc=2026-10-31T20:02:11Z
[PalitoEvent] v=1 seq=2 t=14.050 ev=join_attempt target=public via=enter
[PalitoEvent] v=1 seq=3 t=15.318 ev=join_result result=ok target=public ms=1268
[PalitoEvent] v=1 seq=4 t=15.901 ev=match_joined role=client slot=3 players=3 match_time=74 late=true seed=3A9F01C2 character=volt
[PalitoEvent] v=1 seq=5 t=15.902 ev=element_set element=electric source=character
[PalitoEvent] v=1 seq=6 t=31.270 ev=wave_started index=3 match_time=90
[PalitoEvent] v=1 seq=7 t=44.600 ev=ability_cast ability=burst element=electric
[PalitoEvent] v=1 seq=8 t=44.712 ev=reaction variant=electrocution_water_electric duo=true me=detonator targets=1
[PalitoEvent] v=1 seq=9 t=44.713 ev=enemy_killed enemy=chaser by=me source=reaction
[PalitoEvent] v=1 seq=10 t=52.004 ev=reaction variant=overload_electric_fire duo=true me=none targets=4
[PalitoEvent] v=1 seq=11 t=60.000 ev=net rtt_ms=66
[PalitoEvent] v=1 seq=12 t=181.230 ev=match_left reason=quit duration=165
[PalitoEvent] v=1 seq=13 t=184.050 ev=session_end duration=184
```

## 9. Implementación de referencia (C#)

Orientativa: el juego puede organizarlo como quiera, mientras las líneas salgan con el formato de la
[sección 2](#2-formato-de-una-línea).

```csharp
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;

/// Escribe eventos para Palito Launcher en Player.log. Formato: Docs/EVENTOS-LAUNCHER.md (contrato v1).
public static class PalitoEvents
{
    const string Prefix = "[PalitoEvent] ";
    static int _seq;

    // Llamar una vez al arrancar (seq=1).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void EmitSessionStart()
    {
        Emit("session_start",
            ("game_version", Application.version),
            ("dev_build", Debug.isDebugBuild),
            ("launcher_session", ReadArg("-palitoSession") ?? "none"),
            ("utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        Application.quitting += () => Emit("session_end", ("duration", (int)Time.realtimeSinceStartupAsDouble));
    }

    public static void Emit(string ev, params (string Key, object Value)[] fields)
    {
        var sb = new StringBuilder(Prefix, 160);
        sb.Append("v=1 seq=").Append(Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture));
        sb.Append(" t=").Append(Time.realtimeSinceStartupAsDouble.ToString("0.000", CultureInfo.InvariantCulture));
        sb.Append(" ev=").Append(ev);
        foreach (var (key, value) in fields)
            sb.Append(' ').Append(key).Append('=').Append(Format(value));

        // Sin traza de pila solo para estas líneas.
        var previous = Application.GetStackTraceLogType(LogType.Log);
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        Debug.Log(sb.ToString());
        Application.SetStackTraceLogType(LogType.Log, previous);
    }

    static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "none",
    };

    static string ReadArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}

// Uso:
// PalitoEvents.Emit("reaction", ("variant", "vapor_fire_water"), ("duo", true), ("me", "detonator"), ("targets", 3));
```

Notas:

- `Application.Get/SetStackTraceLogType` no son seguros entre hilos. Si algún evento se emite desde otro hilo, pásalo al
  hilo principal o quita esas dos líneas y usa la opción de *Player Settings*.
- Con *Enter Play Mode Options* sin recarga de dominio, `_seq` no se reinicia entre Plays del editor. No afecta a las
  builds; si molesta, reinícialo en el mismo método de arranque.
- Antes de emitir, conviene validar en el editor (`Debug.Assert`) que claves y valores cumplen las expresiones de la
  sección 2: un espacio en un valor invalida la línea entera.

## 10. Cómo comprobar que está bien

Con la build, juega una partida corta y busca en el log:

```powershell
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\PalitoGames\Coop2D\Player.log" -Pattern '^\[PalitoEvent\] '
```

- [ ] La primera línea es `session_start` con `seq=1`, y `seq` sube de uno en uno sin huecos.
- [ ] `t` y los decimales usan **punto**, también con Windows en español.
- [ ] Ningún valor tiene espacios, comillas ni `=`, y ninguna línea pasa de 512 caracteres.
- [ ] No aparece ninguna IP, nombre de jugador ni ruta.
- [ ] Al cerrar con normalidad hay un `session_end`; al matar el proceso desde el Administrador de tareas, no.
- [ ] Un Estallido que provoca tres Vapor iguales da **una** línea `reaction` con `targets=3`.
- [ ] Con dos ventanas, cada log refleja su propio punto de vista (`me=detonator` en una, `me=aura` en la otra).
- [ ] Abierto desde el launcher, `launcher_session` trae el valor del argumento; abierto a mano, `none`.
- [ ] La consola sigue limpia: los eventos son de nivel *Log*, no avisos.
