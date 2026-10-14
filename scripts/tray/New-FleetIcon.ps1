<#
.SYNOPSIS
    Draws the Agent Fleet icon (a hub joined to two machines) and writes a multi-size .ico plus a preview .png.
.DESCRIPTION
    Pure System.Drawing, no external tools. Each size is drawn at 8x and scaled down so small sizes stay clean.
    The ICO holds PNG frames (supported by Windows Vista and later).
#>
param(
    [string]$OutDir = $PSScriptRoot,
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
)

Add-Type -AssemblyName System.Drawing

function New-Frame([int]$Size) {
    $scale = 8
    $big = $Size * $scale
    $bitmap = New-Object System.Drawing.Bitmap $big, $big, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square tile.
    $r = $big * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($big - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($big - 2 * $r - 1, $big - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $big - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#0F172A'))), $path)

    $hub = @{ X = 0.50 * $big; Y = 0.33 * $big }
    $left = @{ X = 0.27 * $big; Y = 0.70 * $big }
    $right = @{ X = 0.73 * $big; Y = 0.70 * $big }

    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#64748B')), ([single]($big * 0.075))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, [single]$hub.X, [single]$hub.Y, [single]$left.X, [single]$left.Y)
    $g.DrawLine($pen, [single]$hub.X, [single]$hub.Y, [single]$right.X, [single]$right.Y)

    function Dot($g, $c, $radius, $colour) {
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($colour))
        $g.FillEllipse($brush, [single]($c.X - $radius), [single]($c.Y - $radius), [single](2 * $radius), [single](2 * $radius))
    }
    Dot $g $hub ($big * 0.165) '#F8FAFC'
    Dot $g $left ($big * 0.125) '#38BDF8'
    Dot $g $right ($big * 0.125) '#38BDF8'
    $g.Dispose()

    $small = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $sg.DrawImage($bitmap, 0, 0, $Size, $Size)
    $sg.Dispose()
    $bitmap.Dispose()
    return $small
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$frames = foreach ($size in $Sizes) {
    $bitmap = New-Frame $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bitmap.Save((Join-Path $OutDir 'AgentFleet-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($size -eq 32) { $bitmap.Save((Join-Path $OutDir 'AgentFleet-32.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($size -eq 16) { $bitmap.Save((Join-Path $OutDir 'AgentFleet-16.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frame.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $w.Write($frame.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'AgentFleet.ico'), $ico.ToArray())
Write-Host ("Wrote {0} ({1} sizes, {2} bytes)" -f (Join-Path $OutDir 'AgentFleet.ico'), $frames.Count, $ico.Length)
