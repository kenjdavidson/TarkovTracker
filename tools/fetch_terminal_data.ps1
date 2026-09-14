$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsDataDir = Join-Path $toolsDir 'data'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

$terminal = Get-TarkovJsonMap -NormalizedName 'terminal'
if ($null -eq $terminal) {
    throw 'Terminal map not found in json.tarkov.dev maps dump.'
}

$extractCount = @($terminal.extracts).Count
$transitCount = @($terminal.transits).Count
Write-Output "Terminal extracts: $extractCount"
Write-Output "Terminal transits: $transitCount"
Write-Output "Enemies: $(@($terminal.enemies) -join ', ')"
foreach ($extract in @($terminal.extracts)) {
    if ($null -eq $extract) { continue }
    Write-Output "  $($extract.name) [$($extract.faction)]"
}

$catalog = Get-TarkovLiveTaskCatalog
$questMarkers = 0
foreach ($task in @($catalog.Tasks)) {
    foreach ($obj in @($task.objectives)) {
        foreach ($loc in @($obj.zones)) {
            if ($null -eq $loc) { continue }
            if ([string]$loc.normalizedName -ne 'terminal') { continue }
            $questMarkers++
            Write-Output "Quest: $($task.name) - $($obj.description)"
        }
    }
}
Write-Output "Total terminal quest markers: $questMarkers"

$snapshot = [ordered]@{
    name           = [string]$terminal.name
    normalizedName = [string]$terminal.normalizedName
    tarkovDataId   = $terminal.tarkovDataId
    description    = [string]$terminal.description
    enemies        = @($terminal.enemies)
    raidDuration   = $terminal.raidDuration
    extracts       = @($terminal.extracts)
    transits       = @($terminal.transits)
}
$snapshot | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $toolsDataDir 'terminal_api_snapshot.json') -Encoding utf8
