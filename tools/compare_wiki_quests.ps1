# Interim quest check for when tarkov.dev's API is unavailable.
#
# Pulls the current quest list from the Escape from Tarkov wiki and diffs the names
# against the local task snapshot. This only yields names - no ids, objectives or map
# positions - so treat the output as candidates to confirm against tarkov.dev later.

param(
    [string]$Category = 'Category:Quests',
    [string[]]$ExcludeCategories = @('Category:Historical content', 'Category:Event content'),
    [switch]$ShowAll
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$snapshotPath = Join-Path $toolsDir 'data\tarkov_tasks_raw.json'

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
$client = New-Object System.Net.Http.HttpClient $handler
$client.Timeout = [TimeSpan]::FromSeconds(30)
# The wiki rejects unfamiliar clients, so present a normal browser user agent.
$client.DefaultRequestHeaders.Add('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36')

function CategoryMembers([string]$category) {
    $names = New-Object System.Collections.Generic.List[string]
    $continue = $null
    do {
        $url = 'https://escapefromtarkov.fandom.com/api.php?action=query&list=categorymembers' +
            '&cmtitle=' + [uri]::EscapeDataString($category) + '&cmlimit=500&cmnamespace=0&format=json'
        if ($continue) { $url += '&cmcontinue=' + [uri]::EscapeDataString($continue) }

        $json = $client.GetStringAsync($url).GetAwaiter().GetResult() | ConvertFrom-Json
        foreach ($member in $json.query.categorymembers) { $names.Add([string]$member.title) }
        $continue = $json.continue.cmcontinue
    } while ($continue)
    return $names
}

$titles = CategoryMembers $Category
'wiki      : {0} pages in {1}' -f $titles.Count, $Category

# Retired and seasonal quests stay listed under Quests, so drop them explicitly.
$excluded = @{}
foreach ($category in $ExcludeCategories) {
    $members = CategoryMembers $category
    foreach ($m in $members) { $excluded[$m] = $category }
    '            less {0,4} in {1}' -f $members.Count, $category
}
$client.Dispose()

$titles = @($titles | Where-Object { -not $excluded.ContainsKey($_) })
'            {0} pages remain' -f $titles.Count

# ConvertFrom-Json emits a JSON array as a single object in PowerShell 5.1, so assign
# first and wrap afterwards - @(...) around the pipeline would nest the whole array.
$snapshot = Get-Content $snapshotPath -Raw | ConvertFrom-Json
$snapshot = @($snapshot)
'snapshot  : {0} tasks, {1} distinct names (written {2:yyyy-MM-dd})' -f $snapshot.Count,
    (@($snapshot.name | Sort-Object -Unique)).Count, (Get-Item $snapshotPath).LastWriteTime

function Key([string]$name) {
    # Compare loosely: the wiki styles punctuation and casing inconsistently.
    $k = $name.ToLowerInvariant()
    $k = $k -replace '\s*\[pvp zone\]\s*', ''
    $k = $k -replace '[^a-z0-9]', ''
    return $k
}

$snapKeys = @{}
foreach ($t in $snapshot) {
    $k = Key $t.name
    if (-not $snapKeys.ContainsKey($k)) { $snapKeys[$k] = $t.name }
}
$wikiKeys = @{}
foreach ($title in $titles) {
    $k = Key $title
    if (-not $wikiKeys.ContainsKey($k)) { $wikiKeys[$k] = $title }
}

$onlyWiki = @($wikiKeys.Keys | Where-Object { -not $snapKeys.ContainsKey($_) } | ForEach-Object { $wikiKeys[$_] } | Sort-Object)
$onlySnapshot = @($snapKeys.Keys | Where-Object { -not $wikiKeys.ContainsKey($_) } | ForEach-Object { $snapKeys[$_] } | Sort-Object)

''
'=== on the wiki but not in our data ({0}) - candidate new quests ===' -f $onlyWiki.Count
if ($onlyWiki.Count -eq 0) { '  none' } else { $onlyWiki | ForEach-Object { '  ' + $_ } }

''
'=== in our data but not in this wiki category ({0}) ===' -f $onlySnapshot.Count
if ($onlySnapshot.Count -eq 0) { '  none' }
elseif ($onlySnapshot.Count -le 40 -or $ShowAll) { $onlySnapshot | ForEach-Object { '  ' + $_ } }
else {
    $onlySnapshot | Select-Object -First 40 | ForEach-Object { '  ' + $_ }
    '  ... and {0} more (pass -ShowAll)' -f ($onlySnapshot.Count - 40)
}
