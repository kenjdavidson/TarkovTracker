# Build a map from a tarkov.dev raster tile pyramid.
#
# These tile sets are georeferenced by the transform published in tarkov.dev's maps.json,
# so marker and player positions come out right with no hand-fitted bounds.
#
# Rather than stitching one huge PNG per floor, the generated SVG references the tiles
# directly as a grid of <image> elements. PrepareSvgForWebView rewrites those relative
# paths onto the map asset virtual host, so the tiles keep their native resolution and
# only the tiles holding content need to ship.
#
# Geometry uses the tileSize from tarkov.dev's config (a display size, not the tiles'
# native pixel size), because that is the unit their transform is expressed in. The
# canvas is cropped to the content and squared off, since
# ConvertGameToNormalizedMapWithTransform divides both axes by a single mapPixelSize.
# The crop is folded back into the transform margins so coordinates still land correctly.

param(
    [Parameter(Mandatory = $true)][string]$TileBase,
    [Parameter(Mandatory = $true)][string[]]$Layers,
    [Parameter(Mandatory = $true)][string[]]$LayerIds,
    [Parameter(Mandatory = $true)][string]$OutputName,
    [int]$Zoom = 5,
    [int]$TileSize = 175,
    [double[]]$Transform = @(0.575, 281.2, 0.575, 193.7),
    [int]$EmptyMaxBytes = 500,
    [double]$Overlap = 0.5
)

Add-Type -AssemblyName System.Net.Http

if ($Layers.Count -ne $LayerIds.Count) { throw 'Layers and LayerIds must have the same length' }

$root = Split-Path -Parent $PSScriptRoot
$mapsFolder = Join-Path $root 'Maps'
$assetName = $OutputName.ToLower()
$assetFolder = Join-Path $mapsFolder $assetName
if (Test-Path $assetFolder) { Remove-Item $assetFolder -Recurse -Force }

$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(60)
$tilesPerSide = [int][Math]::Pow(2, $Zoom)

# --- find the tiles that hold content (filler tiles are a tiny transparent PNG) ---
$byLayer = @{}
$allCols = @(); $allRows = @()
foreach ($layer in $Layers) {
    $tiles = @()
    for ($col = 0; $col -lt $tilesPerSide; $col++) {
        $pending = @()
        for ($row = 0; $row -lt $tilesPerSide; $row++) {
            $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Head), "$TileBase/$layer/$Zoom/$col/$row.png"
            $pending += [pscustomobject]@{ Col = $col; Row = $row; Task = $http.SendAsync($req) }
        }
        foreach ($item in $pending) {
            try {
                $resp = $item.Task.GetAwaiter().GetResult()
                if ($resp.IsSuccessStatusCode -and $resp.Content.Headers.ContentLength -gt $EmptyMaxBytes) {
                    $tiles += [pscustomobject]@{ Col = $item.Col; Row = $item.Row }
                    $allCols += $item.Col; $allRows += $item.Row
                }
            } catch { }
        }
    }
    $byLayer[$layer] = $tiles
    '{0,-12} {1,4} tiles with content' -f $layer, $tiles.Count
}

if ($allCols.Count -eq 0) { throw 'no tiles contained any content' }

# --- square crop covering every layer, aligned to tile edges ---
$minCol = ($allCols | Measure-Object -Minimum).Minimum
$maxCol = ($allCols | Measure-Object -Maximum).Maximum
$minRow = ($allRows | Measure-Object -Minimum).Minimum
$maxRow = ($allRows | Measure-Object -Maximum).Maximum

$span = [int][Math]::Max(($maxCol - $minCol + 1), ($maxRow - $minRow + 1))
$cropCol = [int]($minCol - [Math]::Floor(($span - ($maxCol - $minCol + 1)) / 2))
$cropRow = [int]($minRow - [Math]::Floor(($span - ($maxRow - $minRow + 1)) / 2))
$cropCol = [int][Math]::Max(0, [Math]::Min($cropCol, $tilesPerSide - $span))
$cropRow = [int][Math]::Max(0, [Math]::Min($cropRow, $tilesPerSide - $span))

