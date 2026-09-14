$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = Join-Path (Split-Path $toolsDir -Parent) 'Config'
$spawnsPath = Join-Path $configDir 'tarkov_spawns_raw.json'
$outputPath = Join-Path $configDir 'tarkov_boss_spawn_markers.json'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

$maps = @(Get-TarkovJsonMaps)

$spawnsRoot = Get-Content $spawnsPath -Raw | ConvertFrom-Json
$spawnsByMap = @{}
foreach ($map in $spawnsRoot.data.maps) {
    $key = Resolve-TarkovAppMapKey $map.name
    if (-not $spawnsByMap.ContainsKey($key)) {
        $spawnsByMap[$key] = @()
    }
    $spawnsByMap[$key] += $map.spawns
}

$appMaps = @(Get-TarkovAppBossMapKeys)
$byMap = @{}

foreach ($map in $maps) {
    $mapKey = Resolve-TarkovAppMapKey $map.name
    if ($appMaps -notcontains $mapKey) { continue }
    if (-not $map.bosses) { continue }

    $mapSpawns = if ($spawnsByMap.ContainsKey($mapKey)) { $spawnsByMap[$mapKey] } else { @() }

    foreach ($bossEntry in @($map.bosses)) {
        if ($null -eq $bossEntry) { continue }
        foreach ($loc in @($bossEntry.spawnLocations)) {
            if ($null -eq $loc) { continue }
            $spawnKey = $loc.spawnKey
            $match = $mapSpawns | Where-Object {
                $_.zoneName -eq $spawnKey -and $_.categories -contains 'boss'
            } | Select-Object -First 1

            if (-not $match) {
                $match = $mapSpawns | Where-Object { $_.zoneName -eq $spawnKey } | Select-Object -First 1
            }

            if (-not $match -or -not $match.position) {
                Write-Warning "No spawn position for $($map.name) / $($bossEntry.boss.name) / $spawnKey"
                continue
            }

            $marker = [ordered]@{
                bossName       = $bossEntry.boss.name
                normalizedName = $bossEntry.boss.normalizedName
                locationName   = $loc.name
                zoneName       = $spawnKey
                spawnChance    = [double]$loc.chance
                x              = [double]$match.position.x
                y              = [double]$match.position.y
                z              = [double]$match.position.z
            }

            if (-not $byMap.ContainsKey($mapKey)) {
                $byMap[$mapKey] = New-Object System.Collections.Generic.List[object]
            }
            $byMap[$mapKey].Add($marker)
        }
    }
}

$sorted = [ordered]@{}
foreach ($key in ($byMap.Keys | Sort-Object)) {
    $sorted[$key] = @($byMap[$key] | Sort-Object bossName, locationName, zoneName)
}

$sorted | ConvertTo-Json -Depth 5 | Set-Content $outputPath -Encoding utf8

Write-Output 'Boss spawn markers built:'
foreach ($key in ($sorted.Keys | Sort-Object)) {
    Write-Output ("  {0}: {1}" -f $key, $sorted[$key].Count)
}
Write-Output ("Total: {0}" -f (($sorted.Values | ForEach-Object { $_.Count }) | Measure-Object -Sum).Sum)
