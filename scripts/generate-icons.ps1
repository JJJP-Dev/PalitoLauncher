<#
.SYNOPSIS
    Genera los recursos gráficos del launcher a partir de src/PalitoLauncher/Assets/logo_source.png.

.DESCRIPTION
    Salidas (en src/PalitoLauncher/Assets):
      - logo.png        256×256, solo los trazos del logo en NEGRO sobre transparente, centrado con margen.
      - logo_white.png  igual pero en BLANCO (para fondos oscuros: barra de título, fondo por defecto).
      - app.ico         icono multi-tamaño (16, 24, 32, 48, 64, 128, 256) con entradas PNG:
                        cuadrado redondeado ámbar #FFB627 y el logo negro centrado (~68 %).

    El original es un "sticker": trazos negros con relleno blanco opaco. Para las versiones de línea se convierte la
    luminancia en alfa (negro → opaco, blanco → transparente): el relleno desaparece y quedan solo los trazos, que luego
    se recolorean conservando el alfa. En los tamaños pequeños del icono el logo se agranda un poco y se refuerza el alfa
    para que los trazos finos no se pierdan.

    Funciona con Windows PowerShell 5.1 y con PowerShell 7 (usa System.Drawing, solo Windows).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\generate-icons.ps1

.EXAMPLE
    pwsh -File scripts/generate-icons.ps1 -PreviewDir "$env:TEMP\palito-iconos"
#>
[CmdletBinding()]
param(
    # Imagen original del logo (PNG con transparencia). Por defecto: src\PalitoLauncher\Assets\logo_source.png.
    [string]$Source,

    # Carpeta de salida. Por defecto: src\PalitoLauncher\Assets.
    [string]$OutDir,

    # Opcional: carpeta donde dejar cada tamaño del icono en PNG (ampliado ×8 sin suavizar) para revisarlos a ojo.
    [string]$PreviewDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$PixelFormat = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
$AccentColor = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xB6, 0x27)   # #FFB627 (Accent del tema)
$IconSizes = @(16, 24, 32, 48, 64, 128, 256)

# Lee los píxeles BGRA de un bitmap 32bpp ARGB.
function Get-PixelBytes([System.Drawing.Bitmap]$Bitmap) {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $Bitmap.Width, $Bitmap.Height
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $PixelFormat)
    try {
        $bytes = New-Object byte[] ($data.Stride * $Bitmap.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        return @{ Bytes = $bytes; Stride = $data.Stride }
    }
    finally { $Bitmap.UnlockBits($data) }
}

# Escribe los píxeles BGRA en un bitmap 32bpp ARGB.
function Set-PixelBytes([System.Drawing.Bitmap]$Bitmap, [byte[]]$Bytes) {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $Bitmap.Width, $Bitmap.Height
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $PixelFormat)
    try { [System.Runtime.InteropServices.Marshal]::Copy($Bytes, 0, $data.Scan0, $Bytes.Length) }
    finally { $Bitmap.UnlockBits($data) }
}

function New-Canvas([int]$Width, [int]$Height) {
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height, $PixelFormat
    # 96 ppp explícitos: WPF usa los ppp del PNG para calcular su tamaño en pantalla.
    $bmp.SetResolution(96, 96)
    return $bmp
}

function New-HighQualityGraphics([System.Drawing.Bitmap]$Bitmap) {
    $g = [System.Drawing.Graphics]::FromImage($Bitmap)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    return $g
}

