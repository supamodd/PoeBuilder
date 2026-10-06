# Формулы PoB2 → PoeBuilder: что перенесено и что осталось

Источник: `PathOfBuilding-PoE2-master/src` (папка в этом репозитории). Ссылки даны на файл и
строку, чтобы каждое утверждение можно было проверить. Эталонные числа — из самого билда:
`tests/PoeBuilder.Tests/Fixtures/pob-real.txt` это реальный share-код PoB2, и внутри него PoB2
сам записал свою панель как `<PlayerStat stat="…" value="…"/>`. То есть фикстура одновременно
вход и ожидание; тест `Pob2ParityTests` печатает таблицу «PoB2 / PoeBuilder / delta».

## 1. Дерево: пути и атрибуты

| Проблема | Что делал PoB2 | Что делал PoeBuilder | Исправление |
|---|---|---|---|
| «Лишний путь» при импорте | `nodes="…"` — это ПОЛНЫЙ список выделенных узлов (`Classes/PassiveSpec.lua`) | список прогонялся через поиск кратчайшего пути, который добирал узлы, которых в билде нет | `PassiveTreeEngine.AllocateVerbatim` — узел принимается только если он смежен уже выделенному набору (внешний список уже содержит все промежуточные узлы) |
| «+5 к любому атрибуту» всегда Strength | `hashOverrides` → `<AttributeOverride strNodes/dexNodes/intNodes>` (`PassiveSpec.lua:293-312`) | `BuildInterop.AttributeChoice = 26297` для всех узлов | `ApplyPobAttributeOverride` читает три списка и ставит выбор 26297/14927/57022 |
| Обход через всё дерево к чужому классу | `jewelData.alternateClassStart`; PoE2 держит legacy-старты PoE1 (`TEMPLAR` = старт друида) | самоцвет искался только в `<Slot>`, а имя «Templar» не находилось в списке игровых классов | `PobAlternateStartIds` читает и `<Socket>`-самоцветы, а `ResolveClassStartNode` ищет стартовый узел по его собственному имени |
| Строки со разметкой не влияли | statmap PoB2 по plain-тексту | `statMap` хранит plain-текст (`+5 to Strength`), а экспорт дерева — разметку (`+5 to [Strength]`) | `ApplyTreeStats` пробует и разметку, и `TreeCatalog.PlainText(line)` |
| `+5 to Intelligence` отсутствовал в statmap | — | узлы атрибутов молча выпадали | `PatternFallbackStats` разбирает `+N to Strength/Dexterity/Intelligence`, пары и «all Attributes» |

Проверено: импорт фикстуры даёт `str=3, dex=0, int=42` — ровно те списки, что записал PoB2, и
ровно `130` узлов без единого лишнего (было 152 с 22 придуманными).

## 2. Ресурсы и защита

PoB2 `Modules/CalcDefence.lua`:

```lua
-- строка 95 (doActorLifeManaSpirit)
output[res] = max(round((base * (1 - conv/100) + extra) * (1 + inc/100) * more + total), 1)
```

```lua
-- строки 1316-1322, 1399-1409 (buildDefenceEstimations)
resourceList = {
  { name = "Armour",       mods = { "Armour", "ArmourAndEvasion", "Defences" }, ... },
  { name = "Evasion",      mods = { "Evasion", "ArmourAndEvasion", "Defences" }, ... },
  { name = "EnergyShield", mods = { "EnergyShield", "Defences" }, ... },
  ...
}
output[ES] = output[ES] + basePerSlot[slot] * mod(slot) + globalBase * mod(global) + totalBase
-- не-defence цель получает Extra<res> как BASE: modDB:NewMod("ExtraMana","BASE", …,"Conversion")
```

Что из этого теперь в PoeBuilder:

- Порядок `(base + flat) × (1 + inc/100)` для Life/Mana/Spirit/ES/Armour/Evasion.
- **Конверсия ES → Mana** идёт в BASE маны (`ExtraMana`) и затем умножается на increased Mana —
  раньше конвертированный ES добавлялся уже после `ManaInc` (неверный множитель).
- Локальные «X% increased Energy Shield» на предмете масштабируют базу ЭТОГО предмета
  (`StatInterpreter` маршрутизирует `local_*` по `ItemContext`), глобальные — общий пул.
- Печатные значения предмета (`Energy Shield: 243`) — это итоговое значение предмета, поэтому они
  берутся как есть; база из закреплённого каталога дополнительно масштабируется качеством и
  локальными модификаторами. Раньше первые вообще терялись (у уников без базы), а вторые
  масштабировались дважды.
- Armour DR: `A/(A+10·hit)`, кап 90%, отрицательный армор усиливает (`armourReductionF`, строки 56-64).
- Player hit: `ACC·1.25/(ACC+0.3·EV)·100`, min 5 (`hitChance`, строки 32-38).
- Monster hit: `(1 − 0.95·EV/(EV+4·ACC))·100` (`monsterHitChance`, строки 40-46).
- Deflection: `100 − (ACC/(ACC+0.12·DEFL)·150 − 50)`, кап `DeflectionChanceCap` (`deflectChance`, строки 48-54).
- Резисты: база стадии (−60 на эндгейме) + источники, кап 75, поднимаемый `maximum_*`.

### Слоты оружия (важно для защиты)

`Modules/Classes/ImportTab.lua:1150`:

```lua
slotMap = { ["Weapon"] = "Weapon 1", ["Offhand"] = "Weapon 2",
            ["Weapon2"] = "Weapon 1 Swap", ["Offhand2"] = "Weapon 2 Swap", ... }
```

То есть `Weapon 1` и `Weapon 2` — это **правая и левая рука активного набора**, а пара «… Swap» —
второй набор. `BuildInterop.MapPobSlot` теперь читает их именно так; раньше `Weapon 2`
попадал в неактивный второй набор, из-за чего скипетр в левой руке не давал Spirit и ману
(Spirit был 0 против 398 у PoB2, сейчас 390).

### Квестовые награды и штраф резистов

`Modules/CalcSetup.lua:681-683`:

```lua
modDB:NewMod("FireResist", "BASE", env.configInput.resistancePenalty or -60, "Base")
-- то же для ColdResist и LightningResist
```

`Modules/ConfigOptions.lua:113` — список 0 (Act 1) … −60 (Endgame), `defaultIndex = 7` → **−60**.
То есть по умолчанию PoB2 считает персонажа «в эндгейме» и снимает 60% со стихийных резистов.

`Modules/ConfigOptions.lua:56-99` превращает каждую запись `Data/QuestRewards.lua` с
`useConfig = true` в галочку с `defaultState = true`, а имя переменной —
`"quest" .. Description .. Area .. Info`. Значит:

- персонаж «прошёл» все награды-`Stat` (Act 1: +10% Cold Res, +30 Spirit, +20 Life; Act 2: +10%
  Lightning Res; Act 3: +30 Spirit, +10% Fire Res; Act 4: 5% max Mana; Interlude 2: 5% max Life;
  Interlude 3: +40 Spirit);
- награды с `Options` по умолчанию «Nothing», а реальный выбор лежит в share-коде как
  `<Input name="quest…" string="…"/>`.

PoeBuilder читает эти `<Input>` и складывает результат в `BuildDocument.QuestRewards`;
`QuestRewardParser` превращает строки в stat-id, а нераспознанные награды попадают в «не учтено»
(видно в тесте), а не теряются молча.

Проверка на вашем билде: 118/82/110 (снаряжение) + 15/15/15 (квесты) + 15/15/15 (зеркало кольца)
− 60 = 88/52/80 → 75/52/75 — ровно панель PoB2.

### Kalandra's Touch: «Reflects opposite Ring»

`Modules/ModParser.lua:3431-3436` помечает строку как display-only — сам эффект делает обработка
предметов. PoeBuilder повторяет модификаторы противоположного кольца
(`CharacterCalculator.ReflectsOppositeRing`), поэтому второе кольцо даёт те же +15% ко всем
стихиям, +208 маны, +29 интеллекта и т.д.

### Текст предмета: блок `Implicits: N`, строки `Bonded:` и локальные защиты

Три правила, без которых пулы не сходятся (проверены на обоих share-кодах и на модели poe.ninja
того же персонажа, где poe.ninja печатает разбор каждого стата по источникам):

1. **Блок `Implicits: N` — это rune + enchant + implicit строки вместе.**
   `Classes/Item.lua:1574`: `"Implicits: " .. (#runeModLines + #enchantModLines + #implicitModLines)`.
   Поэтому `{enchant}Allocates Paragon` на амулете ВХОДИТ в блок и не должен сдвигать счётчик
   (иначе следующая реальная аффиксная строка `+4 to Level of all Spell Skills` считалась имплиситом
   и терялась). Внутри блока строки разбираются один раз: значение из текста предмета (реальный ролл)
   побеждает значение из закреплённой базы (там хранится один max-ролл на базу) — Lapis Amulet с
   `+12 to Intelligence` давал 15 + 12 = 27, теперь ровно 12.
2. **`Bonded:` гейтится условием `CanUseBondedModifiers`.**
   `Modules/ModParser.lua:1442`: `["^bonded: "] = { tag = { type = "Condition", var =
   "CanUseBondedModifiers" } }`; условие ставит только мод
   `Gain the benefits of Bonded modifiers on Runes and Idols` (`Data/ModCache.lua:5680`).
   Строка Morior Invictus `Idols socketed in this item gain the benefits of their Bonded modifiers`
   в `Data/ModScalability.lua:10292` отображена в **пустой** список модов, то есть условие не
   включает. Панель самого PoB2 это подтверждает: `Bonded: +60 to maximum Mana` (Morior) и два
   `Bonded: +20 to maximum Mana` в его пул маны не входят. У нас они давали +100 маны и +100 жизни.
3. **Плоская защита на предмете, который печатает свою защиту, — локальная.**
   `Classes/Item.lua:1975`: `local energyShieldBase = calcLocal(modList, "EnergyShield", "BASE", 0)`
   — тот же `EnergyShield` BASE идёт в собственную базу предмета, а печатное `Energy Shield: 425`
   уже его содержит. Повторный учёт в глобальном пуле удваивал вклад: `+71` (Soul Glance) и `+47`
   (Anarchy Hoof) давали +118 ES → +162 маны после конверсии в ману.

Плюс два механизма, которые PoB2 считает, а мы раньше теряли:

- **`Allocates X` — это и анойнт-узел.** `Allocates Paragon` (enchant амулета) выдаёт узел Delirium
  `Paragon` (`+5 to all Attributes`, `+5% to Quality of all Skills`), у которого в дереве нет рёбер
  (`isBlighted`, `recipe`). Такой узел выдаётся бесплатно и не требует пути: `PassiveNode.CanBeGranted`
  (в отличие от `IsSupported`) допускает его в наборе выданных узлов, а обычное выделение по-прежнему
  отказывает. В панели PoB2 его +5 к каждому атрибуту есть — значит без него Str/Dex/Int не сходятся.
- **Выданные постоянные баффы.** `Grants Skill: Virtuous Barrier` (Gemling Legionnaire, узел
  «Essence of Virtue») — в `CharacterCalculator.GrantedSkillBuffs` записаны проверенные значения:
  10% increased Life, 15% increased Armour/Evasion/Energy Shield, 189% increased Life Regeneration
  Rate. Все три подтверждаются панелью PoB2: Life = (1168 + 176 от Strength + 139 flat) × 1.15,
  Armour/Evasion = пул × 1.45 (30% квест + 15% бафф), `LifeRegenRecovery` = 2046/60 × 2.89 = 98.5.

## 3. Урон

- База атаки — средний физ. урон оружия × `(1 + (PhysInc + quality)/100)`; локальные модификаторы
  оружия идут через `WeaponContext` (не влияют на заклинания).
- База заклинания — `spell_minimum/maximum_base_*` уровня камня; added damage — с
  `damage_effectiveness`.
- Порядок: конверсия → «gain as extra» (общий `GainAs` и по источнику `SourceGainAs`) → increased
  (аддитивно) → more/less (мультипликативно) → крит.
- Крит: `chance = base·(1+inc/100)·more`, `bonus = (base100 + flat adds)·(1+inc/100)·(1+conditional more/100)·more` —
  `CritBonusInc`/`SpellCritBonusInc` (PoB2 `CritMultiplier` INC), `CritBonusMorePct` (условный
  `MORE`, например Pain Attunement на Low Life). Формула сверена с `CalcOffence.lua:3813-3858` и
  `CalcSetup.lua:679` (база `base_critical_hit_damage_bonus = 100`), результат совпадает до сотых:
  +585% и +546% на двух фикстурах.
- **`PerStat`-теги PoB2 округляют вниз**: `Classes/ModStore.lua` —
  `m_floor(base / div + 0.0001)`. Поэтому `spell_damage_+%_per_100_maximum_mana`,
  `spell_critical_strike_chance_+%_per_100_maximum_mana` и Archmage считаются по ЦЕЛЫМ сотням маны:
  при пуле 4576 это 45 «стаков», а не 45.76. Разница видна в крит-шансе Rathpith Globe.
- Rate: `1000/attackTime|castTime` с учётом cooldown; more/less скорости от поддержек.
- Increase / more / less разделены: `INC`-бакеты складываются, `*_final` (more/less) перемножаются,
  и каждое значение попадает в `Breakdown` скилла (`More (rate)`, `More (crit chance)`,
  `More (damage supports)`, `Quality (…): increased damage`).

### Уровень и качество камней (0.9.8)

- **Уровень** = уровень из кода + `corruptLevel` (у Arc `corrupted="true" corruptLevel="1"` → 21) +
  все глобальные бонусы. Глобальные бонусы приходят как `GemProperty`-моды с ключевым словом-тегом
  (`ModParser.lua:3515`: «+N to Level of all <type> Skills» → тег вида `spell`) или с фильтром
  требований (`gemRequirements`, «…with an Intelligence requirement» → тег `intelligence`), поэтому
  `StatInterpreter` кладёт их в общий бакет с областью, которую проверяет `GemScopeMatches` по тегам
  камня. Раньше строки `+4 to Level of all Spell Skills` (аффикс амулета) и `+1 …` (строка руны)
  вообще не разбирались — уровень Arc выходил 25 вместо 31.
- **Качество** = качество камня + `+N% to Quality of all Skills`, и только для камней с тегом
  `grants_active_skill` (`ModParser.lua:3496`): поддержка его не получает. `Bonded:`-строки
  по-прежнему гейтятся условием `CanUseBondedModifiers`, поэтому Fox Idol в Morior Invictus здесь не
  считается (36% = 20 своих + 16 глобальных: амулет 5 + аномальный узел Paragon 5 + три узла
  восхождения по 2). В игре строка предмета «Idols socketed in this item…» включает его — отсюда
  игровые 41%.
- **Эффективные значения показываются в UI**: строка списка умений и лист персонажа берут уровень и
  качество ТОГО камня, который наносит урон (`SkillDpsInfo.EffectiveLevel/EffectiveQuality`), а не
  хоста: «Spell Totem (Arc)» показывает Arc 31 / 36%, а не Spell Totem 18 / 20%.
- **Скорость тотема**: `summon_totem_cast_speed_+%` — это `TotemPlacementSpeed`
  (`SkillStatMap.lua:2447`), а не скорость каста; скорость кастуемого тотемом умения повышает только
  `totem_skill_cast_speed_+%` (`SkillStatMap.lua:620`, Speed INC + KeywordFlag.Totem). Учёт обоих
  завышал скорость в 1.33 раза (3.16 против 2.3727 у PoB2); теперь 2.25.
- **Стат качества конкретного камня не смоделирован**: в закреплённом экспорте его нет, поэтому
  действует документированное приближение «+1% increased damage за 1% качества». Реальные статы
  лежат в `Data/Skills/*.lua` (у Arc это `number_of_chains +0.1`, а Gemling-вариант —
  `active_skill_projectile_damage_+%_final_for_each_remaining_chain ×0.15`), и PoB2 сам не маппит
  второй id в `SkillStatMap`, то есть его панель его тоже не считает.

## 4. Открытые разрывы (не скрываются, печатаются тестом)

На текущем снимке билда (`pobb-mercenary-ll-arc.txt`, уровень 96) **точно совпадают и проверяются
ассертами**: Strength/Dexterity/Intelligence (88/59/343), Life (1705), Mana (4576), Spirit (206),
Energy Shield (0), Armour (1623), Evasion (1420), сопротивления (75/52/75/0), крит-шанс (47.97%) и
крит-множитель (+546%). Осталось:

1. **EHP.** Формула PoB2 — симуляция серии ударов (`CalcDefence.lua:3040 numberOfHitsToDie` с
   учётом ward/aegis/guard/recoup/регенерации), затем `TotalEHP = TotalNumberOfHits ×
   totalEnemyDamageIn` (строка 3385). Не воспроизведена; наши значения печатаются рядом.
