# PoB comparison

This document records reproducible comparisons against the real PoB2 fixture in
`tests/PoeBuilder.Tests/Fixtures/pob-real.txt`.

## The fixture is the golden reference

`pob-real.txt` is a genuine PoB2 (`<PathOfBuilding2>`) share code of the "PoB · Mercenary" build, and
PoB2 wrote its own computed panel into it:

```xml
<PlayerStat stat="AverageHit" value="152317.6167458"/>
<PlayerStat stat="TotalDPS" value="324021.11198652"/>
<PlayerStat stat="Str" value="88"/>  <PlayerStat stat="Dex" value="59"/>  <PlayerStat stat="Int" value="343"/>
<PlayerStat stat="Life" value="1625"/> <PlayerStat stat="Mana" value="4465"/> <PlayerStat stat="Spirit" value="398"/>
<PlayerStat stat="Armour" value="1623"/> <PlayerStat stat="Evasion" value="1420"/>
<PlayerStat stat="TotalEHP" value="13155.778894472"/> …
```

So no external snapshot is needed: the fixture is both the input and the expectation.
`Pob2ParityTests` imports it, reads those numbers back and prints a side-by-side table.

## Current comparison (level 96 fixture, `pobb-mercenary-ll-arc.txt`)

| Metric | PoB2 | PoeBuilder | Delta | Status |
|---|---:|---:|---:|---|
| Strength | 88 | 88 | 0 | **asserted** |
| Dexterity | 59 | 59 | 0 | **asserted** |
| Intelligence | 343 | 343 | 0 | **asserted** |
| Total Life | 1705 | 1705 | 0 | **asserted** |
| Total Mana | 4576 | 4576 | 0 | **asserted** |
| Total Spirit | 206 | 206 | 0 | **asserted** |
| Armour | 1623 | 1623 | 0 | **asserted** |
| Evasion Rating | 1420 | 1420 | 0 | **asserted** |
| Fire Resistance | 75 | 75 | 0 | **asserted** |
| Cold Resistance | 52 | 52 | 0 | **asserted** |
| Lightning Resistance | 75 | 75 | 0 | **asserted** |
| Chaos Resistance | 0 | 0 | 0 | **asserted** |
| Movement Speed (×100) | 127.7 | 126.0 | −1.7 | **asserted** (±2) |
| Effective Hit Pool | 11878.1 | 77843.9 | open (EHP model) | reported |
| Average Hit (main group) | 239964.7 (crit-weighted) | 34060.6 pre-crit (66313 implied) | open (−51%) | reported |
| TotalDPS (main group) | 569370.8 | 277919.6 | open (−51%) | reported |
| Speed (main group) | 2.3727 | 2.25 | −5% | reported |
| Crit Chance | 47.97% | **47.97%** | **0** ✅ | **asserted to 0.01** |
| Crit Multiplier | +546% | **+546%** | **0** ✅ | **asserted** |

Every pool, attribute and defence above is now exact, so the test asserts them instead of printing them.
The earlier level-95 snapshot (`pob-real.txt`) matches the same way except Life (−28) and Cold
Resistance (−23): that snapshot parks a Shrine Sceptre in its second weapon set, and PoB2 still counts
the **Purity of Ice** aura it grants (cold resistance) plus a small Life source we do not model.

### Crit: exact on both snapshots

PoB2 (`Modules/CalcOffence.lua:3813-3858`):

```lua
extraDamage      = Sum("BASE", cfg, "CritMultiplier") / 100      -- 100 base + every flat bonus
extraDamageInc   = 1 + Sum("INC",  cfg, "CritMultiplier") / 100
extraDamageMore  = More("MORE", cfg, "CritMultiplier")
extraDamage      = extraDamage * extraDamageInc * extraDamageMore
output.CritMultiplier = 1 + max(0, extraDamage)
```

