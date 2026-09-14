# Shared client for json.tarkov.dev (the live tarkov.dev data API).
# Maintenance scripts use only this JSON API; GraphQL is not used.

$script:TarkovJsonApiRoot = 'https://json.tarkov.dev'
$script:TarkovJsonUserAgent = 'TarkovTracker-maintenance'
$script:TarkovJsonSerializer = $null

function Get-TarkovJsonSerializer {
    if ($null -eq $script:TarkovJsonSerializer) {
        Add-Type -AssemblyName System.Web.Extensions -ErrorAction SilentlyContinue
        $script:TarkovJsonSerializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $script:TarkovJsonSerializer.MaxJsonLength = 80MB
        $script:TarkovJsonSerializer.RecursionLimit = 100
    }
    return $script:TarkovJsonSerializer
}

function Get-TarkovJsonCacheDir {
    $dir = Join-Path $env:TEMP 'tarkov-json-api'
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
    return $dir
}

function Save-TarkovJsonUrl {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$OutFile,
        [int]$TimeoutSeconds = 120
    )

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & curl.exe -sS -f -m $TimeoutSeconds -A $script:TarkovJsonUserAgent -o $OutFile $Url
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to download $Url (curl exit $LASTEXITCODE)"
        }
        return
    }

    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd($script:TarkovJsonUserAgent)
    try {
        $bytes = $client.GetByteArrayAsync($Url).GetAwaiter().GetResult()
        [System.IO.File]::WriteAllBytes($OutFile, $bytes)
    }
    finally {
        $client.Dispose()
    }
}

function Get-TarkovJsonDocument {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [int]$TimeoutSeconds = 120
    )

    $safeName = ($RelativePath -replace '[\\/]', '_')
    $outFile = Join-Path (Get-TarkovJsonCacheDir) $safeName
    $url = "$script:TarkovJsonApiRoot/$RelativePath"
    Write-Host ("fetching {0}" -f $url)
    Save-TarkovJsonUrl -Url $url -OutFile $outFile -TimeoutSeconds $TimeoutSeconds
    $ser = Get-TarkovJsonSerializer
    return $ser.DeserializeObject((Get-Content $outFile -Raw -Encoding UTF8))
}

function Get-TarkovLocaleLookup {
    param([object]$Document)

    if ($null -eq $Document) { return @{} }
    if ($Document -is [System.Collections.IDictionary] -and $Document.ContainsKey('data')) {
        return $Document['data']
    }
    return $Document
}

function Get-TarkovLocaleString {
    param(
        [object]$Locale,
        [string]$Key
    )

    if ([string]::IsNullOrWhiteSpace($Key) -or $null -eq $Locale) { return '' }
    if ($Locale.ContainsKey($Key) -and $Locale[$Key]) { return [string]$Locale[$Key] }
    return ''
}

function Convert-TarkovObjectiveTypeName([string]$jsonType) {
    switch ($jsonType) {
        'findItem' { return 'TaskObjectiveItem' }
        'giveItem' { return 'TaskObjectiveItem' }
        'plantItem' { return 'TaskObjectiveItem' }
        'sellItem' { return 'TaskObjectiveItem' }
        'findQuestItem' { return 'TaskObjectiveQuestItem' }
        'giveQuestItem' { return 'TaskObjectiveQuestItem' }
        'plantQuestItem' { return 'TaskObjectiveQuestItem' }
        'mark' { return 'TaskObjectiveMark' }
        'useItem' { return 'TaskObjectiveUseItem' }
        'shoot' { return 'TaskObjectiveShoot' }
        'buildWeapon' { return 'TaskObjectiveBuildItem' }
        'extract' { return 'TaskObjectiveExtract' }
        'skill' { return 'TaskObjectiveSkill' }
        'traderLevel' { return 'TaskObjectiveTraderLevel' }
        'traderStanding' { return 'TaskObjectiveTraderStanding' }
        'taskStatus' { return 'TaskObjectiveTaskStatus' }
        'experience' { return 'TaskObjectiveExperience' }
        default { return 'TaskObjectiveBasic' }
    }
}

function Get-TarkovMapLookup {
    param([object]$MapsDocument)

    $byId = @{}
    $data = $MapsDocument['data']
    $maps = $null
    if ($data -is [System.Collections.IDictionary]) {
        if ($data.ContainsKey('maps')) { $maps = $data['maps'] }
        else { $maps = $data }
    }
    if ($maps -isnot [System.Collections.IDictionary]) { return $byId }

    foreach ($kv in $maps.GetEnumerator()) {
        $map = $kv.Value
        if ($map -isnot [System.Collections.IDictionary]) { continue }
        $id = [string]$map['id']
        if (-not $id) { $id = [string]$kv.Key }
        $normalized = [string]$map['normalizedName']
        $name = [string]$map['name']
        $byId[$id] = [pscustomobject]@{
            id             = $id
            normalizedName = $normalized
            name           = $name
        }
    }
    return $byId
}

