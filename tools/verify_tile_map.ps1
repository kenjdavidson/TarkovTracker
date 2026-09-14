# End-to-end check of the generated tile map: run known game coordinates through the
# exact same maths the app uses, then crop that spot out of the tile grid and mark it.
# If the marker lands on the matching room, the transform, crop and tile placement agree.

param(
    [string]$Map = 'lab',
    [string]$Layer = 'technical',
    [int]$Context = 2   # tiles of surrounding context to include
)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$cfg = (Get-Content (Join-Path $root 'Config\maps.json') -Raw | ConvertFrom-Json).$Map.svg
$svgFile = Join-Path $root ('Maps\' + $cfg.file)
$svgText = Get-Content $svgFile -Raw

$viewBox = ([regex]::Match($svgText, 'viewBox="([^"]+)"').Groups[1].Value -split '\s+')
$vbSize = [double]$viewBox[2]
$scale = [Math]::Pow(2, $cfg.nativeZoom)

# Mirrors GameCoordsToMapPixels + ConvertGameToNormalizedMapWithTransform for rotation 270.
function ProjectToSvg([double]$gameX, [double]$gameZ) {
    $px = $scale * ($cfg.transform[0] * $gameZ + $cfg.transform[1])
    $py = $scale * ($cfg.transform[2] * $gameX + $cfg.transform[3])
    return @{
        SvgX = ($px / $cfg.mapPixelSize) * $vbSize
        SvgY = ($py / $cfg.mapPixelSize) * $vbSize
    }
}

$refs = @(
    @{ Name = 'Sewage Conduit Pump Button'; X = -136.76; Z = -254.51 }
    @{ Name = 'Hangar Gate Switch';         X = -170.18; Z = -281.51 }
    @{ Name = 'Med Elevator Power Button';  X = -124.76; Z = -313.81 }
    @{ Name = 'Cargo Elevator Power Button'; X = -121.01; Z = -353.55 }
    @{ Name = 'Main Elevator Power Button'; X = -271.44; Z = -366.10 }
)

# Tile placements straight out of the generated SVG, so this tests the real file.
$tilePlacements = @{}
foreach ($m in [regex]::Matches($svgText, '<image href="([^"]+)" x="([\d.]+)" y="([\d.]+)"')) {
    $tilePlacements[$m.Groups[1].Value] = @{
        X = [double]$m.Groups[2].Value
        Y = [double]$m.Groups[3].Value
    }
}
'{0} tile placements parsed from {1}' -f $tilePlacements.Count, $cfg.file

$tileUnit = 175.0
$cropUnits = $tileUnit * (1 + 2 * $Context)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Magenta), 3
$font = New-Object System.Drawing.Font 'Arial', 14, ([System.Drawing.FontStyle]::Bold)
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Magenta)

$outputs = @()
''
foreach ($r in $refs) {
    $p = ProjectToSvg $r.X $r.Z
    $centreCol = [int][Math]::Floor($p.SvgX / $tileUnit)
    $centreRow = [int][Math]::Floor($p.SvgY / $tileUnit)

    # Native tile pixels differ from the 175-unit geometry, so scale when compositing.
    $sample = $tilePlacements.Keys | Select-Object -First 1
    $probe = [System.Drawing.Bitmap]::FromFile((Join-Path $root ('Maps\' + $sample.Replace('/', '\'))))
    $native = $probe.Width
    $probe.Dispose()
    $pxPerUnit = $native / $tileUnit

    $size = [int]($cropUnits * $pxPerUnit)
    $canvas = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 25, 25, 25))

    $originUnitX = ($centreCol - $Context) * $tileUnit
    $originUnitY = ($centreRow - $Context) * $tileUnit
    foreach ($key in $tilePlacements.Keys) {
        if ($key -notlike "*/$Layer/*") { continue }
        $place = $tilePlacements[$key]
        if ($place.X -lt $originUnitX -or $place.X -ge ($originUnitX + $cropUnits)) { continue }
        if ($place.Y -lt $originUnitY -or $place.Y -ge ($originUnitY + $cropUnits)) { continue }

        $img = [System.Drawing.Bitmap]::FromFile((Join-Path $root ('Maps\' + $key.Replace('/', '\'))))
        $g.DrawImage($img, [int](($place.X - $originUnitX) * $pxPerUnit), [int](($place.Y - $originUnitY) * $pxPerUnit), $native, $native)
        $img.Dispose()
    }

    $markX = ($p.SvgX - $originUnitX) * $pxPerUnit
    $markY = ($p.SvgY - $originUnitY) * $pxPerUnit
    $g.DrawEllipse($pen, ($markX - 16), ($markY - 16), 32, 32)
    $g.DrawLine($pen, ($markX - 26), $markY, ($markX - 20), $markY)
    $g.DrawLine($pen, ($markX + 20), $markY, ($markX + 26), $markY)
    $g.DrawString($r.Name, $font, $brush, ($markX + 30), ($markY - 9))

    $file = Join-Path $PSScriptRoot ('verify-' + ($r.Name -replace '[^a-zA-Z]', '') + '.png')
    $canvas.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $canvas.Dispose()

    '{0,-30} svg ({1,7:N1},{2,7:N1})  tile {3},{4}' -f $r.Name, $p.SvgX, $p.SvgY, $centreCol, $centreRow
    $outputs += $file
}
''
$outputs | ForEach-Object { 'saved {0}' -f $_ }
