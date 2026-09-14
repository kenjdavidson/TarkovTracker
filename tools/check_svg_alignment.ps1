# Compare each map drawing's aspect ratio against the aspect implied by its coordinate
# bounds. A genuine, georeferenced drawing matches; a mismatch means the artwork and the
# bounds describe different areas, so positions cannot line up no matter how they are fit.

$root = Split-Path -Parent $PSScriptRoot
$config = Get-Content (Join-Path $root 'Config\maps.json') -Raw | ConvertFrom-Json

'{0,-20} {1,-14} {2,9} {3,9} {4,8}  {5}' -f 'map', 'drawing', 'svg', 'bounds', 'ratio', 'verdict'
foreach ($prop in $config.PSObject.Properties) {
    $svgCfg = $prop.Value.svg
    if (-not $svgCfg -or -not $svgCfg.file) { continue }

    $path = Join-Path $root ('Maps\' + $svgCfg.file)
    if (-not (Test-Path $path)) { continue }

    # Tile-based maps draw onto the square tile world rather than the bounds box, so
    # comparing aspect ratios says nothing about them. Their georeferencing comes from
    # the published transform instead.
    if ($svgCfg.mapPixelSize -gt 0) {
        '{0,-20} {1,-14} {2,9} {3,9} {4,8}  {5}' -f $prop.Name, $svgCfg.file, '-', '-', '-', 'tile map (n/a)'
        continue
    }

    $head = Get-Content $path -TotalCount 5 -ErrorAction SilentlyContinue
    $match = [regex]::Match(($head -join ' '), 'viewBox="([^"]+)"')
    if (-not $match.Success) {
        '{0,-20} {1,-14} {2,9} {3,9} {4,8}  {5}' -f $prop.Name, $svgCfg.file, '-', '-', '-', 'no viewBox'
        continue
    }

    $vb = $match.Groups[1].Value -split '\s+'
    $svgAspect = [double]$vb[2] / [double]$vb[3]

    $bounds = if ($svgCfg.svgBounds) { $svgCfg.svgBounds } else { $svgCfg.bounds }
    $spanGameX = [Math]::Abs([double]$bounds[0][0] - [double]$bounds[1][0])
    $spanGameZ = [Math]::Abs([double]$bounds[0][1] - [double]$bounds[1][1])

    # coordinateRotation 90/270 swaps which game axis drives which screen axis.
    $rot = [int]$svgCfg.coordinateRotation
    $boundsAspect = if ($rot -eq 90 -or $rot -eq 270) { $spanGameZ / $spanGameX } else { $spanGameX / $spanGameZ }

    $ratio = $svgAspect / $boundsAspect
    $verdict = if ([Math]::Abs($ratio - 1.0) -lt 0.02) { 'aligned' }
               elseif ([Math]::Abs($ratio - 1.0) -lt 0.08) { 'slightly off' }
               else { 'MISMATCH' }

    '{0,-20} {1,-14} {2,9:N3} {3,9:N3} {4,8:N3}  {5}' -f
        $prop.Name, $svgCfg.file, $svgAspect, $boundsAspect, $ratio, $verdict
}