function Get-TarkovLocalizedMap {
    param(
        [object]$MapById,
        [object]$MapsLocale,
        [string]$MapId
    )

    if ([string]::IsNullOrWhiteSpace($MapId)) { return $null }
    $info = $null
    if ($MapById.ContainsKey($MapId)) { $info = $MapById[$MapId] }

    $normalized = if ($info) { [string]$info.normalizedName } else { '' }
    $name = ''
    foreach ($key in @($MapId, ($MapId + ' name'), ($MapId + ' Name'))) {
        $name = Get-TarkovLocaleString $MapsLocale $key
        if ($name) { break }
    }
    if (-not $name -and $info) {
        $name = Get-TarkovLocaleString $MapsLocale ([string]$info.name)
    }
    if (-not $name -and $normalized) {
        $name = (($normalized -split '-') | ForEach-Object {
            if ($_.Length -eq 0) { return $_ }
            $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
        }) -join ' '
    }
    if (-not $name) { $name = $MapId }

    return [pscustomobject]@{
        id             = $MapId
        normalizedName = $normalized
        name           = $name
    }
}

function Get-TarkovItemInfo {
    param(
        [object]$ItemsLocale,
        [object]$QuestItems,
        [object]$QuestItemsLocale,
        [string]$ItemId
    )

    if ([string]::IsNullOrWhiteSpace($ItemId)) {
        return [pscustomobject]@{ name = ''; shortName = ''; iconLink = '' }
    }

    if ($QuestItems -and $QuestItems.ContainsKey($ItemId)) {
        $qi = $QuestItems[$ItemId]
        $name = Get-TarkovLocaleString $QuestItemsLocale ([string]$qi['name'])
        if (-not $name) { $name = Get-TarkovLocaleString $QuestItemsLocale ($ItemId + ' Name') }
        $short = Get-TarkovLocaleString $QuestItemsLocale ([string]$qi['shortName'])
        if (-not $short) { $short = Get-TarkovLocaleString $QuestItemsLocale ($ItemId + ' ShortName') }
        return [pscustomobject]@{
            name      = $name
            shortName = $short
            iconLink  = [string]$qi['iconLink']
        }
    }

    $name = Get-TarkovLocaleString $ItemsLocale ($ItemId + ' Name')
    $short = Get-TarkovLocaleString $ItemsLocale ($ItemId + ' ShortName')
    return [pscustomobject]@{
        name      = $name
        shortName = $short
        iconLink  = "https://assets.tarkov.dev/$ItemId-icon.webp"
    }
}

function Add-TarkovIdValues {
    param(
        [System.Collections.Generic.List[string]]$Target,
        [object]$Value
    )

    if ($null -eq $Value) { return }
    if ($Value -is [string]) {
        if (-not [string]::IsNullOrWhiteSpace($Value)) { [void]$Target.Add([string]$Value) }
        return
    }
    if ($Value -is [System.Collections.IDictionary]) { return }
    if ($Value -is [System.Collections.IEnumerable]) {
        foreach ($entry in $Value) {
            if ($entry -is [string] -and -not [string]::IsNullOrWhiteSpace($entry)) {
                [void]$Target.Add([string]$entry)
            }
        }
    }
}

function Get-TarkovObjectiveItemIds([object]$Objective) {
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($field in @('questItem', 'markerItem', 'item', 'items')) {
        Add-TarkovIdValues -Target $ids -Value $Objective[$field]
    }
    return @($ids)
}

function Convert-TarkovPosition([object]$Position) {
    if ($null -eq $Position -or $Position -isnot [System.Collections.IDictionary]) { return $null }
    if (-not $Position.ContainsKey('x') -or -not $Position.ContainsKey('z')) { return $null }
    $x = [double]$Position['x']
    $z = [double]$Position['z']
    if ($x -eq 0 -and $z -eq 0) { return $null }
    $y = 0.0
    if ($Position.ContainsKey('y') -and $null -ne $Position['y']) { $y = [double]$Position['y'] }
    return [pscustomobject]@{ x = $x; y = $y; z = $z }
}