`CalcSetup.lua:679` seeds the sum with `data.characterConstants["base_critical_hit_damage_bonus"]`
= **100** (`Data/Misc.lua:159`). Three things were wrong on our side and are fixed:

1. **Every "N% increased Critical (Spell) Damage Bonus" is a `CritMultiplier BASE` addition**, not an
   increase — that is the game's own stat id (`base_critical_strike_multiplier_+` /
   `base_spell_critical_strike_multiplier_+`). Our unique-text parser was mapping the wording to INC.
2. **Socketed jewels' affixes were dropped**: the jewel loop resolved rolls against the jewel pool
   only, so a jewel's `increased Critical Spell Damage Bonus` (Foe Prism +15, Gale Splinter +12) was
   lost. It now resolves against both pools, like the gear loop.
3. **Pain Attunement's conditional bonus** ("30% less Critical Damage Bonus when on Full Life / 30%
   more when on Low Life") was unaccounted. It is now parsed and applied as a `MORE` on the crit
   bonus, gated on the Life state (PoB2 `data.misc.LowPoolThreshold` = 35% of maximum Life). The
   importer takes the state from the share code's own resolved `LifeUnreservedPercent`, because our
   reservation model cannot resolve skill reservations yet.

The crit **chance** also depends on maximum Mana, because Rathpith Globe reads it:

```
Rathpith Globe: 3% increased Spell Critical Hit Chance per 100 maximum Mana
  PoB2  : floor(4576 / 100) = 45 -> +135% inc -> 9 * (1 + 433/100) = 47.97%
  ours  : floor(4576 / 100) = 45 -> +135% inc -> 9 * (1 + 433/100) = 47.97%
```

PoB2's `PerStat` tag floors the quotient (`Classes/ModStore.lua`: `m_floor(base / div + 0.0001)`), so
a single point of Mana is enough to shift the chance. That is why the Mana pool had to be exact first.

| Fixture | PoB2 | PoeBuilder |
|---|---|---|
| `pob-real.txt` (level 95) | 37.17% / +585% | **37.17% / +585%** ✅ |
| `pobb-mercenary-ll-arc.txt` (level 96) | 47.97% / +546% | **47.97% / +546%** ✅ |

### Offence: the main group is "Spell Totem hosting Arc"

The group's active gem in the share code is **Spell Totem**, and the damaging gem it deploys (Arc) is
listed among the supports. The calculator detects the host (a gem with `is_totem` / `is_spell_totem`
that cannot deal damage itself) and computes the hosted gem's damage, which is what the panel shows.

What the damage now includes (all data-driven from the pinned gem statics):

| Mechanic | Source | Effect on Arc |
|---|---|---|
| Host detection | `is_totem` / `is_spell_totem` static | damage comes from Arc, not Spell Totem |
| Support gem levels | `supported_lightning_skill_gem_level_+` (Lightning Mastery) | +1 gem level |
| Item gem levels | `spell_skill_gem_level_+` (wand +3, amulet +4, wand rune +1) | +8 gem levels |
| Ascendancy gem levels | `+2 to Level of all Skills with an Intelligence requirement` | +2 gem levels |
| Corruption | `corruptLevel="1"` on the Arc gem | +1 gem level |
| **Archmage** | `archmage_all_damage_%_to_gain_as_lightning_to_grant_to_non_channelling_spells_per_100_max_mana` | **+180% of damage as extra Lightning** (Mana 4576 → 45 stacks) |
| Gain-as from gear | `Gain 29% of Damage as Extra Lightning/Cold`, 14% Fire, 10% Chaos | added before more/less |
| Totem cast speed | `totem_skill_cast_speed_+%` only | rate = 0.91 × (1 + (cast + totem)/100) |

