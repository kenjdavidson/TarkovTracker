$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = Join-Path (Split-Path $toolsDir -Parent) 'Config'
$outputPath = Join-Path $configDir 'tarkov_extracts_raw.json'

. (Join-Path $toolsDir 'tarkov_json_api.ps1')

function Build-TransferItemObject($transferItem) {
    if ($null -eq $transferItem -or $null -eq $transferItem.item) {
        return $null
    }

    $itemName = [string]$transferItem.item.name
    if ([string]::IsNullOrWhiteSpace($itemName)) {
        return $null
    }

    $count = if ($null -ne $transferItem.count) { [double]$transferItem.count } else { 1.0 }
    $quantity = if ($null -ne $transferItem.quantity) { [double]$transferItem.quantity } else { $count }

    return [ordered]@{
        item     = [ordered]@{ name = $itemName }
        count    = $count
        quantity = $quantity
    }
}

function Build-RequirementsList($extract, $switches) {
    $requirements = New-Object System.Collections.Generic.List[string]

    if ($extract.transferItem -and $extract.transferItem.item -and $extract.transferItem.item.name) {
        $itemName = [string]$extract.transferItem.item.name
        $count = if ($null -ne $extract.transferItem.count) { [double]$extract.transferItem.count } else { 1.0 }
        if ($count -gt 1) {
            $requirements.Add("Requires item: $itemName (x$([int]$count))")
        }
        else {
            $requirements.Add("Requires item: $itemName")
        }
    }

    foreach ($sw in $switches) {
        if ($null -eq $sw) { continue }
        $switchName = if ($sw.name) { [string]$sw.name } else { [string]$sw.id }
        if (-not [string]::IsNullOrWhiteSpace($switchName)) {
            $requirements.Add("Requires switch: $switchName")
        }
    }

    if ($requirements.Count -eq 0) { return @() }
    return [string[]]$requirements.ToArray()
}

$maps = @(Get-TarkovJsonMaps)

$payload = [ordered]@{
    data = [ordered]@{
        maps = @()
    }
}

foreach ($map in $maps) {
    $extracts = @()
    foreach ($extract in @($map.extracts)) {
        if ($null -eq $extract) { continue }
        $switches = @()
        foreach ($sw in @($extract.switches)) {
            if ($null -eq $sw) { continue }
            $switches += [ordered]@{
                id   = [string]$sw.id
                name = [string]$sw.name
            }
        }

        $pos = $null
        if ($extract.position) {
            $pos = [ordered]@{
                x = [double]$extract.position.x
                y = [double]$extract.position.y
                z = [double]$extract.position.z
            }
        }

        $extracts += [ordered]@{
            id           = [string]$extract.id
            name         = [string]$extract.name
            faction      = [string]$extract.faction
            requirements = @(Build-RequirementsList $extract $switches)
            switches     = $switches
            transferItem = Build-TransferItemObject $extract.transferItem
            position     = $pos
        }
    }

    $payload.data.maps += [ordered]@{
        name     = [string]$map.name
        extracts = $extracts
    }
}

$payload | ConvertTo-Json -Depth 12 | Set-Content -Path $outputPath -Encoding UTF8
Write-Host "Wrote $($payload.data.maps.Count) maps to $outputPath"
