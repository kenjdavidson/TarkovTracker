# TarkovTracker maintenance scripts

Scripts in this folder refresh map data from tarkov.dev. They are **not** used at runtime.

## Paths

- **Config output:** `../Config/` (JSON files copied into the app build)
- **Dev data:** `./data/` (large source files not shipped with the app)

## Common tasks

| Task | Script |
|------|--------|
| Refresh quest markers from json.tarkov.dev | `build_quest_markers_from_api.ps1` |
| Refresh extracts | `build_extracts_from_api.ps1` |
| Refresh raw spawn points (Night Factory cultists merge onto Factory) | `fetch-boss-data.ps1` |
| Refresh boss spawn markers (needs spawn points) | `build_boss_spawn_markers.ps1` |
| Refresh map level toggles | `build_map_levels.ps1` |
| Build Icebreaker floor layers from tarkov.dev tiles | `build_icebreaker_layers.ps1` |
| Build any map from tarkov.dev tiles (Labs, Labyrinth) | `build_tile_map.ps1` |
| Check a tile map lands markers on the right rooms | `verify_tile_map.ps1` |
| List which tiles in a pyramid hold content | `scan_tile_extent.ps1` |
| Check drawn maps still match their coordinate bounds | `check_svg_alignment.ps1` |
| Compare quest markers vs API | `compare_quest_markers.ps1` |
| Diff live tasks vs the local snapshot (new/removed/changed) | `compare_tasks.ps1` |
| Audit shipped quest markers offline | `audit_shipped_markers.ps1` |
| Update Terminal map assets | `update_terminal_map.ps1` |

## Quest data sources

`tarkov.dev` is the only source that carries quest ids, objectives **and** map
coordinates in the shape this app needs, so it is the one to update from. Scripts
share `tarkov_json_api.ps1` and call `https://json.tarkov.dev`.
Checked alternatives, none of which can replace it:

| Source | Last updated | Why not |
|--------|--------------|---------|
| `TarkovTracker/tarkovdata` | Jan 2024 | frozen years ago |
| `sp-tarkov/server` on GitHub | Mar 2025 | older than our own data; no map coordinates |
| SPT Gitea (`dev.sp-tarkov.com`) | n/a | returns HTTP 410 |
| EFT Fandom wiki | current | names only, and names diverge (Gunsmith listed per weapon, multi-part quests collapsed, Arena quests mixed in) |

`compare_wiki_quests.ps1` can still name-check against the wiki. Expect a lot of
false hits; it cannot confirm ids, objectives, or map positions.

## Legacy

- `regenerate_quest_markers.ps1` — uses `data/tarkov_tasks_raw.json`; prefer `build_quest_markers_from_api.ps1`
- `Labs-drawing.svg` — the hand-drawn Labs map shipped before 2.7.9; kept only as a reference, it was never georeferenced to tarkov.dev coordinates
- `terminal_*.json` in `data/` — intermediate outputs from Terminal setup scripts