function Get-TarkovLiveTaskCatalog {
    param(
        [string]$GameMode = 'regular',
        [string]$Language = 'en'
    )

    $tasksDoc = Get-TarkovJsonDocument "$GameMode/tasks"
    $tasksLocaleDoc = Get-TarkovJsonDocument "$GameMode/tasks_$Language"
    $mapsDoc = Get-TarkovJsonDocument "$GameMode/maps"
    $mapsLocaleDoc = Get-TarkovJsonDocument "$GameMode/maps_$Language"
    $tradersDoc = Get-TarkovJsonDocument "$GameMode/traders"
    $tradersLocaleDoc = Get-TarkovJsonDocument "$GameMode/traders_$Language"
    $itemsLocaleDoc = Get-TarkovJsonDocument "$GameMode/items_$Language"

    $tasksLocale = Get-TarkovLocaleLookup $tasksLocaleDoc
    $mapsLocale = Get-TarkovLocaleLookup $mapsLocaleDoc
    $tradersLocale = Get-TarkovLocaleLookup $tradersLocaleDoc
    $itemsLocale = Get-TarkovLocaleLookup $itemsLocaleDoc
    $mapById = Get-TarkovMapLookup $mapsDoc

    $data = $tasksDoc['data']
    $rawTasks = $data['tasks']
    $questItems = $data['questItems']
    if (-not $questItems) { $questItems = @{} }

    $tradersData = $tradersDoc['data']

    $tasks = New-Object System.Collections.Generic.List[object]
    $unknownMaps = @{}

    foreach ($taskKv in $rawTasks.GetEnumerator()) {
        $raw = $taskKv.Value
        if ($raw -isnot [System.Collections.IDictionary]) { continue }

        $id = [string]$raw['id']
        $nameKey = [string]$raw['name']
        $name = Get-TarkovLocaleString $tasksLocale $nameKey
        if (-not $name) { $name = Get-TarkovLocaleString $tasksLocale ($id + ' name') }
        if (-not $name) { $name = $nameKey }

        $traderId = [string]$raw['trader']
        $traderName = Get-TarkovLocaleString $tradersLocale ($traderId + ' Nickname')
        if (-not $traderName -and $tradersData -and $tradersData.ContainsKey($traderId)) {
            $traderName = [string]$tradersData[$traderId]['normalizedName']
        }

        $taskMap = Get-TarkovLocalizedMap $mapById $mapsLocale ([string]$raw['map'])

        $objectives = New-Object System.Collections.Generic.List[object]
        foreach ($obj in @($raw['objectives'])) {
            if ($null -eq $obj -or $obj -isnot [System.Collections.IDictionary]) { continue }

            $jsonType = [string]$obj['type']
            $typeName = Convert-TarkovObjectiveTypeName $jsonType
            $objId = [string]$obj['id']
            $description = Get-TarkovLocaleString $tasksLocale $objId
            if (-not $description) {
                $description = Get-TarkovLocaleString $tasksLocale ([string]$obj['description'])
            }

            $itemIds = Get-TarkovObjectiveItemIds $obj
            $itemInfo = Get-TarkovItemInfo $itemsLocale $questItems $tasksLocale ($itemIds | Select-Object -First 1)

            $zones = New-Object System.Collections.Generic.List[object]
            foreach ($zone in @($obj['zones'])) {
                if ($null -eq $zone -or $zone -isnot [System.Collections.IDictionary]) { continue }
                $pos = Convert-TarkovPosition $zone['position']
                if ($null -eq $pos) { continue }
                $mapInfo = Get-TarkovLocalizedMap $mapById $mapsLocale ([string]$zone['map'])
                if ($mapInfo -and -not $mapInfo.normalizedName) {
                    $mid = [string]$zone['map']
                    if ($mid -and -not $unknownMaps.ContainsKey($mid)) { $unknownMaps[$mid] = 0 }
                    if ($mid) { $unknownMaps[$mid]++ }
                }
                $zones.Add([pscustomobject]@{
                    map            = $(if ($mapInfo) { $mapInfo.name } else { [string]$zone['map'] })
                    normalizedName = $(if ($mapInfo) { $mapInfo.normalizedName } else { '' })
                    mapId          = $(if ($mapInfo) { $mapInfo.id } else { [string]$zone['map'] })
                    x              = $pos.x
                    y              = $pos.y
                    z              = $pos.z
                })
            }

            foreach ($pl in @($obj['possibleLocations'])) {
                if ($null -eq $pl -or $pl -isnot [System.Collections.IDictionary]) { continue }
                $mapInfo = Get-TarkovLocalizedMap $mapById $mapsLocale ([string]$pl['map'])
                if ($mapInfo -and -not $mapInfo.normalizedName) {
                    $mid = [string]$pl['map']
                    if ($mid -and -not $unknownMaps.ContainsKey($mid)) { $unknownMaps[$mid] = 0 }
                    if ($mid) { $unknownMaps[$mid]++ }
                }
                foreach ($posRaw in @($pl['positions'])) {
                    $pos = Convert-TarkovPosition $posRaw
                    if ($null -eq $pos) { continue }
                    $zones.Add([pscustomobject]@{
                        map            = $(if ($mapInfo) { $mapInfo.name } else { [string]$pl['map'] })
                        normalizedName = $(if ($mapInfo) { $mapInfo.normalizedName } else { '' })
                        mapId          = $(if ($mapInfo) { $mapInfo.id } else { [string]$pl['map'] })
                        x              = $pos.x
                        y              = $pos.y
                        z              = $pos.z
                    })
                }
            }

            $mapIds = New-Object System.Collections.Generic.List[string]
            Add-TarkovIdValues -Target $mapIds -Value $obj['maps']
            $mapNames = New-Object System.Collections.Generic.List[string]
            foreach ($mapId in $mapIds) {
                $mapInfo = Get-TarkovLocalizedMap $mapById $mapsLocale $mapId
                if ($mapInfo -and $mapInfo.name) { [void]$mapNames.Add([string]$mapInfo.name) }
            }

            $category = 'objective'
            if ($typeName -match 'Item|QuestItem') { $category = 'item' }
            $optionalFlag = $false
            if ($obj.ContainsKey('optional') -and $null -ne $obj['optional']) {
                $optionalFlag = [bool]$obj['optional']
            }
            $foundInRaidFlag = $false
            if ($obj.ContainsKey('foundInRaid') -and $null -ne $obj['foundInRaid']) {
                $foundInRaidFlag = [bool]$obj['foundInRaid']
            }

            $mapsArray = New-Object string[] $mapNames.Count
            if ($mapNames.Count -gt 0) { $mapNames.CopyTo($mapsArray) }
            $zonesArray = New-Object object[] $zones.Count
            if ($zones.Count -gt 0) { $zones.CopyTo($zonesArray) }

            $objectiveRecord = New-Object PSObject
            Add-Member -InputObject $objectiveRecord -NotePropertyName id -NotePropertyValue $objId
            Add-Member -InputObject $objectiveRecord -NotePropertyName objectiveType -NotePropertyValue $typeName
            Add-Member -InputObject $objectiveRecord -NotePropertyName category -NotePropertyValue $category
            Add-Member -InputObject $objectiveRecord -NotePropertyName type -NotePropertyValue $jsonType
            Add-Member -InputObject $objectiveRecord -NotePropertyName description -NotePropertyValue $description
            Add-Member -InputObject $objectiveRecord -NotePropertyName optional -NotePropertyValue $optionalFlag
            Add-Member -InputObject $objectiveRecord -NotePropertyName maps -NotePropertyValue $mapsArray
            Add-Member -InputObject $objectiveRecord -NotePropertyName count -NotePropertyValue $obj['count']
            Add-Member -InputObject $objectiveRecord -NotePropertyName foundInRaid -NotePropertyValue $foundInRaidFlag
            Add-Member -InputObject $objectiveRecord -NotePropertyName itemName -NotePropertyValue ([string]$itemInfo.name)
            Add-Member -InputObject $objectiveRecord -NotePropertyName itemShortName -NotePropertyValue ([string]$itemInfo.shortName)
            Add-Member -InputObject $objectiveRecord -NotePropertyName itemIconLink -NotePropertyValue ([string]$itemInfo.iconLink)
            Add-Member -InputObject $objectiveRecord -NotePropertyName zones -NotePropertyValue $zonesArray
            [void]$objectives.Add($objectiveRecord)
        }

        $minLevel = 0
        if ($null -ne $raw['minPlayerLevel']) { $minLevel = [int]$raw['minPlayerLevel'] }

        $taskRecord = New-Object PSObject
        Add-Member -InputObject $taskRecord -NotePropertyName id -NotePropertyValue $id
        Add-Member -InputObject $taskRecord -NotePropertyName tarkovDataId -NotePropertyValue $raw['tarkovDataId']
        Add-Member -InputObject $taskRecord -NotePropertyName name -NotePropertyValue $name
        Add-Member -InputObject $taskRecord -NotePropertyName normalizedName -NotePropertyValue ([string]$raw['normalizedName'])
        Add-Member -InputObject $taskRecord -NotePropertyName trader -NotePropertyValue $traderName
        Add-Member -InputObject $taskRecord -NotePropertyName map -NotePropertyValue $(if ($taskMap) { [string]$taskMap.name } else { '' })
        Add-Member -InputObject $taskRecord -NotePropertyName factionName -NotePropertyValue ([string]$raw['factionName'])
        Add-Member -InputObject $taskRecord -NotePropertyName minPlayerLevel -NotePropertyValue $minLevel
        Add-Member -InputObject $taskRecord -NotePropertyName kappaRequired -NotePropertyValue ([bool]$raw['kappaRequired'])
        Add-Member -InputObject $taskRecord -NotePropertyName lightkeeperRequired -NotePropertyValue ([bool]$raw['lightkeeperRequired'])
        Add-Member -InputObject $taskRecord -NotePropertyName wikiLink -NotePropertyValue ([string]$raw['wikiLink'])
        $objectiveArray = New-Object object[] $objectives.Count
        if ($objectives.Count -gt 0) { $objectives.CopyTo($objectiveArray) }
        Add-Member -InputObject $taskRecord -NotePropertyName objectives -NotePropertyValue $objectiveArray
        [void]$tasks.Add($taskRecord)
    }

    $taskArray = New-Object object[] $tasks.Count
    if ($tasks.Count -gt 0) { $tasks.CopyTo($taskArray) }
    return [pscustomobject]@{
        Tasks        = $taskArray
        MapById      = $mapById
        UnknownMaps  = $unknownMaps
        TaskCount    = $tasks.Count
        GameMode     = $GameMode
        Language     = $Language
    }
}

