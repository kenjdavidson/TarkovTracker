$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = Join-Path (Split-Path $toolsDir -Parent) 'Config'
$toolsDataDir = Join-Path $toolsDir 'data'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

function Update-JsonMapSection {
    param(
        [string]$FilePath,
        [string]$MapName,
        [string]$PropertyName,
        [object]$Data
    )

    $root = Get-Content $FilePath -Raw | ConvertFrom-Json
    $updated = $false
    foreach ($map in $root.data.maps) {
        if ($map.name -eq $MapName) {
            $map.$PropertyName = $Data
            $updated = $true
            break
        }
    }
    if (-not $updated) {
        $root.data.maps += [pscustomobject]@{
            name = $MapName
            $PropertyName = $Data
        }
    }
    $root | ConvertTo-Json -Depth 20 | Set-Content $FilePath -Encoding utf8
}

$terminal = Get-TarkovJsonMap -NormalizedName 'terminal'
if ($null -eq $terminal) {
    throw 'Terminal map not found in json.tarkov.dev maps dump.'
}

$spawnCount = @($terminal.spawns).Count
$extractCount = @($terminal.extracts).Count
$transitCount = @($terminal.transits).Count
Write-Output "API spawns: $spawnCount"
Write-Output "API extracts: $extractCount"
Write-Output "API transits: $transitCount"

$spawns = @()
foreach ($s in @($terminal.spawns)) {
    if ($null -eq $s -or $null -eq $s.position) { continue }
    $spawns += [ordered]@{
        zoneName   = $s.zoneName
        sides      = @($s.sides)
        categories = @($s.categories)
        position   = [ordered]@{
            x = [double]$s.position.x
            y = [double]$s.position.y
            z = [double]$s.position.z
        }
    }
}

$extracts = @()
foreach ($e in @($terminal.extracts)) {
    if ($null -eq $e) { continue }
    $entry = [ordered]@{
        id       = $e.id
        name     = $e.name
        faction  = $e.faction
        switches = @()
        position = $null
    }
    if ($e.position) {
        $entry.position = [ordered]@{
            x = [double]$e.position.x
            y = [double]$e.position.y
            z = [double]$e.position.z
        }
    }
    foreach ($sw in @($e.switches)) {
        if ($null -eq $sw) { continue }
        $entry.switches += [ordered]@{ name = $sw.name }
    }
    if ($e.transferItem -and $e.transferItem.item) {
        $entry.transferItem = [ordered]@{
            item  = [ordered]@{ name = $e.transferItem.item.name }
            count = $e.transferItem.count
        }
    }
    else {
        $entry.transferItem = $null
    }
    $extracts += $entry
}

$transits = @()
foreach ($t in @($terminal.transits)) {
    if ($null -eq $t) { continue }
    $pos = $null
    if ($t.position) {
        $pos = [ordered]@{
            x = [double]$t.position.x
            y = [double]$t.position.y
            z = [double]$t.position.z
        }
    }
    $transits += [ordered]@{
        id          = $t.id
        description = $t.description
        conditions  = $t.conditions
        position    = $pos
    }
}

if ($spawns.Count -gt 0) {
    Update-JsonMapSection -FilePath (Join-Path $configDir 'tarkov_spawns_raw.json') -MapName 'Terminal' -PropertyName 'spawns' -Data $spawns
}
else {
    Write-Warning 'json.tarkov.dev has no Terminal spawns; leaving Config/tarkov_spawns_raw.json unchanged.'
}

if ($extracts.Count -gt 0) {
    Update-JsonMapSection -FilePath (Join-Path $configDir 'tarkov_extracts_raw.json') -MapName 'Terminal' -PropertyName 'extracts' -Data $extracts
}
else {
    Write-Warning 'json.tarkov.dev has no Terminal extracts; leaving Config/tarkov_extracts_raw.json unchanged.'
}

if ($transits.Count -gt 0) {
    Update-JsonMapSection -FilePath (Join-Path $configDir 'tarkov_transits_raw.json') -MapName 'Terminal' -PropertyName 'transits' -Data $transits
}
else {
    Write-Warning 'json.tarkov.dev has no Terminal transits; leaving Config/tarkov_transits_raw.json unchanged.'
}

[ordered]@{
    tdevId       = $terminal.tarkovDataId
    description  = $terminal.description
    enemies      = @($terminal.enemies)
    raidDuration = $terminal.raidDuration
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $toolsDataDir 'terminal_map_meta.json') -Encoding utf8

Write-Output 'Updated Terminal map data from json.tarkov.dev.'
