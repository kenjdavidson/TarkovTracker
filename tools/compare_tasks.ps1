# Compare live tarkov.dev tasks against the local snapshot in data/tarkov_tasks_raw.json
# and report new, removed and changed quests.
#
# Pass -Update to overwrite the snapshot once the differences have been reviewed.
# Pass -Json <path> to read a previously saved API response instead of fetching.

param(
    [switch]$Update,
    [string]$Json,
    [int]$Retries = 3,
    [int]$RetryDelaySeconds = 10
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$snapshotPath = Join-Path $toolsDir 'data\tarkov_tasks_raw.json'

$query = @'
query {
  tasks {
    id
    tarkovDataId
    name
    normalizedName
    trader { name }
    map { name }
    factionName
    minPlayerLevel
    kappaRequired
    lightkeeperRequired
    wikiLink
    objectives {
      id
      __typename
      type
      description
      optional
      maps { name }
      ... on TaskObjectiveBasic { zones { map { name } position { x y z } } }
      ... on TaskObjectiveItem {
        count
        foundInRaid
        item { name shortName iconLink }
        zones { map { name } position { x y z } }
      }
      ... on TaskObjectiveQuestItem {
        count
        questItem { name shortName iconLink }
        zones { map { name } position { x y z } }
        possibleLocations { map { name } positions { x y z } }
      }
      ... on TaskObjectiveMark {
        markerItem { name shortName iconLink }
        zones { map { name } position { x y z } }
      }
      ... on TaskObjectiveUseItem { zones { map { name } position { x y z } } }
      ... on TaskObjectiveShoot { count zones { map { name } position { x y z } } }
    }
  }
}
'@

# --- fetch (or load) the live task list ---
if ($Json) {
    $response = Get-Content $Json -Raw | ConvertFrom-Json
}
else {
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    $client.DefaultRequestHeaders.Add('User-Agent', 'TarkovTracker-maintenance')

    $payload = @{ query = $query } | ConvertTo-Json
    $response = $null
    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        $content = New-Object System.Net.Http.StringContent $payload, ([System.Text.Encoding]::UTF8), 'application/json'
        try {
            $httpResponse = $client.PostAsync('https://api.tarkov.dev/graphql', $content).GetAwaiter().GetResult()
            $text = $httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not $httpResponse.IsSuccessStatusCode) {
                Write-Output ("attempt {0}/{1}: HTTP {2} - {3}" -f $attempt, $Retries, [int]$httpResponse.StatusCode, $text.Trim())
            }
            else {
                $response = $text | ConvertFrom-Json
                break
            }
        }
        catch {
            Write-Output ("attempt {0}/{1}: {2}" -f $attempt, $Retries, $_.Exception.Message)
        }
        if ($attempt -lt $Retries) { Start-Sleep -Seconds $RetryDelaySeconds }
    }
    $client.Dispose()

    if ($null -eq $response) {
        Write-Output ''
        Write-Output 'tarkov.dev GraphQL API is not responding. Nothing was compared; try again later.'
        exit 2
    }
}

if ($response.errors) {
    Write-Output 'GraphQL errors:'
    $response.errors | ConvertTo-Json -Depth 5
    exit 1
}