function Resolve-TarkovQuestMapKey {
    param([string]$NormalizedName)

    switch ($NormalizedName) {
        'factory' { return 'factory' }
        'night-factory' { return 'factory' }
        'the-lab' { return 'thelab' }
        'the-lab-dark' { return 'thelabdark' }
        'streets-of-tarkov' { return 'streetsoftarkov' }
        'ground-zero' { return 'groundzero' }
        'ground-zero-21' { return 'groundzero' }
        'ground-zero-21+' { return 'groundzero' }
        'ground-zero-tutorial' { return 'groundzerotutorial' }
        'the-labyrinth' { return 'thelabyrinth' }
        'icebreaker' { return 'icebreaker' }
        default {
            $k = -join (($NormalizedName -replace '-', '').ToLowerInvariant().ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) })
            if ($k -eq 'lab') { return 'thelab' }
            return $k
        }
    }
}

function ConvertTo-TarkovHashtable {
    param([object]$Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [ValueType]) { return $Value }
    $netType = $Value.GetType().FullName
    if ($netType -eq 'System.Management.Automation.PSCustomObject' -or $netType -eq 'System.Management.Automation.PSObject') {
        $hash = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        foreach ($prop in $Value.PSObject.Properties) {
            if ($prop.MemberType.ToString() -ne 'NoteProperty') { continue }
            $hash[$prop.Name] = ConvertTo-TarkovHashtable $prop.Value
        }
        return $hash
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $hash = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        foreach ($key in $Value.Keys) {
            $hash[[string]$key] = ConvertTo-TarkovHashtable $Value[$key]
        }
        return $hash
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $items = New-Object System.Collections.ArrayList
        foreach ($entry in $Value) {
            [void]$items.Add((ConvertTo-TarkovHashtable $entry))
        }
        return $items.ToArray()
    }
    return $Value
}

