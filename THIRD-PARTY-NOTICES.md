# Third-party notices — PoeBuilder 0.4.0

**This product isn't affiliated with or endorsed by Grinding Gear Games in any way.**

## Grinding Gear Games — Path of Exile 2 passive tree

Game data, text and skill icons are © Grinding Gear Games. They are **not covered by PoeBuilder's MIT license**. Attribution is not a separate grant of rights. No independent permissive license is claimed for GGG resources. Review the applicable GGG terms before public distribution, modification or any commercial use; this project does not grant commercial rights to game assets.

- Official source: https://github.com/grindinggear/poe2-skilltree-export
- Pinned commit: `bd87e6512c92b868542eddfb1ba4ea8b6dc2da36`
- Commit/export version: **0.5.5**
- Official data-export documentation: https://www.pathofexile.com/developer/docs/data
- API policies / third-party requirements: https://www.pathofexile.com/developer/docs
- Terms: https://www.pathofexile.com/legal/terms-of-use-and-privacy-policy

The upstream README states:

> # poe2-skilltree-export
> Exported data for Path of Exile 2's Passive Skill Tree.
>
> Please see the [OAuth Documentation](https://www.pathofexile.com/developer/docs) for more information.

Files under `src/PoeBuilder.App/Data/Tree/`:

| Shipped file | Upstream path / transformation |
|---|---|
| `data.json` | `data.json`, unmodified official snapshot |
| `skills.json` | `assets/skills.json`, unmodified atlas coordinates |
| `skills.png` | `assets/skills.webp`, decoded to lossless RGBA PNG for native WPF image support; no icon edits |
| `Data/Icons/**` (0.5.0) | item/gem art PNGs from exiledata-assets @ 3354944d and poe2-tools/poe2-build-planner @ a173f7b0, resized to 256px; see `Data/Icons/NOTICE.txt` and `Data/Icons/manifest.json` |
| `manifest.json` | PoeBuilder provenance metadata, including SHA-256 of every data/atlas file and original WebP |
| `NOTICE.txt` | Condensed attribution, copied to application output |
| `Portraits/*.jpg` | 31 frames cropped from the eight official `assets/background-<class>.webp` atlases using their JSON coordinates; resized to 640×640, composited on #0D1319 and encoded as JPEG quality 92. Frame keys and source hashes are recorded in manifest. |

Raw base URL: `https://raw.githubusercontent.com/grindinggear/poe2-skilltree-export/bd87e6512c92b868542eddfb1ba4ea8b6dc2da36/`.

`data.json` SHA-256: `b52be9c4f17e4114064255ef1b8c58292e9db0e395d95af235a8d3fef0d44642`.

The export text is English. No official Russian translation is included. No assertion is made that this snapshot is identical to game patch 0.5.5c.

The application is an independent offline planner. It does not read installed game files, attach to the game, automate inputs or call undocumented endpoints. There are no embedded account credentials or API clients. Future account import requires a supported authorization flow and appropriate API access; at the time of this implementation the GGG developer documentation said new application registrations were not being processed.

## RePoE (community export) — Path of Exile 2 items, mods, socketables and gems

Game data and text are © Grinding Gear Games. They are **not covered by PoeBuilder's MIT license**. RePoE's community-maintained export formatting does not transfer game rights either. Review the applicable GGG terms before public distribution, modification or any commercial use; this project does not grant commercial rights to game data.

- Export source: https://github.com/repoe-fork/poe2
- Pinned commit: `b818b843337cae43b090b272fd98bbc0fd3a34f3`
- Published export version: **4.5.5.2**
- GGG terms: https://www.pathofexile.com/legal/terms-of-use-and-privacy-policy

Files under `src/PoeBuilder.App/Data/Game/`:

| Shipped file | Upstream path / transformation |
|---|---|
| `catalog.json` | Normalized subset of `data/base_items.json`, `data/mods.json`, `data/mods_by_base.json`, `data/skill_gems.json`, `data/skills.json`, `data/augments.json`, `data/item_classes.json`: 1845 released item/flask bases in supported classes, 1294 regular prefix/suffix affixes from per-base pools (no essence-only, effect-granting or tag-adding mods), 300 Rune/SoulCore/Idol socketables, 1115 active/support/spirit gems. English display markup removed. See `tools/prepare_catalog.py` and `manifest.json`. |
| `manifest.json` | PoeBuilder provenance metadata, including SHA-256 of every pinned upstream source file |
| `NOTICE.txt` | Condensed attribution, copied to application output |

`catalog.json` SHA-256: `140e4289ee23e4b96c42fa7b725421cc08e9292d9580ae78b5431c4c3c19dd39`.

This is a community export, not an official GGG data release. The text is English; no official Russian translation is included. No assertion is made that this snapshot matches game patch 0.5.5c or the GGG 0.5.5 passive-tree snapshot shipped in `Data/Tree/`. No Path of Building data, source or runtime is used.

## Original resources and dependencies

- Original PoeBuilder source: MIT, see `LICENSE.md`.
- `Assets/observatory.jpg`: AI-generated decorative illustration for this project, not official game art or a screenshot.
- Application icon, UI pictograms and tree frames: original geometric drawings.
- .NET / WPF: Microsoft components with their own licenses, restored through the installed SDK, not bundled in this source archive.
- No Path of Building source, assets or runtime is included.