# --- flatten the API shape into the same form the snapshot uses ---
function Flatten([object]$apiTasks) {
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($task in $apiTasks) {
        $objectives = New-Object System.Collections.Generic.List[object]
        foreach ($obj in $task.objectives) {
            $type = [string]$obj.__typename

            $itemName = ''; $itemShort = ''; $itemIcon = ''
            foreach ($candidate in @($obj.item, $obj.questItem, $obj.markerItem)) {
                if ($candidate) {
                    $itemName = [string]$candidate.name
                    $itemShort = [string]$candidate.shortName
                    $itemIcon = [string]$candidate.iconLink
                    break
                }
            }

            $zones = New-Object System.Collections.Generic.List[object]
            foreach ($z in @($obj.zones)) {
                if (-not $z -or -not $z.position) { continue }
                $zones.Add([pscustomobject]@{ map = [string]$z.map.name; x = $z.position.x; y = $z.position.y; z = $z.position.z })
            }
            foreach ($pl in @($obj.possibleLocations)) {
                if (-not $pl -or -not $pl.positions) { continue }
                foreach ($pos in $pl.positions) {
                    $zones.Add([pscustomobject]@{ map = [string]$pl.map.name; x = $pos.x; y = $pos.y; z = $pos.z })
                }
            }

            $objectives.Add([pscustomobject]@{
                id            = [string]$obj.id
                objectiveType = $type
                category      = $(if ($type -match 'Item|QuestItem') { 'item' } else { 'objective' })
                type          = [string]$obj.type
                description   = [string]$obj.description
                optional      = [bool]$obj.optional
                maps          = @(@($obj.maps) | ForEach-Object { [string]$_.name })
                count         = $obj.count
                foundInRaid   = $obj.foundInRaid
                itemName      = $itemName
                itemShortName = $itemShort
                itemIconLink  = $itemIcon
                zones         = @($zones)
            })
        }

        $out.Add([pscustomobject]@{
            id                  = [string]$task.id
            tarkovDataId        = $task.tarkovDataId
            name                = [string]$task.name
            normalizedName      = [string]$task.normalizedName
            trader              = [string]$task.trader.name
            map                 = [string]$task.map.name
            factionName         = [string]$task.factionName
            minPlayerLevel      = $task.minPlayerLevel
            kappaRequired       = $task.kappaRequired
            lightkeeperRequired = $task.lightkeeperRequired
            wikiLink            = [string]$task.wikiLink
            objectives          = @($objectives)
        })
    }
    return $out
}

$live = Flatten $response.data.tasks
# ConvertFrom-Json emits a JSON array as a single object in PowerShell 5.1, so assign
# first and wrap afterwards - @(...) around the pipeline would nest the whole array.
$snapshot = Get-Content $snapshotPath -Raw | ConvertFrom-Json
$snapshot = @($snapshot)

'tarkov.dev : {0} tasks' -f $live.Count
'snapshot   : {0} tasks   (last written {1:yyyy-MM-dd})' -f $snapshot.Count, (Get-Item $snapshotPath).LastWriteTime
''

$liveById = @{}; foreach ($t in $live) { $liveById[$t.id] = $t }
$snapById = @{}; foreach ($t in $snapshot) { $snapById[$t.id] = $t }

# --- new and removed ---
$new = @($live | Where-Object { -not $snapById.ContainsKey($_.id) })
$removed = @($snapshot | Where-Object { -not $liveById.ContainsKey($_.id) })

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

# --- changed ---
$scalars = @('name', 'normalizedName', 'trader', 'map', 'factionName', 'minPlayerLevel',
    'kappaRequired', 'lightkeeperRequired')

function ObjectiveFingerprint($objective) {
    $positions = (@($objective.zones) | ForEach-Object { '{0}@{1:N1},{2:N1}' -f $_.map, $_.x, $_.z } | Sort-Object) -join ';'
    return '{0}|{1}|{2}|{3}|{4}|{5}' -f $objective.objectiveType, $objective.type, $objective.description,
        $objective.optional, $objective.count, $positions
}

$changed = New-Object System.Collections.Generic.List[object]
foreach ($t in $live) {
    if (-not $snapById.ContainsKey($t.id)) { continue }
    $old = $snapById[$t.id]
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

# --- position-bearing objectives drive the map markers ---
$livePositions = (@($live | ForEach-Object { $_.objectives } | ForEach-Object { @($_.zones).Count }) | Measure-Object -Sum).Sum
$snapPositions = (@($snapshot | ForEach-Object { $_.objectives } | ForEach-Object { @($_.zones).Count }) | Measure-Object -Sum).Sum
''
'objective positions (map markers): snapshot {0}, tarkov.dev {1}, delta {2}' -f
    $snapPositions, $livePositions, ($livePositions - $snapPositions)

if ($Update) {
    $live | ConvertTo-Json -Depth 8 | Set-Content $snapshotPath -Encoding utf8
    ''
    'snapshot updated: {0}' -f $snapshotPath
    'run build_quest_markers_from_api.ps1 next to rebuild Config/tarkov_quest_markers.json'
}