# Convierte el sticker (trazos negros + relleno blanco) en una máscara negra con alfa = alfa × (1 − luminancia).
# Devuelve el bitmap de la máscara y el rectángulo que ocupa el dibujo (para centrarlo sin depender del margen original).
function Get-LineArtMask([System.Drawing.Bitmap]$Original) {
    $w = $Original.Width
    $h = $Original.Height
    $src = New-Canvas $w $h
    $g = [System.Drawing.Graphics]::FromImage($src)
    try { $g.DrawImage($Original, 0, 0, $w, $h) } finally { $g.Dispose() }

    $px = Get-PixelBytes $src
    $bytes = $px.Bytes
    $stride = $px.Stride
    $minX = $w; $minY = $h; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $h; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $w; $x++) {
            $i = $row + $x * 4
            $lum = 0.299 * $bytes[$i + 2] + 0.587 * $bytes[$i + 1] + 0.114 * $bytes[$i]
            $alpha = [int][Math]::Round($bytes[$i + 3] * (255.0 - $lum) / 255.0)
            if ($alpha -lt 0) { $alpha = 0 } elseif ($alpha -gt 255) { $alpha = 255 }
            $bytes[$i] = 0; $bytes[$i + 1] = 0; $bytes[$i + 2] = 0; $bytes[$i + 3] = [byte]$alpha
            if ($alpha -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { throw "El logo '$Source' está vacío o es todo blanco." }
    Set-PixelBytes $src $bytes
    $bounds = New-Object System.Drawing.Rectangle $minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1)
    return @{ Bitmap = $src; Bounds = $bounds }
}

# Dibuja la máscara recoloreada con <Color> (conservando el alfa), escalada para caber en un cuadrado de
# <BoxSize> px y centrada en un lienzo transparente de <CanvasSize> px.
function New-LogoLayer($Mask, [int]$CanvasSize, [double]$BoxSize, [System.Drawing.Color]$Color) {
    $bounds = $Mask.Bounds
    $scale = [Math]::Min($BoxSize / $bounds.Width, $BoxSize / $bounds.Height)
    $dw = $bounds.Width * $scale
    $dh = $bounds.Height * $scale
    $dest = New-Object System.Drawing.RectangleF (($CanvasSize - $dw) / 2.0), (($CanvasSize - $dh) / 2.0), $dw, $dh

    # Matriz de color: RGB = color fijo, alfa = alfa de la máscara.
    $m = New-Object System.Drawing.Imaging.ColorMatrix
    $m.Matrix00 = 0; $m.Matrix11 = 0; $m.Matrix22 = 0; $m.Matrix33 = 1
    $m.Matrix40 = $Color.R / 255.0; $m.Matrix41 = $Color.G / 255.0; $m.Matrix42 = $Color.B / 255.0
    $attrs = New-Object System.Drawing.Imaging.ImageAttributes
    $attrs.SetColorMatrix($m)
    # Evita el borde semitransparente que deja el bicúbico al muestrear fuera del rectángulo de origen.
    $attrs.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

    $layer = New-Canvas $CanvasSize $CanvasSize
    $g = New-HighQualityGraphics $layer
    try {
        $points = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF $dest.Left, $dest.Top),
            (New-Object System.Drawing.PointF $dest.Right, $dest.Top),
            (New-Object System.Drawing.PointF $dest.Left, $dest.Bottom))
        $g.DrawImage($Mask.Bitmap, $points,
            (New-Object System.Drawing.RectangleF $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height),
            [System.Drawing.GraphicsUnit]::Pixel, $attrs)
    }
    finally {
        $g.Dispose()
        $attrs.Dispose()
    }
    return $layer
}

# Refuerza el alfa (gamma < 1) para que los trazos finos se vean en tamaños pequeños.
function Set-AlphaGamma([System.Drawing.Bitmap]$Bitmap, [double]$Gamma) {
    if ($Gamma -eq 1.0) { return }
    $px = Get-PixelBytes $Bitmap
    $bytes = $px.Bytes
    for ($i = 3; $i -lt $bytes.Length; $i += 4) {
        $a = $bytes[$i]
        if ($a -gt 0 -and $a -lt 255) {
            $bytes[$i] = [byte][Math]::Min(255, [Math]::Round(255.0 * [Math]::Pow($a / 255.0, $Gamma)))
        }
    }
    Set-PixelBytes $Bitmap $bytes
}

