# PoeBuilder — Phase 0 аудит

**Дата:** 2026-09-21
**Ветка:** `arena/01a0c56e-poebuilder`
**База аудита:** `812cc29fa91ecd267121cf35608354bdc2b90ffb`
**Статус:** аудит завершён для расчётного/import/tree/test ядра; follow-up implementation начат с P0-01.

**Follow-up implementation:** P0-01 effective player resistance считается как stage baseline + raw sources с верхним cap; отрицательные значения сохраняются. P0-02 ordinary stat lines from allocated ascendancy nodes теперь проходят через тот же stat interpreter. PoB XML import теперь сохраняет active/support gem quality; quality effects в skill calculation ещё не реализованы. Полный enemy/penetration pipeline и special conditional ascendancy mechanics по-прежнему не входят в эти небольшие PR. Runtime test run требует .NET 10 SDK и ещё не выполнен в sandbox.

> Этот документ фиксирует фактическое состояние репозитория, а не обещания из README. Все формулы, которые ещё не подтверждены одновременно исходным кодом PoB2, данными целевого патча и regression fixture, помечены **VERIFY**.

---

## 1. Итоговое заключение

PoeBuilder сейчас является самостоятельным PoE 2 planner с рабочими базовыми редакторами дерева, снаряжения, умений и частичным импортом, но не является PoB-паритетным калькулятором. В проекте это честно сформулировано в `README.md:43-56`: текущий экран персонажа — **v1**, а supports, условные эффекты, враг и часть conversion не моделируются.

Главный риск — не отдельная неточность округления, а то, что существующий `CharacterCalculator` вычисляет только panel-like snapshot и один упрощённый skill DPS. Он не имеет единого state/config/damage pipeline, поэтому добавление ещё одного `switch` в `StatInterpreter` само по себе не приведёт к PoB parity.

Наиболее важные результаты:

1. **Effective resistances сейчас не рассчитываются.** В UI показывается stage baseline (`0` или `-40`), а resistance от дерева/gear/jewel выводится отдельным sidecar. Это намеренная v1-семантика, но для build calculator она численно неверна.
2. **Ascendancy импортируется в отдельный plan, но его stats не попадают в `CharacterCalculator`.** В расчёт проходят `build.Tree.AllocatedNodes`, а `build.Tree.Ascendancy.AllocatedNodes` не проходят.
3. **Defence pipeline неполный:** нет hit chance, monster hit chance, block attack/spell, suppression, dodge/deflect chance, recovery model, EHP/hit simulation. Armour — только оценка против одного default monster hit.
4. **Offence pipeline неполный:** нет enemy config, resistance/penetration/exposure/reduction, full skill effectiveness, ailment/DoT, conditional states, charges/buffs, dual-wield/off-hand и полноценной conversion/gain ordering.
5. **Conversion order в коде не совпадает с актуальным PoB2 reference.** В `CharacterCalculator` порядок `physical → fire → cold → lightning → chaos`; текущий PoB2 `CalcOffence.lua` явно задаёт `Physical → Lightning → Cold → Fire → Chaos` и нормализует conversion, если сумма превышает 100%.
6. **Проблема gems/jewels из poe.ninja имеет две разные причины:** JSON fixture действительно содержит gems/supports, но формат не содержит equipment/jewels/sockets; PoB XML импортирует jewels частично. Нельзя лечить отсутствие данных JSON UI-binding-ом.
7. **PoB импорт не сохраняет `quality`, не выбирает active ItemSet корректно и не проверяет Adler-32 zlib envelope.** Это отдельные реальные дефекты, не только ограничения формата.
8. **Pinned data не подтверждены для текущего game patch.** Это явно указано в обоих manifests. Дополнительно `Data/Game/manifest.json` содержит hash `catalog.json`, не совпадающий с фактическим файлом.
9. Текущие тесты полезны как tests для v1 contract и parser shape, но не являются тестами PoE2/PoB correctness. В коде сейчас 107 зарегистрированных проверок (82 в test modules + 25 в `Program.cs`); `docs/test-results.txt` устарел и говорит о 102.

---

## 2. Методика и границы Phase 0

### Проверено подробно

- `src/PoeBuilder.Core/Calculation/CharacterCalculator.cs`
- `src/PoeBuilder.Core/Calculation/StatInterpreter.cs`
- `src/PoeBuilder.Core/Calculation/UnavailableCalculationEngine.cs`
- модели `BuildDocument`, tree/equipment/skills и их structural validation
- `src/PoeBuilder.Core/Tree/PassiveTreePlan.cs`, `TreeCatalog.cs`, `AscendancyPlan.cs`
- `src/PoeBuilder.App/Services/BuildInterop.cs` целиком по codec, JSON, PoB XML, items/jewels, export
- `SkillsViewModel.cs`, `GroupDraftViewModel.cs`, `TreeViewModel.cs`, `CharacterViewModel.cs` по relevant paths
- все test modules и обе реальные fixtures
- data manifests, notices, README, project files и solution layout

