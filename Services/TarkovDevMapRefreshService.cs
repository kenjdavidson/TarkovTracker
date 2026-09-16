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
    public int TrackedLootMaps { get; init; }
    public int TrackedLootNew { get; init; }
    public int TrackedLootChanged { get; init; }
    public int TrackedLootUnchanged { get; init; }
    public int FilesWritten { get; init; }
    public int DownloadsSkipped { get; init; }
    public bool HazardsUpdated { get; init; }
    public DateTime RefreshedUtc { get; init; } = DateTime.UtcNow;
}

public static class TarkovDevMapRefreshService
{
    private const string GameMode = "regular";
    private const string Language = "en";
    private const int TrackedLootSchemaVersion = 8;
    private const string ValuablesHandbookCategoryId = "5b47574386f77428ca22b2f1";
    private const string BattlePassHandbookCategoryId = "6a28212a0368f4438b0d0a45";
    private const string SafeContainerId = "578f8782245977354405a1e3";
    private const string GroundCacheContainerId = "5d6d2b5486f774785c2ba8ea";

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

        progress?.Report("Checking tarkov.dev…");
        Task<CachedJsonDocument>[] fetchTasks =
        {
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/maps", overlayConfigDirectory, cancellationToken),
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/maps_{Language}", overlayConfigDirectory, cancellationToken),
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/items", overlayConfigDirectory, cancellationToken),
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/items_{Language}", overlayConfigDirectory, cancellationToken),
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/tasks", overlayConfigDirectory, cancellationToken),
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/tasks_{Language}", overlayConfigDirectory, cancellationToken),
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/traders", overlayConfigDirectory, cancellationToken),
            TarkovDevJsonClient.GetDocumentAsync($"{GameMode}/traders_{Language}", overlayConfigDirectory, cancellationToken)
        };

        CachedJsonDocument[] fetched;
        try
        {
            fetched = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        }
        catch
        {
            foreach (Task<CachedJsonDocument> task in fetchTasks)
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    task.Result.Dispose();
            }