2. **Урон.** Главная группа — «Spell Totem hosting Arc». 309 298/с против 569 371 у PoB2 (было
   277 920); крит-шанс (47.97%) и крит-множитель (+546%) точны, скорость 2.51 против 2.3727. Осталось:
   **хит до крита ×1.84** (34 061 против 66 313). Найденные слагаемые этого разрыва:
   - **Flame Wall**: конфиг кода содержит `flameWallAddedDamage=true`, и PoB2 добавляет урон Flame Wall
     к projectile-умению, пролетавшему через стену (`ConfigOptions.lua:379` →
     `Condition:FlameWallAddedDamage`, а сами статы — `flame_wall_minimum_added_fire_damage` /
     `flame_wall_maximum_added_fire_damage` из **вторичного эффекта** «Projectile Damage» в
     `Data/Skills/act_int.lua`). В закреплённом каталоге этих статов нет вообще (проверено:
     `flame_wall_*` в `catalog.json` отсутствует), поэтому нужна выгрузка эффектов из
     `Data/Skills/*.lua`. poe.ninja показывает результат: база Arc у него fire 52–78 — это ровно
     добавленный урон Flame Wall;
   - **`_final`-статы поддержек**: общий случай уже читается (см. ниже), но часть id PoB2 вообще не
     маппит (`support_dominus_grasp_chain_count_+%_final` — это число цепей, `branching_fissure_damage_+%_final`
     — урон осколков), а `support_heft_maximum_physical_damage_+%_final` применяется как приближение
     (эффект только на максимум урона);
   - **стат качества камня** (см. выше) — сейчас это документированное приближение;
   - **конфигурация врага**: сопротивления, экспозиция (`conditionEnemyFireExposure` в конфиге кода),
     аилменты не моделируются.
   - **Кулдаун.** Закреплённый экспорт не содержит времени перезарядки, поэтому у Frost Bomb
     rate 2.85 вместо 1/6 с = 0.167 (в 17 раз больше урона в секунду при почти совпавшем хите:
     24 941 против 21 845 у poe.ninja). Нужны данные из `Data/Skills/*.lua`.
3. **Выданные ауры предметов.** `Grants Skill: Level 15 Purity of Ice` (скипетр в снапшоте уровня 95)
   не смоделирован: для произвольной ауры нужны её статы по уровням, которых в закреплённом экспорте
   нет. Отсюда остаток Life −28 и Cold Res −23 на старом снапшоте `pob-real.txt`.
4. **Скорость передвижения** 126.0 против 127.7: остаток дают модификаторы штрафа при спринте/действии,
   они сохраняются, но не моделируются.

### Что уже считается в уроне (data-driven, из закреплённых статик камней)

| Механика | Источник | Эффект |
|---|---|---|
| Хост-камень | `is_totem` / `is_spell_totem` | урон берётся с Arc, а не со Spell Totem |
| Уровни от поддержек | `supported_lightning_skill_gem_level_+` | +1 уровень |
| Уровни от предметов | `spell_skill_gem_level_+` | +8 уровней |
| **Archmage** | `archmage_all_damage_%_to_gain_as_lightning_…per_100_max_mana` | **+196% урона доп. молнией** |
| Gain-as со снаряжения | `Gain N% of Damage as Extra X` | до множителей more/less |
| Скорость тотема | `summon_totem_cast_speed_+%`, `totem_skill_cast_speed_+%` | к скиллу, который ставит тотем |
| **`_final` поддержек** | `support_*_damage_+%_final` (Short Fuse −30, Spell Cascade −30, Fortress −40) | множитель more/less на урон умения |
| **Запрет типов урона** | `deal_no_elemental_damage`, `base_deal_no_chaos_damage` (Brutality) | убирает стихии/хаос и не даёт Archmage-gain; Entangle становится чисто физическим — как в poe.ninja |
| **Условные строки** | `cast_speed_+%_if_have_crit_recently`, `cast_speed_+%_when_on_low_life`, `cast_speed_+%_when_on_full_life`, `mana_regeneration_rate_+%_while_moving`, `mana_regeneration_rate_+%_while_stationary` | применяются, если включено условие конфига (`conditionMoving`, `conditionCritRecently`) или состояние жизни (Low Life < 35% максимума) |
| **Снижение стоимости** | `base_mana_cost_-%` | делитель стоимости маны (PoB2: `mod("ManaCost","INC")`, `mult = -1`) |

Условия берутся из **конфига билда** (`<Config>`/`<ConfigSet>` в коде PoB2): PoB2 сохраняет только те
галочки, которые билд изменил, поэтому отсутствие ключа = значение по умолчанию. Импортёр читает их в
`BuildDocument.Conditions` (типизированные флаги Moving/CritRecently/BeenHitRecently/Enemy*/FlameWall*),
и расчёт применяет условные строки после того, как известны пулы и состояние Low Life. Для этого снимка
включены `conditionMoving`, `conditionCritRecently`, `conditionBeenHitRecently`, `flameWallAddedDamage`
и экспозиции; **`arcLightningInfused` не включён**, поэтому Arc НЕ получает `+200% more` от потребления
молниевой инфузии (это отдельная галочка PoB2, `ConfigOptions.lua:212`, по умолчанию выключена; механика
реализована и включается вместе с флагом — тест печатает 309 298 → 927 893, ×3).

Что сознательно **каталогизируется без формулы** (с причиной, а не «не учтено»): редкость предметов,
требования к атрибутам, скорость спринта, резерв мета-умений, величина шока на себе/враге,
`witch_passive_maximum_lightning_damage_+%_final` (PoB2 его не мапит) и
`base_self_critical_strike_multiplier_-%` — PoB2 маппит его в **вражеский** `SelfCritMultiplier`
(`CalcOffence.lua:3847`, раздел «Enemy modifiers»), то есть на ваш крит-урон он не влияет.
Остаются только строки квестов/дерева без числового эффекта (`quest:`/`tree:`), их 6 из 22.

Правило для `_final` поддержек выведено из `Data/Skills/*.lua` (per-skill `statMap`), где каждый такой
id маппится в `mod("Damage", "MORE")`; слова перед `damage` (`support_`, `skill_`, `spell_`, `maximum_`)
описывают применимость поддержки, а не область урона. Id, которые PoB2 не маппит, а мы не моделируем
(осколки, время детонации, число цепей, площадь, стоимость), пропускаются и попадают в заметки.

### Сверка под-групп с poe.ninja (тот же снимок)

| Умение | poe.ninja | У нас | Комментарий |
|---|---|---|---|
| Arc (хост Spell Totem) | 569 371 | 309 298 | хит ×1.84 ниже — открытый разрыв; скорость 2.51 против 2.3727 |
| Entangle | 44 661 | 54 462 | +22% при скорости 2.84 против 2.956 — физ. урон с Brutality и Heft |
| Flame Wall | 80 918 | 75 472 | −7% — Spell Cascade и Fortress, скорость сошлась |
| Frost Bomb (хит до крита) | 21 845 | 24 941 | +14%; DPS в разы выше из-за отсутствия кулдауна 6 с |

poe.ninja печатает и финальное разложение урона (`offensive.flat`: база, вклад каждой стихии), поэтому
эта таблица — независимая проверка уровня камня (база Frost Bomb 792/1188 совпадает с уровнем 29).

## 7. Данные PoB2 по камням (`skilldata.json`)

Закреплённый RePoE-каталог не содержит части данных, которая нужна атакам и качествам. Она
выгружается из исходников PoB2 в воркспейсе генератором `build/extract-pob2-skilldata.ps1`
(источник — `PathOfBuilding-PoE2-master/src/Data/Skills/*.lua` + `src/Data/Gems.lua`), результат
`Data/Game/skilldata.json` (1 429 камней, 967 связок камень→эффект, sha256 в манифесте) и
проверяется `SkillDataIndex.Load` по этому хешу.

Что в нём есть и как используется:

| Поле | Смысл | Куда идёт |
|---|---|---|
| `baseMultiplier` (по уровням) | «X% урона базового оружия» — есть у **каждой** атаки | умножает базовый урон атаки (PoB2 `CalcOffence.lua:3944`) |
| `attackSpeedMultiplier` | множитель скорости атаки (у Twister −20) | множит rate (`CalcOffence.lua:2735`) |
| `critChance`, `cooldown`, `Mana`/`cost` (по уровням) | крит/перезарядка/стоимость | отчёт и стоимость (перезарядка — см. ниже) |
| `qualityStats` | настоящий эффект качества камня | заменяет прежнее приближение «+1% increased damage за пункт качества» |
| `variants[].stats` + `levels[].values` | позиционные значения эффекта по уровням | например добавленный урон Flame Wall: эффект «Projectile Damage», level 29 → `flame_wall_minimum/maximum_added_fire_damage = 52/78` |
| `gems` | `gameId` → `grantedEffectId` (тот же путь, что у PoB2) | связка каталога с этими данными |

### Важное уточнение по перезарядке

PoB2 **не** ограничивает скорость удара перезарядкой: `CalcOffence.lua:325-346` считает её для
раздела «Cooldown» и для uptime скиллов с длительностью, а DPS считается от скорости каста/атаки.
Поэтому Frost Bomb (перезарядка 6 с, теперь она видна из данных) в панели PoB2 не превращается в
1/6 в секунду — это делает **собственный движок poe.ninja** (в его разборе Frost Bomb стоит rate
0.1665). У нас перезарядка из данных только **печатается** в разборе, а rate не режется; исключение —
значение из закреплённого каталога, где миллисекунды уже означают ограничение.

### Билд Twister (второй эталон)

`pobb.in/BnfRYrC5wjwL` — Mercenary 98, Gemling Legionnaire, `mainSocketGroup=11` = Twister (копьё,
Gemling огонь, `baseMultiplier` 2.97 на 26 уровне). Панель PoB2 из самого кода: `AverageDamage`
951 097.9, `Speed` 2.072, `PreEffectiveCritChance` 50 (панель печатает 75 из-за бифуркации критов),
`CritMultiplier` 12.39, `TotalDPS` 2 049 501.9, `IgniteDPS` 46 225.2, `ManaCost` 102.

Что уже сошлось: оружие разбирается в **те же** базовые значения, что у PoB2
(`Data/Bases/spear.lua`: Soaring Spear 35–65, 1.7/с, 5% крита; 20% качества → 60 среднего), а
множители камня (2.97 урона, ×0.8 скорости) применяются — от этого Twister поднялся с 4 540 до
**9 751/с**. Остаток до 2 М — карта известных пробелов:

1. **Модификаторы уников** — закреплённый каталог не содержит их вообще (документировано), а билд
   почти весь на униках (Morior Invictus, Headhunter, The Taming, The Ordained, Sylvan's Effigy,
   Rite of Passage…). Их текст сохраняется, но в расчёт не идёт — это главный блокер обоих билдов.
2. **27 несопоставленных строк предметов** (механики 0.3: «Explicit Critical Modifier magnitudes»,
   «Augment Items»…) и **59 неучтённых строк дерева**.
3. **Статы поддержек за пределами `*_final`**: наш проход поддержек читает только `_final`-ids,
   а билд состоит из 30+ поддержек, где урон идёт и другими id (Verglas, Execute III, Direstrike,
   Esh's Prowess, Eonyr's Thunder…).
4. **Заряды/ярость/герольды**: `multiplierRage=30`, Combat Frenzy, Trinity, Elemental Conflux,
   Herald of Thunder/Ice — условные множители, не смоделированы.
5. **Крит**: 16.9% против 50% (pre-effective) и +438% против 12.39 — источники крита в основном те
   же непрочитанные модификаторы + бифуркация критов.

## 9. Данные уников из PoB2 (`uniques.json`)

Закреплённый RePoE-каталог хранит у уников **только личность** (имя/класс/иконка) — модификаторов там
нет. Поэтому добавлен генератор `build/extract-pob2-uniques.ps1`, который выгружает из
`PathOfBuilding-PoE2-master/src/Data/Uniques/*.lua` в `Data/Game/uniques.json` (0.28 МБ, **435 уников =
все блоки PoB2**, 216 с вариантами) имя, базовый тип, класс, список вариантов (включая `Selected
Variant`, который PoB2 помечает как живой), число имплиситов и строки модификаторов с их фильтрами
`{variant:N}`/`{tags:...}`; хеш записан в манифест и проверяется в `UniqueDataIndex.Load`.

Как это попадает в расчёт:

| Механизм | Что делает |
|---|---|
| Импортированный уник | в расчёт идёт **его собственный текст** (точные роллы) — как и раньше; теперь строки, которые парсер не понял, **печатаются** в «НЕ УЧТЕНО» (`unique: …`), а не исчезают |
| Уник без текста (созданный в редакторе) | текст синтезируется из данных PoB2: диапазоны `(10-20)` берутся по максимуму (та же конвенция, что у закреплённых имплиситов), `Implicits: N` сохраняется |
| Уникальное оружие | его локальные модификаторы теперь попадают в контекст **этого оружия** (раньше локальный id в глобальном бакете молча терялся): «226% increased Physical Damage», «Adds 1 to 296 Lightning Damage», «+7.54% to Critical Hit Chance» |

Новые маппинги строк (по ModParser PoB2, не по догадкам):

| Строка PoB2 | Куда | Источник |
|---|---|---|
| `Gain X% of Damage as Extra Damage of all Elements` | три gain-as (fire/cold/lightning) | `ModParser.lua:3713` |
| `X% increased Damage for each type of Elemental Ailment on Enemy` | по одному условному Damage INC на состояние врага (Ignited/Chilled/Shocked — те, что умеет конфиг) | `ModParser.lua:3837` |
| `+X% to Critical Hit Chance` | плоская добавка к шансу крита | `ModCache.lua` (name="CritChance", type="BASE"), формула `CalcOffence.lua:3718`: `(baseCrit + base) * (1 + inc/100) * more` |

Честные ограничения: (1) у нескольких уников есть **альт-варианты** (`Has Alt Variant`: Morior
Invictus, Grand Spectrum и др.) — их строки пронумерованы в нескольких измерениях, и `ModsFor` пока
берёт только выбранный `Selected Variant`; импортированных предметов это не касается, потому что у них
авторитетен их текст; (2) строки-механики флаконов/чармов, «Headhunter», «Kalandra's Touch»,
«Idols socketed in this item…» PoB2 **тоже не мапит** — они остаются в «НЕ УЧТЕНО» с полным текстом.

## 11. Собственный маппинг PoB2 (`statMap`)

Третья выгрузка из данных PoB2 — `statMap`: таблица «id стата → мод» в `Data/Skills/*.lua` (357 скиллов
имеют её; у поддержек она лежит в `statSets`). Генератор `build/extract-pob2-skilldata.ps1` сохраняет
каждую запись как **исходный текст спеки** (`mod("Damage","MORE", nil, …)`, `flag(…)`) плюс `mult`
(например `mult = -1` инвертирует знак стата, как это делает сам PoB2). Хеш — в манифесте.

