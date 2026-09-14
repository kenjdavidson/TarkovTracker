$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $toolsDir 'tarkov_json_api.ps1')

$maps = @(Get-TarkovJsonMaps)
foreach ($map in $maps) {
    $total = 0
    foreach ($b in @($map.bosses)) {
        if ($null -eq $b) { continue }
        $total += @($b.spawnLocations).Count
    }
    if ($total -gt 0) {
        Write-Output "$($map.name): $total boss spawn locations"
        foreach ($b in @($map.bosses)) {
            if ($null -eq $b) { continue }
            $locCount = @($b.spawnLocations).Count
            if ($locCount -gt 0) {
                Write-Output "  $($b.boss.name): $locCount"
                foreach ($loc in @($b.spawnLocations)) {
                    if ($null -eq $loc) { continue }
                    Write-Output "    $($loc.spawnKey) / $($loc.name) ($($loc.chance)%)"
                }
            }
        }
    }
}
