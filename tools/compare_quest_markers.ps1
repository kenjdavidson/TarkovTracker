$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = Join-Path (Split-Path $toolsDir -Parent) 'Config'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

$catalog = Get-TarkovLiveTaskCatalog
$appMaps = Get-TarkovAppQuestMapKeys

$devCounts = @{}
$devKeys = @{}

foreach ($task in @($catalog.Tasks)) {
    foreach ($obj in @($task.objectives)) {
        foreach ($loc in @($obj.zones)) {
            $k = Resolve-TarkovQuestMapKey ([string]$loc.normalizedName)
            if (-not $devCounts.ContainsKey($k)) { $devCounts[$k] = 0 }
            $devCounts[$k]++
            $devKeys[$k] = $true
        }
    }
}

$localPath = Join-Path $configDir 'tarkov_quest_markers.json'
$local = Get-Content $localPath -Raw | ConvertFrom-Json
$localCounts = @{}
foreach ($prop in $local.PSObject.Properties) {
    $localCounts[$prop.Name] = @($prop.Value).Count
}

Write-Output '=== Quest marker counts: local vs json.tarkov.dev ==='
Write-Output ''
foreach ($key in ($appMaps | Sort-Object)) {
    $localCount = if ($localCounts.ContainsKey($key)) { $localCounts[$key] } else { 0 }
    $devCount = if ($devCounts.ContainsKey($key)) { $devCounts[$key] } else { 0 }
    $delta = $localCount - $devCount
    $status = if ($localCount -eq $devCount) { 'OK' } elseif ($localCount -gt $devCount) { 'LOCAL+' } else { 'MISSING' }
    Write-Output ("{0,-18} local={1,3}  tarkov.dev={2,3}  delta={3,3}  [{4}]" -f $key, $localCount, $devCount, $delta, $status)
}

Write-Output ''
Write-Output '=== Extra maps on tarkov.dev (not in app) ==='
foreach ($key in ($devCounts.Keys | Sort-Object)) {
    if ($appMaps -notcontains $key) {
        Write-Output ("  {0}: {1}" -f $key, $devCounts[$key])
    }
}