function New-RoundedRectPath([single]$X, [single]$Y, [single]$Size, [single]$Radius) {
    [single]$d = $Radius * 2
    [single]$far = $Size - $d
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([single]$X, [single]$Y, $d, $d, [single]180, [single]90)
    $path.AddArc([single]($X + $far), [single]$Y, $d, $d, [single]270, [single]90)
    $path.AddArc([single]($X + $far), [single]($Y + $far), $d, $d, [single]0, [single]90)
    $path.AddArc([single]$X, [single]($Y + $far), $d, $d, [single]90, [single]90)
    $path.CloseFigure()
    return $path
}

# Una imagen del icono: cuadrado redondeado ámbar + logo negro.
function New-IconImage($Mask, [int]$Size) {
    # Margen y tamaño del logo según el tamaño: en 16/24 px se aprovecha más el espacio para que se reconozca.
    $margin = if ($Size -le 16) { 0 } else { [Math]::Max(1, [Math]::Round($Size * 0.03)) }
    $logoFraction = switch ($Size) { 16 { 0.84 } 24 { 0.80 } 32 { 0.74 } default { 0.68 } }
    $alphaGamma = switch ($Size) { 16 { 0.3 } 24 { 0.45 } 32 { 0.7 } 48 { 0.85 } default { 1.0 } }

    $bmp = New-Canvas $Size $Size
    $g = New-HighQualityGraphics $bmp
    try {
        $square = $Size - 2 * $margin
        $path = New-RoundedRectPath $margin $margin $square ($square * 0.22)
        $brush = New-Object System.Drawing.SolidBrush $AccentColor
        try { $g.FillPath($brush, $path) } finally { $brush.Dispose(); $path.Dispose() }

        $layer = New-LogoLayer $Mask $Size ($Size * $logoFraction) ([System.Drawing.Color]::Black)
        try {
            Set-AlphaGamma $layer $alphaGamma
            $g.DrawImageUnscaled($layer, 0, 0)
        }
        finally { $layer.Dispose() }
    }
    finally { $g.Dispose() }
    return $bmp
}

# Quita el bloque pHYs que añade GDI+ (guarda 95,99 ppp por redondeo): sin él, WPF asume 96 ppp exactos y la imagen
# se dibuja al píxel. Cada bloque PNG = longitud (4, big-endian) + tipo (4) + datos + CRC (4).
function Remove-PngPhysChunk([byte[]]$Png) {
    $out = New-Object System.IO.MemoryStream
    try {
        $out.Write($Png, 0, 8)   # firma PNG
        $pos = 8
        while ($pos + 12 -le $Png.Length) {
            $len = ([int]$Png[$pos] -shl 24) -bor ([int]$Png[$pos + 1] -shl 16) -bor ([int]$Png[$pos + 2] -shl 8) -bor [int]$Png[$pos + 3]
            $type = [System.Text.Encoding]::ASCII.GetString($Png, $pos + 4, 4)
            $total = 12 + $len
            if ($len -lt 0 -or $pos + $total -gt $Png.Length) { throw 'PNG generado con un bloque no válido.' }
            if ($type -ne 'pHYs') { $out.Write($Png, $pos, $total) }
            $pos += $total
        }
        return , $out.ToArray()
    }
    finally { $out.Dispose() }
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$Bitmap) {
    $ms = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , (Remove-PngPhysChunk $ms.ToArray())
    }
    finally { $ms.Dispose() }
}

# Formato ICO: ICONDIR (reservado 0, tipo 1, n) + n × ICONDIRENTRY (16 bytes) + imágenes PNG.
function Write-IcoFile([string]$Path, $Entries) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    try {
        $bw.Write([uint16]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]$Entries.Count)
        $offset = 6 + 16 * $Entries.Count
        foreach ($e in $Entries) {
            $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }   # 0 = 256 px
            $bw.Write([byte]$dim)        # ancho
            $bw.Write([byte]$dim)        # alto
            $bw.Write([byte]0)           # colores en paleta
            $bw.Write([byte]0)           # reservado
            $bw.Write([uint16]1)         # planos
            $bw.Write([uint16]32)        # bits por píxel
            $bw.Write([uint32]$e.Png.Length)
            $bw.Write([uint32]$offset)
            $offset += $e.Png.Length
        }
        foreach ($e in $Entries) { $bw.Write([byte[]]$e.Png) }
        $bw.Flush()
        [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    }
    finally {
        $bw.Dispose()
        $ms.Dispose()
    }
}