$side = $span * $TileSize
''
'content cols {0}-{1}, rows {2}-{3}  ->  {4}x{4} tile crop at ({5},{6}), canvas {7} units' -f
    $minCol, $maxCol, $minRow, $maxRow, $span, $cropCol, $cropRow, $side

# --- download the tiles that survive the crop ---
$totalBytes = 0
foreach ($layer in $Layers) {
    New-Item -ItemType Directory -Force -Path (Join-Path $assetFolder $layer) | Out-Null
    $kept = $byLayer[$layer] | Where-Object {
        $_.Col -ge $cropCol -and $_.Col -lt ($cropCol + $span) -and
        $_.Row -ge $cropRow -and $_.Row -lt ($cropRow + $span)
    }

    $batch = @()
    foreach ($t in $kept) {
        $batch += [pscustomobject]@{
            Tile = $t
            Task = $http.GetByteArrayAsync("$TileBase/$layer/$Zoom/$($t.Col)/$($t.Row).png")
        }
    }
    $layerBytes = 0
    foreach ($item in $batch) {
        $dest = Join-Path $assetFolder "$layer\$($item.Tile.Col)-$($item.Tile.Row).png"
        $bytes = $item.Task.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllBytes($dest, $bytes)
        $layerBytes += $bytes.Length
    }
    $totalBytes += $layerBytes
    '  {0,-12} {1,4} tiles  {2,7:N1} MB' -f $layer, $kept.Count, ($layerBytes / 1MB)
}
'  {0,-12} {1,16:N1} MB total' -f '', ($totalBytes / 1MB)

# --- wrapper SVG: one group per floor, ids matching Config/map_levels.json ---
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.1" viewBox="0 0 ' + $side + ' ' + $side + '">')
$boxSize = ([double]$TileSize + $Overlap).ToString([System.Globalization.CultureInfo]::InvariantCulture)
for ($i = 0; $i -lt $Layers.Count; $i++) {
    $layer = $Layers[$i]
    [void]$sb.AppendLine('  <g id="' + $LayerIds[$i] + '">')
    foreach ($t in ($byLayer[$layer] | Sort-Object Col, Row)) {
        if ($t.Col -lt $cropCol -or $t.Col -ge ($cropCol + $span)) { continue }
        if ($t.Row -lt $cropRow -or $t.Row -ge ($cropRow + $span)) { continue }
        $x = ($t.Col - $cropCol) * $TileSize
        $y = ($t.Row - $cropRow) * $TileSize
        [void]$sb.AppendLine('    <image href="' + $assetName + '/' + $layer + '/' + $t.Col + '-' + $t.Row +
            '.png" x="' + $x + '" y="' + $y + '" width="' + $boxSize + '" height="' + $boxSize + '" />')
    }
    [void]$sb.AppendLine('  </g>')
}
[void]$sb.AppendLine('</svg>')

$svgPath = Join-Path $mapsFolder "$OutputName.svg"
[System.IO.File]::WriteAllText($svgPath, $sb.ToString())
'  wrote {0} ({1:N0} bytes)' -f $svgPath, (Get-Item $svgPath).Length

# --- config values: fold the crop into the transform margins ---
$scale = [Math]::Pow(2, $Zoom)
$marginX = $Transform[1] - ($cropCol * $TileSize) / $scale
$marginY = $Transform[3] - ($cropRow * $TileSize) / $scale

''
'Config/maps.json values:'
'  "transform": [{0}, {1}, {2}, {3}],' -f $Transform[0], [Math]::Round($marginX, 4), $Transform[2], [Math]::Round($marginY, 4)
'  "nativeZoom": {0},' -f $Zoom
'  "mapPixelSize": {0},' -f $side
$http.Dispose()