`PoB2ModTranslator` разбирает спеку на имя/тип/условия, `SupportModSink` применяет её к расчёту, а
`CharacterCalculator` вкладывает результат ровно туда, где это делает PoB2: урон (MORE/INC, в том числе
по типам), скорость, шанс и множитель крита, gain-as, регенерация жизни/маны, точность и **потолок шанса
крита** (`CritChanceCap OVERRIDE`, Garukhan's Resolve). Порядок применения: сначала PoB2-маппинг, а
ручные эвристики `*_final` остаются только для id, которые статмап не покрыл (проверка через
возвращаемое значение `Apply`) — поэтому ничего не считается дважды и ничего не теряется.

**Условные моды.** Спека применяется, если все её условия известны и выполняются; условия берутся из
конфига билда и состояния жизни (`ModConditions`: LowLife/FullLife/Moving/BeenHitRecently/CritRecently/
EnemyIgnited/Chilled/Shocked), а `ModFlag.*` трактуется как форма скилла (Hit/Melee/Projectile —
выполнены, Attack/Spell — по признаку скилла). Неизвестное условие (заряды, «враг уникален», набор
оружия) → мод каталогизируется, а не применяется.

Что из этого уже даёт цифры: `Debuff`/`-30% MORE` (Short Fuse), `-30%` (Spell Cascade), `-40%`
(Fortress), `+30%` физ. (Brutality), `ElementalDamage MORE` (атака-элемент), регенерация от Vitality II,
регенерация маны от Clarity II, точность от Precision II, потолок крита от Garukhan's Resolve.
Что осталось (печатается как `SupportSpecUnhandled:*` и является точным списком работ):
`Cost/MORE`, `Duration/MORE`, `AreaOfEffect/MORE`, `AilmentMagnitude/MORE`, `ArmourBreakEffect/MORE`,
`ChainCountMax/*`, `DetonationTime/MORE`, `CurseDelay/MORE`, `MaxPhysicalDamage/MORE` (приближение,
применяется эвристикой) и **условные** `Damage/MORE` на зарядах/ярости/уникальном враге/наборе оружия.

### 11.1 Глобальная таблица `SkillStatMap.lua`

Часть статов не описана в статмапе скилла: они лежат в общей таблице PoB2 `Data/SkillStatMap.lua`
(**832 записи**, выгружаются в тот же `skilldata.json` под ключом `skillStats`). Именно там живёт
`attacks_roll_crits_twice` → `flag("BifurcateCrit")` — флаг Garukhan's Resolve.

Поиск идёт в порядке PoB2: сначала статмап скилла, затем общая таблица. **Но из общей таблицы
применяются только `flag(...)`**: её числовые записи пересекаются с уже реализованными эвристиками
`*_final` (например `base_cast_speed_+%` поддержки Rapid Casting II считался бы дважды — проверено на
экспорте: PoB2 сам печатает `Speed = 2.3727`, а с числовой записью получалось 2.69). Пока два пути
не объединены, числовые записи общей таблицы остаются в «НЕ УЧТЕНО» — это видимый, а не молчаливый
разрыв.

### 11.2 Набор оружия и бифуркация критов (разбор присланных «Calcs»)

Два механизма, найденных по присланным выкладкам PoB2 (`TwisterDMG.txt`, `SpellTotemArcDMG.txt`):

* **Активный набор оружия.** В экспорте `pobb.in/BnfRYrC5wjwL` стоит
  `<Items useSecondWeaponSet="true">` (и `<ItemSet useSecondWeaponSet="true">`), а группа Twister —
  `slot="Weapon 1 Swap"`, `source="Item:13:The Ordained, Grand Spear"`. PoB2 считает **второй набор**:
  его `Speed 2.072` — это ровно Grand Spear (0.714 с) × 1.85 (inc. attack speed) × 0.8
  (`attackSpeedMultiplier` Twister). Импорт теперь читает `useSecondWeaponSet` и отдаёт его в
  `EquipmentPlan.WeaponSet`, а расчёт берёт оружие этого набора (в `pobb-twister.txt` это меняет
  скорость 3.2 → 2.16, крит 16.9% → 42.4%, DPS 11 214 → 58 285).
* **Бифуркация критов.** Garukhan's Resolve: `stats = attacks_roll_crits_twice`, константа
  `maximum_critical_strike_chance_is_%_from_support_garukhans_resolve = 50` (потолок шанса крита).
  PoB2 (`CalcOffence.lua:3734-3756`): шанс = `1 − (1 − p)²` (49.66 → 74.66, в панели `CritChance 75`
  против `PreEffectiveCritChance 50`), а множитель получает `2 × pre/post` = ×1.33 (`CritBifurcates`).
  Реализовано буквально: у Twister крит 42.39 → 66.81%, DPS 58 285 → 80 109.

* **Weapon-class-scoped tree mods.** Дерево PoE2 даёт моды, привязанные к классу оружия в руке, и у них
  отдельное семейство id: `spear_critical_strike_multiplier_+`, `spear_critical_strike_chance_+%`,
  `spear_attack_speed_+%` (41 id на все классы). Голые `critical_strike_multiplier_+`-версии мы уже
  обрабатывали, а эти — нет: у Twister терялись Javelin (`+40%` крит-бонуса с копьём) и два узла
  `+10%` шанса крита с копьём. Теперь они собираются как scoped-значения и разрешаются по группе, как
  уже работающие `<class>_damage_+%`. Плюс разобран шаблон `Attacks have +N% to Critical Hit Chance`
  (плоский `critical_strike_chance_+`). Итог: крит Twister 66.8% → **73.45%** (PoB2 75%),
  крит-бонус +478%, DPS 80 109 → **92 042**.

### 11.4 «Effective»-режим: урон, который реально получает враг

Панель Calcs и экспортируемый `TotalDPS` PoB2 считает в «эффективном» режиме — с резистами и армором
врага. Формула (`CalcOffence.lua:4117-4205`):

```
effMult        = (1 + takenInc/100) * takenMore
effectiveResist = resist                      (как правило)
                  resist*(1-c) + (-resist)*c  (если HitsInvertEleResChance = c)
effMult       *= (1 - effectiveResist/100)
DR (физ.)      = armour / (armour + raw * ArmourRatio)
```

Значения по умолчанию (наши текущие дефолты, выведенные из трёх панелей):
**50% стихийного резиста** (Huntress: `x 0.5` по стихиям), **0% хаоса** (Arc: `x 1`),
армор врага `monsterArmourTable[82] = 5 375 × 1.5` при `ArmourRatio = 10` (`Data.lua:255`) — это даёт
`x 0.285` физического у Huntress и `x 0.918` у Twister, ровно как в панелях. Значения можно переопределить
ключами конфига (`enemyFireResist`, `enemyArmour`, `enemyLevel`, …) — они теперь читаются при импорте.

Инверсия — Rakiata's Flow: `treat_enemy_resistances_as_negated_on_elemental_damage_hit_%_chance = 100`
(`constants` поддержки), что PoB2 превращает в `HitsInvertEleResChance` (`ModParser.lua:3792`), и это
даёт Twister `x 1.5` по стихиям (50% → −50%).

Результат сохраняется в `SkillDpsInfo.EffectiveDps` и печатается строкой «Effective DPS mod (enemy)» —
рядом с сырым `Dps`, поэтому видно, что именно изменил враг. Остаток для Arc (`x 0.89` = 50% минус
проклятие Elemental Weakness и экспоуз Potent Exposure) — это уже модель проклятий/экспоузов, она
пока не сделана и печатается как неучтённое.

### 11.5 Что говорит «Calcs» про остаток разрыва Twister

Из выкладок читаются опорные числа PoB2: `MH Hit Damage 49,447 to 148,577`, `MH Total Increased 635%`,
`MH Total More 98%`, не-критический средний удар **99,011.6**, критический **1,235,126.7** при шансе
0.7500, `MH Average Hit 951,097.9`, `Skill DPS = 951,097.9 × 2.07 × 1.04`. Отсюда база до увеличений
≈ **6 805**, у нас ≈ 894; множитель крита у PoB2 **+1139%**, у нас +438%. То есть остаток — это не
потерянный мод, а база удара и источники крита (в том числе **24 пассивки набора оружия 2**,
`<WeaponSet2 nodes="…"/>`, которые мы ещё не импортируем, и `MH Eff. DPS Mod ×0.918 / ×1.5` —
резисты/армор врага, применяемые PoB2 в эффективном режиме). `Barrage Repeats 42.39` в DPS не входит
(`×1.04` — это и есть его множитель), а `Added Min/Max` подтверждают добавленный урон оружия.

### 11.6 Дефект сопоставления аффиксов предметов (исправлен)

При разборе скриншотов (полные списки модов PoB2 с источниками) обнаружился дефект в
`ModLineMatcher.Normalize`: регэксп `RangeOrNumber` захватывал пробел вокруг числа, но делал это
**неодинаково** для числа в скобках и без них:

| Шаблон из каталога | Строка предмета |
|---|---|
| `Adds (11-13) to (18-21) Cold damage to Attacks` → `adds # to # cold damage to attacks` | `Adds 14 to 24 Cold damage to Attacks` → `adds#to#cold damage to attacks` |

Поэтому **всё семейство «Adds X to Y …» не сопоставлялось**: ни `Adds 4 to 70 Lightning damage to
Attacks` (перчатки), ни `Adds 23 to 36 Fire damage to Attacks`, ни `Adds 21 to 34 Fire`. Строки просто
попадали в «не сопоставлено», а добавочный урон не влиял на расчёт. Это и есть причина, по которой у
Huntress «добавленного урона не хватало».

Исправленный регэксп (границы значения больше не съедают пробелы) даёт:

| Билд | Средний удар до/после | DPS до/после |
|---|---|---|
| Huntress Ice Shot | 1 323,0 → **1 896,8** | 3 611,8 → **5 178,2** |
| Huntress Bow Shot | 1 454,4 → **2 235,4** | 4 411,7 → **6 780,5** |
| Twister | 9 435,7 → **13 230,6** | 92 042,2 → **129 060,4** |

Побочный эффект того же исправления пришлось закрыть отдельно: стали применяться **настоящие**
плоские defence-аффиксы, а `local_energy_shield` («+71 to maximum Energy Shield» на доспехе — это
**локальный** аффикс, а не глобальный `base_maximum_energy_shield`) уходил в **глобальный** пул и
удваивал защиту предмета. Панель PoB2 для Mercenary показывает разложение маны по источникам:

```
Base   Inc/red  Total   Source      Name
425    x1.37    582.25  Helmet      EnergyShield to Mana conversion
295    x1.37    404.15  Body Armour EnergyShield to Mana conversion
243    x1.37    332.91  Boots       EnergyShield to Mana conversion
172    x1.37    235.64  Weapon 2    EnergyShield to Mana conversion
75     x1.37    102.75  Global      EnergyShield to Mana conversion   <- глобальный аффикс амулета
13     x1.37    17.81   Gloves      EnergyShield to Mana conversion
```

то есть PoB2 берёт **печатное** значение ES каждого предмета (оно уже содержит локальный флэт) плюс один
глобальный `+75`, и умножает сумму на `1 + ManaInc`. Приведено к тому же виду:

1. `StatInterpreter`: `local_energy_shield`, `local_ward` и `local_base_evasion_rating` маршрутизируются
   по контексту предмета (`ItemContext.ArmourFlat/EvFlat/EsFlat/WardFlat`), а не в глобальный пул;
2. `CharacterCalculator.ApplyTextBases`: база предмета = закреплённая база + его локальный флэт, затем
   локальные «increased» и качество; печатное значение по-прежнему побеждает и ничего не удваивает.

После этого `Mana` снова точно совпадает с панелью PoB2 (4 576 / 4 465) на обоих снимках Mercenary и в
poe.ninja-фикстуре, `EnergyShield` перестал быть завышенным, а записанная база регрессии Flameblast
обновлена 10 855,6/s → **17 210,6/s** (+58,54 %) — это ровно два аффикса посоха `Glyph Chant`
(«Gain 29% of Damage as Extra Lightning Damage» и «… as Extra Cold Damage»), которые PoB2 применяет.

Дополнительно из тех же таблиц видно, что разрешение PoE2-разметки `[Tag|Display]` в шаблонах и
ограничение выбора мода пулом его класса предмета (`GameData.ModPools` по `ItemBase.ModPool`) —
безопасные и уже включённые улучшения: без ограничения строка «+208 to maximum Mana» кольца
приписывалась моду двуручного оружия `IncreasedManaTwoHandWeapon8_`.

### 11.7 Зацепка по криту Huntress: радиусные нотейблы Time-Lost

Панель PoB2 для Huntress: `CritChance 48.87`, `CritMultiplier 6.52`. Мы считаем 25,3 % ×4,89.
Базовый шанс совпадает: 10,86 % — это `Gemini Bow` (6 %) + `+4.86% to Critical Hit Chance` на луке
(строка округляется до 5 и матчится в `LocalCriticalStrikeChance1`). Значит расходится только
**увеличение**: у нас +133 %, у PoB2 +350 % (эта цифра написана прямо в подписи панели
«Inc. Crit Chance: 350 %»). Разница ≈ **+217 %**.

Ровно её дают два радиусных самоцвета из фикстуры:

```
Dusk Wound (Time-Lost Sapphire, radius Very Large):
  Notable Passive Skills in Radius also grant 5% increased Critical Hit Chance
Damnation Star (Time-Lost Sapphire, radius Very Large):
  Notable Passive Skills in Radius also grant 6% increased Critical Hit Chance
```

При ~20 нотейблах в радиусе каждого это 11 % × 20 ≈ **+220 %**, то есть крит сходится до десятых.
Сейчас строки `Notable Passive Skills in Radius also grant …` не моделируются вообще (радиус-геометрия
дерева + подсчёт нотейблов в радиусе); та же механика управляет `Controlled Metamorphosis`
(`5% increased Critical Hit Chance / 10% increased Critical Damage Bonus / 3% Global Armour, Evasion
and Energy Shield`) и Time-Lost Emerald у Twister (`2% increased Attack Speed / 3% Projectile Speed /
12% increased Critical Damage Bonus for Attack Damage`). Это следующий по величине кусок разрыва после
§11.6: он же объясняет часть `CritMultiplier` (10 % / 12 % increased Critical Damage Bonus за нотейбл).
**Подтверждено скриншотом.** Список «Player modifiers» с источниками показывает именно это
разложение: три узла дерева с именем `Critical Chance` по 10 % (`Source = Tree`) и **много строк по
6 % с `Source = Jewel Socket`** — это и есть `Notable Passive Skills in Radius also grant 6% increased
Critical Hit Chance` от `Damnation Star`, по одной строке на каждый нотейбл в радиусе. Строк по 5 %
(`Dusk Wound`) в видимой части списка нет, потому что список прокручен; видно ровно то поведение,
которое описано выше: каждое попадание в радиус добавляет отдельный модификатор с источником
«Jewel Socket». Это делает механику радиусных нотейблов следующим крупным куском паритета: она
закрывает +217 % крита у Huntress и часть `CritMultiplier` у Huntress и Twister.



### 11.8 Награды за задания и конфигурация: те же данные, что в PoB2

Две вкладки перед листом персонажа повторяют PoB2, и обе берут значения из его же данных.

**Награды за задания.** `build/extract-pob2-questrewards.ps1` разбирает
`PathOfBuilding-PoE2-master/src/Data/QuestRewards.lua` (29 записей) в `Data/Game/questrewards.json`:
акт/зона/источник, строку награды, число очков умений набора, варианты выбора («Options») и флаг
`useConfig`. Загрузчик `QuestRewardIndex` сверяет SHA-256 файла (константа записана и в
`manifest.json`), поэтому подменённая таблица не пройдёт. Импорт кода PoB строит ключ
`quest + Description + Area + Info` — так же, как его пишет сам PoB2, — и берёт оттуда и умолчания, и
выбранный вариант. Практическое следствие: у актов 5 PoB2 использует `Description = "Interlude N"`,
поэтому три записи (`Khari Crossing`, `Qimah`, `Kriar Village`), которые раньше не сопоставлялись,
теперь сопоставляются — наград в фикстуре стало 16 → 17.

Строки попадают в `BuildDocument.QuestRewards` и считаются тем же `QuestRewardParser`. Вариант выбора
сравнивается по «схлопнутой» форме строки (`QuestRewardIndex.NormaliseLine`): PoB2 хранит в одной
Lua-строке `\n\t`, а код билда — уже плоскую форму (XML превращает перевод строки и табуляцию в два
пробела), и это одна и та же награда.

**Конфигурация.** Вкладка показывает только те параметры, которые читает наш расчёт, и текстом
перечисляет, что пока не поддержано (заряды, Onslaught, Arcane Surge, Unholy Might,
crushed/intimidated/unnerved, враги рядом, тотемы, consumed infusion, shapeshifted, Ash/Frost, боссы,
дистанция, цепи). Значения ложатся в `BuildDocument.Conditions`; пустое поле врага означает умолчание
PoB2 для эффективного режима (50 % элементальных сопротивлений, 0 % хаоса, броня монстра по уровню).


### 11.9 Набор оружия, радиусные самоцветы и резисты (этап 2)

**Ноды набора оружия.** PoB2 пишет их дочерними элементами спека
(`<WeaponSet1 nodes="…"/>`, Classes/PassiveSpec.lua:272-277) и даёт каждой такой ноде `allocMode`
(0 — всегда, 1/2 — только при активном наборе), а её модам — условие `WeaponSetN`
(Modules/CalcSetup.lua:264-277). Персонаж считается с активным набором
(`itemsTab.activeItemSet.useSecondWeaponSet`), поэтому ноды другого набора не дают ничего.

До этапа 2 мы импортировали только атрибут `nodes` (в котором, как и у PoB2, лежат **все** взятые
ноды) и применяли всё безусловно. У Twister это давало 48 нод вместо 24: фикстура берёт
**по 24 ноды на каждый набор**. Теперь `<WeaponSet1/2>` читаются в
`PassiveTreePlan.WeaponSetNodes` (валидируются: id обязан быть в `AllocatedNodes`, набор — 1 или 2),
а калькулятор оставляет только ноды активного набора (`CharacterCalculator.ActiveTreeNodes`,
счётчик `WeaponSetN OnlyNodes` в extras). Это же множество используется радиусной геометрией.

Следствие для скорости атаки Twister: 2,16/с → **1,69/с** (PoB2 2,072). Прежние 2,16 были завышены
на скорость атаки из 24 нод **чужого** набора; остаток гэпа — незамоделированные источники скорости.
Запись в тесте обновлена вместе с этой причиной.

**Радиусные самоцветы.** Реализовано по данным PoB2, а не по догадкам:

* полосы радиусов — `data.jewelRadii["0_1"]` (Modules/Data.lua:626-690): Small 0-1000,
  Medium 0-1150, Large 0-1300, Very Large 0-1500 плюс 8 «переменных» полос;
* масштаб — `gameConstants.PassiveTreeJewelDistanceMultiplier` (Data/Misc.lua:36) = **1.2**,
  то есть «Very Large» реально 1500 × 1.2 = 1800 единиц дерева (умножаются радиусы, а не дистанция);
* тип ноды — `"^(%w+) Passive Skills in Radius also grant (.*)$"` (Modules/ModParser.lua:7041):
  `Notable`/`Keystone`/`Small` (последнее — «Normal» без атрибутов);
* эффект применяется **по одному разу на каждую взятую ноду** в радиусе — именно так выглядит
  список источников в панели PoB2 (по строке на нотейбл).

Загрузка самоцветов: `<Socket itemId nodeId>` — авторитетный признак «это самоцвет дерева». Раньше
редкие самоцветы с базой, которой нет в закреплённом каталоге (**Time-Lost Sapphire**), отбрасывались
дважды: список баз `PobJewelBases` их не знал, а страховка «нет базы и нет роллов → это шелл» убивала
предмет целиком. Теперь признак берётся из `<Socket>`, а шелл отбрасывается только при полном
отсутствии строк. Это вернуло в билд оба радиусных сапфира Huntress.

Итог по Huntress (панель PoB2: `AverageDamage 9394.6`, `Speed 1.43865`, `Crit 48.87 ×6.52`,
`TotalDPS 13785.9`):

| Метрика | до | после |
|---|---|---|
| Крит шанс | 25,3 % | **35,42 %** |
| Крит множитель | ×4,89 | ×11,23 |
| Эффективный удар | 3 428,7 | **7 989,9** |

В радиусе «Very Large» (1800 ед.) вокруг гнёзд: Dusk Wound — 10 нотейблов и 18 малых,
Damnation Star — 7 и 13; применено 57 разовых грантов (`RadiusGrantsApplied`).

**Открытый вопрос по крит-множителю.** Наши входы: `CritBonusAdd = 274` (плоские «+N% to Critical
Damage Bonus», PoB2 маппит их в `CritMultiplier BASE`, ModCache подтверждает) и `CritBonusInc = 163`
(ровно радиусные 10×10 + 9×7). Формула PoB2 (`CalcOffence.lua:3813-3818`):
`multiplier = 1 + (BASE/100) × (1 + INC/100) × MORE`. У PoB2 панель даёт ×6.52, то есть
`(1+INC/100)·MORE ≈ 1.48` при BASE = 374. Это значит, что PoB2 **не** складывает радиусные
`increased Critical Damage Bonus` в INC так, как это делаем мы (у нас ±2,63). Нужен разбор на его
стороне: вероятнее всего, Time-Lost сапфир получает индекс «Variable» из `jewelData.radiusIndex`
(Classes/Item.lua:1359-1363), а не фиксированную полосу 4, и тогда часть линий просто не попадает в
радиус. Пока это единственное место, где радиусные линии уводят нас **дальше** от панели, и оно
зафиксировано честно.

**Резисты Twister.** Ваши скриншоты и `PlayerStat` фикстуры совпадают: Fire 69 (+0),
Cold 75 (+14), Lightning 75 (+12), Chaos 75 (+3). Мы были: fire 24, cold 75, lightning 75, chaos 18.
Оба расхождения объяснились **точно**:

* `+60 base Chaos Resist` — это Morior Invictus `+15% to Chaos Resistance per Socket filled` на
  4 руны. Форма «одного сопротивления» в этом правиле не поддерживалась (было только
  `all Elemental Resistances`); теперь поддержана → chaos 18 → **75** ✓.
* `+45 base Fire Resist` — аура **Purity of Fire** (столбец `Source = Skill`) в наборе 2. Уточнение
  из §11.11: посох в активном наборе — `Sacred Flame, Shrine Sceptre` (слот Weapon 2 Swap), а та же
  аура лежит и в сокете группы 8; PoB2 сливает два экземпляра по имени бафа, поэтому строка одна.
  **Закрыто в §11.11** (24 + 45 = 69 ✓).

### 11.11 Ауры и постоянные навыки: 45 = 40 + 5, и почему строка одна

**Формула.** PoB2 превращает каждый стат постоянного навыка с тегом `GlobalEffect`
(`effectType = Aura / Buff / Global`) в баф этого навыка и добавляет его модификаторы персонажу, пока
навык включён (Modules/CalcSetup.lua:1855 — `enableGlobalN` гема; Modules/CalcActiveSkill.lua:1029-1103 —
разбор `GlobalEffect`, `effectCond`/`modCond` и сборка `buffList`). Значение модификатора —

```
value = значение эффекта на уровне гема + math.modf(коэффициент качества × качество)
```

(Modules/CalcTools.lua:138-205: `stats[stat] = (stats[stat] or 0) + math.modf(stat[2] * quality)`,
затем `+ statValue` из таблицы уровней; `math.modf` берёт **целую** часть).

**Экземпляры не складываются.** «Merge an instance of a buff, taking the highest value of each modifier»
(Modules/CalcPerform.lua:40-57): бафы сливаются по **имени**, а из одинаковых модификаторов остаётся
наибольшее значение. Поэтому сокетовая Purity of Fire (уровень 19) и та же аура от посоха дают не 90, а
одну строку 45 — ровно как в панели PoB2.

**Качество: откуда 14.** У Purity of Fire `qualityStats = { base_skill_buff_fire_damage_resistance_%_to_apply, 0.4 }`,
т.е. +0,4 за 1 % качества. Глобальное качество у Twister (GemProperty `quality`, Modules/CalcSetup.lua:466-491):

| Источник | Значение |
|---|---|
| «Skill Gem Quality» ×2 (Gemling Legionnaire, узлы 34882 и 45248) | +4 |
| амулет `Storm Choker` «+5 % to Quality of all Skills» | +5 |
| руна `Bonded: +5 % to Quality of all Skills` (Morior Invictus) | +5 |
| **итого** | **14** |

`0,4 × 14 = 5,6 → math.modf → 5`; уровень 19 по данным PoB2 (и по закреплённому RePoE: наши
`skills.json`-значения совпадают) даёт **40**; 40 + 5 = **45** ✓. Ранее мы считали, что нужно 24-й
уровень (там тоже 45) — это оказалось неверным следом: 45 складывается из 19-го уровня и качества.

**Реализация.** `Core/Calculation/AuraSkillCalculator.cs`: `ParseGrants` читает `Grants Skill: Level N X`
(Modules/ModParser.lua:3560-3561), `IsAura` отбирает навыки с `GlobalEffect` типа Aura/Buff/Global (курсы
с `effectType = "Curse"` остаются врагу — ими владеет offence-конвейер), `Apply` считает value и **сливает
по имени бафа, оставляя максимум**. `CharacterCalculator.AuraInstances` собирает экземпляры из:
активных гемов включённых групп, чей `WeaponSet` совпадает с активным (0 — оба), и «Grants Skill» строк
предметов активного набора. Статы, которые модель ещё не знает, попадают в отчёт как
`aura: <навык> <statId>` (а не исчезают), условные — как `(condition not resolved)`.

**Отображение резистов.** Панель PoB2 печатает `69+0 / 75+14 / 75+12 / 75+3`, то есть **кап и перекап
отдельно**, а не сырую сумму. Мы показывали сырые источники (`+149`), из-за чего билд с перекапом
выглядел как «сопротивление 149 %». Теперь строка: значение = эффективное сопротивление
(`75% (+14%)`), деталь = «кап 75 % · всего 89 % · от дерева и снаряжения: +149»; для этого
`CharacterSummary` публикует ещё и максимум каждого сопротивления.

**Цвета набора оружия.** PoB2 красит ноды по `allocMode`: набор I — `colorCodes.NEGATIVE` `#DD0022`
(красный), набор II — `colorCodes.POSITIVE` `#33FF77` (зелёный); цвет коннектора берётся у любого из
двух концов (Classes/PassiveTreeView.lua:716-730, 1072-1073). Наш `TreeViewport` теперь рисует так же,
добавляет легенду с активным набором и показывает набор в тултипе/панели ноды; в панели появились три
кнопки («Набор I/II/общий») — они пишут `allocMode` ноды в `PassiveTreePlan.WeaponSetNodes`.
Попутно закрыт инвариант: `PassiveTreeEngine.Refund` теперь снимает с удаляемых нод не только
`AttributeSelections`, но и `WeaponSetNodes`, `Jewels` и `JewelAllocatedNodes` — иначе следующий расчёт
падал на собственной валидации плана.

### 11.12 Радиус-аллокация: From Nothing, Intuitive Leap и формулировки Time-Lost

**Проблема.** В билде Twister (побб-код `BnfRYrC5wjwL`) аллоцированы ноды, у которых в графе НЕТ
ребра к остальному дереву: «True Strike», «For the Jugular», «Spellblade», «Event Horizon» и другие.
Их держит самоцвет **From Nothing** (Diamond): `Radius: Small` +
`Passives in Radius of Resonance can be Allocated⏎without being connected to your tree`. До этой правки
импорт не знал такого правила и **достраивал путь** — в плане появлялись 23–24 ноды, которых в
источнике нет, а вместе с ними менялись резисты, скорость атаки и крит.

**Как это устроено в PoB2.**
- `Modules/ModParser.lua:5507-5509`: `passives in radius of ([%a%s']+) can be allocated without being
  connected to your tree` → `JewelData.fromNothingKeystone` + `FromNothingKeystones`.
  Там же `5511`: `passives in radius can be allocated without being connected to your tree`
  (Intuitive Leap) → `JewelData.intuitiveLeapLike`.
- `Classes/PassiveSpec.lua:1361-1392` (`NodesInIntuitiveLeapLikeRadius`): для гнезда с таким самоцветом
  берётся `nodesInRadius[radiusIndex]` **того кистоуна, чьё имя названо**, и в зависимости попадают
  только уже аллоцированные ноды. **Аллоцирован ли сам кистоун — не проверяется**: `tree.keystoneMap`
  это таблица дерева, а не выделенных нод.
- `Classes/PassiveSpec.lua:1814-1862`: такие ноды становятся зависимостями гнезда, поэтому путь им не
  нужен, а всё, что стоит ЗА ними, доступно через них.

**Подтверждение на живых данных (фикстура `pobb-twister.txt`).** Кистоун **Resonance** (id 25520)
в `<Spec nodes="…">` отсутствует — то есть билд его НЕ берёт, а десять нод лежат от него на расстоянии
456 … 1172 юнита, тогда как полоса `Small` = 1000 × `PassiveTreeJewelDistanceMultiplier` 1.2 = **1200**
(`Modules/Data.lua`, `Data/Misc.lua:36`). Это и есть кластер From Nothing, и он работает без кистоуна —
ровно как в коде PoB2 выше. Поэтому наша модель (и `Validate`, и геометрия) требует только
**аллоцированное гнездо с самоцветом**, но не аллокацию кистоуна.

**Как это теперь в коде.**
- `PassiveTreePlan.RadiusJewels: Dictionary<int, RadiusAllocationRule>` — гнездо → правило
  (`RadiusIndex`, `KeystoneName`; пустое имя = радиус вокруг самого гнезда). Читается из текста
  самоцвета (`JewelRadius.AllocationRule`: склеивает перенос строки внутри мода, берёт полосу из
  `Radius:`/`Upgrades Radius to:`).
- `PassiveTreeEngine.RadiusCentres` / `RadiusAt` / `RadiusAllocatable` — центры и достижимые ноды;
  `FindPath` возвращает `[target]` (1 очко за саму ноду) для ноды внутри радиуса;
  `Validate` расширяет корни достижимости этими нодами.
- `KeepReachable` — закрытие плана: снял самоцвет (или вернул гнездо) → всё, что держалось только на
  радиусе, снимается вместе с ним, и в панели пишется, сколько нод вернулось (`TreeRadiusDropped`).
- Импорт (`BuildInterop`): правила читаются ДО аллокации, такие ноды откладываются на второй проход и
  берутся после того, как гнездо и правило уже в плане; `WeaponSetNodes` применяются ПОСЛЕ этого
  прохода (иначе нода, взятая через радиус, выглядела как «нода, которой нет в дереве», и её привязка
  к набору оружия терялась). Незакрытые ноды по-прежнему достраиваются кратчайшим путём и **теперь
  честно перечисляются** нотой «часть узлов соединена кратчайшим путём…».

**Результат на фикстуре:** план содержит 148 нод вместо 171; в источнике 156 нод = 146 обычных + 10 нод
восхождения + 1 особая (50986) + старт класса; «лишних» нод нет вовсе, вне списка источника остались
только две, выданные предметами (`Allocates Augmented Flesh`, `Allocates Paragon`), — так же, как в
тесте на `pob-real.txt`. Пути к радиус-кластеру больше не строятся.

**Формулировки радиус-эффектов Time-Lost.** Строки `radius: …` в отчёте «не учтено» были эффектами
самоцветов, которые мы не умеем переводить в статы. Теперь `RadiusEffects.Resolve` идёт по порядку:
общий парсер наград за задания → явные формулировки → **закреплённая таблица `statmap.json`**
(`GameStatMap.TryResolve`, поиск с вырезанным числом, только строки с ОДНИМ числом и ОДНИМ статом —
где порядок значений неоднозначен, мы не угадываем) → обратная таблица переводов PoB2.
Идентификаторы при этом уже были в `StatInterpreter`, не хватало только перевода формулировок:

| Формулировка самоцвета | Стат | Куда идёт |
| --- | --- | --- |
| `12% increased Critical Damage Bonus for Attack Damage` | `attack_critical_strike_multiplier_+` | `AttackCritBonusAdd` |
| `12% increased Critical Damage Bonus with Spears` | `spear_critical_strike_multiplier_+` | `ScopedCritBonusAdd` («spear») |
| `7% increased Critical Hit Chance for Attacks` | `attack_critical_strike_chance_+%` | `AttackCritInc` |
| `3% increased Projectile Speed` | `base_projectile_speed_+%` | `Extras` (каталогизировано) |
| `2% increased Projectile Damage` | `projectile_damage_+%` | `ScopedDamage` («projectile») |
| `2% increased Damage with Spears` | `spear_damage_+%` | `ScopedDamage` («spear») |

На Twister это видно сразу: крит-шанс **75 %** (у PoB2 `CritChance 75`, `PreEffectiveCritChance 50`),
крит-множитель был **×5.78** → стал **×8.28** (PoB2 ×12.39), бонус `+478 %` → **`+728 %`** (PoB2
`+1139 %`), урон за удар 13 230 → **17 805**, DPS 101 000 → **185 580** (PoB2 `TotalDPS 2 049 502`).
Полоса «Weapon-class mods (spear)» показывает `crit bonus +160 %` = Javelin `+40` + по `12 %` с десяти
нотейблов в радиусе.

**Честная гигиена отчёта.** Служебные строки предмета (`Limited to:`, `Radius:`, `Source:`, `Variant:`,
`Twice Corrupted`, `Mirrored`) больше не считаются модификаторами, а обе половины строки From Nothing и
формулировка Intuitive Leap не попадают в «не учтено» повторно — они смоделированы правилом плана.
Заодно в формулы добавились: `attack_damage_+%_when_on_low_life` / `_when_on_full_life` (через
`StatCondition.LowLife/FullLife`, порог 35 % — `data.misc.LowPoolThreshold`) и
`global_armour_evasion_energy_shield_+%` (те же три защиты, что и `defences_+%`). Список «не учтено» по
Twister сократился с **138 строк (75 ключей)** до **62 ключей**, и в нём больше нет ни одной строки
`radius:`.

**Что осталось из урона Twister (измерено, не спрятано).** Удар у нас 17 805, у PoB2 до-критовый удар
≈ 99 012 (её `AverageDamage 951 098` — это уже с критом: `951 098 / (1 + 0.75 × (12.39 − 1)) = 99 671`,
min-max 49 447 … 148 577). То есть остаётся ~5–6× в базовом уроне оружия и ~1.7× в крит-множителе.
Проверенные факты для следующего шага:
- оружие — `The Ordained, Grand Spear` (item 13, `Quality: 26`, локально `226% increased Physical
  Damage`, `Adds 1 to 296 Lightning Damage`, `+7.54% to Critical Hit Chance`), база в закреплённом
  каталоге 56–84 физ. при 0.714 с;
- в нашем разборе «Base (weapon): 84.0 physical» — это НЕувеличенная база, а локальные +226 % и
  +26 % качества учтены как глобальные увеличения (`Increased (physical): +539%`); у PoB2 локальные
  увеличения оружия применяются к СУММЕ физики и добавленного урона (то есть к `Adds 1 to 296` тоже),
  а в глобальный пул не входят — это и есть основной кандидат на разрыв;
- скорость атаки: 1.61 против 2.072 (у PoB2 учтено больше источников attack speed);
- крит-бонус: `+728 %` против `+1139 %`;
- плюс 3 самоцвета с `57/59/60% increased Effect of Suffixes` — это локальный мод, который у PoB2
  масштабирует собственные суффиксы предмета (у нас
  `local_non_unique_item_explicit_suffix_mod_magnitudes_+%` пока в списке «не учтено»: в закреплённом
  каталоге нет поля «префикс/суффикс» у jewel-модов).

### 11.15 «Не учтено» больше нет: критерий PoB2 и класс «распознано, но не моделируется»

Итог работы по всему списку (62 ключа на Twister) — **0 строк «не учтено» на всех фикстурах**. Список
закрыт не «на глаз», а критерием: **реализует ли этот стат сам PoB2**.

**Критерий.** Для каждого оставшегося id проверено дерево исходников PoB2 (`Modules/`, `Classes/`,
`Data/`, исключая каталог описаний). Результат:

| Что нашли | Сколько | Что делаем |
|---|---|---|
| id есть **только** в `Data/StatDescriptions/stat_descriptions.lua` | 14 | **не реализуем** — PoB2 сам его не считает; реализация разошлась бы с эталоном |
| стата нет в панелях, которые мы считаем (регенерация, стоимость, станы, слоты чармов, триггеры фляг…) | ~40 | записываем в «распознано» с причиной |
| эффект гейтится множителем PoB2, которого этот билд не включает (`Multiplier:ElementalConflux<Type>Effect`, резонанс Trinity, `Multiplier:RageEffect`) | 7 | записываем с указанием множителя |
| PoB2 считает, и мы можем | 6 | **реализуем** (см. ниже) |

**Реализовано в этом проходе:**
1. **Flame Wall** — обе части: «Flame Wall» (`flame_wall_minimum/maximum_added_fire_damage`) и «Infused
   Flame Wall» (`…_lightning_damage_to_add_to_projectile`) через условия `flameWallAddedDamage` /
   `flameWallInfused` (`ConfigOptions.lua:379-383`, второй чекбокс доступен только при первом). Значения
   идут в отдельный пул `Added{Attack,Spell}Projectile*`, потому что PoB2 даёт им
   **`ModFlag.Projectile`**: Flameblast (не снаряд) их не получает — проверено на фикстуре
   (`POB COMPARISON: Flameblast recorded=17210.6/s … delta=0,00%`).
2. **Стоимость Archmage** (`archmage_max_mana_permyriad_to_add_to_non_channelled_spell_mana_cost`,
   `ManaCostNoMult BASE`): +значение × максимум маны / 10 000 для не-каналируемых заклинаний.
3. **Dance with Death** — «25% more Skill Speed while Off Hand is empty…» (`ModParser.lua:2333-2337`,
   пара `UsingOneHandedWeapon` + `OffHandIsEmpty` решается снаряжением активного набора).
4. **Sylvan's Effigy** — `ElementalDamageUsesLowestResistance` (`ModParser.lua:4436`): элементальный урон
   считается по минимальному сопротивлению врага (`CalcOffence.lua:4134-4152`).
5. **Узел Gemling «most numerous colour»** — три «пульки» как три стата (`ModParser.lua:3365-3372`),
   «Blue: 30% less cost» как `ManaCost` more/less, цвет — по правилу `CalcSetup.lua:2155-2162` (ничья
   отдаётся красному, затем зелёному).
6. **Регенерация жизни с дерева** — «Regenerate 0.5% of maximum Life per second».

**Баг, который это вскрыло:** формула разбора этой строки отсекала лишний символ
(`line.Length - suffix.Length - 1`) и парсила число через `decimal.Parse` **без `InvariantCulture`** —
под русской локалью «0.5» не разбирается вовсе, поэтому такие строки молча терялись. Исправлено и в
`IsDecimalNumber`, и во всех парсингах чисел из текста игры.

**Как это читать в отчёте.** Два разных списка: `Unaccounted` — «мы не знаем, что это» (теперь пусто), и
`Known` — «знаем, что это, и вот почему оно не в цифрах»; причина печатается рядом со строкой, например
`local_non_unique_item_explicit_suffix_mod_magnitudes_+% | PoB2 only describes it - no effect in its numbers`.

Работа по списку «не учтено» (62 ключа на Twister) с проверкой **в самом PoB2**: реализуем только то, что
реализует он (иначе мы разойдёмся с его цифрами).

**1. Дистанция и состояния — из конфига PoB2, а не из догадок.** `enemyDistance` в share-коде лежит как
`<Placeholder number="20" .../>`, а `ConfigTab.GetDefaultState` возвращает placeholder, когда `<Input>` не
записан (`ConfigTab.lua:712-715`) — то есть у билда дистанция **20 юнитов = 2 м**. Поэтому семейство
«against enemies within 2m» применяется: это `MultiplierThreshold:enemyDistance` (`ModParser.lua:2154`),
а «further than 6m» — нет. Импортёр теперь читает и `<Placeholder>`, и `<Input>` (вход перекрывает
placeholder). Так же смоделированы `conditionSurrounded`, `conditionStunnedRecently`, `conditionAtCloseRange`
и семейство `attack_damage_+%_while_surrounded` / `_if_been_heavy_stunned_recently` /
`projectile_damage_+%_vs_enemies_within_2m_distance` / `critical_hit_damage_bonus_+%_vs_enemies_within_2m_distance`
— строки, удовлетворяющие условию, идут в урон, остальные честно записываются как `Condition:…=off` и
уходят из «не учтено».

**2. Масштабирование от цвета поддержек (Gemling «Integrated Efficiency»).** Точный порт
`CalcOffence.lua:684-722`: `skills_gain_damage_+%_per_sockted_or_adjacent_red_support_gem` → × число КРАСНЫХ
поддержек в уроне, `…_skill_speed_…green_…` → в скорость (значит и в rate), `…_critical_strike_chance_…blue_…`
→ в крит-шанс. У Twister это **+20 % урона** (1 красная × 20 %), **+18 % скорости** (3 зелёных × 6 %) и
**+20 % крит-шанса** (1 синяя × 20 %).

**3. «N% more Skill Speed while Off Hand is empty…»** (Dance with Death) — `mod("Speed","MORE")` с парой
`UsingOneHandedWeapon` + `OffHandIsEmpty` (`ModParser.lua:2333-2337`); пара решается снаряжением активного
набора оружия (у Twister в левой руке Sylvan's Effigy → строка неактивна, но она больше не «не учтено»).

**4. «Enemies in your Presence Resist Elemental Damage based on their Lowest Resistance»** (Sylvan's Effigy) —
`flag("ElementalDamageUsesLowestResistance")` (`ModParser.lua:4436`): каждый элементальный тип считается по
**наименьшему** из трёх элементальных сопротивлений (`CalcOffence.lua:4134-4152`), до инверсии Rakiata's Flow.

**5. «Most numerous colour» узел** (Gemling): три «пульки» разбираются как три стата, как в
`ModParser.lua:3365-3372`; цвет определяется по группе умения (`CalcSetup.lua:2155-2162`, ничья отдаётся
красному, затем зелёному) и «Blue: 30 % less cost» применяется как `ManaCost` more/less.

**Итог по списку:** **62 → 51 ключ**. Проверено и осознанно НЕ реализовано:
`local_non_unique_item_explicit_suffix_mod_magnitudes_+%` — PoB2 держит для него только описание
(`stat_descriptions.lua:171569`) и **нигде не считает**, так что реализация дала бы расхождение с ним.

**Что осталось (и это последний большой блок).** Share-код сам перечисляет, что PoB2 применяет:
`<Buffs buffList="Barrage, Berserk, Blazing Critical, Breachlords Rift, Cannibalism II, Charge Infusion,
Clarity II, Combat Frenzy, Direstrike I, Discipline, Elemental Conflux, EternalRage, Herald of Ice,
Herald of Thunder, Herbalism I, Precision II, Purity of Fire, Rage, Trinity, Virtuous Barrier, Vitality II"
combatList="3 Power Charges, 3 Frenzy Charges, 3 Endurance Charges"/>`.
Это **само-баффы и заряды**, а не ауры предметов: наш движок их собирает (`aura: …` в отчёте), но не
разворачивает в моды, потому что их эффекты живут в множителях PoB2 (`Multiplier:RageEffect`,
`Multiplier:ElementalConflux<Type>Effect`, `Multiplier:FrenzyCharge`…), а значения этих множителей задаёт
игровая механика (Rage 30, резонанс Trinity, стадии Conflux). Именно там лежит остаток ×~4.7 по
крит-взвешенному удару Twister (у нас 202 097 против 951 098) вместе с крит-множителем (×8.68 против ×12.39).

### 11.13 Урон оружия по PoB2, условные поддержки и тир поддержки

**Что было не так.** Удар Twister был 13 230 против 99 671 до-критового у PoB2. Разбор по источникам в
`PathOfBuilding-PoE2-master` нашёл четыре независимых расхождения, каждое из которых теперь закрыто.

**1. Урон оружия — теперь буквально формула PoB2** (`Classes/Item.lua:1909-1949`):
```
physical:            (base + local added physical) * (1 + local phys% / 100) * (1 + quality / 100)
fire/cold/lightning: (base + local added)           * (1 + (local <type>% + local Elemental%) / 100)
chaos:               (base + local added)            (локальные увеличения не применяются вовсе)
AttackRate = base.AttackRateBase * (1 + local Speed INC / 100)
CritChance = (base + local BASE) * (1 + local INC / 100)
```
Наш прежний код **складывал** качество с локальным процентом (`1 + (phys% + quality)/100`) вместо
перемножения и зажимал качество в 20 (у `The Ordained, Grand Spear` напечатано **26**; PoB2 берёт
напечатанное значение, поэтому и в `GearItem.Quality`, и в импортёре теперь допустимо больше 20).
Результат: `Base (weapon) = (56+84)/2 × 3.26 × 1.26 = 287.5` — ровно как у PoB2 (было 84.0).

**2. Строки самого оружия читаются ЛОКАЛЬНО.** PoB2 читает каждую строку урона/скорости/крита оружия через
`calcLocal`, то есть «226% increased Physical Damage» на оружии — это `local_physical_damage_+%`, а не
глобальное увеличение. Наша `CharacterCalculator.WeaponLocalIds` задаёт это семейство
(`physical/fire/cold/lightning/chaos/elemental_damage_+%`, `attack_speed_+%`,
`critical_strike_multiplier_+`, добавленный урон всех типов), `WeaponContext` переводит по ней текст
оружия, а глобальный проход эти строки пропускает — иначе они считались бы дважды и раздували бы ВСЕ
источники урона. Добавлен и сам приёмник: `ItemContext.LocalTypeInc` / `LocalElemInc`
(«local elemental damage»), `base_projectile_speed_+%` больше не «просто каталог».

**3. Условные моды поддержек теперь оцениваются по состоянию билда.** `StatBucket.Conditions` до этого
оставался `ModConditions.None`, поэтому любая условная запись statMap попадала в «не учтено». Теперь
состояние жизни (порог 35 %, `data.misc.LowPoolThreshold`) и флаги конфига передаются до цикла умений —
и `Execute III` даёт свою настоящую ×1.3 MORE («30% more Damage while you are on Low Life»). При этом
условие на **врага** (`ActorCondition actor = enemy`) больше не отвечается состоянием игрока: парсер
кодирует актора (`ActorCondition:enemy.LowLife`), а `ModConditions.EnemyLowLife` остаётся `null` без
`conditionEnemyLowLife` в конфиге — как в PoB2, где этот чекбокс по умолчанию выключен. Попутно закрыт
двойной учёт: id, у которого есть запись в statMap, больше не аппроксимируется «_final»-эвристикой
(раньше это давало лишнюю ×1.3).

**4. Поддержка сохраняет свой тир.** Скилл-ид PoB2 («SupportProjectileAccelerationPlayerThree») длиннее
игрового («SupportGemAccelerationThree»), а наш матчер выбирал **самый короткий** подходящий id — из-за
этого `Projectile Acceleration III` импортировался как `Projectile Acceleration I`, и вместе с тиром
терялись его статы и флаг. Теперь при вложенности «ключ PoB2 содержит хвост» побеждает **самый длинный**
(самый специфичный) хвост. Это вернуло флаг
`projectile_speed_additive_modifiers_also_apply_to_projectile_damage` («увеличения скорости снарядов
также применяются к урону»): с ним `+134 %` скорости снарядов билда стали `+134 %` урона Twister.

**Измеримый результат на Twister:** урон за удар **13 230 → 26 691**, DPS **101 000 → 278 197**
(PoB2 `TotalDPS 2 049 502`), крит-шанс 75 % (как в панели), крит-множитель ×8.28 (было ×5.78).
Проверка «на простом ударе»: у Huntress `Bow Shot` наш до-критовый удар 2 580 против 2 540 у PoB2
(разница 1.6 %) — то есть конвейер удара оружия сходится.

**Что осталось по Twister (измерено).** Остаток ×3.7 — это постоянные баффы билда, каждый из которых
PoB2 считает по своему конфигу: `Trinity` (`trinity_damage_+%_final_to_grant_per_50_resonance`),
`Elemental Conflux` (`skill_elemental_conflux_active_element_damage_+%_final` — MORE на «неактивную»
стихию через `Multiplier: ElementalConflux<Type>Effect`), `Berserk` (`skill_base_rage_effect_+%_to_apply`),
а также заряды (Frenzy/Power) и скорость атаки (`1.61` против `2.072`). Все они сейчас честно видны в
отчёте строками `aura: …` и `SupportSpecUnhandled: …`, то есть список для следующего шага уже готов.


### 11.16 Заряды, баффы-поддержки и `constantStats`: что именно даёт остаток Twister

Версия 0.9.9 разобрала блок из §11.10 п.5 — постоянные само-баффы и заряды — на три конкретные механики,
каждая проверена по данным PoB2.

**1. Заряды приходят из конфига, а их эффекты — из `StatThreshold`/`Multiplier`.** PoB2 хранит флаг
«использовать заряды» (`useFrenzyCharges`…) и **экспортирует в share-код уже посчитанное количество**
(`<PlayerStat stat="FrenzyCharges" value="3"/>`, рядом `FrenzyChargesMax`). Импорт читает сначала
`PlayerStat`, потом (если его нет) флаг + максимум, и кладёт в `BuildConditions.FrenzyCharges/PowerCharges/
EnduranceCharges` (`BuildInterop.cs`, `ToConditions`). У Twister это 3 + 3 + 3.

Теги, которые теперь считаются в проходе аур (`AuraSkillCalculator.ConditionOf` / `MultiplierOf`):

- `{ type = "StatThreshold", stat = "FrenzyCharges", threshold = 1 }` — мод живёт, только если зарядов не
  меньше порога; `ChargeCount` знает Frenzy/Power/Endurance/Total по этим числам;
- `{ type = "Multiplier", var = "TotalCharges" }` — **значение мода умножается** на это число (как в PoB2,
  где множитель применяется к каждому моду).

**2. «Charge Infusion» — это `ChargeRegulationPlayer`, и её моды держатся на зарядах.** Из statMap этой
эффект-таблицы (`act_int.lua:3180-3196`):

| стат | мод PoB2 | условие | у Twister |
|---|---|---|---|
| `charge_mastery_skill_speed_+%_with_frenzy_charges` | `Speed INC` | 1+ frenzy | **+25 %** → rate 1.82 → **2.10** |
| `charge_mastery_crit_chance_+%_final_with_power_charges` | `CritChance MORE` | 1+ power | **×1.26** |
| `charge_mastery_armour_evasion_energy_shield_+%_final_with_endurance_charges` | `Armour/Evasion/ES MORE` | 1+ endurance | **+20 %** |
| `charge_regulation_damage_per_charge_granted_+%` | `Damage INC` × `TotalCharges` | — | у этой сборки нет alt-quality, поэтому 0 |

`Speed` у PoB2 — это и атака, и каст, поэтому у нас он ложится в `SkillSpeedInc`: обе формулы rate его уже
складывают. Первая попытка класть его и в `AttackSpeedInc`, и в `CastSpeedInc` давала **двойной учёт** для
атак (rate 2.38 вместо 2.10) — это была ошибка, а не механика.

**3. Поддержка может выдавать бафф всему персонажу.** `SupportBlazingCriticalPlayer` (`sup_int.lua:939-970`)
в своём statMap даёт `DamageGainAsFire BASE 15` с `ModFlag.Attack`, `{ Condition: CritRecently }` и
`{ GlobalEffect, effectType = "Buff", effectName = "Blazing Critical" }` — то есть «критические попадания
поддержанными умениями пропитывают **все ваши атаки** огнём». У Twister в конфиге стоит
`conditionCritRecently=true`, поэтому бонус обязан применяться: удар **29 896 → 34 318.8** (+15 % базового
урона как extra Fire), DPS 423 441 → **486 084.5**. Именно поэтому проход аур теперь смотрит и поддержки
включённых групп, а не только активный камень группы.

**4. `constantStats` и пустой `stats`.** У Blazing Critical в эффект-таблице **нет списка `stats` вообще** —
id живёт только в `constantStats` и statMap. Поэтому:

- у эффекта появилось поле `Constants` (`SkillDataIndex`, читается из `constants` того же варианта), а
  значение мода = `per-level (если есть) + constant + quality`;
- обход статов эффекта — это **объединение** `stats` ∪ ключей `statMap` ∪ ключей `constants`
  (`AuraSkillCalculator.StatIds`), иначе такой бафф не находится ни `IsAura`, ни проходом значений.

**5. Одна строка statMap — несколько `mod(...)`.** PoB2 пишет в одной строке сразу несколько вызовов
(Charge Regulation: `Speed` + `WarcrySpeed` + `TotemPlacementSpeed`). Парсер читал условия по всей строке и
подхватывал `stat = "FrenzyCharges"` **следующего** вызова как условие текущего → не резолвил и молча
выбрасывал мод. Теперь разбирается только первый вызов (`PoB2ModTranslator.Parse`: обрезка до `), mod(`).

**6. Чтобы не считать дважды.** Стат может принадлежать и общему словарю id, и statMap самого умения.
`StatInterpreter.Maps(id)` отличает «попадает в бакет» от «просто каталогизируется» (ранний фильтр
интерпретатора каталогизирует всё, где есть слово *charge*), а `archmage_*` пропускается в проходе аур
целиком: его уже собирает `CollectGlobalGemStatics` из статиков камней (иначе Archmage давал удвоенные
+360 % вместо +180 %).

**Итог по Twister** (по 4 фикстурам тесты 194/0): удар **34 318.8** против 951 098 у PoB2 (крит-взвешенный
232 000 против 951 098, было 202 097), rate **2.10** против **2.072** (было 1.82 — то есть скорость почти
сошлась), крит-множитель ×8.68 против ×12.39, DPS **486 085** против 2 049 502 (было 366 831).

**Что ещё осталось в этом блоке, по убыванию отдачи:** крит-множитель (+371 п.п.) и база урона
(см. §11.15/§11.13); `Elemental Conflux` и `Trinity` — их моды (`skill_elemental_conflux_active_element_damage_+%_final`,
`trinity_damage_+%_final_to_grant_per_50_resonance`) висят на `Multiplier: ElementalConflux<Type>Effect` и на
резонансе, значения которых **не экспортируются** в share-код (у Trinity видно только
`trinity_skill_speed_+%_while_all_resonance_is_at_least_250_to_grant = 25` — тоже на множителе), поэтому они
остаются в классе «распознано, но не моделируется» с этой записанной причиной; `Barrage` (число повторов
на заряды — множитель), `Direstrike I`, `Breachlords Rift`, `Virtuous Barrier`, заряды как отдельные
`Multiplier:FrenzyCharge/PowerCharge/EnduranceCharge` для модов предметов, а также Rage: у баффа «Rage» в
PoB2 вообще нет урона — только `LifeDegenPercent` на `Multiplier: Rage`, так что «Rage 30» в панели — это
счётчик, а урон от него дают лишь моды вида «…per Rage» (`ModParser.lua:1470`).


### 11.17 Rage, Elemental Conflux и Trinity: множители PoB2, которые задаёт конфиг

**Rage даёт урон — пользователь прав.** В `Modules/CalcPerform.lua:777-791`:

```lua
if modDB:Flag(nil, "Condition:CanGainRage") or modDB:Sum("BASE", nil, "RageRegen") > 0 then
  local maxStacks = modDB:Sum("BASE", skillCfg, "MaximumRage") * calcLib.mod(modDB, nil, "MaximumRage")
  local stacks = m_max(m_min(rageConfig, maxStacks), (minStacks > 0 and minStacks) or 0)   -- из <Input multiplierRage>
  output.RageEffect = m_floor(stacks * calcLib.mod(modDB, nil, "RageEffect"))              -- floor(30 x 1.75)
  modDB:NewMod("Multiplier:Rage", "BASE", output.Rage, "Base")
  if modDB:Flag(nil, "Condition:RageSpellDamage") then modDB:NewMod("Damage","MORE", output.RageEffect, "Rage", ModFlag.Spell)
  else modDB:NewMod("Damage", "MORE", output.RageEffect, "Rage", ModFlag.Attack) end       -- атаки!
end
```

- `MaximumRage` = `Data/Misc.lua:123 ["BaseMaximumRage"] = 30` плюс любые «+N to Maximum Rage»;
- количество берётся из конфига `multiplierRage`, который PoB2 показывает **только если билд может набирать
  ярость** (`ifFlag = "Condition:CanGainRage"`, `ConfigOptions.lua:964`, в подсказке прямо: «Base Maximum Rage
  is 30, and inherently grants 1% More Attack Damage per 1 Rage») — поэтому ненулевое значение само по себе
  доказывает, что ярость живёт, и заменяет проверку флага;
- **Berserk увеличивает силу ярости**: `act_str.lua:1508-1510` маппит `skill_base_rage_effect_+%_to_apply` в
  `mod("RageEffect","INC")` (у Twister 75 %) → `RageEffect = floor(30 × 1.75) = 52` → **Damage MORE 52 %**
  (было учтено 30 % или, до этой версии, 0).

**Elemental Conflux считается.** `ConfigOptions.lua:389-407` (список `elementalConfluxElement`, по умолчанию
**Average**) выставляет три множителя: Average → 3/3/3, выбранный элемент → 1 для него и 0 для остальных. Мод
самого бафа — `act_int`/skillstatMap: `mod("LightningDamage","MORE", …, { Multiplier, var =
"ElementalConfluxLightningEffect", invert = true })` и то же для Cold/Fire, а `Classes/ModStore.lua:385-400`
делает `mult = floor(base / div + 0.0001)`, затем `if invert then mult = 1 / mult`, затем `value = value × mult`.
Итог для Average: 75 % / 3 = **+25 % more к огню, холоду и молнии**; при выбранной стихии она получает 75 %,
а две другие — множитель 0 (мод умножается на ноль). Проверено тестом по долям стихий: молния ×1.4 относительно
Average, а огонь ×0.8 (= 1/1.25).

**Trinity считается, но по умолчанию равна нулю.** `ConfigOptions.lua:673-675` (count `configResonanceCount`,
0..300) → `Multiplier:ResonanceCount`, а statMap камня: `mod("ElementalDamage","MORE", …, { Multiplier, var =
"ResonanceCount", div = 30 })` (в описании стата «per 50», но PoB2 делит на **30**) и
`MultiplierThreshold: ResonanceCount ≥ 250` для его скорости. У фикстуры Twister этого инпута нет → 0 → молчит;
при 300 резонанса (10 стеков × 6 %) наш хит 61 663.9 → **92 061.3**, а каждая стихия ×1.6 — ровно как у PoB2.

**Что для этого понадобилось поправить в разборе данных** (три «тихие» потери мод):

1. `ConditionName` искал `var = "…"` по всему вызову `mod(...)`, поэтому у тега
   `{ GlobalEffect, effectType = "Buff", effectName = "X" }` в качестве переменной подхватывался `var` **следующего**
   тега (`Multiplier: ElementalConfluxLightningEffect`) → условие «неизвестно» → мод выбрасывался. Теперь имя
   ищется только внутри своего тега (до `}`).
2. Одна строка statMap может содержать **несколько** вызовов `mod(…)` (Conflux: три стихии; Charge Regulation:
   Speed + WarcrySpeed + TotemPlacementSpeed). Разбирался только первый → у Conflux считалась одна стихия.
   Появился `PoB2ModTranslator.ParseAll`, и каждый вызов оценивается по своим тегам.
3. Множитель с `invert`/`div` теперь применяется как значение (`AuraSkillCalculator.MultiplierOf`), а его
   теги (`Multiplier`/`MultiplierThreshold`/`StatThreshold`) снимаются перед передачей мода в sink (иначе они
   выглядели как неизвестные условия).

**Итог по Twister (0.9.10):** удар **62 138.7** (было 34 318.8), rate **2.10** против 2.072, крит-взвешенный
удар **420 058** против 951 098 (было 202 097 в начале работы), DPS **880 121** против 2 049 502. Остаток —
крит-множитель (×8.68 против ×12.39) и база урона.

**Честно осталось:** `Barrage` (повторы — множитель `FrenzyCharges`), `Virtuous Barrier` (`gem_barrier_*`:
у них условие на цвета гнёзд, которое наш проход баффов не резолвит), `Direstrike II`/`Cool Headed`/`Strong
Hearted`/`Warm Blooded`/`Cannibalism II` — их моды теперь **пытаются** примениться, и то, что не вышло,
попадает в отчёт с точной причиной (`mod Damage INC not modelled`, `condition not resolved`). Причина
«condition not resolved» у «на низком здоровье» — порядок проходов: у нас баффы идут до пулов, а `LowLife`
определяется по невыделенной жизни (у Mercenary это 748/2202 = 34 %, то есть PoB2 его включает); чтобы
резолвить такие условия, нужен двухфазный CalcSetup, как у PoB2.


### 11.18 Двухфазный CalcSetup, потерянная бифуркация крита и like-for-like сравнение с PoB2

**1. Двухфазный порядок.** PoB2 собирает моды игрока, считает пулы и только затем переоценивает условия.
У нас проход постоянных баффов идёт ДО пулов, поэтому мод, зависящий от состояния здоровья («+70 % increased
Attack Damage while on Low Life» у Direstrike II), раньше не мог быть решён и просто попадал в отчёт. Теперь:

- `ModConditions.LowLife/FullLife` стали **nullable**: `null` значит «пока неизвестно»;
- первый проход (`AuraSkillCalculator.Apply`) при неизвестном условии кладёт мод в `StatBucket.DeferredBuffs`
  (с тем же текстом заметки, что и раньше) вместо того чтобы считать условие ложным;
- второй проход (`AuraSkillCalculator.ApplyDeferred`) вызывается сразу после `bucket.Conditions` (там уже есть
  `LowLife`/`FullLife` из невыделенной жизни) и применяет всё, что теперь выполняется, снимая заметку
  (`StatBucket.Forget`);
- моды с `ModFlag.Attack` идут в attack-only поля (`AttackDamageInc`, `AttackDamageMoreFactor`) — иначе
  Direstrike II давал бы +70 % и заклинаниям (это была реальная ошибка: Flameblast-базлайн сразу показал +43 %).

Проверено: у Mercenary `Aura:deferredApplied = 1`, `Aura:Direstrike II … = 70`, а Flameblast вернулся к
записанному базлайну 17 210.6 (0.00 %).

**2. Бифуркация крита затиралась (настоящая ошибка).** Garukhan's Resolve даёт «Critical Hits Bifurcate»:
PoB2 (`CalcOffence.lua:3824-3843`) считает `conditionalChance = (pre²/100)/post` и делает это **MORE** к
крит-множителю, то есть `extraDamage = damageBonus + conditionalChance × damageBonus`. У нас множитель
вычислялся и тут же **перезаписывался** строкой `critBonus = (Base + …) × …` ниже. Исправлено: множитель идёт
в `critBonusMore`. Twister: крит-множитель **×8.68 → ×11.24**, DPS 943 257 → **1 211 164**, эффективный DPS
1 683 706.

**3. Сравнение с PoB2 стало like-for-like.** Фикстура выгружена в режиме **EFFECTIVE**
(`<Input string=\"EFFECTIVE\" name=\"misc_buffMode\"/>`), то есть её `AverageDamage`/`TotalDPS` уже включают
модификаторы врага (сопротивления, «урон от крита по врагу»). Поэтому честно сравнивать нужно наш
«Effective DPS (PoB2 mode)» и эффективный удар, а не сырые:

| величина | наш | PoB2 | остаток |
|---|---|---|---|
| эффективный удар | 92 579.1 | 49 447-148 577 (среднее 99 012) | **×1.069** |
| эффективный DPS | 1 683 705.6 | 2 049 501.9 | **×1.217** |
| крит-множитель | ×11.24 (+1024 %) | ×12.39 (+1139 %) | ×1.102 |
| rate | 2.10 | 2.072 | мы чуть быстрее |

Остаток по криту распадается на два: наша сумма **до** бифуркации +768 % против PoB2-овских +854 % (не
хватает +86 — это следующий шаг; из них 86 = 27+28+31 «increased Critical Damage Bonus with Spears», то есть
проверить надо именно эти три строки самоцветов и радиусные «Notables in Radius»), и то, что ×1.217 по DPS
сравнимо с ×1.069 по удару × ×1.102 по криту (то есть после закрытия крита остаётся ~7 % по базе/инкреасам).


### 11.19 Два бага в сопоставлении строк, из-за которых крит-множитель Twister был на 10 % ниже

Остаток §11.18 («+768 % против +854 % — не хватает ровно 86») оказался **двумя настоящими ошибками** в разборе
текста, а не механикой.

**Баг 1: обратный словарь статов брал первую часть алиаса.** `ReverseStatTextMatcher.PatternFromTemplate`
разворачивал разметку как `[Tag|Display] → Tag` (первая часть), тогда как игра печатает **Display** (последнюю).
В результате любой шаблон с алиасом был неотличим от текста строки: `{0}% increased
[CriticalDamageBonus|Critical Damage Bonus] with [Spear|Spears]` превращался в
«… increased criticaldamagebonus with spear», а в предмете стоит «31% increased Critical Damage Bonus with
Spears» → **null**. Таблица используется и деревом, и предметами, поэтому молча терялись все такие строки.
Теперь берётся последняя часть (`ModLineMatcher` делает так же с самого начала).

**Баг 2: матчер аффиксов при значении вне диапазона подставлял чужой аффикс.** Строки трёх Emerald-самоцветов
(«31/28/27% increased Critical Damage Bonus with Spears») не попадали ни в диапазон аффикса «of Hunting»
(он катается 5-10 %), ни в диапазон любого другого, поэтому срабатывал последний fallback `any` — и строка
уходила аффиксу `JewelRadiusSpearCriticalDamage`. Калькулятор такие id **сознательно пропускает** (это
радиусные аффиксы, они считаются по нотблам), так что весь бонус исчезал: `+160` в weapon-class вместо `+246`.
Теперь при отсутствии подходящего по диапазону аффикса строка сначала сверяется с ** игровым текстом стата**
(`ReverseStatTextMatcher`), и только если и он не знает строку — берётся одноформенный кандидат.

**Проверено составом, а не итогом** (тест печатает источники):

```
crit bonus sources: attack_critical_strike_multiplier_+=175 | base_critical_strike_multiplier_+=293 | spear_critical_strike_multiplier_+=246
radius crit sources: attack_critical_strike_multiplier_+=160 | spear_critical_strike_multiplier_+=120
```

- `+40` — узел Javelin («40% increased Critical Damage Bonus with Spears»),
- `+120` — радиус-линии Time-Lost («12% … with Spears» × 10 нотблов в радиусе),
- `+86` — те самые три строки самоцветов (31+28+27), которых не хватало,
- `+160`/`+175` — «for Attack Damage» (радиус + узел дерева),
- `+293` — обычные «increased Critical Damage Bonus» (предметы 27+11, дерево, условные «within 2m»).

Итог: наш крит-множитель **×12.39 = PoB2 ровно** (было ×11.24), DPS Twister 1 331 165, эффективный DPS
**1 850 524** против 2 049 501.9 = **×1.108** (было ×1.217), эффективный удар 92 579.1 против 99 012 = ×1.069.

**Что осталось, по двум множителям:**
1. эффективный удар ×1.069 — не хватает ~7 % инкреасов/базы;
2. `TotalDPS` у PoB2 = `AverageDamage × HitSpeed × DpsMultiplier`, и `2 049 501.93 / 951 097.94 / 2.072 = 1.04`
   — это его `DpsMultiplier`, которого мы не применяем (наша rate 2.10 сама по себе даже чуть выше 2.072).
   Следующий шаг — прочитать множитель DPS из данных умения (`skillData.dpsMultiplier`/повторы) и сверить.


### 11.20 Barrage: DPS-множитель 1.04 закрыт; что осталось для последних 7 % удара

**Barrage = `DPS MORE (1 + repeats) × repeatDamage`.** `Modules/CalcOffence.lua:962-966`:

```lua
if activeSkill.skillTypes[SkillType.Barrageable] and skillModList:Flag(nil, "SequentialProjectiles") … then
  if skillModList:Sum("BASE", skillCfg, "BarrageRepeats") > 0 then
    local dpsMulti = (1 + skillModList:Sum("BASE", skillCfg, "BarrageRepeats"))
                   * calcLib.mod(skillModList, skillCfg, "BarrageRepeatDamage")
    skillModList:NewMod("DPS", "MORE", dpsMulti, "Barrage Repeats")
```

`DPS` затем попадает в `output.DpsMultiplier = (skillData.dpsMultiplier or 1) × calcLib.mod(…, "DPS")`
(`:3897`), а он — в `TotalDPS = AverageDamage × (HitSpeed or Speed) × DpsMultiplier × quantityMultiplier`
(`:4447`). Значения мода берутся из `Data/Skills/act_dex.lua:216-230` и `:240-248`:
`base_number_of_barrage_repeats` = 2 (constant), `number_of_barrage_repeats_per_frenzy_charge` = 1 с
`Multiplier: RemovableFrenzyCharge` и `Condition: CannotConsumeCharges (neg = true)`, а
`damage_-%_final_with_repeated_projectiles` = 50 (constant) с `mult = -1`.

Для Twister: повторов `2 + 1×3 = 5`, штраф повторов 0.58 (наш уровень камня даёт -42) →
`dpsMulti = 6 × 0.58 = 3.48` → `DPS MORE 3.48` = **×1.0348**, что и есть та самая 1.04 из отношения фикстуры
(`2 049 501.93 / 951 097.94 / 2.072 = 1.04`). Итог: DPS 1 331 165 → **1 377 489**, эффективный DPS
1 850 524 → **1 914 923** против 2 049 501.9 = **×1.070** (было ×1.108).

**Баг 3 того же семейства (тихая потеря мода): `Parse` возвращал FLAG для смешанной записи.** PoB2 пишет
`mod("BarrageRepeats", …), flag("SequentialProjectiles", …)` **одной строкой**; наш парсер искал `flag(` где
угодно и отдавал FLAG, теряя мод → `BarrageRepeats` оставался нулём. Теперь FLAG отдаётся только когда это
**первый** вызов, а `ParseAll` режет вызовы по сбалансированным скобкам (`EndOfCall`), а не по подстроке
`), mod(`.

**Поддержан `neg = true`:** PoB2 инвертирует такое условие («returns 1 repeat per Frenzy Charge **unless** you
cannot consume charges»), поэтому символ условия получает префикс `!`, а `ModConditions.Evaluate` его
инвертирует. Сам `CannotConsumeCharges` пока false: в закреплённом statmap нет такой формулировки и в
фикстурах такого мода нет — записано честно, а не угадано.

**Что осталось (×1.069 по эффективному удару) — конкретные строки снаряжения, как и предполагалось:**
1. у оружия **Entropy Edge** есть `Adds 39 to 56 Fire Damage` (локальный explicit оружия, 47.5 в среднем).
   В нашем разборе он не попадает в локальный урон оружия: в `Added (attack): 27.5 fire` видно только перчатки.
   PoB2 локальные добавленные стихии оружия считает;
2. **блок рун оружия** (`{enchant}{rune}6% increased Attack Speed`, `Bonded: +6% …`) лежит внутри
   `Implicits: N` и у нас пропускается целиком (`BuildInterop`: `if (insideImplicits) continue`), хотя PoB2
   читает руну как отдельный мод (`Classes/Item.lua:1168-1195`);
3. к рунам применяется `26% increased effect of Socketed Augment Items` — PoB2 масштабирует их значения
   (`Classes/Item.lua:1144-1180`: `valueScalar = 1 + effectModifier`, `applyRange(line, 1, valueScalar)`).
**Проверено, что добавлять НЕ нужно:** `30% increased Explicit Critical/Speed Modifier magnitudes` — для них
PoB2 держит только описания статов (`Data/StatDescriptions/stat_descriptions.lua:1366+`), в расчёте их нет.


### 11.21 Обновлённый Arc Spell Totem (pobb.in/adaw0EHKkiFk): что совпало и где остаток

Билд перевыгружен владельцем, поэтому фикстура `pobb-arc-totem-v2.txt` добавлена в тесты и её собственный
`<PlayerStat>` — эталон. Тест `Parity: the updated Arc Spell Totem build …` печатает и наши числа, и
эталон, и полный разбор Arc.

**Совпало точно (пулы):** наша жизнь **1658** = PoB2, мана **4425** = PoB2 (это важно: Archmage даёт «gain N%
of damage as extra Lightning» от максимума маны), ES 0 ✓, броня **1623** ✓, уклонение **1420** ✓,
сопротивления 75/52/75 ✓. Тесты: **196/0**.

**Где остаток** (PoB2 выгружен в режиме EFFECTIVE, а в конфиге **нет** `arcLightningInfused` — то есть его
610 132 посчитаны **без** «Lightning Infusion»):

| умение | наш (эффективный) | PoB2 | остаток |
|---|---|---|---|
| Arc (main group) | 457 291.5 | **610 132.74** (`AverageHit 257 144.07`, `Speed 2.3727`, крит 54.45 % ×6.46) | **×1.334** |
| Flame Wall | 69 405.7 (сырой) | ≈85 000 (панель владельца) | ×1.22 |
| Entangle | 38 873.5 (сырой) | ≈49 000 | ×1.26 |

Две ловушки в самом сравнении, которые надо держать в голове: (1) PoB2-евские числа — «эффективные»
(с модификаторами врага), поэтому сравнивать их надо с нашим столбцом `Effective DPS (PoB2 mode)`, а не с
сырым `DPS`; (2) `arcLightningInfused` в конфиге не выставлен, поэтому «наш 919 819 с галочкой» сравнивать с
610 132 нельзя — наша галочка добавляет ×3 (это семантика самого PoB2: `ConfigOptions.lua:214-215`,
`Condition:ArcLightningInfused`).

**Конкретные лиды по остатку (проверяемые, не догадки):**
1. **Крит-шанс Arc: у нас 47.70 %, у PoB2 54.45 %** (в старой фикстуре совпадало ровно). В новом билде есть
   `2×14%` и `59% increased Critical Hit Chance for Spells` (то есть **+87 %** именно на заклинания) плюс общие
   `25% increased Critical Hit Chance`. Разница 1.1415 по шансу как раз соответствует этим +87 %: значит строки
   «…for Spells» с предметов у нас не попадают в `SpellCritInc` (`StatInterpreter` имеет для этого
   `spell_critical_strike_chance_+%`, вопрос в том, во что их маппит сопоставитель аффиксов).
2. **Chaos resistance: у нас 0, у PoB2 45** — новая строка в билде, которую мы не считаем.
3. Урон Arc ×1.334 при точной мане — значит остаток в конвейере урона (поддержки/инкрисы/Archmage-гейн), а не в
   пулечки. Проверять по строкам разбора, как это уже сделано для крита Twister (extras `CritSrc:`/`RadiusCrit:`
   и печать состава).
4. Flame Wall ×1.22 и Entangle ×1.26 — те же семейства, что найдены для Twister в §11.20 (локальный
   `Adds X to Y …` оружия и блок рун внутри `Implicits:`).


### 11.22 Mageblood: «Legacy of X» и Effective DPS по умолчанию

**Легаси Mageblood.** В данных PoB2 строка «Legacy of X» — это **маркер**, а не мод:
`Data/ModCache.lua:6076-6089` даёт `LegacyOfX BASE 1` плюс флаг `MagebloodEquipped`, а сами эффекты лежат
таблицей в движке (`Modules/CalcPerform.lua:65-141`) и применяются там же (`:1502-1529`):

```lua
local stacks = modDB:Sum("BASE", nil, name)                       -- сколько копий легаси
local effectPerDupe = modDB:Sum("INC", nil, "MagesLegacyEffect")  -- «за дубликат»
local globalEffect = 1 + totalDuplicates * (effectPerDupe / 100)
modDB:NewMod(entry.stat, entry.type, m_floor(globalEffect * entry.value), "Mageblood")
```

Значения из таблицы (все 14): Amethyst `ChaosResist BASE 45`, **Diamond `CritChance INC 75`**, Basalt
`Armour INC 150`, Bismuth `ElementalResist BASE 45`, Gold `LootRarity INC 45`, Granite `Armour BASE 2000`,
Jade `Evasion BASE 2000`, Quicksilver `MovementSpeed INC 30`, Ruby/Sapphire/Topaz `…Resist BASE 60` + `…Max
BASE 5`, Silver `Speed/WarcrySpeed/TotemPlacementSpeed INC 30`, Stibnite `Evasion INC 150`, Sulphur
`Damage INC 60`. Реализовано в `MagebloodLegacies` (с масштабированием за дубликаты и честной записью того,
что модели нет — rarity и скорость установки тотема).

**Проверено на новом билде владельца (у него Diamond + Amethyst + Quicksilver + Gold):**

| | было | стало | PoB2 |
|---|---|---|---|
| Chaos Resistance | 0 | **45** | **45** ✓ |
| Крит-шанс Arc | 47.70 % | **54.45 %** | **54.45 %** ✓ точно |
| Arc DPS (эффективный) | 457 291.5 | **504 049.3** | 610 132.74 (×1.210) |
| Flame Wall | 69 405.7 | **75 213.4** | ≈85 000 (×1.13) |
| Entangle | 38 873.5 | **42 961.8** | ≈49 000 (×1.14) |
| пулы | — | жизнь 1658, **мана 4425**, ES 0, броня 1623, уклонение 1420, res 75/52/75/**45** | всё совпадает ✓ |

Тест на новый билд держит это как **data-driven регресс-гард**: крит-шанс Arc, chaos resistance и максимум
маны сверяются с `<PlayerStat>` самой фикстуры, так что они не могут разъехаться незаметно.

**Effective DPS по умолчанию.** Числа PoB2 (и публикация poe.ninja) — «эффективные»: они посчитаны с
модификаторами врага. Поэтому в списке умений и в таблице персонажа теперь показывается именно
`EffectiveDps` (строка `Effective DPS (PoB2 mode)` из разбора), а сырое значение остаётся рядом как
«без врага» — чтобы разница между ними не пряталась. Остальные Calc Mode добавим позже, как и договорились.


### 11.10 Что делать дальше, по убыванию отдачи

1. ~~**Ауры предметных умений** (Purity of Fire +45 fire resist у Twister)~~ — **сделано в §11.11**.
   Заодно сверено на второй фикстуре: Purity of Ice уровня 15 даёт 36 + 8 = 44, и cold у `pob-real.txt`
   сходится с панелью (75 + 21).
2. ~~**Радиусные линии крит-множителя**: выяснить, какой индекс полосы PoB2 даёт Time-Lost сапфиру
   (фиксированный 4 или «Variable»), и привести INC к панели.~~ — **сделано в §11.12**: формулировки
   Time-Lost читаются таблицей `statmap.json`, крит-шанс Twister = 75 % (как у PoB2), множитель ×8.28.
3. ~~**Радиус-аллокация From Nothing / Intuitive Leap**~~ — **сделано в §11.12** (лишние ноды импорта
   убраны: 148 вместо 171).
4. ~~**Локальные увеличения оружия** (`AttackSplit`): применить локальные `% increased Physical Damage` и
   качество оружия к СУММЕ физики и добавленного урона и убрать их из глобального пула~~ — **сделано
   в §11.13** (формула PoB2 буквально, `Base (weapon)` = 287.5, удар Twister 13 230 → 26 691).
5. ~~**Постоянные само-баффы и заряды**~~ — **сделано в §11.16**: заряды читаются из `PlayerStat`
   (3+3+3) и через `StatThreshold`/`Multiplier` дают моды «Charge Infusion» (`Speed INC` +25 % → rate
   **2.10** против 2.072 у PoB2, было 1.82; `CritChance MORE` ×1.26; `Armour/Evasion/ES MORE` +20 %),
   поддержка-бафф Blazing Critical добавляет **+15 % базового урона как extra Fire** всем атакам (удар
   29 896 → **34 318.8**), а `constantStats` теперь вообще читаются (у Blazing Critical список `stats`
   пуст — id живёт только в constants/statMap; см. `Constants` и `StatIds`). Остаток по урону — крит-множитель
   (×8.68 против ×12.39) и база, а из баффов не смоделированы Elemental Conflux/Trinity (их значения на
   `Multiplier: ElementalConflux<Type>Effect` и резонансе в share-код не экспортируются), Barrage,
   Direstrike I, Breachlords Rift, Virtuous Barrier. Rage урона не даёт вовсе: у баффа PoB2 только
   `LifeDegenPercent` (`Multiplier: Rage`).
   — **дополнено в 0.9.10 (§11.17)**: Rage урон ВСЁ-ТАКИ даёт — `Damage MORE` = `RageEffect` из
   `CalcPerform.lua:777-791` (у билда ×1.52 при 30 свирепости и +75 % от Berserk), Elemental Conflux даёт
   +25 % more каждой стихии в режиме «Average», Trinity — +60 % при 300 резонанса. Остались Barrage,
   Virtuous Barrier, Direstrike II (LowLife), Cool Headed/Strong Hearted/Warm Blooded, Cannibalism II.
6. ~~**`local_non_unique_item_explicit_suffix_mod_magnitudes_+%`**~~ — проверено: PoB2 держит только
   описание стата (`stat_descriptions.lua:171569`) и нигде его не считает, поэтому реализовывать нельзя —
   иначе мы разойдёмся с его цифрами.
7. ~~**Условные атакующие статы дерева**~~ — **сделано в §11.14** (placeholder `enemyDistance` = 20 юнитов,
   `conditionSurrounded`/`conditionStunnedRecently`/`conditionAtCloseRange`, невыполненные условия
   записываются как `Condition:…=off`).
9. **Крит шанс Huntress**: у PoB2 `Inc. Crit Chance = 350 %`, у нас 222 % — разница ищется в
   полном списке источников панели (нужен скриншот с прокруткой до строк крита).
10. **Ice Shot как основная группа** Huntress (`active_skill_base_physical_damage_%_to_convert_to_cold`,
    `baseMultiplier`), сейчас тест сравнивает Bow Shot.
11. Остаточные статы аур: `Archmage` (расход маны), `Flame Wall` (добавленный урон проходящих снарядов) —
    уже видны в отчёте как `aura: …`.
12. Остальные незамоделированные строки из extras: `Duration/MORE`, `Cost/MORE`, `ProjectileSpeed/MORE`,
    `ArmourBreakEffect/MORE` и т.п.

## 12. Как проверять

```powershell
dotnet run --project tests\PoeBuilder.Tests -c Release
```

Тест `Parity: PoB2 Mercenary fixture — defence and attribute table (…)` печатает таблицу по каждому
share-коду и проверяет метрики, формулы которых сверены (пулы, атрибуты, защиты, сопротивления,
Spirit, крит). Остальные строки таблицы — открытые разрывы из раздела 4.

## 6. Импорт по ссылкам

Ни pobb.in, ни poe.ninja не отдают билд по тому URL, который копирует пользователь, поэтому ссылка
сначала разрешается (`BuildInterop.ResolveImportLink`):

| Вставленная ссылка | Что скачивается | Что внутри |
|---|---|---|
| `https://pobb.in/<id>` | `https://pobb.in/<id>/raw` | сам share-код |
| `https://pobb.in/<id>/raw` | как есть | сам share-код |
| `<id>` (голый токен pobb.in) | `https://pobb.in/<id>/raw` | сам share-код |
| `https://poe.ninja/<game>/profile/<account>/<league>/character/<name>` | `https://poe.ninja/<game>/api/profile/characters/<account>/<league>/<character>/model/0` | `charModel.pathOfBuildingExport` |
| любая другая `http(s)`-ссылка | как есть | JSON либо код, встроенный в страницу |

- Короткая ссылка pobb.in — это JavaScript-страница (поэтому вставка падала с «код не
  декодируется»); код лежит в `/raw`.
- Страница персонажа poe.ninja собирается в браузере из собственного model-API (это тот же
  эндпоинт, который вызывает её компонент `ProfileCharPage`). В модели лежит готовый
  Path of Building экспорт персонажа, поэтому импорт идёт тем же путём, что и вставленный код —
  второго парсера нет.
- Код ищется **по форме, а не по имени поля** (`BuildInterop.FindPobCode`): подходит любая строка
  JSON, которая декодируется как base64url + zlib, поэтому переименованное или вложенное поле
  продолжит работать; страница со встроенным в скрипт кодом тоже распознаётся.
- Если кода в странице нет вовсе (сайт рендерит всё клиентским скриптом и API не отдаёт), окно
  говорит об этом прямо и предлагает кнопку PoB, вместо «строка не распознана».
- Скачивание выполняется один раз — только по нажатию «Импортировать».

Проверяется тестами `Interop: build links resolve to the URL that actually carries the code` и
`Interop: a poe.ninja character model imports through its Path of Building export` (фикстура
`Fixtures/ninja-model-ll-arc.json` — реальная модель персонажа с его экспортом).

## 13. Клик по дереву и подсказка узла (0.9.16)

Клик по узлу — это не UI-мелочь, а модель поведения PoB2, и она перенесена буквально:

| Поведение | Где у PoB2 | Что делает PoeBuilder |
|---|---|---|
| ЛКМ по невыделенному узлу | `Classes/PassiveTreeView.lua:411-414` → ветка `else` («не выделен») вызывает `spec:AllocNode` | `TreeViewModel.ClickNode` → `PassiveTreeEngine.Allocate` (узел + путь до него) |
| ЛКМ по выделенному узлу | `…:414-428` → `spec:DeallocNode` | `PassiveTreeEngine.Refund` (+ подтверждение, если снимется ветка) |
| Узел нельзя снять | `shouldBlockGlobalNodeDeallocation` (`…:405-409`), старты классов и «чужие» узлы | старт класса и узел с `JewelAllocatedNodes` — `TreeClickAction.Ignore` |
| `Shift` = трассировка маршрута | `…:262-269`: `self.traceMode = true`, путь только рисуется | `Shift+ЛКМ` → `Select` (предпросмотр пути), без изменения плана |
| `Ctrl`+клик = зум | `…:238-241` | не переносилось (у нас зум колесом/кнопками) |

Решение «что делать с кликом» живёт в ядре — `Core/Tree/TreeClickModel.cs` (`TreeClickAction`), поэтому
оно проверяется без UI: `Tree: a click toggles allocation — allocate, refund, never the class start or a
jewel-granted node`.

Подсказка узла (вклад узла) считается не эвристикой, а **разницей двух расчётов**: текущий документ и
тот же документ с одним изменённым узлом (`CharacterViewModel.NodeImpact`). Для невыделенного узла это
«выделение», для выделенного — «снятие», поэтому подсказка работает в обе стороны, как в PoB2. Если
снятие узла утащит за собой ветку, дельта не показывается: одно число на ветку было бы правдоподобным,
но неверным. Стоимость этого — один пересчёт на смену наведённого узла, поэтому отзывчивость подсказки
прямо зависит от скорости `CharacterCalculator.Calculate` (см. `docs/VALIDATION.md`, «0.9.16»: 458 мс →
1-2 мс).

## 14. Арт узлов дерева и орбит (0.5, перенесено в 0.9.17)

Игра (и PoB2 вслед за ней) рисует пассив как **спрайт рамки + иконку умения поверх**; спрайт кодирует и
тип узла, и его состояние, поэтому состояния читаются по одному изображению, без дополнительных колец:

| Тип узла | Состояние | Спрайт PoB2 (`nodeOverlay`) | Срез атласа | Размер среза |
|---|---|---|---|---|
| обычный | не выделен | `PSSkillFrame` | `group-background_104_104`: 6 | 104×104 |
| обычный | на пути клика | `PSSkillFrameHighlighted` | там же: 5 | 104×104 |
| обычный | выделен | `PSSkillFrameActive` | там же: 4 | 104×104 |
| нотабельный | не выделен / на пути / выделен | `NotableFrameUnallocated` / `CanAllocate` / `Allocated` | `group-background_152_156`: 15 / 14 / 13 | 152×156 |
| гнездо самоцвета | не выделен / на пути / выделен | `JewelFrameUnallocated` / `CanAllocate` / `Allocated` | там же: 18 / 17 / 16 | 152×156 |
| кистоун | не выделен / на пути / выделен | `KeystoneFrameUnallocated` / `CanAllocate` / `Allocated` | `group-background_220_224`: 3 / 2 / 1 | 220×224 |
| узел восхождения | не выделен / на пути / выделен | `<Класс>FrameLargeNormal` / `CanAllocate` / `Allocated` | `group-background_208_208`: 6 / 5 / 4 (у всех классов одни и те же срезы) | 208×208 |

Три вещи, которые из этого стоит помнить:

1. **Атлас — это DDS-массив, а не сетка.** `ddsCoords` в `TreeData/0_5/tree.json` задаёт для каждого файла
   словарь «имя спрайта → номер», и этот номер и есть индекс среза текстуры (`arraySize` в DX10-заголовке),
   а размеры среза равны числам в имени файла. Поэтому спрайт берётся целиком, без арифметики по клеткам.
2. **Шаг среза** = `(длина файла − 148) / arraySize` (148 = 128-байтовый DDS-заголовок + 20-байтовый заголовок
   DX10); это сходится с `dwPitchOrLinearSize`, то есть мип-цепочку пересчитывать не нужно. Данные начинаются
   сразу после заголовка: сначала все мипы среза 1, затем среза 2 и так далее.
3. **Формат — BC7 (`dxgiFormat = 98`)**, поэтому декодировать его в приложении нечем, и конвертация делается
   один раз вне решения (`BCnEncoder.Net` + `PngBitmapEncoder`), а в репозиторий попадают уже PNG
   (`src/PoeBuilder.App/Data/Tree/Art/`, 15 файлов). Различие рамки и гнезда проверяется численно: у рамок
   центр прозрачный, у гнёзд самоцветов — залит.

## 15. Связи дерева: дуги орбит и прямые сегменты (перенесено в 0.9.17)

Ассеты связей — обычные PNG (`TreeData/0_5/Character_orbit_*.png`), и `tree.json` → `assets` задаёт их
имена: `CharacterLineConnector{Normal,Intermediate,Active}` → `Character_orbit_{state}0.png`, а
`CharacterOrbit<N>{State}` → `Character_orbit_{state}{index}.png`, где индекс **не равен** 10 − N: PoB2 берёт
файл, чей радиус арта совпадает с радиусом орбиты. Измеренные (по пикселям) соответствия:

| Орбита | Радиус каталога | Спрайт | Размер | Радиус арта |
|---|---|---|---|---|
| 1 | 82 | …normal9 | 91×90 | 81.7 |
| 2 | 162 | …normal8 | 176×176 | 163.2 |
| 3 | 335 | …normal6 | 346×346 | 333.4 |
| 4 | 493 | …normal5 | 501×502 | 488.3 |
| 5 | 662 | …normal4 | 671×671 | 657.1 |
| 6 | 846 | …normal3 | 853×853 | 838.5 |
| 7 | 251 | …normal7 | 263×263 | 250.6 |
| 8 | 1080 | …normal2 | 1090×1091 | 1076.9 |
| 9 | 1322 | …normal1 | 1333×1333 | 1318.4 |

Две измеренные величины превращают перенос в аффинное преобразование:

1. **Центр дуги — правый нижний пиксель изображения** (`sd` расстояний непрозрачных пикселей до этого угла
   равен 3–4 px при радиусе арта, то есть дуга круговая), а биссектриса дуги смотрит в левый верхний угол,
   то есть на −135°.
2. **Радиус арта ≈ радиус орбиты в юнитах мира** (расхождение ≤ 1 %), поэтому масштаб равен
   `радиус орбиты / радиус арта`, а не подбирается.

Отсюда размещение: сдвинуть правый нижний пиксель в начало координат, масштабировать, повернуть на
`θ_биссектрисы + 135°`, сдвинуть в центр орбиты — и подрезать клином по угловому сектору связи. Дуга шире
90° делится на равные части (PoB2 вместо подрезки сдвигает два угла квада, `:710-728`, и для > 90° рисует
две дуги, `:659-679`). Линейный спрайт — полоса 1435×29 с видимой лентой 11 px (строки 9–19): рисуется
прямоугольником вдоль отрезка с тайловым `ImageBrush`, то есть повторяется каждые 1435 юнитов ровно как
PoB2 (`endS = distance / art.width`, `:689`).

Проверять это можно без глаз: тест берёт точки дуги арта, применяет преобразование и убеждается, что они
попадают на окружность орбиты (`< 0.01` юнита), сверяет размеры 30 файлов и отношение радиусов арт/орбита.



## 16. Слои отрисовки дерева: фон, эффект узла, бэкдропы (перенесено в 0.9.18)

PoB2 рисует вьюпорт строго по слоям, и этот порядок объясняет всё, что видно на скриншотах:

| # | Слой | Что это | Где в PoB2 |
|---|---|---|---|
| 1 | `Background2` | текстура `background_1024_1024_BC7.dds.zst` (срез 1), растянутая на вьюпорт; из неё берётся угол `ширина/100 × высота/100` | `PassiveTreeView.lua:575-582` |
| 2 | бэкдроп класса | `classes[].background.image`, half-size 1500 → 3000 world units в центре дерева | `:592-602` |
| 3 | свечение круга | `class.background.active` (это `BGTreeActive`) — квад, повёрнутый на `π/2 + atan2(startNode − центр)` | `:604-611` |
| 4 | рама круга | `class.background.bg` (`BGTree`), half-size 2000 → 4000 world units — поверх свечения | `:613-615` |
| 5 | кружки восхождений | `ascendancies[].background`, half-size 1500 → 3000 world units по своим координатам; своё — полная яркость, остальные ×0.5 | `:618-640` |
| 6 | связи | `CharacterOrbit<N><State>` / `CharacterLineConnector<State>` (см. §15) | `:696-756` |
| 7 | эффект узла | `node.activeEffectImage` (узоры `Mastery*Pattern`): **0.15** у неназначенного, **1.0** у назначенного | `:1026-1040` |
| 8 | иконка узла | `node.icon`, размер — из таблицы ниже | `:1042-1112` |
| 9 | рамка узла | `node.overlay[state]` из `nodeOverlay` (см. §14) | `:1115-1132` |

Таблица размеров арта узла — `GetNodeTargetSize` (`PassiveTree.lua:777-835`). Числа там **полуразмеры**:
`DrawAsset` центрирует арт и даёт ему **двойную** ширину (`PassiveTreeView.lua:1326-1343`), то есть рамка
обычного узла — 54 → 108 юнитов в поперечнике.

| Тип узла | Рамка | Иконка | Эффект |
|---|---|---|---|
| обычный | 54 | 37 | — |
| обычный восхождения | 80 | 37 | — |
| гнездо самоцвета | 80 | 80 | — |
| нотабельный/кистоун восхождения | 100 | 54 | — |
| нотабельный | 80 | 54 | 380 |
| кистоун | 120 | 82 | 380 |
| старт восхождения | 50 | — | 380 |
| `OnlyImage` (мастери) | — | 380 (сам узор) | — |
| старт класса | 1 (невидимо) | — | — |

Из этой таблицы следует и внешний вид мастери: у него нет ни рамки, ни иконки — он **и есть узор**. И она же
объясняет два элемента со скриншотов: «молнии» вокруг кластера — это узор неназначенного мастери на 15 %, а
золотой всполох — тот же узор на назначенном узле в полную силу. В закреплённом дереве такой узор несут 347
узлов, и все они мастери; 56 разных узоров лежат в `Data/Tree/Art/effect/`.

## 17. Скорость скилла: как её считать и где мы пока не сходимся (0.9.21)

PoB2 считает скорость одной формулой (`Modules/CalcOffence.lua:2835-2840`):

```
baseTime = (skillData.castTimeOverride or grantedEffect.castTime or 1) + Sum("BASE", cfg, "Speed")
more     = skillModList:More(cfg, "Speed")
inc      = skillModList:Sum("INC", cfg, "Speed")
Speed    = 1 / (baseTime / round((1 + inc/100) * more, 2) + Sum("BASE","TotalAttackTime") + Sum("BASE","TotalCastTime"))
```

Три следствия, важные для паритета:

1. **`Speed` в PoB2 — и атака, и каст.** Поэтому «N% increased Attack and Cast Speed» это один мод
   `mod("Speed","INC")` без флага (`Data/SkillStatMap.lua`), а «N% increased Skill Speed» — три мода
   (`ModCache.lua`: `Speed INC` + `WarcrySpeed` + `TotemPlacementSpeed`). У нас этому соответствуют
   `CastSpeedInc + SkillSpeedInc` в одной сумме и `AttackSpeedInc` рядом.
2. **Множитель округляется до двух знаков** (`round(…, 2)`) — поэтому панель PoB2 воспроизводится до 0.01
   множителя, и по обратному счёту можно точно восстановить произведение `(1 + inc/100) * more`.
3. **Тотемность скилла — это ключевое слово**, а не отдельная формула: поддержка-мета ставит
   `skillKeywordFlags |= KeywordFlag.Totem` (`CalcActiveSkill.lua:632-633`), поэтому к скиллу, который кастует
   тотем, применяются моды с `KeywordFlag.Totem` (`totem_skill_cast_speed_+%`), а «N% increased Totem Placement
   speed» (`summon_totem_cast_speed_+%`) к скорости каста **не** относится — это скорость постановки
   (`TotemPlacementSpeed`). Один раз это уже давало ×2 к скорости эталонного билда, поэтому записано отдельно.

Наши пулы публикуются в отчёте вместе с источниками (`Pool:CastSpeedInc`, `Pool:TotemCastSpeedInc`,
`Pool:TotemAttackSpeedInc`, `Pool:TotemsSpellsCastSpeedPerActiveTotem`, `Pool:TotemsAttackSpeedPerActiveTotem`,
`Pool:CastSpeedSrc:<id>`, `Pool:SkillSpeedSrc:<id>`, `Pool:AttackSpeedSrc:<id>`, `Pool:TotemCastSpeedSrc:<id>`,
`Pool:TotemAttackSpeedSrc:<id>`) — без этого разбор ниже был бы невозможен, потому что экспорт PoB2 содержит
только число панели. Развёрнутый скилл печатает и своё число призванных тотемов
(`Totem cast speed: +34% (totem pool 8%, per summoned totem 3% x 2 summoned, supports 20%)`), а тест сверяет его
с `ActiveTotemLimit` из золотого экспорта.

### Что осталось (числа, а не оценка)

Эталон `pobb-arc-totem-v2` (Arc кастует тотем): у PoB2 `Speed = 2.3727`, `AverageHit = 257144.07`,
`TotemPlacementTime = 0.2264`. Панельная скорость даёт точный множитель: `Speed x 1.1 с = 2.61` ровно
(экспорт пишет `2.3727272727273`, а формула округляет множитель до двух знаков), то есть
`round((1 + inc/100) * more, 2) = 2.61`.

**Что теперь применено с нашей стороны:** `inc = 190` — 156 у игрока и 34 тотемных. Обе части перечислены
построчно (отчёт печатает `Pool:SpeedScope:…`, то есть источник каждой строки):

| Источник | Значение | id |
|---|---|---|
| Жезл Glyph Chant, `IncreasedCastSpeed7` | 35 | `base_cast_speed_+%` |
| Амулет Dragon Locket, `IncreasedCastSpeed7` | 35 | `base_cast_speed_+%` |
| Кольцо Behemoth Gyre, `CastSpeedJewellery3` = 18 и столько же от отражения Kalandra's Touch | 36 | `base_cast_speed_+%` |
| Рунический имплисит перчаток Grim Touch | 8 | `base_cast_speed_+%` |
| Уникальный самоцвет Heart of the Well | 3 | `base_cast_speed_+%` |
| Древесные узлы 17505 и 28774 | 3 + 3 | `base_cast_speed_+%` |
| Узел 16466 Mental Alacrity (пришёл через «Allocates …» Megalomaniac) | 5 | `base_cast_speed_+%` |
| Условие Low Life (`LifeUnreservedPercent = 21.65` в самом экспорте PoB2) | 20 | `cast_speed_+%_when_on_low_life` |
| Условие Crit Recently | 8 | `cast_speed_+%_if_have_crit_recently` |
| Древесные узлы 33209 и 10534 «Spells Cast by Totems …» | 8 | `totem_skill_cast_speed_+%` |
| Узел 19249 «… per Summoned Totem» на 2 тотема | 6 | `totems_spells_cast_speed_+%_per_active_totem` |
| Поддержка Urgent Totems III | 20 | `totem_skill_cast_speed_+%` |
| **Итого inc** | **190** | — |

**Никакой опечатки в PoB2 нет:** `TotemsSummoned` пишут и `Data/ModCache.lua:6709`, и
`Modules/ModParser.lua:1653`, и `CalcOffence.lua:1786`; варианта `TotalsSummoned` в исходниках **ноль**
вхождений (прошлая запись об «опечатке» была ошибкой чтения). Счётчик у PoB2 тот же — 2, поэтому «per Summoned
Totem» у нас и у него даёт +6. Поддержки группы Arc проверены все: Spell Totem (14), Arc (20), Sione's Temper,
Urgent Totems III (единственная со скоростью, +20) и Rakiata's Flow с Dominus' Grasp; Rapid Casting II
(`base_cast_speed_+%` = 20) стоит в другой группе (Mana Tempest) и в пул игрока не входит — у нас тоже.

**Остаток — ровно один множитель.** Панель даёт `2.61` при базе Arc 1.1 с, значит `(1 + inc/100) * more = 2.61`.
При проверенном полном инвентаре (`inc = 190`, то есть `2.90`) это возможно только с `more = 0.90`: либо в PoB2
есть ещё один MORE-множитель 0.9, которого мы не нашли ни в текстах сборки, ни в статмапах камней её групп,
либо его `inc = 248`, то есть на 58 больше полного инвентаря. Обе ветки теперь ограничены числами, и «список
подозреваемых» больше не нужен — вопрос один: каков набор MORE у PoB2 в этой сборке.
`support_spell_totem_cast_speed_+%_final` (−25 %, `act_str.lua:18299`) в него точно входит: мета-камень Spell
Totem попадает в `supportList` Arc (он даёт `SkillType.UsedByTotem`, `CalcActiveSkill.lua:362-379`), а
`getTotemBaseStats` отвечает только за жизнь/уровень/id тотема. У нас этот MORE пока не применён — и он не
может быть применён в одиночку: 190 с ним дают 1.98/с, что дальше от панели, чем нынешние 2.64/с.

**Чтобы получить набор MORE из самого PoB2, нужен Lua-интерпретатор — и в этом окружении его нет.**
`Modules/HeadlessWrapper.lua` позволяет считать headless «любым стандартным Lua», но `lua`/`luajit` отсутствуют
в PATH, в `Program Files` и в `runtime/` (там лишь `lua51.dll` для GUI-лаунчера и сам
`Path of Building-PoE2.exe`), `runtime-win32.zip` интерпретатора не содержит, Docker не установлен, а specs
(`.busted`, helper `HeadlessWrapper.lua`) требуют `busted` + Lua. Проверено и записано, чтобы не искать заново.

**Независимая проверка, что инвентарь скоростей постановки полон:** `TotemPlacementTime = 0.2264` у PoB2 —
это `0.6 / 2.65` (`CalcOffence.lua:1770`: `1 / (1 / Sum("BASE","TotemPlacementTime") x (1 + INC/100))`), где
2.65 = наши 65 % из дерева + 100 % от Urgent Totems III. Никакой −25 % здесь нет (иначе было бы 0.302), то
есть MORE принадлежит скорости каста, а не постановки. Заодно видно, что базовое время постановки у PoB2
0.6 с, а в нашем каталоге мета-камня `castTime = 500` — время постановки тотема модель пока не считает.

Порядок дальнейшей работы: (1) раздобыть Lua-рантайм (или воспользоваться разбором панели PoB2 извне), чтобы
увидеть его `more` для этой сборки — это единственный оставшийся вопрос по скорости; (2) импортировать
`conditionSummonedTotemRecently` (+12 к inc, когда условие в конфиге включено); (3) связать
`TotemPlacementTime` с `summon_totem_cast_speed_+%` (проверка готова: 0.6 / 2.65 = 0.2264); (4) и только затем
включать MORE −25 % — вместе со всем остальным набором, чтобы не подгонять коэффициент под панель.