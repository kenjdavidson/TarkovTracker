# Refreshes raw spawn points from json.tarkov.dev (Night Factory cultists merge onto Factory).
# Usage: powershell -ExecutionPolicy Bypass -File tools\fetch-boss-data.ps1
# Then run build_boss_spawn_markers.ps1 to join boss zones to these coordinates.

$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = Join-Path $toolsDir '..\Config'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

$maps = @(Get-TarkovJsonMaps)
$appMaps = @(Get-TarkovAppBossMapKeys)
$spawnsByAppKey = @{}
$bossSpawnCounts = @{}

foreach ($map in $maps) {
    $appKey = Resolve-TarkovAppMapKey $map.normalizedName
    if ($appMaps -notcontains $appKey) { continue }

    if (-not $spawnsByAppKey.ContainsKey($appKey)) {
        $spawnsByAppKey[$appKey] = @()
    }

    foreach ($spawn in @($map.spawns)) {
        if ($null -eq $spawn -or $null -eq $spawn.position) { continue }
        $spawnsByAppKey[$appKey] += [PSCustomObject]@{
            zoneName   = $spawn.zoneName
            sides      = @($spawn.sides)
            categories = @($spawn.categories)
            position   = [PSCustomObject]@{
                x = [double]$spawn.position.x
                y = [double]$spawn.position.y
                z = [double]$spawn.position.z
            }
        }
    }

    $bossCount = @($map.spawns | Where-Object { $_.categories -contains 'boss' }).Count
    if ($bossCount -gt 0) {
        if (-not $bossSpawnCounts.ContainsKey($appKey)) {
            $bossSpawnCounts[$appKey] = 0
        }
        $bossSpawnCounts[$appKey] += $bossCount
    }
}

function Get-SpawnDedupKey($spawn) {
    $x = [math]::Round([double]$spawn.position.x, 2)
    $y = [math]::Round([double]$spawn.position.y, 2)
    $z = [math]::Round([double]$spawn.position.z, 2)
    return ('{0}|{1}|{2}|{3}' -f $spawn.zoneName, $x, $y, $z)
}

$spawnsRoot = @{ data = @{ maps = @() } }

foreach ($appKey in ($spawnsByAppKey.Keys | Sort-Object)) {
    $deduped = @()
    $seen = @{}

    foreach ($spawn in $spawnsByAppKey[$appKey]) {
        $key = Get-SpawnDedupKey $spawn
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $deduped += $spawn
    }

    $spawnsRoot.data.maps += [PSCustomObject]@{
        name   = Get-TarkovAppMapDisplayName $appKey
        spawns = $deduped
    }
}

$spawnsRoot | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $configDir 'tarkov_spawns_raw.json') -Encoding UTF8

Write-Output 'Updated Config/tarkov_spawns_raw.json'
Write-Output ''
Write-Output 'Boss spawn points per map (should match tarkov.dev boss toggle):'
$bossSpawnCounts.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Output ('  {0}: {1}' -f $_.Name, $_.Value)
}
Write-Output 'Run build_boss_spawn_markers.ps1 next to refresh Config/tarkov_boss_spawn_markers.json.'
