using System.Text.Json.Serialization;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.Install;

/// <summary>
/// Información de la versión instalada. Se guarda como "palito_install.json" dentro de la carpeta del juego
/// (es la fuente de verdad: sobrevive aunque se borren los ajustes del launcher).
/// </summary>
public sealed class InstalledGameInfo
{
    public const string MarkerFileName = "palito_install.json";

    public int SchemaVersion { get; set; } = 1;
    public string Tag { get; set; } = "";
    public long ReleaseId { get; set; }

    /// <summary>GameVersion.ToString() (p.ej. "0.0.2-prototype.2") o null si no se pudo parsear.</summary>
    public string? Version { get; set; }

    /// <summary>Texto para la UI en el momento de instalar ("0.0.2 · Prototipo 2" o el tag).</summary>
    public string VersionDisplay { get; set; } = "";

    public string AssetName { get; set; } = "";
    public long AssetId { get; set; }
    public long AssetSize { get; set; }
    public string? Sha256 { get; set; }

    /// <summary>Ejecutable del juego relativo a la carpeta del juego (normalmente "Coop2D.exe").</summary>
    public string ExecutableName { get; set; } = LauncherConfig.GameExecutableName;

    public DateTimeOffset InstalledAtUtc { get; set; }

    [JsonIgnore]
    public GameVersion? ParsedVersion => GameVersion.TryParse(Version, out var v) ? v : GameVersion.FromRelease(AssetName, Tag, null);
}

public enum InstallState
{
    /// <summary>No hay juego en la carpeta.</summary>
    NotInstalled,

    /// <summary>Instalado y el ejecutable existe.</summary>
    Installed,

    /// <summary>Hay restos (carpeta/marcador) pero falta el ejecutable o el marcador está corrupto → conviene reparar.</summary>
    Broken,

    /// <summary>La unidad de la carpeta de instalación no existe o no está lista (disco USB desconectado, letra cambiada).</summary>
    DriveMissing,
}

public sealed class InstallStatus
{
    public InstallState State { get; init; }
    public InstalledGameInfo? Info { get; init; }
    public string InstallRoot { get; init; } = "";
    public string GameDir { get; init; } = "";

    /// <summary>Ruta completa del exe si existe.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// game\ contiene archivos que el launcher no reconoce (sin marcador válido, sin exe y sin *_Data\app.info).
    /// El instalador nunca los borra: los aparta a "game.backup-yyyyMMddHHmmss".
    /// </summary>
    public bool HasForeignContent { get; init; }

    /// <summary>Hay una descarga parcial (.part) en .launcher\downloads (para mostrar "CONTINUAR").</summary>
    public bool HasPendingDownload { get; init; }
}

public enum InstallPhase
{
    Preparing,
    Downloading,
    Verifying,
    Extracting,

    /// <summary>La build está lista pero el juego (o UnityCrashHandler) sigue abierto desde la carpeta: se espera a que se cierre.</summary>
    WaitingForGameExit,

    Installing,
    CleaningUp,
    Moving,
    Uninstalling,
}

/// <summary>Progreso de una operación del instalador. <see cref="Status"/> es texto en español listo para la UI.</summary>
public readonly record struct InstallProgress(InstallPhase Phase, double? Fraction, string Status, DownloadProgress? Download = null)
{
    /// <summary>True durante fases en las que cancelar es seguro.</summary>
    public bool CanCancel => Phase is InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Verifying
        or InstallPhase.Extracting or InstallPhase.WaitingForGameExit or InstallPhase.Moving;
}

public sealed class InstallOptions
{
    /// <summary>Límite de velocidad de descarga en bytes/s (0 = sin límite).</summary>
    public long MaxBytesPerSecond { get; init; }

    /// <summary>Si es true, el archivo descargado se conserva en .launcher\downloads tras instalar.</summary>
    public bool KeepDownloadedArchive { get; init; }
}
