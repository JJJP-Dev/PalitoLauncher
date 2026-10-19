<#
.SYNOPSIS
    Publica PalitoLauncher.exe (un único exe autocontenido para win-x64) en la carpeta publish\ del repo.

.DESCRIPTION
    Hace lo mismo que el workflow de release, pero en local: dotnet publish con el RuntimeIdentifier (el csproj activa
    entonces SelfContained + PublishSingleFile + compresión) y muestra versión, tamaño y SHA-256 del exe resultante.
    Para publicar una versión para los amigos NO hace falta este script: basta con crear y subir un tag (ver README.md).

.PARAMETER Version
    Versión a grabar en el exe (1.2.3, se acepta "v1.2.3"). Si se omite, la de Directory.Build.props.

.PARAMETER RunTests
    Ejecuta antes los tests de la solución.

.PARAMETER OutputDir
    Carpeta de salida (por defecto <repo>\publish, que se vacía antes). Una carpeta distinta debe estar vacía o no existir.

.PARAMETER ArtifactsPath
    Opcional: carpeta para obj/bin intermedios (dotnet --artifacts-path), para no mezclarlos con los de Visual Studio.

.EXAMPLE
    pwsh scripts/publish.ps1

.EXAMPLE
    pwsh scripts/publish.ps1 -Version 1.0.1 -RunTests
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [string]$ArtifactsPath,
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
$solution = Join-Path $repoRoot 'PalitoLauncher.sln'
$project = Join-Path $repoRoot 'src\PalitoLauncher\PalitoLauncher.csproj'
$defaultOutputDir = Join-Path $repoRoot 'publish'
$OutputDir = if ($OutputDir) { [System.IO.Path]::GetFullPath($OutputDir) } else { $defaultOutputDir }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No se encuentra "dotnet". Instala el SDK de .NET 10: https://dotnet.microsoft.com/download'
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "No se encuentra el proyecto: $project" }

if ($Version) {
    $Version = $Version.Trim() -replace '^[vV]', ''
    if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
        throw "Versión no válida: '$Version'. Usa el formato 1.2.3 (opcionalmente con sufijo: 1.2.3-beta.1)."
    }
}

$commonArgs = @('-c', $Configuration, '-nologo')
if ($ArtifactsPath) { $commonArgs += @('--artifacts-path', [System.IO.Path]::GetFullPath($ArtifactsPath)) }

if ($RunTests) {
    Write-Host '==> Ejecutando tests…' -ForegroundColor Cyan
    & dotnet test $solution @commonArgs
    if ($LASTEXITCODE -ne 0) { throw "Los tests fallaron (código $LASTEXITCODE). No se publica nada." }
}

# Carpeta de salida limpia (solo se vacía sola la carpeta publish\ del repo; otra carpeta debe venir vacía).
if (Test-Path -LiteralPath $OutputDir) {
    $existing = @(Get-ChildItem -LiteralPath $OutputDir -Force)
    if ($existing.Count -gt 0) {
        if ($OutputDir.TrimEnd('\') -ieq $defaultOutputDir.TrimEnd('\')) {
            $existing | Remove-Item -Recurse -Force
        }
        else {
            throw "La carpeta de salida no está vacía: $OutputDir"
        }
    }
}

Write-Host "==> Publicando $Runtime ($Configuration)…" -ForegroundColor Cyan
$publishArgs = @('publish', $project, '-r', $Runtime, '-o', $OutputDir) + $commonArgs
if ($Version) { $publishArgs += "-p:Version=$Version" }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló (código $LASTEXITCODE)." }

# Comprobaciones: el auto-update busca un asset llamado exactamente PalitoLauncher.exe y el exe debe ser autónomo.
$exe = Join-Path $OutputDir 'PalitoLauncher.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "No se generó $exe (¿cambió AssemblyName?)." }

$stream = [System.IO.File]::OpenRead($exe)
try {
    $b0 = $stream.ReadByte()
    $b1 = $stream.ReadByte()
}
finally { $stream.Dispose() }
if ($b0 -ne 0x4D -or $b1 -ne 0x5A) { throw "$exe no es un ejecutable de Windows válido." }

$looseDlls = @(Get-ChildItem -LiteralPath $OutputDir -Filter '*.dll' -File)
if ($looseDlls.Count -gt 0) {
    Write-Warning ("Hay DLL sueltas junto al exe (" + (($looseDlls | ForEach-Object Name) -join ', ') +
        "): el exe no funcionaría solo. Revisa PublishSingleFile / IncludeNativeLibrariesForSelfExtract en el csproj.")
}

$item = Get-Item -LiteralPath $exe
$sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$sizeText = '{0} MB ({1} bytes)' -f ($item.Length / 1MB).ToString('0.0', $inv), $item.Length.ToString('N0', $inv)

Write-Host ''
Write-Host 'PalitoLauncher.exe listo' -ForegroundColor Green
Write-Host "  Ruta:    $exe"
Write-Host "  Versión: $($item.VersionInfo.ProductVersion)"
Write-Host "  Tamaño:  $sizeText"
Write-Host "  SHA-256: $sha256"

$extras = @(Get-ChildItem -LiteralPath $OutputDir -File | Where-Object { $_.Name -ne 'PalitoLauncher.exe' })
if ($extras.Count -gt 0) {
    Write-Host ("  (Otros archivos en la carpeta, no hace falta subirlos: " + (($extras | ForEach-Object Name) -join ', ') + ')') -ForegroundColor DarkGray
}