function Save-Png([System.Drawing.Bitmap]$Bitmap, [string]$Path) {
    [System.IO.File]::WriteAllBytes($Path, (ConvertTo-PngBytes $Bitmap))
}

# Copia ampliada sin suavizar (para revisar los tamaños pequeños a ojo).
function Save-Preview([System.Drawing.Bitmap]$Bitmap, [string]$Path, [int]$Zoom) {
    $big = New-Canvas ($Bitmap.Width * $Zoom) ($Bitmap.Height * $Zoom)
    $g = [System.Drawing.Graphics]::FromImage($big)
    try {
        $g.Clear([System.Drawing.Color]::FromArgb(255, 0x16, 0x1A, 0x22))
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $g.DrawImage($Bitmap, 0, 0, $big.Width, $big.Height)
    }
    finally { $g.Dispose() }
    try { Save-Png $big $Path } finally { $big.Dispose() }
}

# --- Principal ---
# (En Windows PowerShell 5.1 $PSScriptRoot no siempre está disponible en los valores por defecto de param.)
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$assetsDir = Join-Path $scriptDir '..\src\PalitoLauncher\Assets'
if (-not $Source) { $Source = Join-Path $assetsDir 'logo_source.png' }
if (-not $OutDir) { $OutDir = $assetsDir }
$Source = [System.IO.Path]::GetFullPath($Source)
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "No se encuentra el logo original: $Source" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if ($PreviewDir) { New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null }

$original = New-Object System.Drawing.Bitmap $Source
try {
    Write-Host "Origen: $Source ($($original.Width)×$($original.Height))"
    $mask = Get-LineArtMask $original
}
finally { $original.Dispose() }

try {
    Write-Host "Dibujo recortado: $($mask.Bounds.Width)×$($mask.Bounds.Height) en ($($mask.Bounds.X), $($mask.Bounds.Y))"

    # logo.png / logo_white.png: lienzo 256×256 con 16 px de margen.
    foreach ($variant in @(
            @{ Name = 'logo.png'; Color = [System.Drawing.Color]::Black },
            @{ Name = 'logo_white.png'; Color = [System.Drawing.Color]::White })) {
        $layer = New-LogoLayer $mask 256 224 $variant.Color
        try {
            $path = Join-Path $OutDir $variant.Name
            Save-Png $layer $path
            Write-Host ("  {0,-15} {1,8:N0} bytes" -f $variant.Name, (Get-Item -LiteralPath $path).Length)
        }
        finally { $layer.Dispose() }
    }

    # app.ico con todos los tamaños como PNG.
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($size in $IconSizes) {
        $img = New-IconImage $mask $size
        try {
            $entries.Add(@{ Size = $size; Png = (ConvertTo-PngBytes $img) })
            if ($PreviewDir) {
                $zoom = [Math]::Max(1, [int](256 / $size))
                Save-Preview $img (Join-Path $PreviewDir ("icon-{0}.png" -f $size)) $zoom
            }
        }
        finally { $img.Dispose() }
    }
    $icoPath = Join-Path $OutDir 'app.ico'
    Write-IcoFile $icoPath $entries
}
finally { $mask.Bitmap.Dispose() }

# Comprobación de la cabecera del .ico (00 00 01 00 + número de imágenes).
$head = [System.IO.File]::ReadAllBytes($icoPath)
$count = [BitConverter]::ToUInt16($head, 4)
if ($head[0] -ne 0 -or $head[1] -ne 0 -or $head[2] -ne 1 -or $head[3] -ne 0 -or $count -ne $IconSizes.Count) {
    throw "app.ico generado con una cabecera no válida."
}
Write-Host ("  {0,-15} {1,8:N0} bytes ({2} tamaños: {3})" -f 'app.ico', $head.Length, $count, ($IconSizes -join ', '))
Write-Host "Listo: $OutDir"
