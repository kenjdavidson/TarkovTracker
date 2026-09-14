$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = Join-Path (Split-Path $toolsDir -Parent) 'Config'
$outputPath = Join-Path $configDir 'tarkov_quest_markers.json'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

$appMapKeys = Get-TarkovAppQuestMapKeys
$catalog = Get-TarkovLiveTaskCatalog
$tasks = @($catalog.Tasks)

$byMap = @{}
$skippedMaps = @{}
$trackableTasks = @{}
$placedTasks = @{}

foreach ($task in $tasks) {
    $questSlug = if ($task.normalizedName) { [string]$task.normalizedName } else { '' }

    foreach ($obj in @($task.objectives)) {
        $locations = @($obj.zones)
        if ($locations.Count -eq 0) { continue }

        $trackableTasks[$task.id] = $task.name

        foreach ($loc in $locations) {
            $mapKey = Resolve-TarkovQuestMapKey ([string]$loc.normalizedName)
            if ($appMapKeys -notcontains $mapKey) {
                $skipLabel = [string]$loc.normalizedName
                if (-not $skipLabel) { $skipLabel = [string]$loc.map }
                if (-not $skippedMaps.ContainsKey($skipLabel)) { $skippedMaps[$skipLabel] = 0 }
                $skippedMaps[$skipLabel]++
                continue
            }

            $placedTasks[$task.id] = $task.name
            $marker = [ordered]@{
                quest          = $task.name
                questSlug      = $questSlug
                objectiveType  = [string]$obj.objectiveType
                category       = [string]$obj.category
                iconType       = [string]$obj.category
                description    = [string]$obj.description
                questItem      = if ($obj.itemName) { [string]$obj.itemName } else { '' }
                itemShortName  = if ($obj.itemShortName) { [string]$obj.itemShortName } else { '' }
                itemIconLink   = if ($obj.itemIconLink) { [string]$obj.itemIconLink } else { '' }
                trader         = if ($task.trader) { [string]$task.trader } else { '' }
                minPlayerLevel = [int]$task.minPlayerLevel
                optional       = [bool]$obj.optional
                x              = [double]$loc.x
                y              = [double]$loc.y
                z              = [double]$loc.z
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
    $sorted[$key] = @($byMap[$key] | Sort-Object quest, description, x, z)
}

$sorted | ConvertTo-Json -Depth 6 | Set-Content $outputPath -Encoding utf8

Write-Output ''
Write-Output 'Quest markers built from json.tarkov.dev:'
$total = 0
foreach ($key in ($appMapKeys | Sort-Object)) {
    $count = 0
    if ($sorted.Contains($key)) { $count = @($sorted[$key]).Count }
    $total += $count
    Write-Output ('  {0}: {1}' -f $key, $count)
}
Write-Output ('Total markers: {0}' -f $total)
Write-Output ('Live tasks: {0}' -f $tasks.Count)
Write-Output ('Tasks with map locations: {0}' -f $trackableTasks.Count)
Write-Output ('Tasks placed on app maps: {0}' -f $placedTasks.Count)

$unplaced = @($trackableTasks.GetEnumerator() | Where-Object { -not $placedTasks.ContainsKey($_.Key) } | Sort-Object Value)
if ($unplaced.Count -gt 0) {
    Write-Output ''
    Write-Output ('Trackable tasks with no marker on an app map ({0}):' -f $unplaced.Count)
    foreach ($entry in $unplaced) { Write-Output ('  {0}' -f $entry.Value) }
}

if ($skippedMaps.Count -gt 0) {
    Write-Output ''
    Write-Output 'Positions on maps the app does not show:'
    foreach ($name in ($skippedMaps.Keys | Sort-Object)) {
        Write-Output ('  {0}: {1}' -f $name, $skippedMaps[$name])
    }
}

if ($catalog.UnknownMaps.Count -gt 0) {
    Write-Output ''
    Write-Output 'Unknown map ids in the API dump:'
    foreach ($name in ($catalog.UnknownMaps.Keys | Sort-Object)) {
        Write-Output ('  {0}: {1}' -f $name, $catalog.UnknownMaps[$name])
    }
}
