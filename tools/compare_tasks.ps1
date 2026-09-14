# Compare live json.tarkov.dev tasks against the local snapshot in data/tarkov_tasks_raw.json
# and report new, removed and changed quests.
#
# Pass -Update to overwrite the snapshot once the differences have been reviewed.
# Pass -Json <path> to read a previously saved flattened task list instead of fetching.

param(
    [switch]$Update,
    [string]$Json
)

$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$snapshotPath = Join-Path $toolsDir 'data\tarkov_tasks_raw.json'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

if ($Json) {
    $live = @(Read-TarkovJsonSnapshot $Json)
}
else {
    $catalog = Get-TarkovLiveTaskCatalog
    $live = @($catalog.Tasks)
}

$snapshot = @(Read-TarkovJsonSnapshot $snapshotPath)

'tarkov.dev : {0} tasks' -f $live.Count
'snapshot   : {0} tasks   (last written {1:yyyy-MM-dd})' -f $snapshot.Count, $(if (Test-Path $snapshotPath) { (Get-Item $snapshotPath).LastWriteTime } else { [datetime]::MinValue })
''

$liveById = @{}; foreach ($t in $live) { $liveById[[string]$t.id] = $t }
$snapById = @{}; foreach ($t in $snapshot) { $snapById[[string]$t.id] = $t }

$new = @($live | Where-Object { -not $snapById.ContainsKey([string]$_.id) })
$removed = @($snapshot | Where-Object { -not $liveById.ContainsKey([string]$_.id) })

'=== NEW quests ({0}) ===' -f $new.Count
foreach ($t in ($new | Sort-Object trader, name)) {
    $mapNote = $(if ($t.map) { $t.map } else { 'no map' })
    '  {0,-42} {1,-14} lvl {2,3}  {3}{4}' -f $t.name, $t.trader, $t.minPlayerLevel, $mapNote,
        $(if ($t.kappaRequired) { '  [kappa]' } else { '' })
    '{0,46}{1} objectives, {2} with positions' -f '', @($t.objectives).Count,
        @($t.objectives | Where-Object { @($_.zones).Count -gt 0 }).Count
}
if ($new.Count -eq 0) { '  none' }

''
'=== REMOVED quests ({0}) ===' -f $removed.Count
foreach ($t in ($removed | Sort-Object trader, name)) {
    '  {0,-42} {1,-14} lvl {2,3}' -f $t.name, $t.trader, $t.minPlayerLevel
}
if ($removed.Count -eq 0) { '  none' }

$scalars = @('name', 'normalizedName', 'trader', 'map', 'factionName', 'minPlayerLevel',
    'kappaRequired', 'lightkeeperRequired')

function ObjectiveFingerprint($objective) {
    $positions = (@($objective.zones) | ForEach-Object { '{0}@{1:N1},{2:N1}' -f $_.map, $_.x, $_.z } | Sort-Object) -join ';'
    return '{0}|{1}|{2}|{3}|{4}|{5}' -f $objective.objectiveType, $objective.type, $objective.description,
        $objective.optional, $objective.count, $positions
}

$changed = New-Object System.Collections.Generic.List[object]
foreach ($t in $live) {
    $tid = [string]$t.id
    if (-not $snapById.ContainsKey($tid)) { continue }
    $old = $snapById[$tid]
    $diffs = New-Object System.Collections.Generic.List[string]

    foreach ($field in $scalars) {
        $a = [string]$old.$field
        $b = [string]$t.$field
        if ($a -ne $b) { $diffs.Add(('{0}: "{1}" -> "{2}"' -f $field, $a, $b)) }
    }

    $oldObjectives = @($old.objectives); $newObjectives = @($t.objectives)
    if ($oldObjectives.Count -ne $newObjectives.Count) {
        $diffs.Add(('objective count: {0} -> {1}' -f $oldObjectives.Count, $newObjectives.Count))
    }

    $oldPrints = @{}; foreach ($o in $oldObjectives) { $oldPrints[[string]$o.id] = ObjectiveFingerprint $o }
    $newPrints = @{}; foreach ($o in $newObjectives) { $newPrints[[string]$o.id] = ObjectiveFingerprint $o }

    foreach ($key in $newPrints.Keys) {
        if (-not $oldPrints.ContainsKey($key)) {
            $match = $newObjectives | Where-Object { [string]$_.id -eq $key } | Select-Object -First 1
            $diffs.Add(('objective added: {0}' -f $match.description))
        }
        elseif ($oldPrints[$key] -ne $newPrints[$key]) {
            $match = $newObjectives | Where-Object { [string]$_.id -eq $key } | Select-Object -First 1
            $diffs.Add(('objective changed: {0}' -f $match.description))
        }
    }
    foreach ($key in $oldPrints.Keys) {
        if (-not $newPrints.ContainsKey($key)) {
            $match = $oldObjectives | Where-Object { [string]$_.id -eq $key } | Select-Object -First 1
            $diffs.Add(('objective removed: {0}' -f $match.description))
        }
    }

    if ($diffs.Count -gt 0) {
        $changed.Add([pscustomobject]@{ Task = $t; Diffs = $diffs })
    }
}

''
'=== CHANGED quests ({0}) ===' -f $changed.Count
foreach ($entry in ($changed | Sort-Object { $_.Task.name })) {
    '  {0}  ({1})' -f $entry.Task.name, $entry.Task.trader
    foreach ($d in $entry.Diffs) { '      - {0}' -f $d }
}
if ($changed.Count -eq 0) { '  none' }

$livePositions = (@($live | ForEach-Object { $_.objectives } | ForEach-Object { @($_.zones).Count }) | Measure-Object -Sum).Sum
$snapPositions = (@($snapshot | ForEach-Object { $_.objectives } | ForEach-Object { @($_.zones).Count }) | Measure-Object -Sum).Sum
''
'objective positions (map markers): snapshot {0}, tarkov.dev {1}, delta {2}' -f
    $snapPositions, $livePositions, ($livePositions - $snapPositions)

$appMapKeys = Get-TarkovAppQuestMapKeys
$liveTrackable = @($live | Where-Object { @($_.objectives | Where-Object { @($_.zones).Count -gt 0 }).Count -gt 0 })
$unmapped = 0
foreach ($t in $liveTrackable) {
    $onAppMap = $false
    foreach ($obj in @($t.objectives)) {
        foreach ($z in @($obj.zones)) {
            $key = Resolve-TarkovQuestMapKey ([string]$z.normalizedName)
            if ($appMapKeys -contains $key) { $onAppMap = $true }
        }
    }
    if (-not $onAppMap) { $unmapped++ }
}
''
'trackable tasks: {0}  (with at least one map position)' -f $liveTrackable.Count
'trackable tasks not on an app map: {0}' -f $unmapped

if ($Update) {
    ConvertTo-TarkovJsonSnapshot $live | Set-Content $snapshotPath -Encoding utf8
    ''
    'snapshot updated: {0}' -f $snapshotPath
    'run build_quest_markers_from_api.ps1 next to rebuild Config/tarkov_quest_markers.json'
}