**Gem level and quality are now the effective ones.** The row in the skill list and the character sheet
show the gem that actually deals the damage, with every global bonus folded in — for this build
**Arc 31 (21 base + 10 from items) at 36% quality (20 own + 16 global)**, instead of the host gem's
`Spell Totem 18 / 20%` that was displayed before. The quality figure follows PoB2: a
`+N% to Quality of all Skills` mod reaches only gems that grant an active skill (`ModParser.lua` maps
the wording to a `GemProperty` mod with the `grants_active_skill` keyword), and rune `Bonded: …` lines
are gated by `CanUseBondedModifiers`, so Morior Invictus' `Bonded: +5% to Quality of all Skills`
(Fox Idol) is not counted here — in the game that item line enables it, which is where the in-game
41% comes from.

Arc's DPS is now 277 920 against PoB2's 569 371 (crit chance and multiplier exact). The sub-skills no
longer run away from poe.ninja's published breakdown, because two whole classes of support stats are now
consumed:

| Skill | poe.ninja | Ours | Note |
|---|---|---|---|
| Arc (Spell Totem host) | 569 371 | 309 298 | pre-crit hit still ×1.84 low; rate 2.51 vs 2.3727 |
| Entangle | 44 661 | 54 462 | +22% at rate 2.84 vs 2.956: physical only, Brutality + Heft |
| Flame Wall | 80 918 | 75 472 | −7%: Spell Cascade −30% more, Fortress −40% more |
| Frost Bomb (pre-crit hit) | 21 845 | 24 941 | +14%; DPS is far higher because the cooldown is missing |

* **Conditional stat lines now apply.** The importer reads PoB2's own `<Config>` inputs into
  `BuildDocument.Conditions` (PoB2 saves only the inputs a build changed, so an absent key is a
  default), and the calculator resolves `cast_speed_+%_when_on_low_life` (ModCache maps it to
  Speed INC with `Condition:LowLife`), `cast_speed_+%_if_have_crit_recently` (a ModParser tag,
  `ModParser.lua:1926` → `Condition:CritRecently`) and the moving/stationary mana-regeneration lines
  once the pools and the Low Life state are known. Arc's rate went 2.25 → 2.51 against PoB2's 2.3727.
* **`arcLightningInfused` stays off**, matching this build: PoB2's Arc consumes a Lightning Infusion
  for `+200% more` damage *only* when that config checkbox is set (`ConfigOptions.lua:212`), and this
  build's config does not set it. The mechanic is implemented behind the imported flag — the parity test
  prints Arc 309 298 → **927 893 (×3)** with the flag on — so a build that consumes the infusion is
  modelled exactly like PoB2. Note PoB2's own panel for this build (569 371) is therefore *without* the
  infusion; if the character really consumes it (Mana Tempest grants the Lightning Infusion), the checkbox
  has to be enabled on both sides.
* **"N% reduced Mana Cost"** (`base_mana_cost_-%`) is PoB2's `ManaCost` INC with `mult = -1`: the cost
  is divided by (1 + value/100) and then multiplied by its final (more/less) term.
* **`base_self_critical_strike_multiplier_-%` is an enemy-side mod** in PoB2 (`SelfCritMultiplier`,
  `CalcOffence.lua:3847`, listed under "Enemy modifiers" in `CalcSections.lua`) — it reduces the enemy's
  critical damage against you and never touches your own crit total.
* The unaccounted list is down from 22 to 6, and the six leftovers are quest/tree *text* lines with no
  numeric effect (`quest:`/`tree:`), catalogued rather than hidden.

The remaining offence gaps, in order of size:

