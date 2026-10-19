# Guía del código de Palito Launcher

Esta guía es para **aprender el código y poder cambiarlo sin depender de nadie**. Está pensada para alguien que sabe C#
de Unity: muchas ideas te sonarán (eventos, corrutinas, hilo principal) y aquí se explica su equivalente en .NET/WPF
con ejemplos sacados del propio proyecto.

- El **mapa** del proyecto (capas, diagramas, qué se guarda en disco, decisiones) está en
  [ARQUITECTURA.md](ARQUITECTURA.md). Léelo antes o en paralelo.
- Describe el código de la **versión 1.0.4**; lo nuevo de la 1.0.5 se menciona solo donde hace falta.
- Ningún número de línea: busca los nombres con **Ctrl+T** (Visual Studio) o **Ctrl+Shift+F**.

## Índice

1. [Preparar el entorno](#1-preparar-el-entorno)
2. [Recorrido recomendado por el código](#2-recorrido-recomendado-por-el-código)
3. [Conceptos de C# y .NET que usa el proyecto](#3-conceptos-de-c-y-net-que-usa-el-proyecto)
4. [Cómo depurar](#4-cómo-depurar)
5. [Tests](#5-tests)
6. [Recetas paso a paso](#6-recetas-paso-a-paso)
7. [Glosario](#7-glosario)
8. [Checklist antes de hacer commit](#8-checklist-antes-de-hacer-commit)

---

## 1. Preparar el entorno

- Windows 10/11 y el **SDK de .NET 10** (10.0.401 o superior).
- Visual Studio 2026 (carga de trabajo «Desarrollo de escritorio de .NET») o Rider. VS Code también sirve para leer.
- Abre `PalitoLauncher.sln`. En Visual Studio, clic derecho en el proyecto **PalitoLauncher** → «Establecer como
  proyecto de inicio» para poder pulsar F5.

Comandos básicos (desde la carpeta del repo, en PowerShell):

```powershell
dotnet build PalitoLauncher.sln                           # compilar todo
dotnet test PalitoLauncher.sln                            # todos los tests
dotnet run --project src/PalitoLauncher -- --portable     # abrir el launcher compilado
pwsh scripts/publish.ps1                                  # generar publish\PalitoLauncher.exe como en una release
```

Atajos de Visual Studio que más vas a usar al leer este código:

| Atajo | Para qué |
|---|---|
| F12 | Ir a la definición |
| Shift+F12 | Buscar todas las referencias (¿quién llama a esto?) |
| Ctrl+T | Buscar un tipo o un miembro por nombre |
| Ctrl+- | Volver a donde estabas |
| Ctrl+M, Ctrl+O | Plegar todo (para ver la «forma» de una clase grande) |

---

## 2. Recorrido recomendado por el código

El orden importa: empieza por lo **puro y pequeño** y termina por lo grande. Algunos archivos tienen más de 1000 líneas;
no los leas de arriba abajo. **En cada clase, lee primero el `<summary>` de la clase y los de sus métodos públicos**:
son la especificación. Después, solo los métodos privados que te interesen.

### Etapa 1 — Las piezas fijas

1. [`LauncherConfig.cs`](../../src/PalitoLauncher.Core/LauncherConfig.cs): todo lo que identifica al juego y a los repos,
   y los intervalos de comprobación. Fíjate en los comentarios sobre el cupo de GitHub.
2. [`AppPaths.cs`](../../src/PalitoLauncher.Core/AppPaths.cs): dónde se guarda cada cosa.
3. Los modelos (clases con propiedades y casi sin lógica):
   [`GitHubModels.cs`](../../src/PalitoLauncher.Core/GitHub/GitHubModels.cs) (`ReleaseInfo`, `ReleaseAsset`, `GameRelease`,
   `ReleaseFetchResult`), [`InstallModels.cs`](../../src/PalitoLauncher.Core/Install/InstallModels.cs) (`InstalledGameInfo`,
   `InstallStatus`, `InstallPhase`, `InstallProgress`) y
   [`LauncherSettings.cs`](../../src/PalitoLauncher.Core/Settings/LauncherSettings.cs).

### Etapa 2 — Lógica pura (sin disco ni red)

4. [`LauncherStateLogic.cs`](../../src/PalitoLauncher.Core/State/LauncherStateLogic.cs) junto con su test
   [`LauncherStateLogicTests.cs`](../../tests/PalitoLauncher.Tests/LauncherStateLogicTests.cs). Es el corazón de «qué
   muestra el botón principal». Es corto y cada caso tiene su test: perfecto para empezar.
5. [`GameVersion.cs`](../../src/PalitoLauncher.Core/Versioning/GameVersion.cs) (cómo se lee `2D_0.0.3_Prototype2.zip`) y
   [`ReleaseSelector.cs`](../../src/PalitoLauncher.Core/GitHub/ReleaseSelector.cs) (qué asset es el juego y en qué orden van
   las releases).
6. [`RoadmapLogic.cs`](../../src/PalitoLauncher.Core/State/RoadmapLogic.cs): otro ejemplo de lógica pura.

### Etapa 3 — Infraestructura del Core

7. [`LauncherException.cs`](../../src/PalitoLauncher.Core/Util/LauncherException.cs) (unas pocas líneas que definen cómo
   se tratan los errores) y [`Log.cs`](../../src/PalitoLauncher.Core/Logging/Log.cs) (solo la clase `Log`; `LogSink` es el
   detalle del archivo).
8. [`SettingsStore.cs`](../../src/PalitoLauncher.Core/Settings/SettingsStore.cs): cargar y guardar ajustes sin perderlos
   nunca (copia `.bak`, archivo corrupto apartado, propiedad a propiedad).
9. [`HttpFactory.cs`](../../src/PalitoLauncher.Core/Net/HttpFactory.cs) y
   [`GitHubClient.cs`](../../src/PalitoLauncher.Core/GitHub/GitHubClient.cs): lee el `<summary>` de la clase entero y los
   métodos públicos (`GetReleasesAsync`, `GetMilestonesAsync`, `GetLatestReleaseAsync`, `GetCachedFileAsync`,
   `GetCachedTextFileAsync`). Luego `FetchReleasesAsync` para ver el flujo completo con caché.

### Etapa 4 — Instalar y jugar

10. [`FileDownloader.cs`](../../src/PalitoLauncher.Core/Downloads/FileDownloader.cs): solo el `<summary>` de la clase al
    principio (es largo pero lo explica todo) y `DownloadAsync`.
11. [`ArchiveExtractor.cs`](../../src/PalitoLauncher.Core/Install/ArchiveExtractor.cs): `ExtractAsync` y `FindContentRoot`.
12. [`GameInstaller.cs`](../../src/PalitoLauncher.Core/Install/GameInstaller.cs): el `<summary>` de la clase (estructura en
    disco), luego `InstallAsync` → `InstallCoreAsync` → `SwapIn`, y `Recover` → `RecoverCore`. Ten al lado el diagrama
    de la [sección 6 de ARQUITECTURA.md](ARQUITECTURA.md#6-instalar-y-actualizar-el-juego).
13. [`GameProcessManager.cs`](../../src/PalitoLauncher.Core/Launch/GameProcessManager.cs): `Launch`,
    `TryAttachToRunning`, los eventos `GameStarted`/`GameExited`.

### Etapa 5 — El launcher se cuida a sí mismo

14. [`LauncherInstallation.cs`](../../src/PalitoLauncher.Core/Update/LauncherInstallation.cs): `EnsureInstalled` y `Decide`.
15. [`LauncherSelfUpdater.cs`](../../src/PalitoLauncher.Core/Update/LauncherSelfUpdater.cs): el `<summary>` de la clase,
    `CheckAsync` y `DownloadAndApplyCoreAsync`.

### Etapa 6 — La interfaz

16. [`App.xaml.cs`](../../src/PalitoLauncher/App.xaml.cs): `OnStartup` de arriba abajo (los pasos están numerados).
17. [`ObservableObject.cs`](../../src/PalitoLauncher/ViewModels/ObservableObject.cs) y
    [`RelayCommand.cs`](../../src/PalitoLauncher/ViewModels/RelayCommand.cs): la base de todo el MVVM (ver
    [3.9](#39-mvvm-a-mano-observableobject-e-inotifypropertychanged) y [3.10](#310-comandos-relaycommand-y-asyncrelaycommand)).
18. [`MainWindow.xaml`](../../src/PalitoLauncher/MainWindow.xaml): la estructura visual completa (barra de título,
    navegación, páginas, barra de acción, overlay). Busca cada `{Binding ...}` en el ViewModel con F12.
19. [`MainViewModel.cs`](../../src/PalitoLauncher/ViewModels/MainViewModel.cs): el constructor (crea todo y los comandos) y
    `UpdateState()` (traduce el estado a lo que se ve).
20. [`MainViewModel.Startup.cs`](../../src/PalitoLauncher/ViewModels/MainViewModel.Startup.cs): `InitializeAsync`,
    `RefreshReleasesAsync`, `PeriodicCheckAsync`, `HandleGameExitedAsync`.
21. [`MainViewModel.Operations.cs`](../../src/PalitoLauncher/ViewModels/MainViewModel.Operations.cs): `ExecutePrimary`,
    `RunFlowCoreAsync`, `StartOperation` / `RunOperationCoreAsync`, `InstallBodyAsync`, `PlayAsync`,
    `HandleExceptionAsync`.
22. [`SettingsViewModel.cs`](../../src/PalitoLauncher/ViewModels/SettingsViewModel.cs) +
    [`SettingsView.xaml`](../../src/PalitoLauncher/Views/SettingsView.xaml): el ejemplo más simple de vista + ViewModel.
23. [`DialogService.cs`](../../src/PalitoLauncher/Services/DialogService.cs) +
    [`MessageDialogViewModel.cs`](../../src/PalitoLauncher/ViewModels/MessageDialogViewModel.cs): cómo funcionan los diálogos.
24. [`Theme.xaml`](../../src/PalitoLauncher/Themes/Theme.xaml): por encima, para saber qué estilos existen.

### Etapa 7 — Funciones añadidas después

25. Roadmap: [`MainViewModel.Roadmap.cs`](../../src/PalitoLauncher/ViewModels/MainViewModel.Roadmap.cs) y
    [`RoadmapView.xaml`](../../src/PalitoLauncher/Views/RoadmapView.xaml) (más detalle en [ROADMAP.md](../remoto/ROADMAP.md)).
26. Opiniones: [`SurveyProvider.cs`](../../src/PalitoLauncher.Core/Feedback/SurveyProvider.cs),
    [`FeedbackService.cs`](../../src/PalitoLauncher.Core/Feedback/FeedbackService.cs),
    [`MainViewModel.Feedback.cs`](../../src/PalitoLauncher/ViewModels/MainViewModel.Feedback.cs) (más detalle en
    [FEEDBACK.md](../remoto/FEEDBACK.md)).
27. [`MarkdownRenderer.cs`](../../src/PalitoLauncher/Helpers/MarkdownRenderer.cs): cómo se dibujan las notas de las releases.

---

## 3. Conceptos de C# y .NET que usa el proyecto

### 3.1 async/await y por qué no bloquear la UI

WPF, como Unity, tiene **un hilo de UI** (el «hilo principal»). Ese hilo dibuja la ventana y atiende clics. Si lo
ocupas 3 segundos descargando algo, la ventana se congela 3 segundos y Windows pone «(No responde)».

`async`/`await` es parecido a una corrutina de Unity, pero devolviendo un valor y propagando excepciones: el método se
«pausa» en el `await` **sin bloquear el hilo**, y cuando el trabajo termina continúa justo después, **de vuelta en el
hilo de UI** (en los métodos llamados desde la UI).

Ejemplo real, al arrancar (`MainViewModel.InitializeCoreAsync`):

```csharp
_settings = await Task.Run(() => _settingsStore.Load());
```

- `Task.Run(...)` ejecuta la lectura del archivo en un hilo del *pool* (fondo).
- `await` libera el hilo de UI mientras tanto (la ventana sigue respondiendo).
- La línea siguiente ya corre otra vez en el hilo de UI, así que puede tocar propiedades enlazadas a la vista.

Reglas que sigue el proyecto:

- **E/S de verdad asíncrona** (HTTP, `FileStream` asíncrono) se espera directamente con `await`.
- **Trabajo síncrono pesado** (leer un archivo con `File.ReadAllText`, calcular un tamaño de carpeta, comprobar si una
  unidad existe) se envuelve en `Task.Run` desde la UI.
- `async void` está prohibido salvo en un sitio: `AsyncRelayCommand.Execute`, porque WPF exige esa firma para los
  comandos. Por eso ese método tiene un `try/catch` que no deja escapar nada.
- Para lanzar algo «y no esperar», el `MainViewModel` usa `RunFlow`, que descarta la tarea de forma explícita pero
  captura y registra cualquier error:

```csharp
internal void RunFlow(Func<Task> flow, string context, bool showErrors = true, bool guardPrimary = false)
    => _ = RunFlowCoreAsync(flow, context, showErrors, guardPrimary);
```

### 3.2 Task.Run y ConfigureAwait(false) en el Core

En el Core verás `.ConfigureAwait(false)` en casi todos los `await`, por ejemplo en `GameInstaller.InstallAsync`:

```csharp
return await Task.Run(() => InstallCoreAsync(root, release, options, progress, ct), CancellationToken.None)
    .ConfigureAwait(false);
```

`ConfigureAwait(false)` significa «cuando termines, sigue en cualquier hilo; no hace falta volver al hilo de UI». El
Core no toca la interfaz, así que es más eficiente y evita bloqueos. **En la UI no se usa**, porque después del `await`
sí hay que estar en el hilo de UI.

### 3.3 CancellationToken: pausar es cancelar

Un `CancellationToken` es una «bandera» que el que llama puede levantar para pedir que algo se detenga. El método que
la recibe la mira de vez en cuando (`ct.ThrowIfCancellationRequested()`) y, si está levantada, lanza
`OperationCanceledException`.

En el launcher:

- `MainViewModel._lifetimeCts` se cancela al cerrar el launcher (`MainViewModel.Dispose`): corta todo lo que esté en
  marcha.
- Cada operación larga tiene su propio token **enlazado** al de la vida del launcher (`StartOperation`): se cancela si
  se pulsa PAUSAR/CANCELAR o si se cierra el launcher.

```csharp
var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
_operationCts = cts;
```

- El botón **PAUSAR** no tiene una lógica de pausa especial: llama a `CancelCurrentOperation`, que cancela ese token.
  `FileDownloader` está diseñado para que, al cancelarse, el `.part` se quede en disco; al pulsar CONTINUAR se reanuda
  desde ahí. Más sencillo y más robusto que «pausar» de verdad.
- Para **timeouts** se usa `CancelAfter`. Ejemplo de la comprobación rápida antes de JUGAR (`PlayAsync`):

```csharp
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
timeout.CancelAfter(PrePlayCheckTimeout);
```

- Hay fases que **no se pueden cancelar** a propósito: el intercambio de carpetas (`InstallPhase.Installing`) se ejecuta
  ignorando el token, porque dejarlo a medias sería peor. Por eso `InstallProgress.CanCancel` es `false` en esas fases y
  el botón muestra «INSTALANDO…» deshabilitado.
- Patrón para capturar solo *tu* cancelación (y no un timeout interno):
  `catch (OperationCanceledException) when (ct.IsCancellationRequested)`.

### 3.4 `IProgress<T>`: informar del progreso sin acoplarse a la UI

El Core no sabe qué es una barra de progreso. En su lugar recibe un `IProgress<InstallProgress>` y llama a
`Report(...)`. La UI crea un `Progress<T>` **en el hilo de UI** (`MainViewModel.InstallBodyAsync`):

```csharp
var progress = new Progress<InstallProgress>(OnInstallProgress);
```

`Progress<T>` recuerda el hilo donde se creó y entrega cada aviso en ese hilo: así `OnInstallProgress` puede actualizar
la pantalla aunque el aviso venga del hilo de fondo que descarga. (Dentro del instalador se usa una variante síncrona
propia, `InlineProgress<T>`, para encadenar avisos sin saltar de hilo.)

### 3.5 records y record struct

Un `record` es una clase (o un `record struct`, un struct) pensada para **datos**: se compara por valor y se escribe en
una línea. Ejemplos del proyecto:

```csharp
public readonly record struct InstallProgress(InstallPhase Phase, double? Fraction, string Status, DownloadProgress? Download = null)
{
    /// <summary>True durante fases en las que cancelar es seguro.</summary>
    public bool CanCancel => Phase is InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Verifying
        or InstallPhase.Extracting or InstallPhase.WaitingForGameExit or InstallPhase.Moving;
}
```

- `DownloadProgress`, `DownloadRetryInfo` y `ExtractProgress` siguen el mismo patrón (avisos de progreso pequeños e
  inmutables).
- `LauncherStateInput` es un `sealed record` con propiedades `init` (se rellenan al crearlo y luego no cambian):
  `new LauncherStateInput { Install = ..., Releases = ..., IsBusy = ... }`.
- En Ajustes, las opciones de los ComboBox son records de una línea (`SpeedLimitOption(string Label, int KBps)`,
  `AfterLaunchOption`): el ComboBox muestra `Label` (`DisplayMemberPath="Label"`) y el ViewModel guarda el valor.

### 3.6 Nullable: `string?` y compañía

El proyecto tiene `<Nullable>enable</Nullable>`. Eso significa que `string` **no puede** ser null y `string?` sí, y el
compilador avisa si usas algo que podría ser null sin comprobarlo.

Lo que verás a menudo:

| Sintaxis | Significado | Ejemplo del proyecto |
|---|---|---|
| `string?` | puede ser null | `public string? InstallRoot { get; set; }` en `LauncherSettings` |
| `a ?? b` | `a` si no es null, si no `b` | `_fetchErrorMessage ?? "No se pudo conectar con GitHub."` |
| `a ??= b` | asigna `b` solo si `a` es null | `_installStatus ??= new InstallStatus { ... }` (en `InitializeAsync`) |
| `a?.B` | `null` si `a` es null, si no `a.B` | `_installStatus?.Info` |
| `x!` | «confía en mí, no es null» | `latest!.Asset.Size` (justo después de comprobar `hasPending`) |
| `is { Count: > 0 } list` | patrón: no null y con elementos | `input.Releases is { Count: > 0 } list ? list[0] : null` en `ComputeState` |
| `[NotNullWhen(true)]` | si devuelve true, el `out` no es null | `GameVersion.TryParse(string? text, [NotNullWhen(true)] out GameVersion? version)` |

### 3.7 Pattern matching y switch expressions

En vez de cadenas de `if`, el código usa `switch` como expresión. El botón principal decide qué hacer así
(`MainViewModel.ExecutePrimary`):

```csharp
Func<Task>? flow = state switch
{
    LauncherState.DriveMissing => RetryAfterDriveMissingAsync,
    LauncherState.LoadFailed => RetryLoadAsync,
    LauncherState.NotInstalled => HasPendingDownloadForLatest() ? ContinueFirstInstallAsync : StartFirstInstallAsync,
    LauncherState.UpdateAvailable => () => StartUpdateAsync(automatic: false),
    LauncherState.Broken => () => RepairAsync(confirm: false),
    LauncherState.Ready => PlayAsync,
    _ => null,
};
```

Y patrones con `is`: `ex is LauncherException launcherException`, `progress is { Phase: InstallPhase.Installing or
InstallPhase.CleaningUp }`.

### 3.8 partial class

`MainViewModel` es una sola clase repartida en varios archivos con `public sealed partial class MainViewModel`. El
compilador los junta. Se partió por temas para que cada archivo sea manejable: `MainViewModel.cs` (estado y
presentación), `.Startup.cs`, `.Operations.cs`, `.Roadmap.cs`, `.Feedback.cs`. Todos comparten los mismos campos
privados.

### 3.9 MVVM a mano: ObservableObject e INotifyPropertyChanged

MVVM separa tres cosas:

- **View** (XAML): cómo se ve. No tiene lógica.
- **ViewModel** (C#): qué se muestra y qué hacen los botones. No sabe nada de controles.
- **Model**: los datos y la lógica (aquí, el Core).

La vista se «engancha» al ViewModel con *bindings*. Para que la vista se entere de que una propiedad cambió, el
ViewModel implementa `INotifyPropertyChanged`: un evento que dice «la propiedad X cambió». Toda la base está en
[`ObservableObject.cs`](../../src/PalitoLauncher/ViewModels/ObservableObject.cs):

```csharp
protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
{
    if (EqualityComparer<T>.Default.Equals(field, value)) return false;
    field = value;
    OnPropertyChanged(propertyName);
    return true;
}
```

`[CallerMemberName]` rellena solo el nombre de la propiedad que llama. Uso típico:

```csharp
public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
```

Cuando una propiedad depende de otra, hay que avisar de las dos:

```csharp
public string? SecondaryNoticeText
{
    get => _secondaryNoticeText;
    private set
    {
        if (SetProperty(ref _secondaryNoticeText, value)) OnPropertyChanged(nameof(HasSecondaryNotice));
    }
}

public bool HasSecondaryNotice => !string.IsNullOrWhiteSpace(_secondaryNoticeText);
```

Si se te olvida el `OnPropertyChanged(nameof(...))` de la dependiente, la vista no se actualiza (y no da error).

### 3.10 Comandos: RelayCommand y AsyncRelayCommand

Un botón no llama a un método directamente: se enlaza a un `ICommand` (`Command="{Binding RepairCommand}"`). Un
comando tiene `Execute` (qué hace) y `CanExecute` (si el botón está habilitado). En
[`RelayCommand.cs`](../../src/PalitoLauncher/ViewModels/RelayCommand.cs) hay dos:

- `RelayCommand`: para acciones síncronas o que lanzan un flujo con `RunFlow`.
- `AsyncRelayCommand`: para métodos `async`. Mientras se ejecuta, `CanExecute` devuelve `false` (el botón se
  deshabilita solo y no se puede pulsar dos veces) y captura cualquier excepción.

Se crean en el constructor de `MainViewModel`:

```csharp
CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(includeLauncher: true), () => !_isFetching && _initialized);
RepairCommand = new RelayCommand(() => RunFlow(() => RepairAsync(confirm: true), "Reparar"), () => CanModifyInstallation);
```

WPF vuelve a preguntar `CanExecute` cuando ocurre algo en la interfaz. Para forzarlo, el código llama a
`CommandManager.InvalidateRequerySuggested()` (lo hace `UpdateState()` al final, y existe el atajo
`RelayCommand.RaiseCanExecuteChanged()`).

### 3.11 Bindings en XAML

Lo que verás en las vistas:

| XAML | Significado |
|---|---|
| `Text="{Binding StatusText}"` | Muestra la propiedad `StatusText` del `DataContext` |
| `IsChecked="{Binding AutoDownloadUpdates}"` | En un CheckBox es bidireccional: al marcarlo se asigna la propiedad |
| `Text="{Binding LaunchArguments, UpdateSourceTrigger=PropertyChanged}"` | En un TextBox, actualiza el ViewModel con cada tecla (por defecto lo haría al perder el foco) |
| `Visibility="{Binding HasReleases, Converter={StaticResource BoolToVis}}"` | Convierte un `bool` en `Visible`/`Collapsed` con un conversor de [`Converters/`](../../src/PalitoLauncher/Converters) |
| `Command="{Binding NavigateCommand}" CommandParameter="Settings"` | Ejecuta el comando con un parámetro |
| `Style="{StaticResource ToggleSwitch}"` | Aplica un estilo definido en `Theme.xaml` |

**El `DataContext` se hereda**: `MainWindow` pone `DataContext = _vm` en su constructor, y todo lo que hay dentro
(Inicio, Versiones, Roadmap, la barra de acción) se enlaza al `MainViewModel`. La página de Ajustes cambia el suyo:
`<views:SettingsView DataContext="{Binding Settings}" />`, así que dentro de `SettingsView.xaml` los bindings apuntan al
`SettingsViewModel`.

**Un binding mal escrito no da error de compilación**: simplemente no muestra nada. En la ventana «Salida» de Visual
Studio aparece una línea `System.Windows.Data Error: 40 : BindingExpression path error...`. El test
`BindingSmokeTests` detecta estos errores automáticamente (ver [5.3](#53-tests-de-la-ui)).

### 3.12 El Dispatcher y los hilos

Como en Unity, **los controles de WPF solo se pueden tocar desde el hilo de UI**. Cuando algo llega desde otro hilo hay
que «mandarlo» al hilo de UI con el `Dispatcher` (la cola de mensajes de ese hilo).

`GameProcessManager` dispara `GameStarted` y `GameExited` desde hilos del pool, así que el ViewModel hace
(`MainViewModel.OnGameStarted`):

```csharp
private void OnGameStarted(object? sender, EventArgs e)
{
    _dispatcher.BeginInvoke(new Action(() =>
    {
        try
        {
            if (_disposed) return;
            // El evento llega en diferido: se mira el proceso real por si ya se cerró.
            IsGameRunning = IsProcessRunningSafe();
            if (IsGameRunning) MarkGameSessionStarted(restart: false);
            UpdateState();
        }
        catch (Exception ex)
        {
            Log.Error("Error al procesar el arranque del juego.", ex);
        }
    }));
}
```

Lo mismo hace `App.OnActivationRequested` cuando otra instancia pide traer la ventana al frente. Para tareas
periódicas se usa `DispatcherTimer`, que ya «tickea» en el hilo de UI (`_periodicTimer`, `_toastTimer`).

### 3.13 DataTemplates, estilos y el tema

- **DataTemplate por tipo**: el overlay de diálogos es un `ContentControl` cuyo contenido es el ViewModel del diálogo
  (`Dialogs.CurrentDialog`). WPF elige la vista según el tipo, gracias a esto en `MainWindow.xaml`:

```xml
<DataTemplate DataType="{x:Type vm:MessageDialogViewModel}">
    <views:MessageDialogView />
</DataTemplate>
```

  Para crear un diálogo nuevo basta con un ViewModel que implemente `IOverlayDialog` (con una tarea `Completion` que
  termina al pulsar un botón, como `MessageDialogViewModel`), su vista y un `DataTemplate` como ese; se abre con
  `await Dialogs.ShowDialogAsync(dialog, dialog.Completion)`. Para un simple mensaje con botones ya existe
  `Dialogs.ShowAsync(título, mensaje, textoPrimario, textoSecundario, isDanger, textoTerciario)`.
- **ItemTemplate**: las listas (Versiones, Roadmap) son `ItemsControl` con una plantilla por elemento enlazada a un
  ViewModel pequeño (`ReleaseItemViewModel`, `RoadmapItemViewModel`).
- **El tema** ([`Theme.xaml`](../../src/PalitoLauncher/Themes/Theme.xaml)) se carga en `App.xaml` y define:
  - la paleta como `Color` + `SolidColorBrush` (`AccentBrush`, `TextDimBrush`, `DangerBrush`…);
  - las fuentes (`AppFont`, `IconFont` para los iconos Segoe Fluent, `MonoFont`);
  - los conversores (`BoolToVis`, `InverseBoolToVis`, `InverseBool`, `NullToVis`, `InverseNullToVis`);
  - estilos **implícitos** (sin clave: se aplican a todos los `Button`, `CheckBox`, `TextBox`, `ComboBox`,
    `ProgressBar`, `ScrollBar`, `ToolTip`, `ContextMenu`, `MenuItem`) y estilos **con clave** que se piden a mano:
    `PrimaryButton`, `AccentButton`, `GhostButton`, `LinkButton`, `IconButton`, `NavButton`, `DangerOutlineButton`,
    `ToggleSwitch`, `Card`, `Badge`, `Chip`, `PageTitleText`, `SectionTitleText`, `BodyText`, `DimText`,
    `SmallDimText`, `GlyphText`, `MenuGlyph`…
- Los **iconos** son caracteres de la fuente Segoe Fluent Icons escritos como `&#xE939;` (globo de diálogo),
  `&#xE713;` (engranaje), etc.

### 3.14 Eventos del ViewModel hacia la ventana

El ViewModel no conoce la ventana, pero a veces necesita que haga algo (minimizarse, cerrarse, parpadear). Para eso
publica eventos (`MinimizeRequested`, `RestoreRequested`, `AttentionRequested`, `CloseRequested`, `ShutdownRequested`)
y [`MainWindow.xaml.cs`](../../src/PalitoLauncher/MainWindow.xaml.cs) se suscribe en su constructor. Así el ViewModel se
puede probar sin ventana.

### 3.15 Errores: LauncherException y «nunca lanza»

- [`LauncherException`](../../src/PalitoLauncher.Core/Util/LauncherException.cs) = error **esperado** cuyo mensaje se
  enseña tal cual al usuario: «No hay espacio suficiente en el disco…».
- Cualquier otra excepción = **fallo inesperado**: se registra con `Log.Error` y el usuario ve el mensaje genérico con
  «Crear informe» (`MainViewModel.HandleExceptionAsync`).
- Muchos métodos dicen en su documentación «Nunca lanza»: capturan todo, lo registran y devuelven un valor seguro
  (`false`, `null`, la caché…). Respeta esa promesa si los modificas: hay código que confía en ella.
- Algunas excepciones tienen tipo propio para que la UI reaccione **por tipo y nunca por el texto**:
  `GitHubRateLimitException`, `RemoteAssetChangedException`, `DownloadVerificationException`,
  `LauncherStartFailedException`.

### 3.16 Hecho para poder probarlo

- Lo que los tests necesitan cambiar es `internal` y se expone con `InternalsVisibleTo` en los `.csproj`. Ejemplos:
  `GitHubClient.UtcNow` (reloj), `GitHubClient.RequestTimeout`, `GameInstaller.IsGameRunningFrom`,
  `LauncherSelfUpdater.AliveTimeout`. Algunas son públicas porque también tienen sentido fuera de los tests, como
  `FileDownloader.RetryDelayScale` (los tests la ponen a 0 para no esperar) o `GameInstaller.PostExtractDelay`.
- `HttpFactory.Create(handler)` permite construir un `HttpClient` con un handler falso que responde sin red.
- `AppPaths.OverrideRootForTests` redirige todas las rutas de datos a una carpeta temporal.

---

## 4. Cómo depurar

### 4.1 Los logs

- Dónde: `%LOCALAPPDATA%\PalitoLauncher\logs\launcher-AAAAMMDD.log` (uno por día; se guardan 7 días). En el launcher:
  **Ajustes → Launcher → Abrir carpeta de logs**.
- Formato: `2026-10-19 13:45:01.123 [INFO ] [T12] mensaje` (fecha, nivel, hilo, texto) y, si hay excepción, el detalle
  completo en las líneas siguientes.
- Se puede abrir con el launcher en marcha (se escribe compartido).
- Si el archivo pasa de 50 MB solo se escriben avisos y errores (protección ante un bucle de logs).

Qué buscar en un log:

| Busca | Te dice |
|---|---|
| `===== Palito Launcher arrancando` | Inicio de cada sesión, con los argumentos; las líneas siguientes son la versión, Windows, .NET y la ruta del exe |
| `Instalación del launcher:` | Qué decidió la auto-instalación |
| `Releases:` | Cuántas releases vio y de dónde (`Network`, `NotModifiedCache`, `OfflineCache`) |
| `GitHub GET` | Cada consulta a la API, con su código de respuesta y el cupo restante (`X-RateLimit-Remaining=…`) |
| `límite de consultas` | Bloqueos por el límite de GitHub y hasta cuándo |
| `Operación` | Inicio y fin de instalar, actualizar, reparar, mover… |
| `[ERROR]` y `[WARN ]` | Problemas (con su excepción completa) |

Para añadir líneas desde tu código: `Log.Info("...")`, `Log.Warn("...", ex)`, `Log.Error("...", ex)`. No lanzan nunca,
así que puedes ponerlas en cualquier sitio, incluso dentro de un `catch`.

### 4.2 El informe para el desarrollador

**⋯ → Crear informe para el desarrollador** (o en Ajustes) crea `Coop2D-informe-AAAAMMDD-HHmm.zip` en el Escritorio
con `summary.txt` (estado del launcher, versiones, cupo de GitHub, ajustes principales: ver
`MainViewModel.BuildDiagnosticSummary`), los logs de 7 días, `settings.json`, `palito_install.json`, `Player.log` y
`Player-prev.log` de Unity y el último volcado de crash de Unity si existe. Es lo primero que hay que pedir a un amigo
cuando algo falla.

### 4.3 Ejecutar una build de desarrollo

Con F5 en Visual Studio o con `dotnet run --project src/PalitoLauncher -- --portable`:

- **No se auto-instala ni se auto-actualiza.** Al lado del exe hay un `PalitoLauncher.dll`, así que
  `LauncherSelfUpdater.IsPublishedLauncherExe` lo reconoce como desarrollo. En **Ajustes → Launcher** verás «Modo
  desarrollo: este no es el ejecutable publicado del launcher.». Si aparece el banner de versión nueva, [Actualizar]
  solo abre la página de releases.
- `--portable` **solo evita la auto-instalación**: un exe **publicado** abierto con ese argumento no se copia a
  `%LOCALAPPDATA%\Programs` y se ejecuta donde esté (útil para probar `publish\PalitoLauncher.exe` o para un USB). La
  auto-actualización, en cambio, sigue disponible con un exe publicado (reemplazaría ese mismo exe). Con una build de
  desarrollo es redundante pero no molesta.
- **Usa los mismos datos que el launcher instalado**: los mismos ajustes, caché, logs y carpeta del juego. Ojo con
  desinstalar o mover el juego desde una build de pruebas.
- **Cierra el launcher instalado antes de depurar.** Comparten la instancia única: si el instalado está abierto, tu
  build le pedirá que se muestre y se cerrará al momento (en el log: «Ya había una instancia abierta…»).
- Cada arranque gasta consultas de GitHub (ver [ARQUITECTURA.md, sección 8](ARQUITECTURA.md#8-github-caché-y-límite-de-consultas)).
  Si arrancas muchas veces seguidas puedes agotar el cupo de tu IP durante un rato.

### 4.4 Trucos útiles

- **Probar una encuesta sin publicarla**: crea `%LOCALAPPDATA%\PalitoLauncher\feedback.local.json` (ver
  [FEEDBACK.md](../remoto/FEEDBACK.md)).
- **1.0.5**: igual para `launcher.json`, con `%LOCALAPPDATA%\PalitoLauncher\launcher.local.json` (ver
  [LAUNCHER-JSON.md](../remoto/LAUNCHER-JSON.md)), y un interruptor de modo desarrollador en Ajustes
  (`LauncherSettings.DeveloperMode`) para herramientas como abrir más instancias del juego.
- **Forzar que vuelva a consultar todo**: cierra el launcher y borra la carpeta `%LOCALAPPDATA%\PalitoLauncher\cache`
  (se regenera sola). Ojo: la siguiente vez gasta cupo.
- **Volver a los ajustes por defecto**: cierra el launcher y renombra `settings.json` y `settings.json.bak`.
- **Errores de binding**: ventana «Salida» de Visual Studio mientras depuras, filtrando por `System.Windows.Data Error`.
- **Puntos de interrupción en código async**: funcionan normal. Si una excepción «desaparece», activa en
  «Configuración de excepciones» que se detenga en las excepciones de CLR al lanzarse.
- **Ver qué recibe la UI**: pon un punto de interrupción en `MainViewModel.UpdateState()`; con el estado y las
  variables locales ves por qué el botón muestra lo que muestra.

---

## 5. Tests

Hay más de 700 tests (sin contar los casos de cada `[Theory]`): son tu red de seguridad. **Si cambias algo y los tests
pasan, lo más probable es que no hayas roto nada importante.**

### 5.1 Ejecutar los tests

```powershell
dotnet test PalitoLauncher.sln                                                          # todo
dotnet test tests/PalitoLauncher.Tests --filter "FullyQualifiedName~LauncherStateLogicTests"
dotnet test tests/PalitoLauncher.UI.Tests --filter "FullyQualifiedName~BindingSmokeTests"
```

En Visual Studio: **Prueba → Explorador de pruebas** y «Ejecutar todo», o clic derecho sobre un test.

Los **tests de integración** descargan las builds reales de GitHub (~110 MB) y están desactivados por defecto:

```powershell
$env:PALITO_RUN_INTEGRATION = "1"
dotnet test PalitoLauncher.sln --filter "FullyQualifiedName~IntegrationTests"
```

La CI ([`ci.yml`](../../.github/workflows/ci.yml)) ejecuta todos los demás en cada push a `main`.

### 5.2 Tests del Core

[`tests/PalitoLauncher.Tests`](../../tests/PalitoLauncher.Tests) tiene un archivo por clase (`GameInstallerTests.cs`,
`GitHubClientTests.cs`…). Usan xUnit: `[Fact]` es un test, `[Theory]` + `[InlineData(...)]` es el mismo test con varios
datos, y `Assert.Equal(esperado, real)` comprueba.

Un test de lógica pura es así de simple (`LauncherStateLogicTests`):

```csharp
[Fact]
public void ComputeState_BusyWinsOverEverything()
{
    var input = new LauncherStateInput
    {
        IsBusy = true,
        IsGameRunning = true,
        Install = Status(InstallState.Broken),
        FetchFailed = true,
    };
    Assert.Equal(LauncherState.Busy, LauncherStateLogic.ComputeState(input));
    Assert.Equal(LauncherState.Busy, LauncherStateLogic.ComputeState(new LauncherStateInput { IsBusy = true }));
}
```

Para lo que usa red, **nunca hay red de verdad**: se usa un `HttpMessageHandler` falso que responde lo que el test
quiera y apunta qué se le pidió. Ejemplo de `GitHubClientTests`:

```csharp
private GitHubClient CreateClient(FakeHttpHandler handler)
{
    var client = new GitHubClient(HttpFactory.Create(handler), _dir);
    client.UtcNow = () => _clock.Now;
    return client;
}
```

y luego `FakeHttpHandler.Sequence(Ok(etag: "\"e1\""), _ => GitHubResponses.NotModified())` responde primero un 200 y
después un 304; el test comprueba `handler.RequestCount` para saber cuántas consultas se gastaron.

Ayudantes en `TestHelpers/` (cada subcarpeta la creó una parte distinta del desarrollo inicial, por eso hay varias
`TempDir` y dos `FakeHttpHandler`; usa la de la carpeta del test que estés imitando):

| Ayudante | Para qué |
|---|---|
| `TestHelpers/A/FakeHttpHandler.cs` | Respuestas HTTP falsas y registro de peticiones (GitHub) |
| `TestHelpers/B/FakeFileServer.cs` | Servidor de archivos falso con `Range`, cortes y errores (descargas) |
| `TestHelpers/C/FakeBuildServer.cs`, `TestBuilds.cs` | Builds del juego falsas en zip para probar el instalador |
| `TestHelpers/F3/CapturingHttpHandler.cs` | Captura cada petición con el JSON enviado (tests del envío a Discord) |
| `TempDir.cs` (en B, C, F, F3) | Carpeta temporal única bajo `%TEMP%\palito-tests` que se borra al terminar |

### 5.3 Tests de la UI

[`tests/PalitoLauncher.UI.Tests`](../../tests/PalitoLauncher.UI.Tests) prueba ViewModels y vistas **sin mostrar ninguna
ventana**:

- [`TestHelpers/WpfHost.cs`](../../tests/PalitoLauncher.UI.Tests/TestHelpers/WpfHost.cs) crea un hilo de UI propio con
  una `Application` que carga el `Theme.xaml` real. **No usa la `App` real**: su `OnStartup` arrancaría el launcher de
  verdad. Además redirige los datos a `%TEMP%` y manda todo el HTTP a un proxy inexistente, así que una petición por
  error falla al instante en vez de gastar cupo.
- Todo test de UI se ejecuta dentro de `WpfHost.RunAsync(async () => { ... })`.
- [`BindingSmokeTests`](../../tests/PalitoLauncher.UI.Tests/BindingSmokeTests.cs) construye `MainWindow`, recorre todas
  las páginas, el menú ⋯ y los diálogos, y **falla si hay cualquier error de binding**. Si añades una vista o una página,
  añádela aquí.
- [`TestHelpers/MainViewModelAccess.cs`](../../tests/PalitoLauncher.UI.Tests/TestHelpers/MainViewModelAccess.cs) accede a
  campos y métodos privados del `MainViewModel` con `UnsafeAccessor`:

```csharp
[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_crashNotice")]
public static extern ref string? CrashNotice(MainViewModel vm);
```

  Si renombras ese campo, el test falla con `MissingFieldException` (nunca pasa «en falso»): renómbralo también aquí.
- Los tests de UI se ejecutan **en serie** (`AssemblyInfo.cs` desactiva el paralelismo) porque comparten el hilo de UI.

### 5.4 Escribir un test nuevo

1. Busca el archivo de tests de la clase que cambias (o crea `MiClaseTests.cs` en el proyecto que toque).
2. Copia un test parecido que ya exista y cámbialo. El nombre describe el caso: `Metodo_Situacion_Resultado`.
3. Estructura «preparar → actuar → comprobar».
4. Nada de red ni de rutas fijas: handlers falsos y `TempDir`.
5. Si necesitas controlar el tiempo, usa los relojes inyectables (`UtcNow`) en lugar de esperar de verdad.
6. Ejecútalo solo con `--filter` y luego la batería completa.

---

## 6. Recetas paso a paso

Los nombres inventados para los ejemplos llevan la marca «(ejemplo)» o «nombre de ejemplo»; todos los demás existen en
el código.

### 6.1 Añadir un ajuste nuevo

Ejemplo: un interruptor «Mostrar las novedades en Inicio» (nombre de ejemplo: `ShowNewsOnHome`).

1. **Modelo** — en [`LauncherSettings.cs`](../../src/PalitoLauncher.Core/Settings/LauncherSettings.cs), añade la propiedad
   con su valor por defecto y un comentario:

   ```csharp
   /// <summary>Mostrar la tarjeta de novedades en Inicio.</summary>
   public bool ShowNewsOnHome { get; set; } = true;   // (ejemplo)
   ```

   - **No hace falta tocar `SettingsStore`**: al guardar se serializa el objeto entero, y al cargar se leen una a una
     todas las propiedades públicas con `get`/`set` (`SettingsStore.PersistedProperties`, por reflexión), así que un
     valor dañado en el archivo solo afecta a ese ajuste.
   - Los enums inválidos ya vuelven a su valor por defecto solos. Si tu ajuste necesita un rango (p. ej. «entre 0 y
     100»), añade la corrección en `SettingsStore.Sanitize`.
   - Si es una **lista** u otro objeto mutable, cópiala en `LauncherSettings.Clone()` (la 1.0.5 ya lo hace con sus
     listas), porque los guardados trabajan sobre una copia.
   - Los launchers viejos ignoran propiedades que no conocen, y los nuevos usan el valor por defecto si falta: no hace
     falta migrar nada.

2. **ViewModel** — en [`SettingsViewModel.cs`](../../src/PalitoLauncher/ViewModels/SettingsViewModel.cs), copia el patrón de
   `AutoDownloadUpdates`:

   ```csharp
   public bool AutoDownloadUpdates
   {
       get => S.AutoDownloadUpdates;
       set
       {
           if (S.AutoDownloadUpdates == value) return;
           S.AutoDownloadUpdates = value;
           OnPropertyChanged();
           _owner.OnSettingsChanged(SettingChange.AutoDownload);
       }
   }
   ```

   `S` son los ajustes vivos del `MainViewModel`. `OnSettingsChanged` guarda en segundo plano y recalcula el estado. Si
   el cambio no necesita ninguna reacción especial, usa `SettingChange.General`. Si sí (p. ej. volver a consultar algo),
   añade un valor al enum `SettingChange` y su `case` en `MainViewModel.OnSettingsChanged`.

3. **Vista** — en [`SettingsView.xaml`](../../src/PalitoLauncher/Views/SettingsView.xaml), dentro de la tarjeta que toque:

   ```xml
   <CheckBox Style="{StaticResource ToggleSwitch}" Margin="0,14,0,0"
             Content="Descargar e instalar automáticamente" IsChecked="{Binding AutoDownloadUpdates}" />
   ```

   Para un texto libre, copia el `TextBox` de «Nombre para las opiniones»: se guarda al perder el foco
   (`FeedbackNicknameBox_LostKeyboardFocus` en `SettingsView.xaml.cs` llama a `SettingsViewModel.CommitFeedbackNickname`).
   Para una lista de opciones, copia el `ComboBox` de «Al abrir el juego» (`AfterLaunchOptions` /
   `SelectedAfterLaunch`).

4. **Usarlo** — en el `MainViewModel`, léelo con `_settings.ShowNewsOnHome` (ejemplo) desde el hilo de UI.

5. **Informe** — si ayuda a diagnosticar problemas, añádelo a `MainViewModel.BuildDiagnosticSummary`.

6. **Tests** — un test de ida y vuelta en
   [`SettingsStoreTests.cs`](../../tests/PalitoLauncher.Tests/SettingsStoreTests.cs) (guardar, cargar, mismo valor) y
   ejecuta `BindingSmokeTests` para comprobar que el binding está bien escrito.

### 6.2 Añadir una página nueva a la navegación

Así se añadió el Roadmap en la 1.0.3: úsalo como modelo. Ejemplo: una página «Comunidad» (nombres de ejemplo:
`AppPage.Community`, `CommunityView`).

1. **Enum** — añade el valor a `AppPage` (al principio de [`MainViewModel.cs`](../../src/PalitoLauncher/ViewModels/MainViewModel.cs)).
2. **Propiedad «¿es esta la página?»** — junto a `IsHomePage`, `IsVersionsPage`… añade `IsCommunityPage` (ejemplo):
   `public bool IsCommunityPage => _currentPage == AppPage.Community;`. Y en el `set` de `CurrentPage` añade
   `OnPropertyChanged(nameof(IsCommunityPage));` (si no, el botón y la página no se enteran del cambio).
3. **Al entrar en la página** (opcional) — si hay que cargar algo, añade un caso en `MainViewModel.Navigate`, como el de
   `AppPage.Roadmap` que llama a `RefreshRoadmapIfStale()`.
4. **Datos y comandos** — si la página tiene bastante lógica, ponla en su propio archivo parcial
   (`MainViewModel.Community.cs`, ejemplo), como `MainViewModel.Roadmap.cs`.
5. **Vista** — crea `Views/CommunityView.xaml` (ejemplo) copiando la estructura de `RoadmapView.xaml` o
   `VersionsView.xaml`: un `UserControl` con un `ScrollViewer` y un `StackPanel` con `MaxWidth`. Su `.xaml.cs` solo llama
   a `InitializeComponent()`.
6. **Botón de navegación** — en [`MainWindow.xaml`](../../src/PalitoLauncher/MainWindow.xaml), en el `StackPanel` de
   navegación, copia uno existente:

   ```xml
   <Button Style="{StaticResource NavButton}" Content="&#xE7C1;" Margin="0,0,0,8"
           Tag="{Binding IsRoadmapPage}" Command="{Binding NavigateCommand}" CommandParameter="Roadmap"
           ToolTip="Roadmap" AutomationProperties.Name="Roadmap" />
   ```

   `Tag` marca el botón activo, `CommandParameter` es el nombre del valor de `AppPage`, y el glifo es un icono de Segoe
   Fluent Icons. `ToolTip` y `AutomationProperties.Name` hacen el botón comprensible (también para lectores de pantalla).
7. **Hueco de la página** — en el `Grid` de «Páginas» de `MainWindow.xaml`:
   `<views:CommunityView Visibility="{Binding IsCommunityPage, Converter={StaticResource BoolToVis}}" />` (ejemplo). Las
   páginas se crean una sola vez y se muestran u ocultan. Si la página tiene su propio ViewModel, pásaselo como se hace con Ajustes
   (`<views:SettingsView DataContext="{Binding Settings}" />`).
8. **Tests** — en `BindingSmokeTests.MainWindow_PagesMenuAndDialogs_HaveNoBindingErrors`, añade
   `vm.NavigateCommand.Execute("Community");` (ejemplo) seguido de `await LayoutAsync(window);` y alguna comprobación,
   como hacen las demás páginas.

### 6.3 Añadir un campo a feedback.json o a launcher.json

Los archivos remotos **no se deserializan directamente**: se leen campo a campo y se sanean, para que un error de
escritura en GitHub nunca rompa el launcher. Por eso añadir un campo toca varios sitios. (Para la encuesta,
[FEEDBACK.md](../remoto/FEEDBACK.md) tiene además recetas para un tipo de pregunta nuevo y para un dato nuevo en el mensaje de
Discord.)

**feedback.json** (ejemplo: un campo opcional `thanksMessage` con el texto del toast al enviar):

1. **Modelo** — en [`SurveyModels.cs`](../../src/PalitoLauncher.Core/Feedback/SurveyModels.cs), propiedad nueva en
   `SurveyDefinition` (o en `SurveyQuestion` si es por pregunta) con valor por defecto.
2. **Lectura** — en [`SurveyProvider.cs`](../../src/PalitoLauncher.Core/Feedback/SurveyProvider.cs), método `ReadSurvey`
   (o `ReadQuestion`). Los nombres van en camelCase, igual que en el JSON:

   ```csharp
   var survey = new SurveyDefinition
   {
       Id = GetString(props, "id") ?? "",
       Title = GetString(props, "title") ?? "",
       Intro = GetString(props, "intro"),
       AskAfterSession = GetBool(props, "askAfterSession") ?? true,
       // ...
   };
   ```

   Usa `GetString`, `GetInt` o `GetBool` y un valor por defecto con `??`.
3. **Saneado** — en `SurveyProvider.Sanitize` (o `SanitizeQuestion`): recorta textos con `FeedbackText.Clean(valor,
   máximo, multiline)` y limita números con `Math.Clamp`. Añade la constante del máximo junto a las demás
   (`MaxIntroLength`…).
4. **Usarlo** — donde corresponda: `MainViewModel.Feedback.cs` (cuándo preguntar, toasts),
   `SurveyDialogViewModel` (lo que se ve en la encuesta) o `FeedbackService.BuildDiscordPayload` (lo que llega a
   Discord).
5. **Tests** — en [`SurveyProviderTests.cs`](../../tests/PalitoLauncher.Tests/SurveyProviderTests.cs): que se lee, que
   falta = valor por defecto, que un valor fuera de rango se corrige.
6. **Documentación** — tabla de campos en [FEEDBACK.md](../remoto/FEEDBACK.md) y, si procede,
   [`feedback.example.json`](../ejemplos/feedback.example.json).

Compatibilidad: un launcher viejo ignora los campos que no conoce, así que puedes publicar el `feedback.json` con el
campo nuevo antes de que todos actualicen. Lo contrario no: si el campo es **obligatorio** para que funcione algo, los
launchers viejos no lo tendrán. Diseña siempre los campos nuevos como opcionales.

**launcher.json** (1.0.5): sigue el mismo patrón con sus clases: modelo en
`src/PalitoLauncher.Core/Remote/LauncherManifest.cs`, lectura y saneado en `ManifestProvider.Parse` /
`ManifestProvider.Sanitize`, lógica pura (fechas, coincidencias) en `ManifestLogic`, uso en el `MainViewModel`, tests en
`ManifestProviderTests.cs` / `ManifestLogicTests.cs` y referencia en [LAUNCHER-JSON.md](../remoto/LAUNCHER-JSON.md).

### 6.4 Añadir o cambiar un texto, o traducir

No hay archivos de recursos: **los textos están escritos directamente** donde se usan.

| Texto | Dónde está |
|---|---|
| Títulos, etiquetas y botones fijos de las páginas | En los `.xaml` de [`Views/`](../../src/PalitoLauncher/Views) y en [`MainWindow.xaml`](../../src/PalitoLauncher/MainWindow.xaml) (`Text="..."`, `Content="..."`, `Header="..."`, `ToolTip="..."`) |
| Botón principal y texto de estado (JUGAR, ACTUALIZAR, «Versión instalada: …») | `MainViewModel.UpdateState` y `ComputeBusyPresentation` |
| Avisos y toasts | Llamadas a `ShowToast(...)` y `UpdateNotice` en el `MainViewModel` |
| Diálogos | Llamadas a `Dialogs.ShowAsync("Título", "Mensaje", "Botón", ...)` |
| Mensajes de error | Textos de `LauncherException` en el Core (p. ej. constantes como `FileDownloader.GiveUpMessage`) |
| Nombre del juego | `LauncherConfig.GameDisplayName`: úsalo con `$"..."` en vez de escribir «Coop2D» |

Pasos:

1. Busca el texto actual en toda la solución (Ctrl+Shift+F).
2. Cámbialo. Mantén el tono: español claro para gente no técnica, sin jerga.
3. **Busca también en los tests**: algunos comprueban textos exactos (p. ej. `BindingSmokeTests` busca «En desarrollo»
   y «Contarnos qué pasó»; `MainViewModelHelperTests` tiene los textos de pausa). Si cambias uno, cambia el test.
4. Si el texto describe algo que sale en la documentación (README, guías), actualízala.

**Traducir a otro idioma** no está preparado. Haría falta mover todos los textos a un archivo de recursos
(`Strings.resx` + `Strings.en.resx`), usarlos en el código (`Strings.MiTexto`) y en el XAML (`{x:Static}`), y añadir
un selector de idioma en Ajustes. Es un cambio grande y mecánico; no merece la pena hasta que haya jugadores que lo
necesiten (con Steam, por ejemplo).

### 6.5 Cambiar el repo o el juego en LauncherConfig

Casi todo lo que identifica al juego está en [`LauncherConfig.cs`](../../src/PalitoLauncher.Core/LauncherConfig.cs):

| Constante | Qué cambia |
|---|---|
| `GameDisplayName` | Nombre visible del juego en todos los textos |
| `GameRepoOwner`, `GameRepoName` | De qué repo salen las releases, los milestones, el fondo y `feedback.json` |
| `GameExecutableName` | Exe que se busca dentro del zip y se lanza |
| `GameAssetPattern` | Qué adjunto de cada release es el juego (regex) |
| `InstallFolderName` | Carpeta que se crea dentro de la elegida (`...\Coop2D`) |
| `ShortcutName` | Nombre de los accesos directos |
| `LauncherRepoOwner`, `LauncherRepoName`, `LauncherAssetPattern` | Dónde busca el launcher sus propias actualizaciones |
| `PeriodicCheckInterval`, `LauncherUpdateCheckInterval`, `ApiFreshnessWindow`, `MilestonesFreshnessWindow`, `PrePlayCheckMaxAge`, `RateLimitReserveForUser` | Intervalos y reserva de cupo (no los bajes sin pensar en el límite de 60 por hora y por IP) |

Hay otros sitios que llevan nombres fijos (cámbialos solo si cambias de verdad de producto):

- [`AppPaths.cs`](../../src/PalitoLauncher.Core/AppPaths.cs): carpetas `PalitoLauncher` y `PalitoGames` de los datos.
- [`HttpFactory.cs`](../../src/PalitoLauncher.Core/Net/HttpFactory.cs): `ProductComment` con la URL del repo del launcher en
  el User-Agent.
- [`SingleInstance.cs`](../../src/PalitoLauncher/Services/SingleInstance.cs) y `LauncherSelfUpdater.UpdateAliveEventName`:
  nombres del mutex y de los eventos.
- `AssemblyName` en [`PalitoLauncher.csproj`](../../src/PalitoLauncher/PalitoLauncher.csproj) y
  `LauncherSelfUpdater.AppAssemblyName` (deben coincidir) y la comprobación del nombre del exe en
  [`release.yml`](../../.github/workflows/release.yml).
- El README, [PUBLICAR.md](../operacion/PUBLICAR.md) y los enlaces de la documentación.

Pasos y precauciones:

1. Cambia las constantes, compila y pasa los tests (algunos tests usan estos valores).
2. Prueba con una build de desarrollo que ve las releases correctas (página Versiones).
3. **Cambiar el repo del launcher es delicado**: los launchers ya instalados buscan actualizaciones en el repo que
   tenían compilado. Para mudarse, publica primero en el repo **viejo** una última versión con la configuración nueva;
   cuando todos la tengan, ya buscarán en el nuevo.
4. Cambiar `GameExecutableName` o el patrón de assets afecta a las builds que ya están publicadas: comprueba que las
   releases antiguas siguen encajando o que da igual perderlas.

### 6.6 Publicar una versión del launcher

El proceso completo está en el [README principal](../../README.md), sección «Publicar una versión del launcher», y lo
ejecuta [`release.yml`](../../.github/workflows/release.yml). Resumen:

1. Asegúrate de que `main` compila, los tests pasan (`dotnet test PalitoLauncher.sln`) y la CI está en verde.
2. Sube `<Version>` en [`Directory.Build.props`](../../Directory.Build.props) (p. ej. `1.0.5`) y haz commit y push (el
   historial usa mensajes como «Launcher 1.0.4: …»).
3. Crea y sube el tag:

   ```powershell
   git tag v1.0.5
   git push origin v1.0.5
   ```

4. En GitHub → **Actions → Release**, espera a que termine: compila, pasa los tests, publica el exe, comprueba que se
   llama `PalitoLauncher.exe`, que es autónomo y que su versión coincide con el tag, y crea la release.
5. Comprueba en tu launcher instalado: **Ajustes → Buscar ahora** debe mostrar el banner de la versión nueva.

- Un tag con sufijo (`v1.1.0-beta.1`) se publica como **pre-release** y no le llega a nadie automáticamente: sirve
  para probar.
- Si el workflow falla, mira qué paso falló en Actions; los casos típicos y qué hacer están en
  [TAREAS-MANUALES.md](../operacion/TAREAS-MANUALES.md).
- Las builds del **juego** se publican de otra forma: [PUBLICAR.md](../operacion/PUBLICAR.md).

---

## 7. Glosario

| Término | Significado en este proyecto |
|---|---|
| **InstallRoot** | La carpeta de instalación elegida (contiene `game\` y `.launcher\`) |
| **Marcador** | `game\palito_install.json` (`InstalledGameInfo`): qué versión hay instalada |
| **Release** | Una publicación en GitHub (tag, título, notas, adjuntos) |
| **Asset** | Un archivo adjunto a una release (el zip del juego, el exe del launcher) |
| **Build jugable** | Release no borrador con un asset que encaja con `GameAssetPattern` (`GameRelease`) |
| **Revisión** | Misma versión pero zip distinto (otro SHA-256): el desarrollador re-subió el archivo |
| **Canal de pruebas** | Releases marcadas como pre-release en GitHub; solo las ve quien activa «Incluir versiones de prueba» |
| **`.part`** | Descarga a medias que se puede reanudar |
| **Staging / ready / old / trash** | Carpetas de trabajo del instalador (ver ARQUITECTURA, sección 6) |
| **Swap / diario** | El intercambio de carpetas y su registro `swap.json` |
| **Cupo / rate limit** | Las 60 consultas por hora y por IP de la API de GitHub sin token |
| **ETag / 304** | Mecanismo de «¿ha cambiado desde la última vez?»; ahorra datos, no cupo |
| **Webhook** | URL de Discord que permite publicar mensajes en un canal sin bot |
| **Bandeja de salida** | Carpeta donde esperan las opiniones que aún no se han podido enviar |
| **Overlay** | Capa oscura dentro de la ventana donde se muestran los diálogos |
| **Toast** | Mensaje breve abajo a la derecha que desaparece solo |
| **Aviso secundario** | La línea pequeña de color bajo el texto de estado de la barra de acción |
| **Operación** | Tarea larga de una en una: instalar, actualizar, reparar, mover, desinstalar, actualizar el launcher |

---

## 8. Checklist antes de hacer commit

- [ ] Compila sin avisos nuevos (`dotnet build PalitoLauncher.sln`).
- [ ] Pasan los tests (`dotnet test PalitoLauncher.sln`), incluido `BindingSmokeTests` si tocaste XAML.
- [ ] Nada lento en el hilo de UI; los errores esperables son `LauncherException` con mensaje en español.
- [ ] Textos nuevos en español claro; si cambiaste uno que comprueban los tests, actualizaste el test.
- [ ] Si cambiaste el comportamiento de un método, actualizaste su `<summary>`.
- [ ] Si cambiaste algo que describe la documentación, actualizaste el `.md` correspondiente.
- [ ] Repasaste las [reglas que no hay que romper](ARQUITECTURA.md#13-reglas-que-no-hay-que-romper).