### Зафиксировано как отдельная следующая часть Phase 0

Полный visual/manual line-by-line аудит всех WPF XAML templates, всех localization keys, всех icon loaders и native file lifecycle ещё не превращён в отдельную check-list с acceptance tests. По уже просмотренным VM-контрактам критических расчётных проблем это не отменяет. До начала Phase 1 нужно завершить эту матрицу и провести запуск на Windows/.NET 10.

### Верификация запуска

На текущем sandbox нет `dotnet`. Попытка временно установить .NET 10 SDK в `/tmp/poebuilder-dotnet` не удалась при загрузке `latest.version` с `builds.dotnet.microsoft.com`/`ci.dot.net`. Поэтому в этой сессии не выдаётся ложное утверждение о зелёном runtime test run. Наличие старых `bin/obj` outputs не является воспроизводимым доказательством.

---

## 3. Архитектура и инвентаризация

### Solution/projects

- `src/PoeBuilder.Core/PoeBuilder.Core.csproj` — `net10.0`, pure C# domain/data/calculation.
- `src/PoeBuilder.App/PoeBuilder.App.csproj` — `net10.0-windows`, WPF, resources/data copied to output, version `0.9.1`.
- `tests/PoeBuilder.Tests/PoeBuilder.Tests.csproj` — `net10.0` console executable с собственным async test runner, не xUnit/NUnit.
- `PoeBuilder.sln` — Visual Studio solution. Отдельного `.slnx`, CI workflow или test adapter в репозитории не найдено.

### Core layers

- `Core/Models` — serialized build document, tree, skills, stats contracts.
- `Core/Equipment` — pinned game catalog, item/mod/socketable models и equipment rules.
- `Core/Skills` — gem/skill-group validation and plan.
- `Core/Tree` — graph allocation, path/connection validation, attribute variants, ascendancy graph.
- `Core/Calculation` — `StatBucket`/whitelist interpreter + `CharacterCalculator` simplified v1.
- `Core/Storage` — schema 4 native `.poebuild`, migration/atomic persistence.

Расчётный слой не разделён на PoB-подобные stages (`BuildState → modifiers → skill instance → hit/DoT → enemy result`). `CharacterCalculator` одновременно собирает sources, применяет моды и форматирует итоговую модель. Это основное архитектурное ограничение для последующих PR.

### App layers

- `App/Services/BuildInterop.cs` — import/export/codec и часть domain mapping находится в WPF-проекте, а не в transport-independent Core.
- `App/ViewModels` — editor state, import report display, calculation display.
- `App/Views` и XAML — UI/editor.
- `App/Data` — pinned tree/game/icons datasets.

### Reproducibility / repository hygiene

`.gitignore` отсутствует. В Git tracked около 1,408 файлов под `*/bin/*` и 92 под `*/obj/*`. Это создаёт риск использовать старый compiled output и затрудняет проверку, что именно было собрано. Это не исправлялось в Phase 0, но должно стать отдельным P2 hygiene PR после согласования с владельцем репозитория.

---

## 4. Текущее поведение калькулятора

### 4.1 Resources и attributes

`CharacterCalculator.cs:48-73,127-139`:

- level clamp: `1..100`;
- class base attributes берутся из pinned tree export;
- `life = (baseLife + flatLife) * (1 + lifeInc/100)`;
- `mana = (baseMana + flatMana) * (1 + manaInc/100)`;
- ES, spirit, armour, evasion имеют flat/increased paths;
- level baselines в коде: `+12 life`, `+4 mana`, `+6 accuracy`, `+3 evasion` за уровень;
- attributes: `+2 life/Strength`, `+5 accuracy/Dexterity`, `+2 mana/Intelligence`.

Проблемы:

- reservation/unreserved pool отсутствует;
- life/mana/ES conversion, overrides, more/less, recovery, leech и damage bypass отсутствуют;
- ES recharge задан как постоянные `12.5%/s` (`CharacterCalculator.cs:43-44,173`), без delay/interruption/recharge state;
- качество предметов и gem quality не участвуют в формулах;
- формулы base growth и `12.5%` требуют подтверждения для target patch (**VERIFY**), даже если сейчас они явно зафиксированы в README/tests.

