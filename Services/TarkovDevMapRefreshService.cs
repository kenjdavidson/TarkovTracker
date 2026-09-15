using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovTracker.Models;

namespace TarkovTracker.Services;

public sealed class MapDataRefreshResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = "";
    public int ExtractMaps { get; init; }
    public int TransitMaps { get; init; }
    public int SpawnMaps { get; init; }
    public int BossMarkers { get; init; }
    public int QuestMarkers { get; init; }
    public int SwitchMaps { get; init; }
    public bool HazardsUpdated { get; init; }
    public DateTime RefreshedUtc { get; init; } = DateTime.UtcNow;
}

public static class TarkovDevMapRefreshService
{
    private const string GameMode = "regular";
    private const string Language = "en";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null
    };

    private static readonly HashSet<string> AppMapKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "factory", "customs", "woods", "shoreline", "interchange", "thelab",
        "reserve", "lighthouse", "streetsoftarkov", "groundzero", "terminal",
        "labyrinth", "thelabyrinth", "icebreaker"
    };

    public static async Task<MapDataRefreshResult> RefreshAsync(
        string overlayConfigDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(overlayConfigDirectory);

        progress?.Report("Downloading maps…");
        using JsonDocument mapsDoc = await TarkovDevJsonClient.GetDocumentAsync(
            $"{GameMode}/maps", cancellationToken);
        using JsonDocument mapsLocaleDoc = await TarkovDevJsonClient.GetDocumentAsync(
            $"{GameMode}/maps_{Language}", cancellationToken);
        using JsonDocument itemsLocaleDoc = await TarkovDevJsonClient.GetDocumentAsync(
            $"{GameMode}/items_{Language}", cancellationToken);

        progress?.Report("Downloading quests…");
        using JsonDocument tasksDoc = await TarkovDevJsonClient.GetDocumentAsync(
            $"{GameMode}/tasks", cancellationToken);
        using JsonDocument tasksLocaleDoc = await TarkovDevJsonClient.GetDocumentAsync(
            $"{GameMode}/tasks_{Language}", cancellationToken);
        using JsonDocument tradersDoc = await TarkovDevJsonClient.GetDocumentAsync(
            $"{GameMode}/traders", cancellationToken);
        using JsonDocument tradersLocaleDoc = await TarkovDevJsonClient.GetDocumentAsync(
            $"{GameMode}/traders_{Language}", cancellationToken);

        JsonElement mapsLocale = LocaleRoot(mapsLocaleDoc);
        JsonElement itemsLocale = LocaleRoot(itemsLocaleDoc);
        JsonElement tasksLocale = LocaleRoot(tasksLocaleDoc);
        JsonElement tradersLocale = LocaleRoot(tradersLocaleDoc);

        ParsedMaps parsed = ParseMaps(mapsDoc, mapsLocale, itemsLocale);

        progress?.Report("Writing extracts and map markers…");
        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "tarkov_extracts_raw.json"),
            new TarkovExtractsRoot { Data = new TarkovExtractsData { Maps = parsed.ExtractMaps } },
            cancellationToken);
        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "tarkov_transits_raw.json"),
            new TarkovTransitsRoot { Data = new TarkovTransitsData { Maps = parsed.TransitMaps } },
            cancellationToken);
        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "tarkov_spawns_raw.json"),
            new TarkovSpawnsRoot { Data = new TarkovSpawnsData { Maps = parsed.SpawnMaps } },
            cancellationToken);
        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "tarkov_switches_raw.json"),
            new TarkovSwitchesRoot { Data = new TarkovSwitchesData { Maps = parsed.SwitchMaps } },
            cancellationToken);
        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "tarkov_boss_spawn_markers.json"),
            parsed.BossMarkersByMap,
            cancellationToken);

        bool hazardsUpdated = parsed.HazardOutlineCount > 0;
        if (hazardsUpdated)
        {
            await WriteJsonAtomicAsync(
                Path.Combine(overlayConfigDirectory, "tarkov_hazards_raw.json"),
                new TarkovHazardsRoot { Data = new TarkovHazardsData { Maps = parsed.HazardMaps } },
                cancellationToken);
        }

        progress?.Report("Writing quest markers…");
        Dictionary<string, List<QuestMarker>> quests = BuildQuestMarkers(
            tasksDoc,
            tradersDoc,
            tasksLocale,
            tradersLocale,
            itemsLocale,
            parsed.MapById);

        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "tarkov_quest_markers.json"),
            quests,
            cancellationToken);

        int questCount = quests.Values.Sum(list => list.Count);
        int bossCount = parsed.BossMarkersByMap.Values.Sum(list => list.Count);
        DateTime refreshedUtc = DateTime.UtcNow;

        var result = new MapDataRefreshResult
        {
            Succeeded = true,
            ExtractMaps = parsed.ExtractMaps.Count,
            TransitMaps = parsed.TransitMaps.Count,
            SpawnMaps = parsed.SpawnMaps.Count,
            SwitchMaps = parsed.SwitchMaps.Count,
            BossMarkers = bossCount,
            QuestMarkers = questCount,
            HazardsUpdated = hazardsUpdated,
            RefreshedUtc = refreshedUtc,
            Message = hazardsUpdated
                ? $"Updated {parsed.ExtractMaps.Count} extract maps, {questCount} quest markers, {bossCount} boss markers."
                : $"Updated {parsed.ExtractMaps.Count} extract maps, {questCount} quest markers, {bossCount} boss markers. Hazards kept (API had no outlines)."
        };

        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "map-data-refresh.json"),
            result,
            cancellationToken);

        return result;
    }

    public static MapDataRefreshResult? TryReadLastRefresh(string overlayConfigDirectory)
    {
        string path = Path.Combine(overlayConfigDirectory, "map-data-refresh.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MapDataRefreshResult>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class ParsedMaps
    {
        public List<TarkovExtractMap> ExtractMaps { get; } = new();
        public List<TarkovTransitMap> TransitMaps { get; } = new();
        public List<TarkovSpawnMap> SpawnMaps { get; } = new();
        public List<TarkovSwitchMap> SwitchMaps { get; } = new();
        public List<TarkovHazardMap> HazardMaps { get; } = new();
        public Dictionary<string, List<BossSpawnMarker>> BossMarkersByMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MapLookup> MapById { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int HazardOutlineCount { get; set; }
    }

    private sealed class MapLookup
    {
        public string Id { get; init; } = "";
        public string NormalizedName { get; init; } = "";
        public string Name { get; init; } = "";
        public string AppKey { get; init; } = "";
    }

    private static ParsedMaps ParseMaps(
        JsonDocument mapsDoc,
        JsonElement mapsLocale,
        JsonElement itemsLocale)
    {
        var parsed = new ParsedMaps();
        JsonElement data = mapsDoc.RootElement.GetProperty("data");
        JsonElement maps = data.GetProperty("maps");
        JsonElement mobs = data.TryGetProperty("mobs", out JsonElement mobsElement)
            ? mobsElement
            : default;

        var extractsByName = new Dictionary<string, TarkovExtractMap>(StringComparer.OrdinalIgnoreCase);
        var transitsByName = new Dictionary<string, TarkovTransitMap>(StringComparer.OrdinalIgnoreCase);
        var spawnsByAppKey = new Dictionary<string, List<SpawnInfo>>(StringComparer.OrdinalIgnoreCase);
        var switchesByName = new Dictionary<string, TarkovSwitchMap>(StringComparer.OrdinalIgnoreCase);
        var hazardsByName = new Dictionary<string, TarkovHazardMap>(StringComparer.OrdinalIgnoreCase);
        var bossByAppKey = new Dictionary<string, List<BossSpawnMarker>>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty mapProperty in maps.EnumerateObject())
        {
            JsonElement raw = mapProperty.Value;
            if (raw.ValueKind != JsonValueKind.Object)
                continue;

            string id = ReadString(raw, "id");
            string normalizedName = ReadString(raw, "normalizedName");
            string displayName = LocaleValue(mapsLocale, ReadString(raw, "name"));
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = TitleFromNormalized(normalizedName);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = id;

            string appKey = ResolveAppMapKey(normalizedName);
            parsed.MapById[id] = new MapLookup
            {
                Id = id,
                NormalizedName = normalizedName,
                Name = displayName,
                AppKey = appKey
            };

            Dictionary<string, JsonElement> switchById = IndexSwitches(raw);
            List<ExtractInfo> extracts = ParseExtracts(raw, mapsLocale, itemsLocale, switchById);
            AddOrMergeExtracts(extractsByName, displayName, extracts);

            List<TransitInfo> transits = ParseTransits(raw, mapsLocale);
            if (transits.Count > 0)
                AddOrMergeTransits(transitsByName, displayName, transits);

            List<SpawnInfo> spawns = ParseSpawns(raw);
            if (spawns.Count > 0 && !string.IsNullOrWhiteSpace(appKey))
            {
                if (!spawnsByAppKey.TryGetValue(appKey, out List<SpawnInfo>? spawnList))
                {
                    spawnList = new List<SpawnInfo>();
                    spawnsByAppKey[appKey] = spawnList;
                }

                foreach (SpawnInfo spawn in spawns)
                {
                    if (!spawnList.Any(existing => SpawnsMatch(existing, spawn)))
                        spawnList.Add(spawn);
                }
            }

            List<MapSwitchInfo> switches = ParseSwitchMarkers(raw, mapsLocale);
            if (switches.Count > 0)
                AddOrMergeSwitches(switchesByName, displayName, switches);

            List<HazardInfo> hazards = ParseHazards(raw, mapsLocale);
            parsed.HazardOutlineCount += hazards.Count(h => h.Outline is { Count: >= 3 });
            if (hazards.Count > 0)
                AddOrMergeHazards(hazardsByName, displayName, hazards);

            if (!string.IsNullOrWhiteSpace(appKey) && AppMapKeys.Contains(appKey))
            {
                List<BossSpawnMarker> bosses = ParseBossMarkers(raw, mapsLocale, mobs, spawns);
                if (bosses.Count > 0)
                {
                    if (!bossByAppKey.TryGetValue(appKey, out List<BossSpawnMarker>? bossList))
                    {
                        bossList = new List<BossSpawnMarker>();
                        bossByAppKey[appKey] = bossList;
                    }

                    bossList.AddRange(bosses);
                }
            }
        }

        parsed.ExtractMaps.AddRange(extractsByName.Values.OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase));
        parsed.TransitMaps.AddRange(transitsByName.Values.OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase));
        parsed.SwitchMaps.AddRange(switchesByName.Values.OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase));
        parsed.HazardMaps.AddRange(hazardsByName.Values.OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase));

        foreach (string key in spawnsByAppKey.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            parsed.SpawnMaps.Add(new TarkovSpawnMap
            {
                Name = DisplayNameForAppKey(key),
                Spawns = spawnsByAppKey[key]
            });
        }

        foreach (string key in bossByAppKey.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            parsed.BossMarkersByMap[key] = bossByAppKey[key];

        return parsed;
    }

    private static List<ExtractInfo> ParseExtracts(
        JsonElement rawMap,
        JsonElement mapsLocale,
        JsonElement itemsLocale,
        Dictionary<string, JsonElement> switchById)
    {
        var extracts = new List<ExtractInfo>();
        if (!rawMap.TryGetProperty("extracts", out JsonElement rawExtracts) ||
            rawExtracts.ValueKind != JsonValueKind.Array)
        {
            return extracts;
        }

        List<JsonElement> extractElements = rawExtracts.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .ToList();

        var switchUseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement extract in extractElements)
        {
            foreach (string switchId in ReadIdList(extract, "switches", "switch").Distinct(StringComparer.OrdinalIgnoreCase))
                switchUseCounts[switchId] = switchUseCounts.GetValueOrDefault(switchId) + 1;
        }

        var leakedSwitchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (extractElements.Count > 1)
        {
            foreach ((string switchId, int count) in switchUseCounts)
            {
                if (count == extractElements.Count)
                    leakedSwitchIds.Add(switchId);
            }
        }

        foreach (JsonElement extract in extractElements)
        {
            string extractName = LocalizedName(mapsLocale, ReadString(extract, "name"));
            var usedSwitchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var switches = new List<ExtractSwitch>();

            foreach (string switchId in ReadIdList(extract, "switches", "switch"))
            {
                if (leakedSwitchIds.Contains(switchId) || !usedSwitchIds.Add(switchId))
                    continue;

                string switchName = switchId;
                if (switchById.TryGetValue(switchId, out JsonElement switchRaw))
                {
                    string localized = LocaleValue(mapsLocale, ReadString(switchRaw, "name"));
                    if (!string.IsNullOrWhiteSpace(localized))
                        switchName = localized;
                }

                switches.Add(new ExtractSwitch { Id = switchId, Name = switchName });
            }

            if (extractName.Length >= 4)
            {
                foreach ((string switchId, JsonElement switchRaw) in switchById)
                {
                    if (!usedSwitchIds.Add(switchId))
                        continue;

                    string switchName = LocaleValue(mapsLocale, ReadString(switchRaw, "name"));
                    if (string.IsNullOrWhiteSpace(switchName) ||
                        switchName.IndexOf(extractName, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        usedSwitchIds.Remove(switchId);
                        continue;
                    }

                    switches.Add(new ExtractSwitch { Id = switchId, Name = switchName });
                }
            }

            ExtractTransferItem? transfer = ParseTransferItem(extract, itemsLocale);
            var requirements = new List<string>();
            if (!string.IsNullOrWhiteSpace(transfer?.Item?.Name))
            {
                double count = transfer!.Count > 0 ? transfer.Count : transfer.Quantity;
                requirements.Add(count > 1
                    ? $"Requires item: {transfer.Item!.Name} (x{(int)count})"
                    : $"Requires item: {transfer.Item!.Name}");
            }

            foreach (ExtractSwitch sw in switches)
            {
                string switchName = string.IsNullOrWhiteSpace(sw.Name) ? sw.Id : sw.Name;
                if (!string.IsNullOrWhiteSpace(switchName))
                    requirements.Add("Requires switch: " + switchName);
            }

            extracts.Add(new ExtractInfo
            {
                Id = ReadString(extract, "id"),
                Name = extractName,
                Faction = ReadString(extract, "faction"),
                Requirements = requirements,
                Switches = switches,
                TransferItem = transfer,
                Position = ReadPosition(extract)
            });
        }

        return extracts;
    }

    private static ExtractTransferItem? ParseTransferItem(JsonElement extract, JsonElement itemsLocale)
    {
        if (!extract.TryGetProperty("transferItem", out JsonElement transfer) ||
            transfer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string itemId = ReadString(transfer, "item");
        if (string.IsNullOrWhiteSpace(itemId) &&
            transfer.TryGetProperty("item", out JsonElement itemObj) &&
            itemObj.ValueKind == JsonValueKind.Object)
        {
            itemId = ReadString(itemObj, "id");
        }

        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        string itemName = LocaleValue(itemsLocale, itemId + " Name");
        if (string.IsNullOrWhiteSpace(itemName))
            itemName = itemId;

        double count = ReadDouble(transfer, "count");
        double quantity = ReadDouble(transfer, "quantity");
        if (quantity <= 0)
            quantity = count;
        if (count <= 0)
            count = quantity > 0 ? quantity : 1;

        return new ExtractTransferItem
        {
            Item = new ExtractTransferItemRef { Id = itemId, Name = itemName },
            Count = count,
            Quantity = quantity
        };
    }

    private static List<TransitInfo> ParseTransits(JsonElement rawMap, JsonElement mapsLocale)
    {
        var transits = new List<TransitInfo>();
        if (!rawMap.TryGetProperty("transits", out JsonElement raw) || raw.ValueKind != JsonValueKind.Array)
            return transits;

        foreach (JsonElement transit in raw.EnumerateArray())
        {
            if (transit.ValueKind != JsonValueKind.Object)
                continue;

            transits.Add(new TransitInfo
            {
                Description = LocaleValue(mapsLocale, ReadString(transit, "description")),
                Conditions = NullIfEmpty(LocaleValue(mapsLocale, ReadString(transit, "conditions"))),
                Position = ReadPosition(transit)
            });
        }

        return transits;
    }

    private static List<SpawnInfo> ParseSpawns(JsonElement rawMap)
    {
        var spawns = new List<SpawnInfo>();
        if (!rawMap.TryGetProperty("spawns", out JsonElement raw) || raw.ValueKind != JsonValueKind.Array)
            return spawns;

        foreach (JsonElement spawn in raw.EnumerateArray())
        {
            if (spawn.ValueKind != JsonValueKind.Object)
                continue;

            MapPosition? position = ReadPosition(spawn);
            if (position == null)
                continue;

            spawns.Add(new SpawnInfo
            {
                ZoneName = ReadString(spawn, "zoneName"),
                Sides = ReadIdList(spawn, "sides"),
                Categories = ReadIdList(spawn, "categories"),
                Position = position
            });
        }

        return spawns;
    }

    private static List<MapSwitchInfo> ParseSwitchMarkers(JsonElement rawMap, JsonElement mapsLocale)
    {
        var switches = new List<MapSwitchInfo>();
        if (!rawMap.TryGetProperty("switches", out JsonElement raw) || raw.ValueKind != JsonValueKind.Array)
            return switches;

        foreach (JsonElement sw in raw.EnumerateArray())
        {
            if (sw.ValueKind != JsonValueKind.Object)
                continue;

            switches.Add(new MapSwitchInfo
            {
                Id = ReadString(sw, "id"),
                Name = LocaleValue(mapsLocale, ReadString(sw, "name")),
                Position = ReadPosition(sw)
            });
        }

        return switches;
    }

    private static List<HazardInfo> ParseHazards(JsonElement rawMap, JsonElement mapsLocale)
    {
        var hazards = new List<HazardInfo>();
        if (!rawMap.TryGetProperty("hazards", out JsonElement raw) || raw.ValueKind != JsonValueKind.Array)
            return hazards;

        foreach (JsonElement hazard in raw.EnumerateArray())
        {
            if (hazard.ValueKind != JsonValueKind.Object)
                continue;

            var outline = new List<MapOutlinePoint>();
            if (hazard.TryGetProperty("outline", out JsonElement outlineElement) &&
                outlineElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement point in outlineElement.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Object)
                        continue;
                    outline.Add(new MapOutlinePoint
                    {
                        X = ReadDouble(point, "x"),
                        Z = ReadDouble(point, "z")
                    });
                }
            }

            hazards.Add(new HazardInfo
            {
                Name = LocaleValue(mapsLocale, ReadString(hazard, "name")),
                HazardType = ReadString(hazard, "hazardType"),
                Position = ReadPosition(hazard),
                Outline = outline
            });
        }

        return hazards;
    }

    private static List<BossSpawnMarker> ParseBossMarkers(
        JsonElement rawMap,
        JsonElement mapsLocale,
        JsonElement mobs,
        List<SpawnInfo> mapSpawns)
    {
        var markers = new List<BossSpawnMarker>();
        if (!rawMap.TryGetProperty("bosses", out JsonElement bosses) || bosses.ValueKind != JsonValueKind.Array)
            return markers;

        foreach (JsonElement bossEntry in bosses.EnumerateArray())
        {
            if (bossEntry.ValueKind != JsonValueKind.Object)
                continue;

            string mobId = ReadString(bossEntry, "mob");
            string bossName = mobId;
            string bossNorm = mobId;
            if (!string.IsNullOrWhiteSpace(mobId) &&
                mobs.ValueKind == JsonValueKind.Object &&
                mobs.TryGetProperty(mobId, out JsonElement mob) &&
                mob.ValueKind == JsonValueKind.Object)
            {
                string localized = LocaleValue(mapsLocale, ReadString(mob, "name"));
                bossName = string.IsNullOrWhiteSpace(localized)
                    ? ReadString(mob, "normalizedName")
                    : localized;
                bossNorm = ReadString(mob, "normalizedName");
            }

            if (!bossEntry.TryGetProperty("spawnLocations", out JsonElement locations) ||
                locations.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement loc in locations.EnumerateArray())
            {
                if (loc.ValueKind != JsonValueKind.Object)
                    continue;

                string spawnKey = ReadString(loc, "spawnKey");
                SpawnInfo? match = mapSpawns.FirstOrDefault(spawn =>
                    string.Equals(spawn.ZoneName, spawnKey, StringComparison.OrdinalIgnoreCase) &&
                    spawn.Categories.Any(cat => string.Equals(cat, "boss", StringComparison.OrdinalIgnoreCase)));
                match ??= mapSpawns.FirstOrDefault(spawn =>
                    string.Equals(spawn.ZoneName, spawnKey, StringComparison.OrdinalIgnoreCase));

                if (match?.Position == null)
                    continue;

                markers.Add(new BossSpawnMarker
                {
                    BossName = bossName,
                    NormalizedName = bossNorm,
                    LocationName = LocaleValue(mapsLocale, ReadString(loc, "name")),
                    ZoneName = spawnKey,
                    SpawnChance = ReadDouble(loc, "chance"),
                    X = match.Position.X,
                    Y = match.Position.Y,
                    Z = match.Position.Z
                });
            }
        }

        return markers;
    }

    private static Dictionary<string, List<QuestMarker>> BuildQuestMarkers(
        JsonDocument tasksDoc,
        JsonDocument tradersDoc,
        JsonElement tasksLocale,
        JsonElement tradersLocale,
        JsonElement itemsLocale,
        Dictionary<string, MapLookup> mapById)
    {
        var byMap = new Dictionary<string, List<QuestMarker>>(StringComparer.OrdinalIgnoreCase);
        JsonElement data = tasksDoc.RootElement.GetProperty("data");
        JsonElement tasks = data.GetProperty("tasks");
        JsonElement questItems = data.TryGetProperty("questItems", out JsonElement questItemsElement)
            ? questItemsElement
            : default;
        JsonElement tradersData = tradersDoc.RootElement.TryGetProperty("data", out JsonElement tradersRoot)
            ? tradersRoot
            : default;

        foreach (JsonProperty taskProperty in tasks.EnumerateObject())
        {
            JsonElement raw = taskProperty.Value;
            if (raw.ValueKind != JsonValueKind.Object)
                continue;

            string taskId = ReadString(raw, "id");
            string nameKey = ReadString(raw, "name");
            string questName = LocaleValue(tasksLocale, nameKey);
            if (string.IsNullOrWhiteSpace(questName))
                questName = LocaleValue(tasksLocale, taskId + " name");
            if (string.IsNullOrWhiteSpace(questName))
                questName = nameKey;

            string traderId = ReadString(raw, "trader");
            string traderName = LocaleValue(tradersLocale, traderId + " Nickname");
            if (string.IsNullOrWhiteSpace(traderName) &&
                tradersData.ValueKind == JsonValueKind.Object &&
                tradersData.TryGetProperty(traderId, out JsonElement trader) &&
                trader.ValueKind == JsonValueKind.Object)
            {
                traderName = ReadString(trader, "normalizedName");
            }

            string questSlug = ReadString(raw, "normalizedName");
            int minLevel = (int)ReadDouble(raw, "minPlayerLevel");

            if (!raw.TryGetProperty("objectives", out JsonElement objectives) ||
                objectives.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement obj in objectives.EnumerateArray())
            {
                if (obj.ValueKind != JsonValueKind.Object)
                    continue;

                string jsonType = ReadString(obj, "type");
                string typeName = ConvertObjectiveTypeName(jsonType);
                string category = typeName.Contains("Item", StringComparison.OrdinalIgnoreCase)
                    ? "item"
                    : "objective";
                string objId = ReadString(obj, "id");
                string description = LocaleValue(tasksLocale, objId);
                if (string.IsNullOrWhiteSpace(description))
                    description = LocaleValue(tasksLocale, ReadString(obj, "description"));

                string itemId = ReadFirstItemId(obj);
                (string itemName, string itemShort, string iconLink) = ResolveItemInfo(
                    itemsLocale, questItems, tasksLocale, itemId);

                bool optional = obj.TryGetProperty("optional", out JsonElement optionalEl) &&
                    optionalEl.ValueKind is JsonValueKind.True;

                foreach (QuestZone zone in EnumerateObjectiveZones(obj, mapById))
                {
                    string mapKey = ResolveQuestMapKey(zone.NormalizedName);
                    if (!AppMapKeys.Contains(mapKey))
                        continue;

                    if (!byMap.TryGetValue(mapKey, out List<QuestMarker>? list))
                    {
                        list = new List<QuestMarker>();
                        byMap[mapKey] = list;
                    }

                    list.Add(new QuestMarker
                    {
                        Quest = questName,
                        QuestSlug = questSlug,
                        ObjectiveType = typeName,
                        Category = category,
                        IconType = category,
                        Description = description,
                        QuestItem = itemName,
                        ItemShortName = itemShort,
                        ItemIconLink = iconLink,
                        Trader = traderName,
                        MinPlayerLevel = minLevel,
                        Optional = optional,
                        X = zone.X,
                        Y = zone.Y,
                        Z = zone.Z
                    });
                }
            }
        }

        var sorted = new Dictionary<string, List<QuestMarker>>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in byMap.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sorted[key] = byMap[key]
                .OrderBy(marker => marker.Quest, StringComparer.OrdinalIgnoreCase)
                .ThenBy(marker => marker.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(marker => marker.X)
                .ThenBy(marker => marker.Z)
                .ToList();
        }

        return sorted;
    }

    private readonly record struct QuestZone(string NormalizedName, double X, double Y, double Z);

    private static IEnumerable<QuestZone> EnumerateObjectiveZones(
        JsonElement obj,
        Dictionary<string, MapLookup> mapById)
    {
        if (obj.TryGetProperty("zones", out JsonElement zones) && zones.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement zone in zones.EnumerateArray())
            {
                if (zone.ValueKind != JsonValueKind.Object)
                    continue;

                MapPosition? pos = ReadPosition(zone);
                if (pos == null || (pos.X == 0 && pos.Z == 0))
                    continue;

                MapLookup lookup = LookupMap(mapById, ReadString(zone, "map"));
                yield return new QuestZone(lookup.NormalizedName, pos.X, pos.Y, pos.Z);
            }
        }

        if (obj.TryGetProperty("possibleLocations", out JsonElement locations) &&
            locations.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement location in locations.EnumerateArray())
            {
                if (location.ValueKind != JsonValueKind.Object)
                    continue;

                MapLookup lookup = LookupMap(mapById, ReadString(location, "map"));
                if (!location.TryGetProperty("positions", out JsonElement positions) ||
                    positions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement posRaw in positions.EnumerateArray())
                {
                    MapPosition? pos = ReadNamedPosition(posRaw);
                    if (pos == null || (pos.X == 0 && pos.Z == 0))
                        continue;

                    yield return new QuestZone(lookup.NormalizedName, pos.X, pos.Y, pos.Z);
                }
            }
        }
    }

    private static MapLookup LookupMap(Dictionary<string, MapLookup> mapById, string mapId)
    {
        if (!string.IsNullOrWhiteSpace(mapId) && mapById.TryGetValue(mapId, out MapLookup? lookup))
            return lookup;

        return new MapLookup { Id = mapId, NormalizedName = "", Name = mapId };
    }

    private static (string Name, string ShortName, string IconLink) ResolveItemInfo(
        JsonElement itemsLocale,
        JsonElement questItems,
        JsonElement tasksLocale,
        string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return ("", "", "");

        if (questItems.ValueKind == JsonValueKind.Object &&
            questItems.TryGetProperty(itemId, out JsonElement questItem) &&
            questItem.ValueKind == JsonValueKind.Object)
        {
            string name = LocaleValue(tasksLocale, ReadString(questItem, "name"));
            if (string.IsNullOrWhiteSpace(name))
                name = LocaleValue(tasksLocale, itemId + " Name");
            string shortName = LocaleValue(tasksLocale, ReadString(questItem, "shortName"));
            if (string.IsNullOrWhiteSpace(shortName))
                shortName = LocaleValue(tasksLocale, itemId + " ShortName");
            string icon = ReadString(questItem, "iconLink");
            return (name, shortName, icon);
        }

        return (
            LocaleValue(itemsLocale, itemId + " Name"),
            LocaleValue(itemsLocale, itemId + " ShortName"),
            $"https://assets.tarkov.dev/{itemId}-icon.webp");
    }

    private static string ReadFirstItemId(JsonElement obj)
    {
        foreach (string field in new[] { "questItem", "markerItem", "item", "items" })
        {
            foreach (string id in ReadIdList(obj, field))
            {
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
        }

        return "";
    }

    private static string ConvertObjectiveTypeName(string jsonType) => jsonType switch
    {
        "findItem" or "giveItem" or "plantItem" or "sellItem" => "TaskObjectiveItem",
        "findQuestItem" or "giveQuestItem" or "plantQuestItem" => "TaskObjectiveQuestItem",
        "mark" => "TaskObjectiveMark",
        "useItem" => "TaskObjectiveUseItem",
        "shoot" => "TaskObjectiveShoot",
        "buildWeapon" => "TaskObjectiveBuildItem",
        "extract" => "TaskObjectiveExtract",
        "skill" => "TaskObjectiveSkill",
        "traderLevel" => "TaskObjectiveTraderLevel",
        "traderStanding" => "TaskObjectiveTraderStanding",
        "taskStatus" => "TaskObjectiveTaskStatus",
        "experience" => "TaskObjectiveExperience",
        _ => "TaskObjectiveBasic"
    };

    private static Dictionary<string, JsonElement> IndexSwitches(JsonElement rawMap)
    {
        var byId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!rawMap.TryGetProperty("switches", out JsonElement raw) || raw.ValueKind != JsonValueKind.Array)
            return byId;

        foreach (JsonElement sw in raw.EnumerateArray())
        {
            if (sw.ValueKind != JsonValueKind.Object)
                continue;

            string id = ReadString(sw, "id");
            if (!string.IsNullOrWhiteSpace(id))
                byId[id] = sw;
        }

        return byId;
    }

    private static void AddOrMergeExtracts(
        Dictionary<string, TarkovExtractMap> target,
        string mapName,
        List<ExtractInfo> extracts)
    {
        if (extracts.Count == 0 || string.IsNullOrWhiteSpace(mapName))
            return;

        if (!target.TryGetValue(mapName, out TarkovExtractMap? map))
        {
            target[mapName] = new TarkovExtractMap { Name = mapName, Extracts = extracts };
            return;
        }

        map.Extracts.AddRange(extracts);
    }

    private static void AddOrMergeTransits(
        Dictionary<string, TarkovTransitMap> target,
        string mapName,
        List<TransitInfo> transits)
    {
        if (!target.TryGetValue(mapName, out TarkovTransitMap? map))
        {
            target[mapName] = new TarkovTransitMap { Name = mapName, Transits = transits };
            return;
        }

        map.Transits.AddRange(transits);
    }

    private static void AddOrMergeSwitches(
        Dictionary<string, TarkovSwitchMap> target,
        string mapName,
        List<MapSwitchInfo> switches)
    {
        if (!target.TryGetValue(mapName, out TarkovSwitchMap? map))
        {
            target[mapName] = new TarkovSwitchMap { Name = mapName, Switches = switches };
            return;
        }

        map.Switches.AddRange(switches);
    }

    private static void AddOrMergeHazards(
        Dictionary<string, TarkovHazardMap> target,
        string mapName,
        List<HazardInfo> hazards)
    {
        if (!target.TryGetValue(mapName, out TarkovHazardMap? map))
        {
            target[mapName] = new TarkovHazardMap { Name = mapName, Hazards = hazards };
            return;
        }

        map.Hazards.AddRange(hazards);
    }

    private static bool SpawnsMatch(SpawnInfo left, SpawnInfo right)
    {
        if (!string.Equals(left.ZoneName, right.ZoneName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (left.Position == null || right.Position == null)
            return false;

        const double tolerance = 0.05;
        return Math.Abs(left.Position.X - right.Position.X) < tolerance &&
               Math.Abs(left.Position.Y - right.Position.Y) < tolerance &&
               Math.Abs(left.Position.Z - right.Position.Z) < tolerance;
    }

    private static string ResolveAppMapKey(string normalizedName)
    {
        string key = MapDataService.NormalizeMapName((normalizedName ?? "").Replace("-", ""));
        return key switch
        {
            "nightfactory" => "factory",
            "thelab" or "lab" => "thelab",
            "thelabdark" => "thelab",
            "streetsoftarkov" => "streetsoftarkov",
            "groundzero" or "groundzero21" or "groundzerotutorial" => "groundzero",
            "thelabyrinth" => "labyrinth",
            _ => key
        };
    }

    private static string ResolveQuestMapKey(string normalizedName)
    {
        string hyphen = (normalizedName ?? "").Trim().ToLowerInvariant();
        return hyphen switch
        {
            "factory" or "night-factory" => "factory",
            "the-lab" or "lab" => "thelab",
            "the-lab-dark" => "thelabdark",
            "streets-of-tarkov" => "streetsoftarkov",
            "ground-zero" or "ground-zero-21" or "ground-zero-21+" => "groundzero",
            "ground-zero-tutorial" => "groundzerotutorial",
            "the-labyrinth" => "thelabyrinth",
            "icebreaker" => "icebreaker",
            _ => ResolveAppMapKey(hyphen)
        };
    }

    private static string DisplayNameForAppKey(string appKey) => appKey switch
    {
        "factory" => "Factory",
        "customs" => "Customs",
        "woods" => "Woods",
        "lighthouse" => "Lighthouse",
        "shoreline" => "Shoreline",
        "reserve" => "Reserve",
        "interchange" => "Interchange",
        "streetsoftarkov" => "Streets of Tarkov",
        "thelab" => "The Lab",
        "groundzero" => "Ground Zero",
        "terminal" => "Terminal",
        "labyrinth" or "thelabyrinth" => "The Labyrinth",
        "icebreaker" => "Icebreaker",
        _ => appKey
    };

    private static string TitleFromNormalized(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
            return "";

        return string.Join(' ', normalizedName.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0
                ? part
                : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static JsonElement LocaleRoot(JsonDocument document)
    {
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("data", out JsonElement data))
        {
            return data;
        }

        return document.RootElement;
    }

    private static string LocalizedName(JsonElement locale, string rawKey)
    {
        string localized = LocaleValue(locale, rawKey);
        if (!string.IsNullOrWhiteSpace(localized))
            return localized;
        if (string.IsNullOrWhiteSpace(rawKey))
            return "";

        return rawKey.Replace('_', ' ').Replace('-', ' ');
    }

    private static string LocaleValue(JsonElement locale, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || locale.ValueKind != JsonValueKind.Object)
            return "";

        if (locale.TryGetProperty(key, out JsonElement direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString() ?? "";

        foreach (string suffix in new[] { " Name", " name", " Nickname", " Description" })
        {
            if (locale.TryGetProperty(key + suffix, out JsonElement withSuffix) &&
                withSuffix.ValueKind == JsonValueKind.String)
            {
                string value = withSuffix.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return "";
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static List<string> ReadIdList(JsonElement element, params string[] names)
    {
        var ids = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
            return ids;

        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                continue;

            AddIdValues(ids, value);
        }

        return ids;
    }

    private static void AddIdValues(List<string> ids, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(text))
                ids.Add(text);
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string text = item.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                    ids.Add(text);
            }
        }
    }

    private static MapPosition? ReadPosition(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty("position", out JsonElement position))
        {
            MapPosition? named = ReadNamedPosition(position);
            if (named != null)
                return named;
        }

        return ReadNamedPosition(element) ?? ReadOutlineCentroid(element);
    }

    private static MapPosition? ReadOutlineCentroid(JsonElement element)
    {
        if (!element.TryGetProperty("outline", out JsonElement outline) ||
            outline.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        double x = 0;
        double y = 0;
        double z = 0;
        int count = 0;
        foreach (JsonElement point in outline.EnumerateArray())
        {
            MapPosition? named = ReadNamedPosition(point);
            if (named == null)
                continue;

            x += named.X;
            y += named.Y;
            z += named.Z;
            count++;
        }

        if (count == 0)
            return null;

        return new MapPosition
        {
            X = x / count,
            Y = y / count,
            Z = z / count
        };
    }

    private static MapPosition? ReadNamedPosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Object ||
            !position.TryGetProperty("x", out _) ||
            !position.TryGetProperty("z", out _))
        {
            return null;
        }

        return new MapPosition
        {
            X = ReadDouble(position, "x"),
            Y = ReadDouble(position, "y"),
            Z = ReadDouble(position, "z")
        };
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task WriteJsonAtomicAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, payload, payload.GetType(), WriteOptions, cancellationToken);
        }

        if (File.Exists(path))
            File.Replace(tempPath, path, destinationBackupFileName: null);
        else
            File.Move(tempPath, path);
    }
}