function ConvertTo-TarkovJsonText {
    param([object]$Value)

    if ($null -eq $Value) { return 'null' }
    if ($Value -is [bool]) {
        if ($Value) { return 'true' } else { return 'false' }
    }
    if ($Value -is [string]) {
        $escaped = $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
        return '"' + $escaped + '"'
    }
    if ($Value -is [byte] -or $Value -is [int16] -or $Value -is [int] -or $Value -is [int64] -or
        $Value -is [uint16] -or $Value -is [uint32] -or $Value -is [uint64] -or
        $Value -is [decimal] -or $Value -is [single] -or $Value -is [double]) {
        return ([string]$Value)
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($key in $Value.Keys) {
            $parts.Add([string]((ConvertTo-TarkovJsonText ([string]$key)) + ':' + (ConvertTo-TarkovJsonText $Value[$key])))
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($entry in $Value) {
            $parts.Add([string](ConvertTo-TarkovJsonText $entry))
        }
        return '[' + ($parts -join ',') + ']'
    }
    return (ConvertTo-TarkovJsonText ([string]$Value))
}

function ConvertTo-TarkovJsonSnapshot {
    param([object]$Tasks)

    $payload = New-Object System.Collections.ArrayList
    foreach ($task in @($Tasks)) {
        [void]$payload.Add((ConvertTo-TarkovHashtable $task))
    }
    ConvertTo-TarkovJsonText $payload
}

function Read-TarkovJsonSnapshot {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return @() }
    $ser = Get-TarkovJsonSerializer
    $raw = $ser.DeserializeObject((Get-Content $Path -Raw -Encoding UTF8))
    if ($raw -is [System.Collections.IDictionary] -and $raw.ContainsKey('value')) {
        $raw = $raw['value']
    }
    $tasks = New-Object System.Collections.Generic.List[object]
    if ($raw -is [System.Collections.IDictionary]) {
        $tasks.Add((ConvertTo-TarkovPlainObject $raw))
    }
    elseif ($raw -is [System.Collections.IEnumerable]) {
        foreach ($entry in $raw) {
            $tasks.Add((ConvertTo-TarkovPlainObject $entry))
        }
    }
    if ($tasks.Count -eq 0) { return @() }
    $arr = New-Object object[] $tasks.Count
    $tasks.CopyTo($arr)
    return $arr
}

function ConvertTo-TarkovPlainObject {
    param([object]$Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [System.Collections.IDictionary]) {
        $hash = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $hash[[string]$key] = ConvertTo-TarkovPlainObject $Value[$key]
        }
        return [pscustomobject]$hash
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $list = New-Object System.Collections.Generic.List[object]
        foreach ($entry in $Value) {
            [void]$list.Add((ConvertTo-TarkovPlainObject $entry))
        }
        if ($list.Count -eq 0) { return , @() }
        $arr = New-Object object[] $list.Count
        $list.CopyTo($arr)
        return , $arr
    }
    return $Value
}