### 4.2 Armour, evasion, block, deflection

`CharacterCalculator.cs:75-105,153-159`:

- base armour/evasion/ES item properties умножаются на local item increases, затем armour/evasion/ES global increases;
- block — сумма `base block * (1 + local block increase)` по обработанным shield-like slots;
- deflection — `evasion * DeflectPctOfEvasion / 100`;
- armour estimate — `A / (A + 12 * monsterPhysicalDamage)`, clamp `0..90%`.

Недостатки:

- armour estimate не является damage-taken pipeline и не учитывает hit type, enemy modifiers, penetration/reduction или elemental armour;
- block не разделён на attack/spell, нет cap/enemy accuracy/block chance modifiers, block damage consequence;
- evasion/accuracy остаются ratings, hit chance отсутствует;
- deflection отображается rating, не chance/result; dodge/suppression отсутствуют;
- EHP не вычисляется.

**VERIFY: Armour Ratio.** Текущий код и README используют `12`. Это не следует заменять на пользовательское число `5` без oracle fixture: актуальный PoB2 `CalcDefence.lua` читает `data.misc.ArmourRatio`, то есть ratio data-driven. Текущая страница [PoE2 Wiki: Armour](https://www.poe2wiki.net/wiki/Armour) документирует `A/(A+10*D)` и одновременно помечает раздел формулы как требующий обновления после изменений 0.1.1; публичные guides/community posts также встречаются с `12`. Поэтому `10` и `12` — кандидаты для проверки, а не безусловная истина для текущего target patch. Для sanity-check можно смотреть [Maxroll Defence Guide](https://maxroll.gg/poe2/getting-started/defence-guide), но authoritative target-patch значение должно прийти из pinned PoB/data fixture.

### 4.3 Resistances — подтверждённая ошибка результата

`CharacterCalculator.cs:161-182` и `CharacterViewModel.cs:118-176` намеренно возвращают:

- `FireRes/ColdRes/LightRes = stage baseline`: starter `0`, endgame `-40`;
- `ChaosRes = 0` baseline;
- gear/tree/jewel contribution отдельно как `FireResSources` etc.;
- `StageRes` делает `Math.Max(0, Math.Min(value, 75 + max))`.

Это приводит к нескольким неверным результатам:

1. мод `+50 Fire Resistance` при endgame показывает `-40`, а не `+10` effective total;
2. отрицательная resistance source теряется через `Math.Max(0, ...)`;
3. resistance cap применяется к sidecar, а не к сумме baseline + all sources;
4. max resistance не сопровождается полноценным effective cap calculation;
5. exposure, resistance reduction, penetration и enemy resistance отсутствуют.

Это **P0** для defence correctness. UI может показывать baseline и sources как diagnostic, но итоговый panel value должен быть raw/effective total согласно выбранной конфигурации.

### 4.4 DPS и crit

`CharacterCalculator.cs:209-339`:

- один active gem на группу, main-hand context;
- attack base damage из weapon props, spell damage из per-level gem static data;
- `rate` из attack/cast time и части speed stats;
- crit chance — simplified base × increased × selected support multipliers;
- crit bonus starts at `100%`;
- `DPS = AvgHit * rate * (1 + critChance * critBonus)`;
- supports can apply only recognized `_final` static ids; support level is not used in this pass;
- quality is not used;
- disabled/wrong-set/no-weapon are notes rather than full conditional state.

Missing/wrong for PoB parity:

- no enemy hit chance / accuracy / evasion result;
- no enemy resistance or penetration/exposure/reduction;
- no skill damage effectiveness and full attack/spell base stages;
- no off-hand/dual-wield and weapon-set mechanics beyond selecting one main slot;
- no charges, buffs, flasks, conditions, enemy config, projectile/area/coverage;
- no ailment DPS, DoT, duration, hit frequency model;
- no full support mechanics, support level and quality scaling;
- no separate hit, average hit, burst, sustained, DoT and combined DPS semantics.

### 4.5 Conversion

`CharacterCalculator.cs:353-390` applies only gem-native static ids. `TypeWords` at line 412 is:

```text
physical, fire, cold, lightning, chaos
```

The current PoB2 reference `CalcOffence.lua` explicitly defines:

```text
Physical, Lightning, Cold, Fire, Chaos
```

and its `processDamageConversion` scales conversions if their total exceeds 100%. Therefore current code is not a reliable general conversion engine. It also does not merge all skill/tree/item conversion sources or build a stable per-stage conversion table.

Reference: [PoB2 CalcOffence.lua](https://raw.githubusercontent.com/PathOfBuildingCommunity/PathOfBuilding-PoE2/dev/src/Modules/CalcOffence.lua), specifically `dmgTypeList`, `processDamageConversion`, `calcConvertedDamage` and `calcGainedDamage`.

**VERIFY:** conversion and gain-as ordering must be tested against a PoB-generated fixture, not inferred from one gem’s static data. Required first oracle: 100 physical with chained physical/lightning/cold/fire/chaos conversion plus >100% competing conversion.

---

## 5. StatInterpreter: coverage and failure mode

`src/PoeBuilder.Core/Calculation/StatInterpreter.cs:5-284` uses an explicit whitelist.

Positive property: unknown IDs are not silently lost:

- `Extras` stores known-but-not-consumed lines;
- `Unaccounted` counts default/unknown IDs;
- conditional families (minion, ailment, flask, charge, leech, freeze, poison, etc.) are deliberately not applied.

This is good observability, but not parity. Important current categories:

- life/mana/ES/armour/evasion/resistance/attributes: partial;
- speed/crit/added damage/gem levels: partial;
- scoped damage: partial string/tag matching;
- `GainAs`: collected, then simplified in calculator;
- suppression, penetration, exposure, reduction, enemy damage, ailment/DoT and conditional mechanics: not implemented;
- many catalogued IDs become `Extras`, while unrecognised IDs become `Unaccounted`.

Risk: string ID matching is spread across a large switch and does not carry modifier scope, source, condition, stack rule or stage. A future implementation should introduce typed modifier records and a data-driven semantic mapping instead of growing this switch indefinitely.

---

## 6. Tree and ascendancy audit

### Main tree

`PassiveTreePlan.cs:36-143` provides:

- pinned dataset guard;
- graph connectivity and shortest path allocation;
- attribute variant validation;
- free jewel-granted nodes via `JewelAllocatedNodes`;
- separate ascendancy graph validation.

This is a useful planner foundation, but it is not a complete PoB tree engine:

- unsupported constraints/multiple-choice/mastery/anoint mechanics are rejected or omitted (`TreeCatalog.cs:84-91`);
- stat application uses catalog lines but does not resolve all variant/conditional/passive mechanics;
- tree patch equivalence is explicitly unverified;
- jewel radius is skipped in `CharacterCalculator.cs:108-124`.

### Confirmed UI accounting bug

`PassiveTreePlan.cs:42` correctly computes `Spent` as `AllocatedNodes.Except(JewelAllocatedNodes)`. `TreeViewModel.cs:41` computes its private `Spent` from every `Allocated` node and therefore counts jewel-granted free nodes as paid. The imported plan can validate successfully while the UI point summary/over-budget command reports a larger cost. This needs a unit test at VM/domain boundary.

### Confirmed ascendancy calculation gap

`BuildInterop.cs:88-97,231-245` imports ascendancy nodes into `build.Tree.Ascendancy`. `BuildInterop.ExportBuildJson` also exports them (`:410-416`). However, `CharacterCalculator.cs:65-73` iterates only `build.Tree.AllocatedNodes.Append(start)`. It never traverses `build.Tree.Ascendancy.AllocatedNodes` or applies ascendancy graph stats. Thus ascendancy can look allocated in UI/import report but do nothing to the calculated character.

This is **P0** once ascendancy stats are advertised as affecting a build.

---

## 7. Import/export audit

### 7.1 Official Build Planner JSON / poe.ninja-style data

`BuildInterop.ParseBuildJson` (`BuildInterop.cs:33-150`):

- reads name, ascendancy, passive IDs, active gems, nested `support_skills`;
- resolves stable IDs and chains graph paths;
- reads `level_interval[0]` for active gems;
- supports up to five supports;
- reports unknown IDs;
- explicitly writes notes that quality is zero and items/inventory are not carried.

The real fixture `tests/PoeBuilder.Tests/Fixtures/ninja-real.json` has only these root keys:

```text
name, author, ascendancy, passives, skills
```

It has 14 active skill records and nested supports, but no `items`, `jewels`, `sockets`, `equipment` or tree-socket payload. Therefore no parser can recover jewels/items from this exact payload. This is a format/data limitation, not a missing binding.

Confirmed limitations:

- JSON import cannot preserve gem quality because this schema does not provide it;
- there is no stable adapter for an arbitrary poe.ninja URL/HTML; UI passes user-provided text to the schema parser;
- exported JSON (`BuildInterop.cs:400-436`) contains IDs/support IDs but not local native equipment/jewel data;
- `GameVersion` is hard-coded to `0.5.5c`, even though manifests say patch equivalence is unverified.

The test at `InteropTests.cs:183-197` proves parser shape and fixture accounting, not that poe.ninja supplies full build state.

### 7.2 PoB code

`BuildInterop.ParsePobCode` (`BuildInterop.cs:154-323`) handles base64url → zlib → XML, main/ascendancy node IDs, skills, equipment and jewel sockets partially.

Confirmed defects/limitations:

1. **Gem quality import is fixed in follow-up:** `BuildInterop.cs:264-286` now reads/clamps the XML `quality` attribute for active gems, supports and extra active gems moved into supports. `GemSelection.Quality` is preserved; the calculator still does not consume quality effects.
2. **Active item set selection is wrong.** `BuildInterop.cs:460-477` groups all `<Slot>` elements by name and chooses `.First()`. It does not choose the active ItemSet from the selected spec. A build with two item sets can import gear from the wrong set.
3. **Jewel duplicate path exists.** `BuildInterop.cs:478-493` intends to parse “unreferenced jewels”, but `referenced` is built from our generated `Guid.ToString()` values while the loop keys are PoB numeric item IDs. A jewel already present in a `<Slot>` can be parsed twice. The current fixture uses external `<Socket>` records and may not trigger this branch.
4. **Jewel recognition is heuristic.** `PobJewelBases` is hard-coded to four base names plus unique identity lookup (`:557-583`), so future bases/patch variants require data update.
5. **Mod matching is exact English normalized text.** Unknown/localized/changed lines are skipped; unique item mods are retained as notes and do not affect calculations (`:634-661`).
6. **PoB slot coverage is partial.** `MapPobSlot` (`:532-554`) maps a small fixed set and has no general schema for all weapon/off-hand/charm/item-set semantics.
7. **Only some `Allocates …` grants are applied.** Name matching requires exactly one supported ordinary main-tree node (`:501-529`); variants, ascendancy nodes and conditional grants are skipped and reported.
8. **Socketed jewel radius lines are not calculated.** The item is imported, but the calculator marks `JewelRadiusSkipped` (`CharacterCalculator.cs:108-124`).

The real PoB fixture gives a valuable baseline: 5 socket records, 3 free `Allocates` nodes, 13 slotted gear items plus jewels/uniques. Existing tests verify those counts, but not full stat equivalence to PoB.

### 7.3 zlib codec

`BuildInterop.DecodePobEnvelope` (`:733-766`) inflates from offset 2, falls back to offset 0, and checks only non-empty output. It does **not** explicitly validate zlib header/checksum or Adler-32. `EncodePobEnvelope` writes Adler-32, but the decode test only checks round-trip. A corrupted checksum can potentially be accepted if deflate/XML remain parseable.

Required regression: flip one Adler byte and assert `InvalidDataException`/import rejection.

---

## 8. Data provenance and release risk

### Game catalog

`src/PoeBuilder.App/Data/Game/manifest.json:2-14` says:

- RePoE commit `b818b843...`;
- published version `4.5.5.2`;
- `gamePatchEquivalenceVerified: false`;
- requirements source is a PoE2 `0.1.0` dump and is not verified for current patch.

### Tree

`src/PoeBuilder.App/Data/Tree/manifest.json:2-12` says GGG tree export `0.5.5`, target user patch `0.5.5c`, but `targetPatchEquivalenceVerified: false`.

### Manifest integrity issue

Actual hash check performed during the audit:

```text
actual src/PoeBuilder.App/Data/Game/catalog.json
9a6dfd49b1579f6c37a7a97893d6cdf815e63476f0fba8b8e30c4d34991908ac

manifest.json declares catalog.json
62f29fd142f48517ac1a22a8f1519238e98c84b1092e95ba94d0b72f4e4514e1
```

`statmap.json` and tree `data.json` matched their manifest hashes. `GameCatalog.Sha256` currently matches the actual `catalog.json`, so runtime catalog loading is not necessarily broken, but provenance verification is inconsistent. This must be fixed before publishing a “pinned/reproducible” release.

### External references used

- PoB2 defence reference: [CalcDefence.lua](https://raw.githubusercontent.com/PathOfBuildingCommunity/PathOfBuilding-PoE2/dev/src/Modules/CalcDefence.lua) — data-driven armour ratio, player/monster hit chance and deflect stages.
- PoB2 offence reference: [CalcOffence.lua](https://raw.githubusercontent.com/PathOfBuildingCommunity/PathOfBuilding-PoE2/dev/src/Modules/CalcOffence.lua) — conversion order, over-100% normalization, damage type stages, gain-as and ailment data lists.
- Community wiki reference: [PoE2 Wiki: Armour](https://www.poe2wiki.net/wiki/Armour). It currently documents ratio 10 but marks its formula section as needing update after 0.1.1, so it is evidence, not a final target-patch oracle.
- Public defence sanity reference: [Maxroll PoE2 Defence Guide](https://maxroll.gg/poe2/getting-started/defence-guide). It is useful for cross-checking public explanations, not a replacement for target-patch data.
- Official GGG statement surfaced in the [armour/elemental damage forum thread](https://www.pathofexile.com/forum/view-thread/3788776): armour is applied before elemental resistance in the cited scenario. This still needs a versioned PoB/in-game fixture for the exact current pipeline.
- Official API reference: [GGG developer API](https://www.pathofexile.com/developer/docs/reference#characters). It must not be treated as a complete PoE2 build export; current project uses local user-provided JSON/PoB text.
- Official tree export source is recorded in `Data/Tree/manifest.json`; game asset licensing is documented in `THIRD-PARTY-NOTICES.md`.

---

## 9. Test audit

### Existing strengths

- parser shape and unknown-ID reporting;
- PoB envelope round-trip;
- real PoB2 and JSON fixtures;
- tree connectivity/path rules;
- item pool/range/rarity/slot validation;
- native storage round-trip/migration;
- basic v1 life/mana/accuracy/evasion, Fireball, weapon, armour and catalog checks;
- free jewel-granted tree nodes are checked at engine level.

### Existing false-confidence tests

- `CalculationTests.cs:240-251` explicitly asserts that support level/quality do not alter v1 DPS. This is a valid v1 contract test, but it must be renamed/isolated as a limitation test once support mechanics are implemented.
- `CalculationTests.cs:264-271` asserts endgame elemental resistance remains `-40` even without gear total. This encodes the current stage-baseline UI design, not correct effective resistance.
- `InteropTests.cs:121-137` checks real PoB counts but not quality, active item set, resistance output, ascendancy stats or DPS differential.
- `InteropTests.cs:139-155` verifies engine free-node cost, but no `TreeViewModel` test catches its separate spent-count bug.
- `InteropTests.cs:173-181` asserts imported DPS is merely `>100`, not equal to a PoB oracle.

### Missing mandatory regression suites

1. effective resistance: baseline + gear/tree, max cap, negative, chaos, exposure/reduction/penetration;
2. armour ratio and physical hit damage, including the ratio data source;
3. player hit chance, monster hit chance, evasion entropy/sequence if applicable;
4. block attack/spell, suppression cap and spell damage result;
5. life/ES/mana reservation and recovery/recharge interruption;
6. EHP as a vector by damage type and hit size, not one misleading scalar;
7. full conversion order and >100% conversion normalization;
8. gain-as, more/less ordering, skill effectiveness, crit and speed;
9. ailments and DoT duration/stacks/mitigation;
10. active ItemSet selection, gem level/quality import, Adler corruption rejection;
11. jewel socket identity, radius effects, `Allocates …`, unique mod policy;
12. ascendancy stats changing CharacterSummary;
13. differential fixtures against the same PoB2 build/config, with expected tolerances.

---

## 10. Prioritised defect backlog

### P0 — blocks trustworthy calculator parity

| ID | Finding | Current location | Exit test |
|---|---|---|---|
| P0-01 | **Implemented in follow-up:** effective resistance is total; negative sources are preserved | `ResistanceCalculator.cs`; `CharacterCalculator.cs:160-185` | baseline +50, negative, max-res cases are covered by new tests; runtime run pending |
| P0-02 | **Implemented in follow-up for ordinary mapped stat lines:** ascendancy graph is now applied through the stat interpreter | `CharacterCalculator.cs:65-86,185-196` | new fire-resistance ascendancy fixture; runtime run pending |
| P0-03 | No defence result pipeline for hit chance/block/suppression/EHP | `CharacterCalculator.cs:153-159`, summary contract | deterministic fixture against PoB2 defence oracle |
| P0-04 | No enemy config/resistance/penetration/exposure/reduction stage | `CharacterCalculator.cs:209-339`; `StatInterpreter.cs:74-82,246-255` | cold hit vs enemy 0/50/75 res + penetration fixture |
| P0-05 | Conversion order and source scope are wrong/partial | `CharacterCalculator.cs:353-412` | PoB2 conversion table differential fixture |
| P0-06 | Current data patch equivalence is unverified | both manifests | release gate shows verified dataset or clearly blocks parity claims |
| P0-07 | Imported items can still describe a build different from selected PoB spec; gem quality import is now preserved | `BuildInterop.cs:264-286,460-477` | quality fixture added; active ItemSet fixture remains |

### P1 — required for useful build planner

| ID | Finding | Current location | Exit test |
|---|---|---|---|
| P1-01 | support levels and quality do not affect full skill calculation | `CharacterCalculator.cs:229-279`; VM only displays quality | support level/quality differential fixture |
| P1-02 | life/mana/ES reservation and recovery absent | `CharacterCalculator.cs:127-139,173` | reserved/unreserved/recovery cases |
| P1-03 | block, suppression, dodge/deflect and EHP absent | summary and interpreter contracts | defence matrix |
| P1-04 | ailments/DoT/charges/buffs/conditional states absent | `StatInterpreter.cs:74-82` | ailment/DoT fixture |
| P1-05 | jewels import but radius and unique effects are not calculated | `CharacterCalculator.cs:108-124`; `BuildInterop.cs:649-661` | radius/unique policy fixture |
| P1-06 | PoB zlib Adler-32 is not verified | `BuildInterop.cs:735-766` | corrupted checksum rejects |
| P1-07 | catalog/statmap whitelist misses broad stat families | `StatInterpreter.cs:84-255` | coverage report by catalog stat ID |
| P1-08 | full item set, off-hand, weapon and flask semantics are incomplete | `BuildInterop.cs:532-554`; calculator slots | multi-set equipment fixture |

### P2 — quality, release and UX

| ID | Finding | Current location | Exit test |
|---|---|---|---|
| P2-01 | VM spends free jewel nodes as paid | `TreeViewModel.cs:41` vs `PassiveTreePlan.cs:42` | imported Megalomaniac UI point summary |
| P2-02 | no `.gitignore`; build outputs are tracked | repository root | clean clone/build leaves no tracked generated files |
| P2-03 | `docs/test-results.txt` reports 102 while current code registers 107 | `docs/test-results.txt:1` | generated test count/report |
| P2-04 | manifest/catalog hash mismatch | `Data/Game/manifest.json:30-33` | hash verification script |
| P2-05 | UI parity: no PoB config panel/defence/offence breakdown, limited tooltips for conditional math | WPF views/VMs | manual acceptance checklist on Windows |
| P2-06 | localization and data source labels need patch/version/unknown semantics | `CharacterViewModel.cs:155-166`, manifests | source/assumption UX review |

---

## 11. Required regression test cases

These are acceptance cases, not yet claims about final mechanics. A case marked **VERIFY** needs a PoB2/data oracle before its numeric expected value is frozen.

### Defence

1. **Resistance total:** starter + `+50 fire` ⇒ `50%`; endgame + `+50 fire` ⇒ `10%`; starter `-20 fire` ⇒ `-20%`; cap/max-res fixture verifies cap. Current implementation fails all total/negative cases.
2. **Armour ratio (VERIFY):** armour `1000`, physical hit `500`; run with pinned ratio from data and compare result. Ratio 12 gives `14.2857%` reduction; ratio 10 gives `16.6667%`. This deliberately distinguishes disputed constants.
3. **Evasion hit chance (VERIFY against current PoB2):** accuracy `1000`, evasion `1000`; current PoB2 formula is `clamp((1000*1.25)/(1000+1000*0.3)*100, 5, 100) = 96.1538%`.
4. **Block:** independent attack/spell block input, cap and enemy modifiers; verify blocked-hit damage semantics (**VERIFY**).
5. **Suppression:** 50% suppression against a 100 spell hit; verify cap and ordering against PoB2 fixture (**VERIFY**).
6. **EHP:** report per damage type and hit size; do not freeze one scalar until armour/resistance/recovery semantics are defined.

### Offence

7. **Conversion:** 100 physical through Physical → Lightning → Cold → Fire → Chaos with explicit conversion table; compare every intermediate split to PoB2.
8. **Over-conversion:** competing conversions with total >100%; assert PoB normalization, not sequential overdraw.
9. **Enemy config:** same hit against enemy resistance 0/50/75 with penetration/exposure/reduction; assert effective enemy resistance separately from player resistance.
10. **Crit/speed:** base crit, increased crit, more crit, attack/cast speed and hit rate; compare breakdown, not only rounded DPS.
11. **Support/quality:** a support and quality value known to change a gem’s data must change the correct stage; unsupported mechanics remain explicitly reported.
12. **Ailment/DoT:** hit chance, ailment damage, duration/stacks and mitigation against a fixed target fixture (**VERIFY**).

### Import/tree

13. **PoB quality:** XML `<Gem quality="20">` becomes `GemSelection.Quality == 20`; JSON without quality records “format has no quality”, not silent equivalence.
14. **Active item set:** two `<ItemSet>` records with same slot, selected set 2; only set 2 contributes.
15. **Jewel identity:** five `<Socket itemId nodeId>` records create five unique socket mappings; no duplicate item IDs; malformed item/node is reported.
16. **Allocates:** Megalomaniac-like grant is free in domain and UI; unsupported/ambiguous node names are reported.
17. **Ascendancy:** toggle one ascendancy node with known stat; summary changes and export/import preserves it.
18. **Codec:** mutate zlib Adler-32 only; decode rejects corrupted payload.

---

## 12. Proposed implementation phases and PR boundaries

Each bullet is intended as one small logical PR with tests/manual verification. No phase should silently change old v1 numbers without a migration note and fixture update.

### Phase 0 — close the audit gate

- finish line-by-line inventory of remaining WPF/XAML/native files;
- fix/verify dataset hashes and produce a machine-readable source report;
- add a documented “target patch unverified” gate;
- define PoB2 oracle fixture format and test harness;
- do not yet expand formulas.

### Phase 1 — calculation contracts and configuration

- introduce immutable `CalculationContext` (player, active spec, weapon set, enemy, conditions, stage);
- split raw sources from derived results;
- add explicit `CalculationAssumption`/`UnsupportedMechanic` records;
- apply ascendancy graph stats through the same modifier pipeline;
- fix effective resistance first, with baseline/gear/tree/max/negative tests.

### Phase 2 — defence/EHP

- data-driven armour ratio and hit mitigation;
- evasion/accuracy and hit chance;
- block attack/spell, suppression, deflect/dodge as verified by target patch;
- reservation/unreserved resources, ES recharge/recovery states;
- EHP as a typed vector or scenario result, not a single unqualified number.

### Phase 3 — offence core

- skill instance and base damage effectiveness;
- correct damage type order and conversion table;
- increased/more ordering, added damage, gain-as;
- crit/speed and attack/off-hand/dual-wield;
- enemy resistance, penetration, exposure and reduction;
- differential tests against PoB2 fixture after each stage.

### Phase 4 — supports, quality, ailments and DoT

- level-dependent support data;
- active/support quality effects from catalog;
- ailments, DoT, duration/stacking and target mitigation;
- charges, buffs, flask/charm and condition configuration;
- every unimplemented mechanic continues to appear in breakdown until implemented.

### Phase 5 — import and data adapters

- separate format DTOs from domain plans;
- active PoB spec/item-set selection;
- gem quality/level/weapon set; robust unknown-line report;
- strict zlib validation;
- JSON v1 adapter remains honest about fields it cannot carry;
- add supported source/version/schema metadata rather than hard-coding `0.5.5c`.

### Phase 6 — tree, jewels and items

- data-driven passive variants/constraints/mastery/choice nodes;
- jewel socket/radius/unique effect model;
- ascendancy and `Allocates …` resolution by stable ID, not display-name guess;
- item unique/mod data policy and patch-versioned catalog;
- repair VM/domain point-budget consistency.

### Phase 7 — PoB-like UI/UX

- config panel for enemy/conditions/weapon set;
- breakdown for every derived defence/offence number;
- comparison view against imported PoB oracle;
- import warnings grouped by severity and source;
- manual Windows acceptance matrix and localization review.

### Phase 8 — release/reproducibility

- remove tracked generated build outputs only in a separately reviewed hygiene PR;
- add `.gitignore`, pinned SDK strategy and CI build/test;
- publish dataset manifests with verified hashes and target patch status;
- keep one PR per logical mechanic and one regression/manual check per PR.

---

## 13. Phase 0 exit criteria

Phase 0 should not be declared complete until all of the following are true:

- [ ] remaining source/XAML files have an audit owner/status;
- [ ] target game patch and PoB2 commit/data version are explicit;
- [ ] all current formula assumptions have a source or `VERIFY` label;
- [ ] catalog/tree/statmap hashes agree with their manifests;
- [ ] test count documentation is generated rather than hand-written;
- [ ] an oracle fixture can compare a build’s defence/offence breakdown;
- [ ] resistance and ascendancy bugs have named implementation PRs;
- [ ] the import report distinguishes “format has no field” from “field present but unsupported”;
- [ ] Windows/.NET 10 build/test has been run in an environment with the required SDK.

Until then, green v1 tests should be described as **planner contract tests**, not as PoE2 or Path of Building correctness proof.
