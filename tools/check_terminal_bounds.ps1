$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

# Check static maps.json from github we already have - bounds are [[463,-580],[-433,475]]

$t = Get-TarkovJsonMap -NormalizedName 'terminal'
if ($null -eq $t) {
    throw 'Terminal map not found in json.tarkov.dev maps dump.'
}

$spawns = @($t.spawns | Where-Object { $_ -and $_.position })
if ($spawns.Count -eq 0) {
    throw 'json.tarkov.dev has no Terminal spawns to check bounds from.'
}

$xs = $spawns | ForEach-Object { [double]$_.position.x }
$zs = $spawns | ForEach-Object { [double]$_.position.z }
$minX = ($xs | Measure-Object -Minimum).Minimum
$maxX = ($xs | Measure-Object -Maximum).Maximum
$minZ = ($zs | Measure-Object -Minimum).Minimum
$maxZ = ($zs | Measure-Object -Maximum).Maximum
Write-Output "Spawn extents: X [$minX, $maxX], Z [$minZ, $maxZ]"

# Test transform: tarkov.dev uses transform [0.20, 0, 0.20, 0] at zoom 0
# Leaflet: px = 2^0 * (0.20 * rotLng + 0), py = 2^0 * (-0.20 * rotLat + 0)
# With rotation 180: rotLat = -lat, rotLng = -lng (need to verify rotation formula)

function Get-Pixels($x, $z, $rotation) {
    $lat = $x; $lng = $z
    if ($rotation -ne 0) {
        $angle = $rotation * [Math]::PI / 180.0
        $cos = [Math]::Cos($angle); $sin = [Math]::Sin($angle)
        $rotLng = $lng * $cos - $lat * $sin
        $rotLat = $lng * $sin + $lat * $cos
        $lat = $rotLat; $lng = $rotLng
    }
    $px = 0.20 * $lng
    $py = -0.20 * $lat
    return @($px, $py)
}

Write-Output ''
Write-Output 'Pixel extents with CURRENT bounds (tarkov.dev):'
foreach ($corner in @(@(463,-580), @(-433,-580), @(463,475), @(-433,475))) {
    $p = Get-Pixels $corner[0] $corner[1] 180
    Write-Output ("  game ({0},{1}) -> px ({2:F1},{3:F1})" -f $corner[0],$corner[1],$p[0],$p[1])
}

$sampleSpawn = $spawns[0].position
$pSpawn = Get-Pixels $sampleSpawn.x $sampleSpawn.z 180
Write-Output ("Sample spawn ({0},{1}) -> px ({2:F1},{3:F1})" -f $sampleSpawn.x,$sampleSpawn.z,$pSpawn[0],$pSpawn[1])

$padX = 50; $padZ = 50
$newBounds = @(@($maxX + $padX, $minZ - $padZ), @($minX - $padX, $maxZ + $padZ))
Write-Output ''
Write-Output "Proposed bounds from spawns: [[$($newBounds[0][0]), $($newBounds[0][1])], [$($newBounds[1][0]), $($newBounds[1][1])]]"
