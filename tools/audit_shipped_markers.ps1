# Check the shipped quest markers against the local task snapshot, without touching the
# network. Confirms the marker file is consistent with the task data it came from.

$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = Join-Path (Split-Path $toolsDir -Parent) 'Config'

function Normalize-MapName([string]$name) {
    return -join ($name.ToLowerInvariant().ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) })
}

function Resolve-QuestMapKey([string]$displayName) {
    switch (Normalize-MapName $displayName) {
        'lab' { return 'thelab' }
        'nightfactory' { return 'factory' }
        'groundzero21' { return 'groundzero' }
        'thelabyrinth' { return 'thelabyrinth' }
        default { return Normalize-MapName $displayName }
    }
}

$appMapKeys = @('factory', 'customs', 'woods', 'shoreline', 'interchange', 'thelab',
    'reserve', 'lighthouse', 'streetsoftarkov', 'groundzero', 'terminal', 'thelabyrinth', 'icebreaker')

# ConvertFrom-Json emits a JSON array as a single object in PowerShell 5.1, so assign
# first and wrap afterwards - @(...) around the pipeline would nest the whole array.
$snapshot = Get-Content (Join-Path $toolsDir 'data\tarkov_tasks_raw.json') -Raw | ConvertFrom-Json
$snapshot = @($snapshot)
$shipped = Get-Content (Join-Path $configDir 'tarkov_quest_markers.json') -Raw | ConvertFrom-Json

# Expected markers, applying the same rules as build_quest_markers_from_api.ps1.
$expected = @{}
$expectedQuests = @{}
$skippedMaps = @{}
foreach ($task in $snapshot) {
    foreach ($obj in $task.objectives) {
        foreach ($zone in @($obj.zones)) {
            if ($null -eq $zone) { continue }
            if ($null -eq $zone.x -or $null -eq $zone.z) { continue }
            if ($zone.x -eq 0 -and $zone.z -eq 0) { continue }

            $key = Resolve-QuestMapKey $zone.map
            if ($appMapKeys -notcontains $key) {
                if (-not $skippedMaps.ContainsKey($zone.map)) { $skippedMaps[$zone.map] = 0 }
                $skippedMaps[$zone.map]++
                continue
            }
            if (-not $expected.ContainsKey($key)) {
                $expected[$key] = 0
                $expectedQuests[$key] = @{}
            }
            $expected[$key]++
            $expectedQuests[$key][$task.name] = $true
        }
    }
}

'{0,-18} {1,8} {2,8} {3,7}   {4}' -f 'map', 'shipped', 'snapshot', 'delta', 'quests shipped/snapshot'
$totalShipped = 0; $totalExpected = 0
foreach ($key in ($appMapKeys | Sort-Object)) {
    $markers = @()
    if ($null -ne $shipped.PSObject.Properties[$key]) {
        $markers = @($shipped.$key)
    }
    $shippedCount = $markers.Count
    $expectedCount = $(if ($expected.ContainsKey($key)) { $expected[$key] } else { 0 })
    $totalShipped += $shippedCount
    $totalExpected += $expectedCount

    $shippedQuests = @($markers | ForEach-Object { $_.quest } | Sort-Object -Unique)
    $snapQuests = $(if ($expectedQuests.ContainsKey($key)) { @($expectedQuests[$key].Keys) } else { @() })

    '{0,-18} {1,8} {2,8} {3,7}   {4} / {5}' -f $key, $shippedCount, $expectedCount,
        ($shippedCount - $expectedCount), $shippedQuests.Count, $snapQuests.Count
}
''
'{0,-18} {1,8} {2,8} {3,7}' -f 'TOTAL', $totalShipped, $totalExpected, ($totalShipped - $totalExpected)

# Quests that appear in one artifact but not the other.
$shippedAll = @{}
foreach ($key in $appMapKeys) {
    foreach ($m in @($shipped.$key)) {
        if ([string]::IsNullOrWhiteSpace($m.quest)) { continue }
        $shippedAll[$m.quest] = $true
    }
}
$snapAll = @{}
foreach ($key in $expectedQuests.Keys) {
    foreach ($q in $expectedQuests[$key].Keys) { $snapAll[$q] = $true }
}

$onlyShipped = @($shippedAll.Keys | Where-Object { -not $snapAll.ContainsKey($_) } | Sort-Object)
$onlySnapshot = @($snapAll.Keys | Where-Object { -not $shippedAll.ContainsKey($_) } | Sort-Object)

''
'quests with markers shipped but not in the snapshot ({0}):' -f $onlyShipped.Count
if ($onlyShipped.Count -eq 0) { '  none' } else { $onlyShipped | ForEach-Object { '  ' + $_ } }
''
'quests with positions in the snapshot but no shipped markers ({0}):' -f $onlySnapshot.Count
if ($onlySnapshot.Count -eq 0) { '  none' } else { $onlySnapshot | ForEach-Object { '  ' + $_ } }

if ($skippedMaps.Count -gt 0) {
    ''
    'positions on maps the app does not show:'
    foreach ($m in ($skippedMaps.Keys | Sort-Object)) { '  {0,-24} {1}' -f $m, $skippedMaps[$m] }
}