1. **Pre-crit hit ×1.84 low** (34 061 vs 66 313 implied by PoB2's average hit and crit factor).
   Identified contributors:
   - **Flame Wall added damage**: the share code's config carries `flameWallAddedDamage=true`, and
     PoB2 adds Flame Wall's projectile damage to a projectile skill that travelled through the wall
     (`ConfigOptions.lua:379` sets `Condition:FlameWallAddedDamage`; the stats are
     `flame_wall_minimum_added_fire_damage` / `flame_wall_maximum_added_fire_damage` from the second
     effect, "Projectile Damage", in `Data/Skills/act_int.lua`). The pinned catalog has no
     `flame_wall_*` stat at all, so the values must come from a `Data/Skills/*.lua` extraction; poe.ninja's
     Arc base shows the result (fire 52–78) and they lift the gain-as pool, not only the fire part.
   - **Cooldown data**: the pinned export carries no cooldown, so Frost Bomb free-runs at 2.85/s instead
     of 1/6 s = 0.167/s. `Data/Skills/*.lua` has it (`cooldown`), together with the per-gem quality stats
     and the per-skill `statMap` — one extraction fixes all three.
   - **Heft's `maximum_physical_damage_+%_final`** is applied to the whole physical pool (documented
     approximation: the real mod only moves the maximum).
   - **The per-gem quality stat**: our `+1% increased damage per point of quality` is a documented
     approximation. Arc's real quality stat is `number_of_chains +0.1` (`Data/Skills/act_int.lua`), and
     the Gemling Legionnaire alt-quality stat is
     `active_skill_projectile_damage_+%_final_for_each_remaining_chain ×0.15` — which PoB2's own
     `SkillStatMap` does **not** map, so its panel excludes it as well.
   - **Enemy-side configuration** (resistances, exposure, ailments) is still not modelled.
2. **Rate**: was ×1.33 high, now 2.25 against PoB2's 2.3727 (−5%). The cause was a wording mix-up —
   `summon_totem_cast_speed_+%` is **TotemPlacementSpeed** in PoB2 (`SkillStatMap.lua:2447`), not cast
   speed, and only `totem_skill_cast_speed_+%` speeds up the skill the totem casts. The last few
   percent sit in tree lines we still list as unaccounted (`totem_skill_cast_speed_+%` +8,
   `attack_and_cast_speed_+%_on_placing_totem` +12).

Resistance sources now match exactly (148 / 112 / 140 before the −60 penalty, giving 75 / 52 / 75),
which is what made the resistances line up:

```
gear 118 / 82 / 110   (boots+helmet+ring+Morior "per Socket filled")
quests +15 / +15 / +15 (Act 1/2/3 + Act 4 Halls Of The Dead)
ring reflection +15 / +15 / +15 (Kalandra's Touch mirrors the other ring)
penalty −60 (PoB2's default resistancePenalty)
= 88 / 52 / 80 → capped 75 / 52 / 75
```

The older snapshot (`pob-real.txt`, level 95) is printed by the same test but asserted loosely: its
gear text differs from the state it was exported in, so its cold resistance reads 52 against 75.

The regression-only gate in `InteropTests` prints our Flameblast DPS against a recorded baseline and
also prints the PoB2 golden offence numbers, so the gap is visible rather than hidden.

## What changed in this pass

See `docs/POB2-FORMULAS.md` for the formulas and file/line references. Summary:

- Tree import no longer invents path nodes (`AllocateVerbatim`); the fixture now allocates exactly its
  130 nodes with zero extras (previously 152 with 22 invented ones).
- `<AttributeOverride strNodes/dexNodes/intNodes>` is honoured, so the 42 Intelligence / 3 Strength
  generic attribute nodes land on the right attribute instead of defaulting to Strength.
- Alternate class starts resolve from socketed jewels and legacy class names (`Templar` shares the
  Druid's start node), which removes the cross-tree detour.
- Tree stat lines are looked up with and without game markup, so attribute nodes and every other
  `[Tagged]` line now contribute.
- Imported items keep their full PoB text for all rarities; the item's own printed defence values
  (`Energy Shield: 243`) are authoritative, and rune/enchant (`{enchant}{rune}…`) lines are read.
- **`Bonded:` lines are gated** by PoB2's `CanUseBondedModifiers` condition
  (`ModParser.lua:1442`), which only "Gain the benefits of Bonded modifiers on Runes and Idols" sets.
  Without it those lines do not count — PoB2's own panel proves it for this build (Morior Invictus'
  `Bonded: +60 to maximum Mana` and the two `+20 to maximum Mana` bonded rune lines are absent from its
  Mana pool). This alone was **+100 Mana and +100 Life** on our side.