function Get-TarkovAppQuestMapKeys {
    return @(
        'factory', 'customs', 'woods', 'shoreline', 'interchange', 'thelab',
        'reserve', 'lighthouse', 'streetsoftarkov', 'groundzero', 'terminal',
        'thelabyrinth', 'icebreaker'
    )
}

function Get-TarkovAppBossMapKeys {
    return @(
        'factory', 'customs', 'woods', 'shoreline', 'interchange', 'thelab',
        'reserve', 'lighthouse', 'streetsoftarkov', 'groundzero', 'terminal',
        'labyrinth', 'icebreaker'
    )
}

function Resolve-TarkovAppMapKey([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    $key = -join ($Name.ToLowerInvariant().ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) })
    switch ($key) {
        'nightfactory' { return 'factory' }
        'groundzero21' { return 'groundzero' }
        'lab' { return 'thelab' }
        'thelabyrinth' { return 'labyrinth' }
        default { return $key }
    }
}

function Get-TarkovAppMapDisplayName([string]$AppKey) {
    switch ($AppKey) {
        'factory' { return 'Factory' }
        'customs' { return 'Customs' }
        'woods' { return 'Woods' }
        'lighthouse' { return 'Lighthouse' }
        'shoreline' { return 'Shoreline' }
        'reserve' { return 'Reserve' }
        'interchange' { return 'Interchange' }
        'streetsoftarkov' { return 'Streets of Tarkov' }
        'thelab' { return 'The Lab' }
        'groundzero' { return 'Ground Zero 21+' }
        'terminal' { return 'Terminal' }
        'labyrinth' { return 'The Labyrinth' }
        'thelabyrinth' { return 'The Labyrinth' }
        'icebreaker' { return 'Icebreaker' }
        default { return $AppKey }
    }
}

function Resolve-TarkovLocalizedText {
    param(
        [object]$Locale,
        [string]$Key
    )

    if ([string]::IsNullOrWhiteSpace($Key)) { return '' }
    $translated = Get-TarkovLocaleString $Locale $Key
    if ($translated) { return $translated }
    foreach ($suffix in @(' name', ' Name', ' Nickname', ' Description')) {
        $translated = Get-TarkovLocaleString $Locale ($Key + $suffix)
        if ($translated) { return $translated }
    }
    return $Key
}

function Convert-TarkovIdList([object]$Value) {
    $ids = New-Object System.Collections.Generic.List[string]
    Add-TarkovIdValues -Target $ids -Value $Value
    if ($ids.Count -eq 0) { return @() }
    $arr = New-Object string[] $ids.Count
    $ids.CopyTo($arr)
    return $arr
}

function Convert-TarkovMapPosition([object]$Position) {
    if ($null -eq $Position -or $Position -isnot [System.Collections.IDictionary]) { return $null }
    if (-not $Position.ContainsKey('x') -or -not $Position.ContainsKey('z')) { return $null }
    $y = 0.0
    if ($Position.ContainsKey('y') -and $null -ne $Position['y']) { $y = [double]$Position['y'] }
    return [pscustomobject]@{
        x = [double]$Position['x']
        y = $y
        z = [double]$Position['z']
    }
}

