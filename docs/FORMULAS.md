# Аудит формул: паритет с PoB2

Цель: честная карта того, что одно-в-одно совпадает с PoB2/pinned-данными, что является
подтверждённой PoE2-механикой из закреплённых источников, а что остаётся «verification item»
(не хватает pinned PoB2-данных/состояний). Числа в таблицах — текущее состояние
`PoeBuilder.Core.Calculation` (0.5.5c, RePoE 4.5.5.2).

## 1. Ресурсы

| Ресурс | Формула | Статус |
|---|---|---|
| Баз. Life | `Vitals.BaseLife (16) + 12·(lvl-1) + 2·Str` + flat → `×(1+LifeInc/100)`; CI → 1 | Паритет |
| Баз. Mana | `Vitals.BaseMana (30) + 4·(lvl-1) + 2·Int` + flat → `×(1+ManaInc/100)` | Паритет |
| Баз. ES | `Σ base·(1+(EsInc+quality)/100)` → `×(1+EsInc/100)` | Паритет (качество 0.5.5) |
| Ward | `Σ base·(1+(WardInc+quality)/100)` → `×(1+WardInc/100)` | Код готов; pinned базы ward=null |
| Spirit | flat → `×(1+SpiritInc/100)` | Паритет там, где источник есть |
| Реген жизни | `LifeRegenPerMin/60·(1+LifeRegenInc/100)` + `LifeRegenPercentPerSecond·maxLife` | Паритет |
| ES речардж | `EsX·12.5%/s`, задержка `4s/(1+EsRechargeFaster/100)` | Паритет PoB2 |

### Порядок Eldritch Battery (ES→Mana)
1. `es = EsFlat·(1+EsInc/100)`
2. `converted = es·EnergyShieldToMana/100; es -= converted`
3. `mana += converted` (после применения `ManaInc` — порядок PoB)
4. `spell_damage_+%_per_100_maximum_mana` читает **итоговую** максимальную ману (включая конверсию)
5. `skill_mana_cost_X%_final` масштабирует стоимость маны каждого скилла (EB: ×2)

Проверено тестами: `Item quality scales …, Eldritch Battery doubles displayed mana costs…`.

## 2. Защита

| Механика | Формула | Статус |
|---|---|---|
| Armour DR | `A/(A+12·hit)`, кап 90% (сверху, отрицательная армор брейк усиливает) | Паритет (фикстура PoB2) |
| Player hit | `ACC·1.25/(ACC+0.3·EV)·100`, мин 5, max 100 | PoB2-reference |
| Monster hit | `(1 − 0.95·EV/(EV+4·ACC))·100` | PoB2-reference |
| Deflection | `100 − (ACC/(ACC+0.12·DEFL)·150−50)`, кап 95%, предотвращает 40% | PoB2-reference, verification |
| Block | `(base+additional)·(1+inc/100)`, макс 50 общий кап 90 | Паритет PoB2 |
| Spell block | отдельная база, тот же кап-контракт | Паритет PoB2 |
| Dodge | кап 75 | Паритет PoB2 |
| Spell suppression | шанс → эффект 50% (не игнорирует крит; вероятность×эффект) | Паритет PoB2 |
| Resist | `baseline(stage) + sources`, cap 75 (+maximum, до 90) | Паритет |
| Endgame penalty | −40 ко всем элементам | Паритет |

## 3. Урон

| Механика | Формула | Статус |
|---|---|---|
| Attack base | среднее физ. оружия `·(1+(PhysInc+quality)/100)` | Паритет (качество добавлено) |
| Spell base | среднее `spell_minimum/maximum_base_*` с уровня | Паритет |
| Added damage | attack/spell flat c `damage_effectiveness` (у заклинаний; 100 по умолчанию) | Паритет/verification |
| Increase | per-type: Damage/Phys/Elem/Attack|Spell + scope | Паритет |
| More | поддержки `_final` из pinned статик (rate/crit/damage) | Паритет |
| Конверсия | `ConvertDamage` из статик гема (physical→X) | Паритет |
| Gain as extra | все-урон (`GainAs`) и source-specific (`SourceGainAs`) | Паритет |
| Crit | `chance = base·(1+inc)·more`, bonus `100% + add · more` | Паритет PoE2 |
| Rate | `1000/attackTime|castTime`, cooldown cap | Паритет |
| Effective crit | `crit·hitChance` | Паритет PoB2 |
| Ailments | Ignite 20%/s·4s; Poison 20%/s·2s; Bleed 15%/s·5s (1 стек) | Паритет PoB2 |

## 4. Пул/маршрутизация урона
- `ResourceDamageCalculator`: Mana→ES→Ward→Life; хаос обходит ES (×2 урона по ES), CI отключает обход.
- `DamageRoutingCalculator`: взято-как (taken-as) с циклами и капом 100% на источник.
- EHP-сценарии: успешный хит + ожидаемые (хит-чейнс, блок, отклонение, уворот) отдельно.

## 5. Известные разрывы паритета (закрыты кодом/документом, но не данными)
1. **Уникальные предметы**: pinned каталог не содержит модов уников, но их full-текст хранится в
   `GearItem.Notes`. `UniqueTextParser` интерпретирует **точные** английские строки модов
   (пулы, сопротивления, атрибуты, +% урона/скорости/крита, Addeds, реген, gain-as) в stat-ids
   расчёта; нераспознаные моды и базовые защиты уников (не экспортируются) остаются за скобками.
   `bucket.Extras["UniqueTextMods"]` показывает количество применённых строк.
2. **Ward-базы**: поле `ward` в pinned `base_items.json` равно null для всех баз (экспорт 4.5.5.2
   не содержит значений). Код готов, данные обновлятся при следующем RePoE/PoB2-снимке.
3. **Условные механики** (Pain Attunement Full/Low Life, заряды, статусы, Onslaught) — не
   применяются по умолчанию и остаются в «не учтено».
4. **Чарджи/бафы врагов**, проникновение/экспозиция — вне игрок-панели (не «придумывать»).
5. **Мана-стоимость скиллов от ставок и резерва**: резерв из `ResourceReservationContext`/
   `SkillGroup` применяется, но не выводится per-skill в breakdown (не меняет итог DPS).

## 6. Что добавлять при следующем обновлении данных
- Новый RePoE/PoB2 снапшот с `ward` в base_items и модами уников (двойной выигрыш: мана и паритет защиты).
- `unique items` reverse-translation (stat translations уже в pinned; добавить матчер в BuildInterop).
- Условия (Low Life / Full Life) как `ConditionFlags` для Pain Attunement и аналогов.