- **The `Implicits: N` block is read as one block**: PoB2 counts rune + enchant + implicit lines
  together (`Classes/Item.lua`), so an `Allocates X` enchant sits inside it and must not shift the
  count. The item's own rolls win over the pinned base implicit (a Lapis Amulet rolled
  `+12 to Intelligence` is no longer 15 + 12), and the block is never applied twice.
- **A flat defence line of an item that prints that defence is local** (PoB2 feeds the same
  `EnergyShield` BASE mod into the item's own `armourData`), so it is already inside
  `Energy Shield: 425` and must not be added to the global pool again: **−118 ES → −162 Mana** here.
- **`Allocates X` also grants anoints**: the amulet's `Allocates Paragon` hands over the Delirium node
  `+5 to all Attributes / +5% to Quality of all Skills`, which has no edges at all. Granted nodes are
  free and need no path, so `PassiveNode.CanBeGranted` admits it while path allocation still refuses
  it: **+5 Str/Dex/Int**.
- **Granted persistent buffs**: `Grants Skill: Virtuous Barrier` (Gemling Legionnaire's "Essence of
  Virtue") is modelled with its verified effect (10% increased Life, 15% increased Armour/Evasion/ES,
  189% increased Life Regeneration Rate — every value corroborated by PoB2's own panel).
- Affixes the pinned catalog cannot place are interpreted with the reverse stat table
  (`ApplyUnmatchedAffixText`), which is what brings back `+# to Intelligence`, `+# to Spirit`,
  `+# to maximum Energy Shield` and `+# to Level of all Spell Skills`.
- `Weapon 2` is the off-hand of the active set (PoB2 `ImportTab.slotMap`), so the off-hand item's
  Spirit and mana are counted.
- **Quest rewards** (`src/Data/QuestRewards.lua`) are resolved from the share code's
  `<Input name="quest…"/>` entries and applied: +20 Life, +30/+30/+40 Spirit, +15% to each elemental
  resistance, 5% increased Life/Mana, 30% increased Global Armour/Evasion/Energy Shield.
- **Kalandra's Touch** ("Reflects opposite Ring") duplicates the opposite ring's modifiers.
- **The elemental resistance penalty** is taken from PoB2's config: absent → −60 (Endgame), which is
  what our "endgame" stage applies.
- Energy Shield converted into Mana joins Mana's base before its increased modifiers, as PoB2 does.
- **The active weapon set is imported** (`useSecondWeaponSet` on `<Items>`/`<ItemSet>`) and priced in:
  the Twister reference build plays its swap set (*The Ordained, Grand Spear*, 0.714 s, +226% local
  physical, +7.54% crit), and PoB2's own `Speed 2.072` is exactly that weapon's rate × 1.85 × 0.8. This
  moved Twister from avg hit 2,016/rate 3.20/crit 16.9% to avg hit 9,436/rate 2.16/crit 42.4%.
- **Crit bifurcation** (Garukhan's Resolve: cap 50% + `attacks_roll_crits_twice`) per
  `CalcOffence.lua:3734-3756`: Twister's crit 42.4% → **66.8%** and the multiplier ×1.35, so its DPS went
  58,285 → **80,109** (PoB2 2,049,502; the remaining factor is base hit damage and crit sources — see
  `docs/POB2-FORMULAS.md` §11.3, which reads PoB2's own Calcs dump back to a base of ~6,805 vs our ~894).
- **Weapon-class-scoped tree mods** (`spear_critical_strike_multiplier_+` and friends — Javelin's +40%
  crit bonus, two +10% crit chance nodes): crit **73.45%** against PoB2's 75%, DPS → **92,042**. The
  `Weapon-class mods (spear)` breakdown line makes the resolved scopes visible.
- PoB2's **global** skill-stat table (`Data/SkillStatMap.lua`, 832 entries) is exported and consulted
  after each skill's own `statMap`; only its `flag(...)` entries are applied today (numeric ones would
  double count against the existing `*_final` fallback — proven with the fixture's own `Speed = 2.3727`),
  plus one allowlisted numeric id: Rakiata's Flow's resistance inversion.
- **PoB2's "effective" mode is implemented** (its Calcs panel and exported `TotalDPS` price the damage the
  enemy takes): 50% elemental resistance, 0% chaos, the level-82 monster armour at the enemy preset's ×1.5
  scale with `ArmourRatio = 10`, physical reduction `armour / (armour + raw × 10)` and Rakiata's Flow
  inversion (`×1.5` for the Twister, `×0.5` for the Huntress — both match PoB2's panels). Reported as
  `SkillDpsInfo.EffectiveDps` and as an "Effective DPS mod (enemy)" breakdown line, next to the raw DPS;
  enemy config keys (`enemyFireResist`, `enemyArmour`, …) are imported and override the defaults.
- **Third reference build added**: Huntress (Ice Shot, `pobb.in/Z-Y5RgB2CDO0`) is now a fixture with a
  parity test that prints our raw/effective numbers against PoB2's panel.


## Build links (import by URL)

Both tabs of the import window accept a link, because neither site serves the build at the URL a user
copies:

| Pasted link | Fetched | Payload |
|---|---|---|
| `https://pobb.in/<id>` | `https://pobb.in/<id>/raw` | the share code itself |
| `https://pobb.in/<id>/raw` | as-is | the share code itself |
| `<id>` (bare pobb.in token) | `https://pobb.in/<id>/raw` | the share code itself |
| `https://poe.ninja/<game>/profile/<account>/<league>/character/<name>` | `https://poe.ninja/<game>/api/profile/characters/<account>/<league>/<character>/model/0` | `charModel.pathOfBuildingExport` |
| any other `http(s)` link | as-is | JSON, or a share code embedded in the page |

- The pobb.in short link is a JavaScript page, which is why pasting it used to fail as "does not
  decode"; `/raw` returns the code.
- A poe.ninja character page is assembled in the browser from its own model API (the endpoint the
  site's own `ProfileCharPage` component calls). The model carries the character's Path of Building
  export, so the import runs through the same share-code path as a pasted code — no second parser.
- The code is located **by shape, not by field name** (`BuildInterop.FindPobCode`): any string in the
  JSON that decodes as a base64url + zlib envelope is accepted, so a renamed or nested export field
  keeps working, and a page that embeds the code in a script block is handled too.
- When a page carries no code at all (a site that renders everything client-side and exposes no API),
  the window says so explicitly and points at the PoB button instead of reporting "unrecognised text".
- Fetching happens once, only when Import is pressed; nothing is requested while typing.

## Remaining work for exact parity

1. Offence: see the table above — pre-crit hit, cast rate and enemy configuration. The defence panel,
   the attributes and the crit numbers are exact, so offence is the only open area left.
2. PoB2's `EffectiveHitPool` / `MaximumHitTaken` model. `TotalEHP` is
   `TotalNumberOfHits × totalEnemyDamageIn` (`CalcDefence.lua:3385`) where `TotalNumberOfHits` comes
   from a repeated-hit simulation (`numberOfHitsToDie`, line 3040) over pools, ward, aegis, guard,
   recoup and recovery. Our current value is the sum of per-type single-hit estimates, which is why it
   is much larger; it is printed next to PoB2's so the gap stays visible.
3. Granted **skills and auras** of imported items: a "Grants Skill: Level 15 Purity of Ice" line is
   modelled for the persistent buffs listed in `CharacterCalculator.GrantedSkillBuffs` (Virtuous
   Barrier), but an arbitrary granted aura still needs its per-level stat data, which the pinned export
   does not carry. That is the residual Life (−28) and Cold Resistance (−23) of the level-95 snapshot.
4. Movement speed is 126.0 vs 127.7: the remaining 1.7 points come from the sprint/action penalty
   modifiers, which are stored but not modelled.
5. Item affixes whose pinned template writes the game's display markup, and more importantly the whole
   `Adds X to Y …` family: `ModLineMatcher`'s range regex swallowed the whitespace around a bare number
   but not around a parenthesised range, so `Adds 14 to 24 Cold damage to Attacks` normalised
   differently from its own template and never matched. The panels' item lists (screenshots) made this
   visible — the Huntress has `Adds 4 to 70 Lightning` / `Adds 23 to 36 Fire` on the gloves and
   `Adds 21 to 34 Fire` / `Adds 14 to 24 Cold` on a ring, none of which reached the calculation.
   **Fixed.** The matcher resolves the game's display markup and its range templates now agree, so the
   whole family lands: Huntress Ice Shot 1 323 → **1 897** average hit, Huntress Bow Shot 1 454 →
   **2 235**, Twister 9 436 → **13 231**, and the Mercenary's Flameblast baseline moved 10 855.6 →
   **17 210.6**/s (+58.54 %), which is exactly the Glyph Chant wand's `Gain 29% of Damage as Extra
   Lightning/Cold Damage` affixes that PoB2 applies as well. The side effect the fix uncovered was the
   routing of flat defence affixes: `local_energy_shield` (`+71 to maximum Energy Shield` on armour) was
   going into the global pool and doubling the item's defence. PoB2's Mana breakdown proves it takes each
   item's **printed** Energy Shield (which already contains the local flat) plus the one global `+75` of
   the amulet, and our routing now matches — Mana is exact again on both Mercenary snapshots
   (4 576 / 4 465) and in the poe.ninja fixture. See `POB2-FORMULAS.md` §11.6.

6. Weapon-set allocations and radius jewels were the next two structural gaps.
   **Weapon sets:** PoB2 stores them as `<WeaponSet1/2 nodes="…">` and gives those nodes an allocation
   mode, so only the active set's nodes count (Classes/PassiveSpec.lua:272-277,
   Modules/CalcSetup.lua:264-277). The Twister fixture allocates 24 nodes per set; we used to apply both,
   which inflated its attack rate to 2.16/s against PoB2's 2.072/s. With the active set's nodes only the
   rate is 1.69/s, so the old figure was built on nodes PoB2 never counts — the remaining difference is
   unmodelled attack-speed sources, not the weapon.
   **Radius jewels:** implemented from PoB2's own bands (`data.jewelRadii["0_1"]`, Modules/Data.lua:626)
   times `PassiveTreeJewelDistanceMultiplier = 1.2` (Data/Misc.lua:36), matching the node type and
   applying each grant once per allocated node in radius (Modules/ModParser.lua:7041). This also required
   fixing the import: rare jewels whose base is outside the pinned catalog (Time-Lost Sapphire) were
   dropped twice over — the base-name list did not know them and the "shell" guard then discarded the
   item. The `<Socket itemId>` reference is now the authoritative signal. Huntress: crit 25.3 % → 35.42 %,
   effective hit 3 428.7 → 7 989.9. The crit *multiplier* now overshoots PoB2 (×11.23 against ×6.52):
   its panel implies the radius `increased Critical Damage Bonus` lines are not all in INC the way ours
   are, which is written up as the next open item in `POB2-FORMULAS.md` §11.9.
   **Resistances:** the Twister's two divergences were both identified and closed — `Purity of Fire` (+45
   fire) from the sceptre's granted skill and from the same gem in a socket, and Morior Invictus's
   `+15% to Chaos Resistance per Socket filled` × 4 runes, which is now implemented (chaos 18 → 75, the
   panel's value). All four rows now match PoB2 exactly, including the overcaps it prints separately:
   Fire 69 (+0), Cold 75 (+14), Lightning 75 (+12), Chaos 75 (+3). The aura is `40` at gem level 19 plus
   `math.modf(0.4 × 14 quality) = 5`, where the 14 points of global quality come from the two Gemling
   "Skill Gem Quality" nodes (+4), the amulet (+5) and a bonded rune (+5); the two instances of the same
   buff merge by name keeping the highest value, which is why the panel shows one row, not 90
   (`POB2-FORMULAS.md` §11.11). The same pass covers the second fixture: `Purity of Ice` at level 15 gives
   36 + 8 = 44 and `pob-real.txt`'s cold resistance matches its panel (75 + 21).
   **Weapon-set display:** the tree paints set I red (`#DD0022`) and set II green (`#33FF77`) — PoB2's own
   `colorCodes.NEGATIVE`/`POSITIVE` — for both nodes and connectors, with a legend naming the set that is
   currently in hand, the node's set in its tooltip, and three buttons that write the node's allocation
   mode. Socketed jewels now draw their radius circle too (`JewelRadius.OuterRadius` = PoB2's band × 1.2),
   so the nodes a Time-Lost jewel reaches are visible at a glance. The resistance rows now print the cap and
   the overcapped part (`75% (+14%)`) instead of a raw source sum, which is what made a build with
   overshooting gear look like "149% resistance".
   **Radius allocation (From Nothing / Intuitive Leap):** `PassiveTreePlan.RadiusJewels` stores the rule a
   socketed jewel states, and the engine measures that radius from the NAMED KEYSTONE — which, exactly like
   PoB2's `PassivesInIntuitiveLeapLikeRadius`, does not have to be allocated — letting those nodes be taken
   for their own point with no edge. The importer therefore stops routing a path to them: the Twister fixture
   allocates 148 nodes instead of 171 with nothing invented, and a jewel's cluster is refunded with the jewel.
   The Time-Lost grant wordings resolve through the pinned stat map, which lifted the Twister's critical
   chance to PoB2's 75% and its crit multiplier from ×5.78 to ×8.28.

**0.9.5 — the weapon itself, and the supports that were being dropped.** The weapon damage is now built by
   PoB2's own formula (`Classes/Item.lua:1909-1949`): the weapon's local `% increased Physical Damage` and its
   printed quality (quality as its own factor, physical only, and *not* clamped to 20 — The Ordained carries 26)
   multiply the weapon, while a weapon's added elemental damage takes only the local elemental increases and
   chaos takes none. `Base (weapon)` went 84 → **287.5** = (56+84)/2 × 3.26 × 1.26, and the weapon's own
   "Adds 1 to 296 Lightning Damage" is weapon damage instead of a global add. Support statMap mods are now
   evaluated against the build's own state, so Execute III contributes its real ×1.3 more (and the enemy-side
   half of it is *not* applied, exactly like PoB2 without `conditionEnemyLowLife`). The gem matcher prefers the
   most specific id tail, which keeps Projectile Acceleration **III** instead of silently importing tier I and
   restores its "projectile speed increases also apply to damage" flag (+134% of the build's projectile speed
   became damage). Net effect on the Twister: avg hit **13 230 → 26 691**, DPS **101 000 → 278 197** against
   PoB2's `TotalDPS 2 049 502`; the control case is a plain attack, where the Huntress's Bow Shot pre-crit hit
   is 2 580 against PoB2's 2 540 (1.6%). What is left on the Twister is the stack-based buffs it runs
   (Trinity, Elemental Conflux, Berserk) and charges — each already reported by name in the "unaccounted" list
   and listed in `POB2-FORMULAS.md` §11.13.