function Get-TarkovJsonMaps {
    param(
        [string]$GameMode = 'regular',
        [string]$Language = 'en'
    )

    $mapsDoc = Get-TarkovJsonDocument "$GameMode/maps"
    $mapsLocale = Get-TarkovLocaleLookup (Get-TarkovJsonDocument "$GameMode/maps_$Language")
    $itemsLocale = Get-TarkovLocaleLookup (Get-TarkovJsonDocument "$GameMode/items_$Language")

    $data = $mapsDoc['data']
    $rawMaps = $data['maps']
    $mobs = $data['mobs']
    if (-not $mobs) { $mobs = @{} }

    $maps = New-Object System.Collections.Generic.List[object]
    foreach ($mapKv in $rawMaps.GetEnumerator()) {
        $raw = $mapKv.Value
        if ($raw -isnot [System.Collections.IDictionary]) { continue }

        $switchById = @{}
        foreach ($sw in @($raw['switches'])) {
            if ($null -eq $sw -or $sw -isnot [System.Collections.IDictionary]) { continue }
            $swId = [string]$sw['id']
            if (-not $swId) { continue }
            $switchById[$swId] = $sw
        }

        $rawExtracts = New-Object System.Collections.Generic.List[object]
        foreach ($extract in @($raw['extracts'])) {
            if ($null -eq $extract -or $extract -isnot [System.Collections.IDictionary]) { continue }
            [void]$rawExtracts.Add($extract)
        }

        $switchIdCounts = @{}
        foreach ($extract in $rawExtracts) {
            $seen = @{}
            $ids = New-Object System.Collections.Generic.List[string]
            Add-TarkovIdValues -Target $ids -Value $extract['switches']
            Add-TarkovIdValues -Target $ids -Value $extract['switch']
            foreach ($switchId in $ids) {
                if ($seen.ContainsKey($switchId)) { continue }
                $seen[$switchId] = $true
                if (-not $switchIdCounts.ContainsKey($switchId)) { $switchIdCounts[$switchId] = 0 }
                $switchIdCounts[$switchId]++
            }
        }

        $leakedSwitchIds = @{}
        if ($rawExtracts.Count -gt 1) {
            foreach ($switchId in $switchIdCounts.Keys) {
                if ($switchIdCounts[$switchId] -eq $rawExtracts.Count) {
                    $leakedSwitchIds[$switchId] = $true
                }
            }
        }

        $extracts = New-Object System.Collections.ArrayList
        foreach ($extract in $rawExtracts) {
            $switchIds = New-Object System.Collections.Generic.List[string]
            Add-TarkovIdValues -Target $switchIds -Value $extract['switches']
            Add-TarkovIdValues -Target $switchIds -Value $extract['switch']

            $switchObjs = New-Object System.Collections.ArrayList
            $usedSwitchIds = @{}
            foreach ($switchId in $switchIds) {
                if ($usedSwitchIds.ContainsKey($switchId)) { continue }
                if ($leakedSwitchIds.ContainsKey($switchId)) { continue }
                $usedSwitchIds[$switchId] = $true
                $swRaw = $null
                if ($switchById.ContainsKey($switchId)) { $swRaw = $switchById[$switchId] }
                $swName = $switchId
                if ($swRaw) {
                    $localized = Resolve-TarkovLocalizedText $mapsLocale ([string]$swRaw['name'])
                    if ($localized) { $swName = $localized }
                }
                [void]$switchObjs.Add([pscustomobject]@{
                    id   = $switchId
                    name = $swName
                })
            }

            $extractName = Resolve-TarkovLocalizedText $mapsLocale ([string]$extract['name'])
            if ($extractName -and $extractName.Length -ge 4) {
                foreach ($switchId in $switchById.Keys) {
                    if ($usedSwitchIds.ContainsKey($switchId)) { continue }
                    $swRaw = $switchById[$switchId]
                    $swName = Resolve-TarkovLocalizedText $mapsLocale ([string]$swRaw['name'])
                    if (-not $swName) { continue }
                    if ($swName.IndexOf($extractName, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
                    $usedSwitchIds[$switchId] = $true
                    [void]$switchObjs.Add([pscustomobject]@{
                        id   = $switchId
                        name = $swName
                    })
                }
            }

            $transfer = $null
            if ($extract['transferItem'] -and $extract['transferItem'] -is [System.Collections.IDictionary]) {
                $itemId = [string]$extract['transferItem']['item']
                $itemName = Resolve-TarkovLocalizedText $itemsLocale ($itemId + ' Name')
                if (-not $itemName) { $itemName = $itemId }
                $count = $extract['transferItem']['count']
                $quantity = $extract['transferItem']['quantity']
                if ($null -eq $quantity) { $quantity = $count }
                $transfer = [pscustomobject]@{
                    item     = [pscustomobject]@{ id = $itemId; name = $itemName }
                    count    = $count
                    quantity = $quantity
                }
            }

            [void]$extracts.Add([pscustomobject]@{
                id           = [string]$extract['id']
                name         = (Resolve-TarkovLocalizedText $mapsLocale ([string]$extract['name']))
                faction      = [string]$extract['faction']
                switches     = $switchObjs.ToArray()
                transferItem = $transfer
                position     = (Convert-TarkovMapPosition $extract['position'])
            })
        }

        $transits = New-Object System.Collections.ArrayList
        foreach ($transit in @($raw['transits'])) {
            if ($null -eq $transit -or $transit -isnot [System.Collections.IDictionary]) { continue }
            [void]$transits.Add([pscustomobject]@{
                id          = [string]$transit['id']
                description = (Resolve-TarkovLocalizedText $mapsLocale ([string]$transit['description']))
                conditions  = (Resolve-TarkovLocalizedText $mapsLocale ([string]$transit['conditions']))
                position    = (Convert-TarkovMapPosition $transit['position'])
            })
        }

        $spawns = New-Object System.Collections.ArrayList
        foreach ($spawn in @($raw['spawns'])) {
            if ($null -eq $spawn -or $spawn -isnot [System.Collections.IDictionary]) { continue }
            $sides = Convert-TarkovIdList $spawn['sides']
            $categories = Convert-TarkovIdList $spawn['categories']
            [void]$spawns.Add([pscustomobject]@{
                zoneName   = [string]$spawn['zoneName']
                sides      = $sides
                categories = $categories
                position   = (Convert-TarkovMapPosition $spawn['position'])
            })
        }

        $bosses = New-Object System.Collections.ArrayList
        foreach ($bossEntry in @($raw['bosses'])) {
            if ($null -eq $bossEntry -or $bossEntry -isnot [System.Collections.IDictionary]) { continue }
            $mobId = [string]$bossEntry['mob']
            $mob = $null
            if ($mobId -and $mobs.ContainsKey($mobId)) { $mob = $mobs[$mobId] }
            $bossName = $mobId
            $bossNorm = $mobId
            if ($mob) {
                $bossName = Resolve-TarkovLocalizedText $mapsLocale ([string]$mob['name'])
                if (-not $bossName) { $bossName = [string]$mob['normalizedName'] }
                $bossNorm = [string]$mob['normalizedName']
            }

            $locations = New-Object System.Collections.ArrayList
            foreach ($loc in @($bossEntry['spawnLocations'])) {
                if ($null -eq $loc -or $loc -isnot [System.Collections.IDictionary]) { continue }
                $chance = 0.0
                if ($null -ne $loc['chance']) { $chance = [double]$loc['chance'] }
                [void]$locations.Add([pscustomobject]@{
                    spawnKey = [string]$loc['spawnKey']
                    name     = (Resolve-TarkovLocalizedText $mapsLocale ([string]$loc['name']))
                    chance   = $chance
                })
            }

            [void]$bosses.Add([pscustomobject]@{
                boss = [pscustomobject]@{
                    id             = $mobId
                    name           = $bossName
                    normalizedName = $bossNorm
                }
                spawnLocations = $locations.ToArray()
            })
        }

        $switches = New-Object System.Collections.ArrayList
        foreach ($sw in @($raw['switches'])) {
            if ($null -eq $sw -or $sw -isnot [System.Collections.IDictionary]) { continue }
            [void]$switches.Add([pscustomobject]@{
                id       = [string]$sw['id']
                name     = (Resolve-TarkovLocalizedText $mapsLocale ([string]$sw['name']))
                position = (Convert-TarkovMapPosition $sw['position'])
            })
        }

        $hazards = New-Object System.Collections.ArrayList
        foreach ($hazard in @($raw['hazards'])) {
            if ($null -eq $hazard -or $hazard -isnot [System.Collections.IDictionary]) { continue }
            [void]$hazards.Add([pscustomobject]@{
                name       = (Resolve-TarkovLocalizedText $mapsLocale ([string]$hazard['name']))
                hazardType = [string]$hazard['hazardType']
                position   = (Convert-TarkovMapPosition $hazard['position'])
            })
        }

        $enemies = New-Object System.Collections.ArrayList
        foreach ($enemy in (Convert-TarkovIdList $raw['enemies'])) {
            [void]$enemies.Add((Resolve-TarkovLocalizedText $mapsLocale $enemy))
        }

        $mapRecord = New-Object PSObject
        Add-Member -InputObject $mapRecord -NotePropertyName id -NotePropertyValue ([string]$raw['id'])
        Add-Member -InputObject $mapRecord -NotePropertyName name -NotePropertyValue (Resolve-TarkovLocalizedText $mapsLocale ([string]$raw['name']))
        Add-Member -InputObject $mapRecord -NotePropertyName normalizedName -NotePropertyValue ([string]$raw['normalizedName'])
        Add-Member -InputObject $mapRecord -NotePropertyName tarkovDataId -NotePropertyValue $raw['tarkovDataId']
        Add-Member -InputObject $mapRecord -NotePropertyName description -NotePropertyValue (Resolve-TarkovLocalizedText $mapsLocale ([string]$raw['description']))
        Add-Member -InputObject $mapRecord -NotePropertyName raidDuration -NotePropertyValue $raw['raidDuration']
        Add-Member -InputObject $mapRecord -NotePropertyName enemies -NotePropertyValue $enemies.ToArray()
        Add-Member -InputObject $mapRecord -NotePropertyName extracts -NotePropertyValue $extracts.ToArray()
        Add-Member -InputObject $mapRecord -NotePropertyName transits -NotePropertyValue $transits.ToArray()
        Add-Member -InputObject $mapRecord -NotePropertyName spawns -NotePropertyValue $spawns.ToArray()
        Add-Member -InputObject $mapRecord -NotePropertyName bosses -NotePropertyValue $bosses.ToArray()
        Add-Member -InputObject $mapRecord -NotePropertyName switches -NotePropertyValue $switches.ToArray()
        Add-Member -InputObject $mapRecord -NotePropertyName hazards -NotePropertyValue $hazards.ToArray()
        [void]$maps.Add($mapRecord)
    }

    if ($maps.Count -eq 0) { return @() }
    $arr = New-Object object[] $maps.Count
    $maps.CopyTo($arr)
    return $arr
}

function Get-TarkovJsonMap {
    param(
        [Parameter(Mandatory = $true)][string]$NormalizedName,
        [string]$GameMode = 'regular',
        [string]$Language = 'en'
    )

    foreach ($map in @(Get-TarkovJsonMaps -GameMode $GameMode -Language $Language)) {
        if ([string]$map.normalizedName -eq $NormalizedName) {
            return $map
        }
    }
    return $null
}
