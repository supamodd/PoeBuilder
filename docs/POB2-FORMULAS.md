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

### 11.10 Что делать дальше, по убыванию отдачи

1. ~~**Ауры предметных умений** (Purity of Fire +45 fire resist у Twister)~~ — **сделано в §11.11**.
   Заодно сверено на второй фикстуре: Purity of Ice уровня 15 даёт 36 + 8 = 44, и cold у `pob-real.txt`
   сходится с панелью (75 + 21).
2. **Радиусные линии крит-множителя**: выяснить, какой индекс полосы PoB2 даёт Time-Lost сапфиру
   (фиксированный 4 или «Variable»), и привести INC к панели.
3. **Крит шанс Huntress**: у PoB2 `Inc. Crit Chance = 350 %`, у нас 222 % — разница ищется в
   полном списке источников панели (нужен скриншот с прокруткой до строк крита).
4. **Ice Shot как основная группа** Huntress (`active_skill_base_physical_damage_%_to_convert_to_cold`,
   `baseMultiplier`), сейчас тест сравнивает Bow Shot.
5. Остаточные статы аур: `Archmage` (расход маны), `Flame Wall` (добавленный урон проходящих снарядов) —
   уже видны в отчёте как `aura: …`.
6. Остальные незамоделированные строки из extras: `Duration/MORE`, `Cost/MORE`, `ProjectileSpeed/MORE`,
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