            throw;
        }

        using CachedJsonDocument mapsDoc = fetched[0];
        using CachedJsonDocument mapsLocaleDoc = fetched[1];
        using CachedJsonDocument itemsDoc = fetched[2];
        using CachedJsonDocument itemsLocaleDoc = fetched[3];
        using CachedJsonDocument tasksDoc = fetched[4];
        using CachedJsonDocument tasksLocaleDoc = fetched[5];
        using CachedJsonDocument tradersDoc = fetched[6];
        using CachedJsonDocument tradersLocaleDoc = fetched[7];

        int skipped = 0;
        foreach (CachedJsonDocument document in fetched)
        {
            if (document.NotModified)
                skipped++;
        }

        bool mapsCached = mapsDoc.NotModified && mapsLocaleDoc.NotModified;
        bool itemsCached = itemsDoc.NotModified && itemsLocaleDoc.NotModified;
        bool questsCached = tasksDoc.NotModified && tasksLocaleDoc.NotModified &&
                            tradersDoc.NotModified && tradersLocaleDoc.NotModified;

        string extractsPath = Path.Combine(overlayConfigDirectory, "tarkov_extracts_raw.json");
        string transitsPath = Path.Combine(overlayConfigDirectory, "tarkov_transits_raw.json");
        string spawnsPath = Path.Combine(overlayConfigDirectory, "tarkov_spawns_raw.json");
        string switchesPath = Path.Combine(overlayConfigDirectory, "tarkov_switches_raw.json");
        string bossesPath = Path.Combine(overlayConfigDirectory, "tarkov_boss_spawn_markers.json");
        string lootPath = Path.Combine(overlayConfigDirectory, "tarkov_tracked_loot.json");
        string hazardsPath = Path.Combine(overlayConfigDirectory, "tarkov_hazards_raw.json");
        string questsPath = Path.Combine(overlayConfigDirectory, "tarkov_quest_markers.json");

        bool haveMapFiles = File.Exists(extractsPath) &&
                            File.Exists(transitsPath) &&
                            File.Exists(spawnsPath) &&
                            File.Exists(switchesPath) &&
                            File.Exists(bossesPath);
        bool haveLoot = File.Exists(lootPath) && ReadLootSchemaVersion(lootPath) >= TrackedLootSchemaVersion;
        bool haveQuests = File.Exists(questsPath);

        MapDataRefreshResult? previous = TryReadLastRefresh(overlayConfigDirectory);
        bool needQuestParse = !questsCached || !mapsCached || !haveQuests;
        bool needMapParse = !mapsCached || !itemsCached || !haveMapFiles || !haveLoot;

        int filesWritten = 0;
        bool extractsUpdated = false;
        bool transitsUpdated = false;
        bool spawnsUpdated = false;
        bool switchesUpdated = false;
        bool bossesUpdated = false;
        bool questsUpdated = false;
        bool lootWritten = false;
        bool hazardsFileWritten = false;
        int lootNew = 0;
        int lootChanged = 0;
        int lootUnchanged = 0;
        int extractMaps = previous?.ExtractMaps ?? 0;
        int transitMaps = previous?.TransitMaps ?? 0;
        int spawnMaps = previous?.SpawnMaps ?? 0;
        int switchMaps = previous?.SwitchMaps ?? 0;
        int lootMaps = previous?.TrackedLootMaps ?? 0;
        int questCount = previous?.QuestMarkers ?? 0;
        int bossCount = previous?.BossMarkers ?? 0;
        bool hazardsUpdated = false;
        ParsedMaps? parsed = null;

        if (needMapParse || needQuestParse)
        {
            await mapsDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);
            await mapsLocaleDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);
            await itemsDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);
            await itemsLocaleDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (needMapParse)
        {
            progress?.Report("Updating map markers…");
            JsonElement mapsLocale = LocaleRoot(mapsLocaleDoc.Document);
            JsonElement itemsLocale = LocaleRoot(itemsLocaleDoc.Document);
            parsed = ParseMaps(mapsDoc.Document, itemsDoc.Document, mapsLocale, itemsLocale);

            extractMaps = parsed.ExtractMaps.Count;
            transitMaps = parsed.TransitMaps.Count;
            spawnMaps = parsed.SpawnMaps.Count;
            switchMaps = parsed.SwitchMaps.Count;
            lootMaps = parsed.LootMaps.Count;
            bossCount = parsed.BossMarkersByMap.Values.Sum(list => list.Count);

            if (!mapsCached || !haveMapFiles)
            {
                if (await WriteJsonIfChangedAsync(
                    extractsPath,
                    new TarkovExtractsRoot { Data = new TarkovExtractsData { Maps = parsed.ExtractMaps } },
                    cancellationToken))
                {
                    filesWritten++;
                    extractsUpdated = true;
                }

                if (await WriteJsonIfChangedAsync(
                    transitsPath,
                    new TarkovTransitsRoot { Data = new TarkovTransitsData { Maps = parsed.TransitMaps } },
                    cancellationToken))
                {
                    filesWritten++;
                    transitsUpdated = true;
                }
                if (await WriteJsonIfChangedAsync(
                    spawnsPath,
                    new TarkovSpawnsRoot { Data = new TarkovSpawnsData { Maps = parsed.SpawnMaps } },
                    cancellationToken))
                {
                    filesWritten++;
                    spawnsUpdated = true;
                }
                if (await WriteJsonIfChangedAsync(
                    switchesPath,
                    new TarkovSwitchesRoot { Data = new TarkovSwitchesData { Maps = parsed.SwitchMaps } },
                    cancellationToken))
                {
                    filesWritten++;
                    switchesUpdated = true;
                }
                if (await WriteJsonIfChangedAsync(bossesPath, parsed.BossMarkersByMap, cancellationToken))
                {
                    filesWritten++;
                    bossesUpdated = true;
                }

                hazardsUpdated = parsed.HazardOutlineCount > 0;
                if (hazardsUpdated &&
                    await WriteJsonIfChangedAsync(
                        hazardsPath,
                        new TarkovHazardsRoot { Data = new TarkovHazardsData { Maps = parsed.HazardMaps } },
                        cancellationToken))
                {
                    filesWritten++;
                    hazardsFileWritten = true;
                }
            }

            if (!mapsCached || !itemsCached || !haveLoot)
            {
                TarkovTrackedLootRoot lootPayload = new()
                {
                    SchemaVersion = TrackedLootSchemaVersion,
                    Data = new TarkovTrackedLootData { Maps = parsed.LootMaps }
                };
                (lootNew, lootChanged, lootUnchanged) = DiffTrackedLoot(lootPath, lootPayload);
                if (await WriteJsonIfChangedAsync(lootPath, lootPayload, cancellationToken))
                {
                    filesWritten++;
                    lootWritten = true;
                }
            }
        }

        if (needQuestParse)
        {
            progress?.Report("Updating quest markers…");
            await tasksDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);
            await tasksLocaleDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);
            await tradersDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);
            await tradersLocaleDoc.EnsureParsedAsync(cancellationToken).ConfigureAwait(false);

            parsed ??= ParseMaps(
                mapsDoc.Document,
                itemsDoc.Document,
                LocaleRoot(mapsLocaleDoc.Document),
                LocaleRoot(itemsLocaleDoc.Document));

            Dictionary<string, List<QuestMarker>> quests = BuildQuestMarkers(
                tasksDoc.Document,
                tradersDoc.Document,
                LocaleRoot(tasksLocaleDoc.Document),
                LocaleRoot(tradersLocaleDoc.Document),
                LocaleRoot(itemsLocaleDoc.Document),
                parsed.MapById);

            questCount = quests.Values.Sum(list => list.Count);
            if (await WriteJsonIfChangedAsync(questsPath, quests, cancellationToken))
            {
                filesWritten++;
                questsUpdated = true;
            }
        }

        DateTime refreshedUtc = DateTime.UtcNow;

        var result = new MapDataRefreshResult
        {
            Succeeded = true,
            ExtractMaps = extractMaps,
            TransitMaps = transitMaps,
            SpawnMaps = spawnMaps,
            SwitchMaps = switchMaps,
            TrackedLootMaps = lootMaps,
            TrackedLootNew = lootNew,
            TrackedLootChanged = lootChanged,
            TrackedLootUnchanged = lootUnchanged,
            FilesWritten = filesWritten,
            DownloadsSkipped = skipped,
            BossMarkers = bossCount,
            QuestMarkers = questCount,
            HazardsUpdated = hazardsUpdated,
            RefreshedUtc = refreshedUtc,
            Message = BuildRefreshMessage(
                refreshedUtc,
                lootWritten,
                lootNew,
                lootChanged,
                extractsUpdated,
                transitsUpdated,
                spawnsUpdated,
                switchesUpdated,
                questsUpdated,
                bossesUpdated,
                hazardsFileWritten,
                extractMaps,
                transitMaps,
                spawnMaps,
                switchMaps,
                questCount,
                bossCount)
        };

        await WriteJsonAtomicAsync(
            Path.Combine(overlayConfigDirectory, "map-data-refresh.json"),
            result,
            cancellationToken).ConfigureAwait(false);

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
        public List<TarkovTrackedLootMap> LootMaps { get; } = new();
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
        JsonDocument itemsDoc,
        JsonElement mapsLocale,
        JsonElement itemsLocale)
    {
        var parsed = new ParsedMaps();
        JsonElement data = mapsDoc.RootElement.GetProperty("data");
        JsonElement maps = data.GetProperty("maps");
        JsonElement mobs = data.TryGetProperty("mobs", out JsonElement mobsElement)
            ? mobsElement
            : default;
        Dictionary<string, ItemCatalogEntry> items = IndexItems(itemsDoc, itemsLocale);

        var extractsByName = new Dictionary<string, TarkovExtractMap>(StringComparer.OrdinalIgnoreCase);
        var transitsByName = new Dictionary<string, TarkovTransitMap>(StringComparer.OrdinalIgnoreCase);
        var spawnsByAppKey = new Dictionary<string, List<SpawnInfo>>(StringComparer.OrdinalIgnoreCase);
        var switchesByName = new Dictionary<string, TarkovSwitchMap>(StringComparer.OrdinalIgnoreCase);
        var hazardsByName = new Dictionary<string, TarkovHazardMap>(StringComparer.OrdinalIgnoreCase);
        var lootByName = new Dictionary<string, TarkovTrackedLootMap>(StringComparer.OrdinalIgnoreCase);
        var lootSourcePriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

            List<TrackedLootPoint> loot = ParseTrackedLoot(raw, items, itemsLocale);
            if (loot.Count > 0)
            {
                string lootMapName = CanonicalLootMapName(appKey, displayName);
                AddOrMergeLoot(lootByName, lootSourcePriority, lootMapName, loot, LootSourcePriority(normalizedName));
            }

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
        parsed.LootMaps.AddRange(lootByName.Values.OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase));

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

    private static void AddOrMergeLoot(
        Dictionary<string, TarkovTrackedLootMap> target,
        Dictionary<string, int> sourcePriority,
        string mapName,
        List<TrackedLootPoint> points,
        int priority)
    {
        if (points.Count == 0 || string.IsNullOrWhiteSpace(mapName) || priority < 0)
            return;

        if (!target.TryGetValue(mapName, out TarkovTrackedLootMap? map))
        {
            target[mapName] = new TarkovTrackedLootMap { Name = mapName, Points = points };
            sourcePriority[mapName] = priority;
            return;
        }

        int existingPriority = sourcePriority.GetValueOrDefault(mapName, 0);
        if (priority > existingPriority)
        {
            map.Points = points;
            sourcePriority[mapName] = priority;
            return;
        }

        if (priority < existingPriority)
            return;

        var usedIds = new HashSet<string>(
            map.Points
                .Select(point => point.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        foreach (TrackedLootPoint point in points)
            AddUniqueLootPoint(map.Points, usedIds, point);
    }

    // Collapse Ground Zero 21+ onto the same map the app displays.
    private static string CanonicalLootMapName(string appKey, string displayName)
    {
        if (string.Equals(appKey, "groundzero", StringComparison.OrdinalIgnoreCase))
            return DisplayNameForAppKey(appKey);
        return displayName;
    }

    // Tutorial dumps are skipped. Current-wipe variants (ground-zero-21) replace the
    // older map instead of merging, matching tarkov.dev's live marker set.
    private static int LootSourcePriority(string normalizedName)
    {
        string key = (normalizedName ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key) || key.Contains("tutorial", StringComparison.Ordinal))
            return -1;
        if (key.Contains("-21", StringComparison.Ordinal))
            return 2;
        return 1;
    }

    private sealed class ItemCatalogEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string NormalizedName { get; init; } = "";
        public HashSet<string> HandbookCategoryIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CategoryIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsLootContainer { get; init; }
    }

    private static Dictionary<string, ItemCatalogEntry> IndexItems(JsonDocument itemsDoc, JsonElement itemsLocale)
    {
        var index = new Dictionary<string, ItemCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        JsonElement root = itemsDoc.RootElement;
        JsonElement items = default;
        if (root.TryGetProperty("data", out JsonElement data) &&
            data.TryGetProperty("items", out JsonElement nested))
        {
            items = nested;
        }
        else if (root.TryGetProperty("items", out JsonElement direct))
        {
            items = direct;
        }

        if (items.ValueKind != JsonValueKind.Object)
            return index;

        foreach (JsonProperty item in items.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object)
                continue;

            string id = ReadString(item.Value, "id");
            if (string.IsNullOrWhiteSpace(id))
                id = item.Name;

            string normalized = ReadString(item.Value, "normalizedName");
            string name = LocaleValue(itemsLocale, id + " Name");
            if (string.IsNullOrWhiteSpace(name))
                name = LocaleValue(itemsLocale, ReadString(item.Value, "name"));
            if (string.IsNullOrWhiteSpace(name))
                name = ReadString(item.Value, "name");
            if (string.IsNullOrWhiteSpace(name))
                name = normalized.Replace('-', ' ');

            bool lootContainer = false;
            foreach (string type in ReadStringList(item.Value, "types"))
            {
                if (type.Contains("lootcontainer", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("loot-container", StringComparison.OrdinalIgnoreCase))
                {
                    lootContainer = true;
                    break;
                }
            }

            var entry = new ItemCatalogEntry
            {
                Id = id,
                Name = name,
                NormalizedName = normalized,
                IsLootContainer = lootContainer
            };
            foreach (string categoryId in ReadCategoryIds(item.Value, "handbookCategories"))
                entry.HandbookCategoryIds.Add(categoryId);
            foreach (string categoryId in ReadCategoryIds(item.Value, "categories"))
                entry.CategoryIds.Add(categoryId);

            index[id] = entry;
        }

        return index;
    }

    private static bool IsSafeContainer(ItemCatalogEntry entry) =>
        MatchesContainerName(entry.NormalizedName, "safe", "bank-safe") ||
        MatchesContainerName(entry.Name, "Safe", "Bank safe");

    private static bool IsGroundCacheContainer(ItemCatalogEntry entry) =>
        MatchesContainerName(entry.NormalizedName, "ground-cache") ||
        MatchesContainerName(entry.Name, "Ground cache");

    private static bool IsValuablesItem(ItemCatalogEntry entry) =>
        !entry.IsLootContainer &&
        entry.HandbookCategoryIds.Contains(ValuablesHandbookCategoryId);

    private static bool IsBattlePassItem(ItemCatalogEntry entry) =>
        entry.HandbookCategoryIds.Contains(BattlePassHandbookCategoryId) ||
        entry.CategoryIds.Contains(BattlePassHandbookCategoryId);

    private static bool MatchesContainerName(string value, params string[] names)
    {
        foreach (string name in names)
        {
            if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static List<TrackedLootPoint> ParseTrackedLoot(
        JsonElement rawMap,
        Dictionary<string, ItemCatalogEntry> items,
        JsonElement itemsLocale)
    {
        var points = new List<TrackedLootPoint>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rawMap.TryGetProperty("lootContainers", out JsonElement containers) &&
            containers.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement container in containers.EnumerateArray())
            {
                if (container.ValueKind != JsonValueKind.Object)
                    continue;

                string containerId = ReadLootContainerId(container);
                if (string.IsNullOrWhiteSpace(containerId) ||
                    !TryResolveTrackedContainer(containerId, items, itemsLocale, out string kind, out string name))
                {
                    continue;
                }

                MapPosition? position = ReadPosition(container);
                if (position == null)
                    continue;

                AddUniqueLootPoint(points, usedIds, new TrackedLootPoint
                {
                    Id = $"{kind}:{containerId}:{FormatCoord(position)}",
                    Kind = kind,
                    Name = name,
                    Position = position
                });
            }
        }

        if (rawMap.TryGetProperty("lootLoose", out JsonElement loose) &&
            loose.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement pile in loose.EnumerateArray())
            {
                if (pile.ValueKind != JsonValueKind.Object)
                    continue;

                MapPosition? position = ReadPosition(pile);
                if (position == null)
                    continue;

                var names = new List<string>();
                var valuablesNames = new List<string>();
                var battlePassNames = new List<string>();
                bool valuables = false;
                bool battlePass = false;
                foreach (string itemId in ReadItemIds(pile))
                {
                    if (!items.TryGetValue(itemId, out ItemCatalogEntry? entry))
                        continue;
                    if (!string.IsNullOrWhiteSpace(entry.Name) &&
                        !names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(entry.Name);
                    }

                    if (IsValuablesItem(entry))
                    {
                        valuables = true;
                        if (!string.IsNullOrWhiteSpace(entry.Name) &&
                            !valuablesNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            valuablesNames.Add(entry.Name);
                        }
                    }
                    if (IsBattlePassItem(entry))
                    {
                        battlePass = true;
                        if (!string.IsNullOrWhiteSpace(entry.Name) &&
                            !battlePassNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            battlePassNames.Add(entry.Name);
                        }
                    }
                }

                if (valuables)
                {
                    AddUniqueLootPoint(points, usedIds, new TrackedLootPoint
                    {
                        Id = $"valuables:{FormatCoord(position)}",
                        Kind = "valuables",
                        Name = "Valuables",
                        Items = valuablesNames.Count > 0 ? valuablesNames : names,
                        Position = position
                    });
                }

                if (battlePass)
                {
                    AddUniqueLootPoint(points, usedIds, new TrackedLootPoint
                    {
                        Id = $"battle-pass:{FormatCoord(position)}",
                        Kind = "battle-pass",
                        Name = "Battle Pass documents",
                        Items = battlePassNames.Count > 0 ? battlePassNames : names,
                        Position = position
                    });
                }
            }
        }

        if (rawMap.TryGetProperty("locks", out JsonElement locks) &&
            locks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement lockEl in locks.EnumerateArray())
            {
                if (lockEl.ValueKind != JsonValueKind.Object)
                    continue;

                MapPosition? position = ReadPosition(lockEl);
                if (position == null)
                    continue;

                string keyId = ReadString(lockEl, "key");
                string keyName = "";
                if (items.TryGetValue(keyId, out ItemCatalogEntry? keyItem))
                    keyName = keyItem.Name;
                if (string.IsNullOrWhiteSpace(keyName))
                    keyName = LocaleValue(itemsLocale, keyId + " Name");

                string lockType = ReadString(lockEl, "lockType");
                string id = ReadString(lockEl, "id");
                if (string.IsNullOrWhiteSpace(id))
                    id = $"lock:{FormatCoord(position)}";

                bool needsPower = lockEl.TryGetProperty("needsPower", out JsonElement power) &&
                                  power.ValueKind == JsonValueKind.True;

                if (IsSafeKeyLock(keyName, lockType))
                {
                    if (TryAttachKeyToNearbySafe(points, position, keyName, lockType, needsPower))
                        continue;

                    AddUniqueLootPoint(points, usedIds, new TrackedLootPoint
                    {
                        Id = $"safe:{id}",
                        Kind = "safe",
                        Name = "Safe",
                        KeyName = NullIfEmpty(keyName),
                        LockType = NullIfEmpty(lockType),
                        NeedsPower = needsPower,
                        Position = position
                    });
                    continue;
                }

                if (string.Equals(lockType, "trunk", StringComparison.OrdinalIgnoreCase) &&
                    TryAttachKeyToNearbySafe(points, position, keyName, lockType, needsPower, maxDistance: 1.25))
                    continue;

                AddUniqueLootPoint(points, usedIds, new TrackedLootPoint
                {
                    Id = id,
                    Kind = "lock",
                    Name = "Lock",
                    KeyName = NullIfEmpty(keyName),
                    LockType = NullIfEmpty(lockType),
                    NeedsPower = needsPower,
                    Position = position
                });
            }
        }

        return points;
    }

    private static bool IsSafeKeyLock(string keyName, string lockType)
    {
        if (keyName.Contains("safe key", StringComparison.OrdinalIgnoreCase) ||
            keyName.Contains("safe-key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(lockType, "trunk", StringComparison.OrdinalIgnoreCase) &&
               keyName.Contains("safe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAttachKeyToNearbySafe(
        List<TrackedLootPoint> points,
        MapPosition position,
        string keyName,
        string lockType,
        bool needsPower,
        double maxDistance = 2.0)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            return false;

        TrackedLootPoint? nearest = null;
        double best = double.MaxValue;
        foreach (TrackedLootPoint point in points)
        {
            if (!string.Equals(point.Kind, "safe", StringComparison.OrdinalIgnoreCase) ||
                point.Position == null)
            {
                continue;
            }

            double dist = Distance(position, point.Position);
            if (dist < best)
            {
                best = dist;
                nearest = point;
            }
        }

        if (nearest == null || best > maxDistance)
            return false;

        if (string.IsNullOrWhiteSpace(nearest.KeyName))
            nearest.KeyName = keyName;
        if (string.IsNullOrWhiteSpace(nearest.LockType))
            nearest.LockType = NullIfEmpty(lockType);
        nearest.NeedsPower = nearest.NeedsPower || needsPower;
        return true;
    }

    private static double Distance(MapPosition left, MapPosition right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        double dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static void AddUniqueLootPoint(
        List<TrackedLootPoint> points,
        HashSet<string> usedIds,
        TrackedLootPoint point)
    {
        string id = string.IsNullOrWhiteSpace(point.Id) ? "loot" : point.Id;
        int suffix = 2;
        while (!usedIds.Add(id))
            id = $"{point.Id}:{suffix++}";

        point.Id = id;
        points.Add(point);
    }

    private static string FormatCoord(MapPosition position) =>
        $"{position.X:0.##}:{position.Y:0.##}:{position.Z:0.##}";

    private static string ReadLootContainerId(JsonElement container)
    {
        if (!container.TryGetProperty("lootContainer", out JsonElement value))
            return "";

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Object)
        {
            string id = ReadString(value, "id");
            return string.IsNullOrWhiteSpace(id) ? ReadString(value, "normalizedName") : id;
        }

        return "";
    }

    private static bool TryResolveTrackedContainer(
        string containerId,
        Dictionary<string, ItemCatalogEntry> items,
        JsonElement itemsLocale,
        out string kind,
        out string name)
    {
        kind = "";
        name = "";

        if (string.Equals(containerId, SafeContainerId, StringComparison.OrdinalIgnoreCase))
        {
            kind = "safe";
            name = "Safe";
            return true;
        }

        if (string.Equals(containerId, GroundCacheContainerId, StringComparison.OrdinalIgnoreCase))
        {
            kind = "ground-cache";
            name = "Ground cache";
            return true;
        }

        if (items.TryGetValue(containerId, out ItemCatalogEntry? entry))
        {
            if (IsSafeContainer(entry))
            {
                kind = "safe";
                name = string.IsNullOrWhiteSpace(entry.Name) ? "Safe" : entry.Name;
                return true;
            }

            if (IsGroundCacheContainer(entry))
            {
                kind = "ground-cache";
                name = string.IsNullOrWhiteSpace(entry.Name) ? "Ground cache" : entry.Name;
                return true;
            }
        }

        string localized = LocaleValue(itemsLocale, containerId);
        if (string.IsNullOrWhiteSpace(localized))
            localized = LocaleValue(itemsLocale, containerId + " Name");

        if (MatchesContainerName(localized, "Safe", "Bank safe"))
        {
            kind = "safe";
            name = localized;
            return true;
        }

        if (MatchesContainerName(localized, "Ground cache"))
        {
            kind = "ground-cache";
            name = localized;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> ReadCategoryIds(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement value))
            yield break;

        if (value.ValueKind == JsonValueKind.String)
        {
            string id = value.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(id))
                yield return id;
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            string id = ReadString(value, "id");
            if (!string.IsNullOrWhiteSpace(id))
                yield return id;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                string id = entry.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id;
            }
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                string id = ReadString(entry, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id;
            }
        }
    }

    private static int ReadLootSchemaVersion(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[1024];
            int read = stream.Read(buffer);
            var reader = new Utf8JsonReader(
                buffer[..read],
                isFinalBlock: read < buffer.Length || stream.Length <= read,
                state: default);

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName ||
                    !reader.ValueTextEquals("schemaVersion"u8))
                {
                    continue;
                }

                if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    return 0;
                return reader.GetInt32();
            }
        }
        catch (JsonException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }

        return 0;
    }

    private static List<string> ReadItemIds(JsonElement pile)
    {
        var ids = new List<string>();
        if (!pile.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string id = item.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                string id = ReadString(item, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
        }

        return ids;
    }

    private static List<string> ReadStringList(JsonElement element, string name)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return values;

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string text = item.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                    values.Add(text);
            }
        }

        return values;
    }

    private static (int added, int changed, int unchanged) DiffTrackedLoot(
        string path,
        TarkovTrackedLootRoot incoming)
    {
        var next = IndexLootPoints(incoming.Data.Maps);

        if (!File.Exists(path))
            return (next.Count, 0, 0);

        try
        {
            TarkovTrackedLootRoot? previous = JsonSerializer.Deserialize<TarkovTrackedLootRoot>(File.ReadAllText(path));
            var prev = IndexLootPoints(previous?.Data.Maps ?? new List<TarkovTrackedLootMap>());

            int added = 0;
            int changed = 0;
            int unchanged = 0;
            foreach ((string id, TrackedLootPoint point) in next)
            {
                if (!prev.TryGetValue(id, out TrackedLootPoint? old))
                {
                    added++;
                    continue;
                }

                if (LootPointsEqual(old, point))
                    unchanged++;
                else
                    changed++;
            }

            return (added, changed, unchanged);
        }
        catch (JsonException)
        {
            return (next.Count, 0, 0);
        }
    }

    private static Dictionary<string, TrackedLootPoint> IndexLootPoints(List<TarkovTrackedLootMap> maps)
    {
        var index = new Dictionary<string, TrackedLootPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (TarkovTrackedLootMap map in maps)
        {
            string mapName = map.Name ?? "";
            foreach (TrackedLootPoint point in map.Points ?? new List<TrackedLootPoint>())
            {
                if (string.IsNullOrWhiteSpace(point.Id))
                    continue;

                string key = mapName + "\n" + point.Id;
                if (!index.ContainsKey(key))
                    index[key] = point;
            }
        }

        return index;
    }

    private static bool LootPointsEqual(TrackedLootPoint left, TrackedLootPoint right) =>
        string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.KeyName, right.KeyName, StringComparison.OrdinalIgnoreCase) &&
        PositionsEqual(left.Position, right.Position);

    private static bool PositionsEqual(MapPosition? left, MapPosition? right)
    {
        if (left == null && right == null)
            return true;
        if (left == null || right == null)
            return false;
        return Math.Abs(left.X - right.X) < 0.05 &&
               Math.Abs(left.Y - right.Y) < 0.05 &&
               Math.Abs(left.Z - right.Z) < 0.05;
    }

    private static string BuildRefreshMessage(
        DateTime refreshedUtc,
        bool lootWritten,
        int lootNew,
        int lootChanged,
        bool extractsUpdated,
        bool transitsUpdated,
        bool spawnsUpdated,
        bool switchesUpdated,
        bool questsUpdated,
        bool bossesUpdated,
        bool hazardsUpdated,
        int extractMaps,
        int transitMaps,
        int spawnMaps,
        int switchMaps,
        int questCount,
        int bossCount)
    {
        string stamp = $"Updated {refreshedUtc.ToLocalTime():g}";
        var details = new List<string>();

        if (lootNew > 0 || lootChanged > 0)
        {
            string loot;
            if (lootNew > 0 && lootChanged > 0)
                loot = $"{lootNew} new and {lootChanged} changed tracked markers";
            else if (lootNew > 0)
                loot = $"{lootNew} new tracked markers";
            else
                loot = $"{lootChanged} changed tracked markers";
            details.Add(loot + " (valuables, battle pass documents, safes, locks, ground caches)");
        }
        else if (lootWritten)
        {
            details.Add("tracked loot");
        }

        if (extractsUpdated)
            details.Add($"{extractMaps} extract maps");
        if (transitsUpdated)
            details.Add($"{transitMaps} transit maps");
        if (spawnsUpdated)
            details.Add($"{spawnMaps} spawn maps");
        if (switchesUpdated)
            details.Add($"{switchMaps} switch maps");
        if (questsUpdated)
            details.Add($"{questCount} quests");
        if (bossesUpdated)
            details.Add($"{bossCount} bosses");
        if (hazardsUpdated)
            details.Add("hazards");

        if (details.Count == 0)
            return stamp + ".";

        return stamp + ". " + string.Join(". ", details) + ".";
    }

    private static async Task<bool> WriteJsonIfChangedAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload, WriteOptions);
        if (File.Exists(path) &&
            string.Equals(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                json,
                StringComparison.Ordinal))
        {
            return false;
        }

        await WriteJsonAtomicAsync(path, payload, cancellationToken).ConfigureAwait(false);
        return true;
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
        await JsonSerializer.SerializeAsync(stream, payload, payload.GetType(), WriteOptions, cancellationToken)
            .ConfigureAwait(false);
        }

        if (File.Exists(path))
            File.Replace(tempPath, path, destinationBackupFileName: null);
        else
            File.Move(tempPath, path);
    }
}
