# Map which tiles hold content, per layer, using Content-Length. Filler tiles are a
# 102-byte fully transparent PNG, so size alone separates them cleanly.

param(
    [string]$TileBase = 'https://assets.tarkov.dev/maps/labs_v4',
    [string[]]$Layers = @('1st', '2nd', 'technical'),
    [int]$Zoom = 5,
    [int]$EmptyMaxBytes = 500
)

Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$tilesPerSide = [int][Math]::Pow(2, $Zoom)

$union = @{}
foreach ($layer in $Layers) {
    $occupied = @{}
    for ($col = 0; $col -lt $tilesPerSide; $col++) {
        $pending = @()
        for ($row = 0; $row -lt $tilesPerSide; $row++) {
            $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Head), "$TileBase/$layer/$Zoom/$col/$row.png"
            $pending += [pscustomobject]@{ Row = $row; Task = $http.SendAsync($req) }
        }
        foreach ($item in $pending) {
            try {
                $resp = $item.Task.GetAwaiter().GetResult()
                if ($resp.IsSuccessStatusCode -and $resp.Content.Headers.ContentLength -gt $EmptyMaxBytes) {
                    $occupied["$col,$($item.Row)"] = $true
                    $union["$col,$($item.Row)"] = $true
                }
            } catch { }
        }
    }

    $cols = @(); $rows = @()
    foreach ($key in $occupied.Keys) {
        $parts = $key.Split(',')
        $cols += [int]$parts[0]
        $rows += [int]$parts[1]
    }
    if ($cols.Count -eq 0) { '{0,-12} no content' -f $layer; continue }
    '{0,-12} {1,4} tiles   cols {2,3}-{3,-3} rows {4,3}-{5,-3}' -f $layer, $occupied.Count,
        ($cols | Measure-Object -Minimum).Minimum, ($cols | Measure-Object -Maximum).Maximum,
        ($rows | Measure-Object -Minimum).Minimum, ($rows | Measure-Object -Maximum).Maximum
}

$cols = @(); $rows = @()
foreach ($key in $union.Keys) {
    $parts = $key.Split(',')
    $cols += [int]$parts[0]
    $rows += [int]$parts[1]
}
$minCol = ($cols | Measure-Object -Minimum).Minimum
$maxCol = ($cols | Measure-Object -Maximum).Maximum
$minRow = ($rows | Measure-Object -Minimum).Minimum
$maxRow = ($rows | Measure-Object -Maximum).Maximum
''
'union        {0,4} tiles   cols {1,3}-{2,-3} rows {3,3}-{4,-3}   span {5} x {6}' -f $union.Count,
    $minCol, $maxCol, $minRow, $maxRow, ($maxCol - $minCol + 1), ($maxRow - $minRow + 1)
$http.Dispose()
