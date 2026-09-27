using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>Deterministic mapper from the English modifier lines of an imported unique item's
/// user-provided text (GearItem.Notes) to stat ids the calculator already understands. Only exact
/// PoE2/PoB wording shapes are matched; anything else stays untouched in the item text.</summary>
public static class UniqueTextParser
{
    private static readonly Regex HeaderLine = new(
        @"^(Rarity:|Item Class:|Item Level:|LevelReq:|Level:|Requirements:|Unique ID:|Corrupted|Sockets:|Charm Slots:|Quality:|Stack Size:|--------|Implicits?:|unclaimed|Unmodifiable|Size:|Type:|Base:)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // PoB item text carries markup tags in front of a line: {enchant}, {rune}, {crafted}, {fractured},
    // {implicit}, {corrupted}, {prefix}/{suffix} … They are display metadata, never part of the
    // wording, so every line is normalised before matching (this alone unlocks the rune/enchant
    // bonuses of imported uniques such as "+40 to maximum Mana").
    private static readonly Regex LeadingTags = new(@"^(?:\{[A-Za-z_]+\}\s*)+", RegexOptions.Compiled);
    // "{enchant}{rune}Bonded: +20 to maximum Life" / "Rune: ..." keep a keyword before the real text.
    private static readonly Regex BondedPrefix = new(@"^(?:Bonded|Rune|Implicit|Explicit|Crafted|Fractured|Enchant)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // A rune/idol "Bonded: …" line. PoB2 gates every one of them behind the CanUseBondedModifiers
    // condition (Modules/ModParser.lua: ["^bonded: "] = { tag = { type = "Condition", var =
    // "CanUseBondedModifiers" } }), which only "Gain the benefits of Bonded modifiers on Runes and
    // Idols" sets — so a build without that modifier does not count them at all.
    private static readonly Regex BondedLine = new(@"^(?:\{[^}]*\}\s*)*Bonded\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeywordPrefix = new(@"^(?:Bonded|Rune|Implicit|Explicit|Crafted|Fractured|Enchant)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---- Item base values printed by the game/PoB in the item's own text ----
    private static readonly Regex BaseValueLine = new(
        @"^(Armour|Evasion Rating|Evasion|Energy Shield|Ward|Spirit)\s*:\s*([\d.,]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SocketCountLine = new(@"^Sockets\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QualityLine = new(@"^Quality\s*:\s*\+?(\d+)\s*%?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // PoB2 writes "Implicits: N" for the rune + enchant + implicit lines together (Classes/Item.lua:
    // the printed count is #runeModLines + #enchantModLines + #implicitModLines), so the block has to
    // be walked in file order to tell an implicit apart from a rune bonus.
    private static readonly Regex ImplicitCountLine = new(@"^Implicits?\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---- Shapes that only exist on imported uniques ----
    private static readonly Regex PerSocketPool = new(
        @"^[+-]?\s*(\d+(?:\.\d+)?)\s+to\s+(?:maximum\s+)?(Life|Mana|Energy Shield|Spirit)\s+per\s+Socket\s+filled$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "+X% to Chaos Resistance per Socket filled" (Morior Invictus) is the single-resistance form of
    // the all-elemental wording; per the owner's PoB2 screenshot its 4 runes give +60 chaos resistance.
    private static readonly Regex PerSocketResistance = new(
        @"^[+-]?\s*(\d+(?:\.\d+)?)%\s+to\s+(Fire|Cold|Lightning|Chaos|all Elemental)\s+Resistances?\s+per\s+Socket\s+filled$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DefencesInc = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Armour,\s*Evasion\s+and\s+Energy Shield$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpellCritBonusInc = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Critical Spell Damage Bonus$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DamagePerMana = new(
        @"^Non-Channelling Spells deal\s+([+-]?\d+(?:\.\d+)?)%\s+increased\s+Damage\s+per\s+(\d+)\s+maximum\s+Mana$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritChancePerMana = new(
        @"^Non-Channelling Skills have\s+([+-]?\d+(?:\.\d+)?)%\s+increased\s+Spell Critical Hit Chance\s+per\s+(\d+)\s+maximum\s+Mana$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LifeCostPercent = new(
        @"^Non-Channelling Spells cost an additional\s+([+-]?\d+(?:\.\d+)?)%\s+of your maximum Life$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QualityOfAllSkills = new(
        @"^[+-]?\s*(\d+)%\s+to\s+Quality\s+of\s+all\s+Skills$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "+4 to Level of all Spell Skills" (an amulet affix) and "+1 to Level of all Spell Skills" (a rune
    // line) are gem-level mods: PoB2 maps the wording to a GemProperty LIST mod whose keyword is the
    // skill type, so the bonus lands on every gem carrying that tag. The generic form without a type
    // ("+N to Level of all Skills") applies to everything.
    private static readonly Regex GemLevelTo = new(
        @"^[+-]?\s*(\d+)\s+to\s+Level\s+of\s+all\s+(?:(Physical|Fire|Cold|Lightning|Chaos|Elemental|Spell|Attack|Melee|Projectile|Minion)\s+)?Skills?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "+2 to Level of all Skills with an Intelligence requirement" (Gemling Legionnaire's "Neurological
    // Implants"): PoB2 attaches a gemRequirements filter instead of a tag, which for a gem is the same
    // as its attribute tag (an Intelligence-requirement gem carries the "intelligence" tag).
    private static readonly Regex GemLevelRequirement = new(
        @"^[+-]?\s*(\d+)\s+to\s+Level\s+of\s+all\s+Skills?\s+with\s+an?\s+(Intelligence|Dexterity|Strength)\s+requirement$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AllAttributes = new(@"^[+-]?\s*(\d+)\s+to\s+all\s+Attributes$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SingleAttribute = new(@"^[+-]?\s*(\d+)\s+to\s+(Strength|Dexterity|Intelligence)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FlatPool = new(@"^[+-]?\s*(\d+)\s+to\s+maximum\s+(Life|Mana|Energy Shield|Ward)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentPool = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+maximum\s+(Life|Mana|Energy Shield|Ward)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentMaxPool = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+(Energy Shield|Ward)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Resistance = new(@"^[+-]?\s*(\d+)%\s+to\s+(Fire|Cold|Lightning|Chaos)\s+Resistance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AllResistance = new(@"^[+-]?\s*(\d+)%\s+to\s+(?:all\s+)?Elemental Resistances$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaxResistance = new(@"^[+-]?\s*(\d+)%\s+to\s+maximum\s+(Fire|Cold|Lightning|Chaos)\s+Resistance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DamageInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+((?:Physical|Fire|Cold|Lightning|Chaos|Elemental|Attack|Spell)\s)?Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpeedInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+(Attack|Cast|Skill)\s+Speed$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritChanceInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Critical Strike Chance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "+X% to Critical Hit Chance" is a FLAT addition to the skill's critical hit chance, not an
    // increase: PoB2 maps the wording to CritChance BASE (Data/ModCache.lua: name="CritChance",
    // type="BASE") and its panel computes (baseCrit + base) * (1 + inc/100) * more.
    private static readonly Regex CritChanceTo = new(@"^\+?(\d+(?:\.\d+)?)%\s+to\s+Critical Hit Chance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritBonusTo = new(@"^[+-]?\s*(\d+)%\s+to\s+Critical Damage Bonus$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritBonusInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Critical Damage Bonus$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddedDamage = new(@"^Adds\s+(\d+)\s+to\s+(\d+)\s+(Physical|Fire|Cold|Lightning|Chaos)\s+Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LifeRegenPercent = new(@"^Regenerate\s+([+-]?\d+(?:\.\d+)?)%\s+of\s+(?:maximum\s+)?Life\s+per\s+second$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LifeRegenFlat = new(@"^[+-]?\s*(\d+)\s+Life Regenerated\s+per\s+second$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GainAsExtra = new(@"^Gain\s+(\d+(?:\.\d+)?)%\s+of\s+(Physical|Fire|Cold|Lightning|Chaos)\s+Damage\s+as\s+Extra\s+(Fire|Cold|Lightning|Chaos)\s+Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "Gain X% of Damage as Extra Damage of all Elements" (The Ordained's rune): PoB2 expands the line
    // into one gain-as per element (Modules/ModParser.lua:3713 — DamageGainAsLightning/Cold/Fire).
    private static readonly Regex AllElementsGain = new(@"^Gain\s+(\d+(?:\.\d+)?)%\s+of\s+Damage\s+as\s+Extra\s+Damage\s+of\s+all\s+Elements$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "X% increased Damage for each type of Elemental Ailment on Enemy" (The Taming): PoB2 expands the
    // line into one conditional Damage INC per enemy ailment (Modules/ModParser.lua:3837 lists
    // Electrocuted, Frozen, Chilled, Ignited and Shocked).
    private static readonly Regex DamagePerEnemyAilment = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Damage\s+for\s+each\s+type\s+of\s+Elemental\s+Ailment\s+on\s+Enemy$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The game's range notation, e.g. "+(10-20) to Strength". PoB2's unique data uses it; an imported
    // item carries the concrete roll instead.
    private static readonly Regex RangeNotation = new(@"\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)", RegexOptions.Compiled);
    private static readonly Regex RuneNameLine = new(@"^(Rune|Augment|Soul Core)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses every English modifier line of an imported unique's text into stat ids.
    /// Lines are normalised first: PoB markup tags ({enchant}/{rune}/…) and their keyword prefixes
    /// are stripped, and "per Socket filled" lines are expanded with the item's real socket count.</summary>
    public static IReadOnlyList<(string Id, decimal Value)> ParseMods(string? text)
        => ParseModsDetailed(text).Select(entry => (entry.Id, entry.Value)).ToArray();

    /// <summary>One parsed modifier line: the stat id and value plus the flags the calculator needs —
    /// whether the line sat inside the item's <c>Implicits: N</c> block (owned by the implicit pass,
    /// which knows the pinned base implicit), whether it is a rune/idol <c>Bonded: …</c> line (gated by
    /// PoB2's <c>CanUseBondedModifiers</c> condition) and whether it carried PoB display markup or a
    /// keyword prefix (such a line never comes from a catalog roll, so it must always be read).</summary>
    public readonly record struct ModEntry(string Id, decimal Value, bool Implicit, bool Bonded, bool Tagged, string Line);

    /// <summary>Parses every modifier line of an imported item's text, tagging the implicit block.
    /// Lines are normalised first: PoB markup tags ({enchant}/{rune}/…) and their keyword prefixes are
    /// stripped, and "per Socket filled" lines are expanded with the item's real socket count.
    /// A flat defence line of an item that prints that defence value is skipped: it is a LOCAL roll
    /// already contained in the printed number (see <see cref="MatchLine"/>).</summary>
    public static IReadOnlyList<ModEntry> ParseModsDetailed(string? text)
    {
        var mods = new List<ModEntry>();
        if (string.IsNullOrWhiteSpace(text)) return mods;
        var printed = ParseBaseValues(text);
        int pending = 0;
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            string raw = rawLine.Trim();
            if (raw.Length == 0) continue;
            if (ImplicitCountLine.Match(raw) is { Success: true } count)
            {
                pending = int.TryParse(count.Groups[1].Value, out int parsed) ? parsed : 0;
                continue;
            }
            // PoB2 counts rune + enchant + implicit lines together in "Implicits: N" and writes them as
            // one contiguous block (Classes/Item.lua: the printed count is #runeModLines +
            // #enchantModLines + #implicitModLines), so every line of the block is consumed here —
            // including an "Allocates X" enchant such as the amulet's "Allocates Paragon". Treating that
            // line as outside the block shifted the count and swallowed the next real affix.
            bool isImplicit = pending > 0;
            if (isImplicit) pending--;
            if (raw.StartsWith("Allocates ", StringComparison.OrdinalIgnoreCase)) continue;
            string line = Normalise(raw);
            if (line.Length == 0 || HeaderLine.IsMatch(line) || BaseValueLine.IsMatch(line)) continue;
            bool bonded = BondedLine.IsMatch(raw);
            bool tagged = raw.StartsWith('{') || bonded || KeywordPrefix.IsMatch(raw);
            var scratch = new List<(string, decimal)>();
            MatchLine(line, printed.Sockets, printed, scratch);
            foreach (var (id, value) in scratch) mods.Add(new(id, value, isImplicit, bonded, tagged, line));
        }
        return mods;
    }

    /// <summary>Parses only the rune/enchant-tagged lines of an imported item's text (a leading
    /// <c>{tag}</c> or a Bonded/Rune keyword). Used for rare/magic items, whose plain affixes are
    /// matched against the pinned mod pool separately and must not be counted twice.</summary>
    public static IReadOnlyList<(string Id, decimal Value)> ParseTaggedMods(string? text)
    {
        var result = new List<(string, decimal)>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var printed = ParseBaseValues(text);
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            string trimmed = rawLine.Trim();
            bool tagged = trimmed.StartsWith('{') || trimmed.StartsWith("Bonded:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Rune:", StringComparison.OrdinalIgnoreCase);
            if (!tagged) continue;
            string line = Normalise(trimmed);
            if (line.Length == 0 || HeaderLine.IsMatch(line) || BaseValueLine.IsMatch(line)) continue;
            MatchLine(line, printed.Sockets, printed, result);
        }
        return result;
    }

    /// <summary>Strips PoB display markup from one item-text line ("{enchant}{rune}Bonded: +20 to
    /// maximum Life" → "+20 to maximum Life").</summary>
    public static string Normalise(string rawLine)
    {
        string line = rawLine.Trim();
        if (line.Length == 0) return line;
        line = LeadingTags.Replace(line, "");
        line = BondedPrefix.Replace(line, "");
        return line.Trim();
    }

    /// <summary>Number of runes/sockets printed in the item's "Sockets: S S S S" line (0 when absent).</summary>
    public static int ParseSocketCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            var match = SocketCountLine.Match(rawLine.Trim());
            if (!match.Success) continue;
            return match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        return 0;
    }

    /// <summary>Base defence values printed in an imported item's own text. The pinned catalog does
    /// not export the base values of many unique armour pieces (Rathpith Globe, Morior Invictus…),
    /// so the item's own "Armour: N" / "Energy Shield: N" lines are the only honest source.</summary>
    public sealed record ItemBaseValues(decimal? Armour, decimal? Evasion, decimal? EnergyShield, decimal? Ward,
        decimal? Spirit, int Sockets, int? Quality);

    public static ItemBaseValues ParseBaseValues(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, null, null, null, null, 0, null);
        decimal? armour = null, evasion = null, es = null, ward = null, spirit = null;
        int? quality = null;
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            string line = Normalise(rawLine);
            if (QualityLine.Match(line) is { Success: true } q && int.TryParse(q.Groups[1].Value, out int parsedQuality))
                quality = Math.Clamp(parsedQuality, 0, 20);
            if (BaseValueLine.Match(line) is not { Success: true } value) continue;
            if (!decimal.TryParse(value.Groups[2].Value.Replace(",", ""), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount)) continue;
            switch (value.Groups[1].Value.ToLowerInvariant())
            {
                case "armour": armour = amount; break;
                case "evasion rating": case "evasion": evasion = amount; break;
                case "energy shield": es = amount; break;
                case "ward": ward = amount; break;
                case "spirit": spirit = amount; break;
            }
        }
        return new(armour, evasion, es, ward, spirit, ParseSocketCount(text), quality);
    }

    private static decimal D(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static void MatchLine(string line, int sockets, ItemBaseValues printed, List<(string, decimal)> result)
    {
        // ---- shapes unique to imported uniques (before the generic patterns) ----
        if (PerSocketPool.Match(line) is { Success: true } perSocket)
        {
            if (sockets > 0)
                result.Add((perSocket.Groups[2].Value.ToLowerInvariant() switch
                {
                    "life" => "base_maximum_life",
                    "mana" => "base_maximum_mana",
                    "energy shield" => "base_maximum_energy_shield",
                    _ => "base_maximum_spirit"
                }, D(perSocket.Groups[1].Value) * sockets));
            return;
        }
        if (PerSocketResistance.Match(line) is { Success: true } perSocketRes)
        {
            if (sockets > 0)
            {
                string kind = perSocketRes.Groups[2].Value;
                string id = kind.Equals("all Elemental", StringComparison.OrdinalIgnoreCase)
                    ? "base_resist_all_elements_%"
                    : "base_" + kind.ToLowerInvariant() + "_damage_resistance_%";
                result.Add((id, D(perSocketRes.Groups[1].Value) * sockets));
            }
            return;
        }
        if (DefencesInc.Match(line) is { Success: true } defences)
        {
            // "391% increased Armour, Evasion and Energy Shield" scales the item's own base values
            // (PoB2 ModParser maps "armour, evasion and energy shield" to the Defences bucket).
            result.Add(("local_armour_and_evasion_and_energy_shield_+%", D(defences.Groups[1].Value)));
            return;
        }
        if (SpellCritBonusInc.Match(line) is { Success: true } spellCrit)
        {
            // PoB2 maps "N% increased Critical Spell Damage Bonus" to CritMultiplier BASE with the
            // Spell flag, matching the game's own stat id base_spell_critical_strike_multiplier_+.
            result.Add(("base_spell_critical_strike_multiplier_+", D(spellCrit.Groups[1].Value)));
            return;
        }
        if (DamagePerMana.Match(line) is { Success: true } perMana)
        {
            decimal divisor = D(perMana.Groups[2].Value);
            if (divisor > 0) result.Add(("spell_damage_+%_per_100_maximum_mana", D(perMana.Groups[1].Value) * 100m / divisor));
            return;
        }
        if (CritChancePerMana.Match(line) is { Success: true } critPerMana)
        {
            decimal divisor = D(critPerMana.Groups[2].Value);
            if (divisor > 0) result.Add(("spell_critical_strike_chance_+%_per_100_maximum_mana", D(critPerMana.Groups[1].Value) * 100m / divisor));
            return;
        }
        if (LifeCostPercent.Match(line) is { Success: true } lifeCost)
        {
            result.Add(("non_channelling_spells_life_cost_+%_of_maximum_life", D(lifeCost.Groups[1].Value)));
            return;
        }
        if (QualityOfAllSkills.Match(line) is { Success: true } quality)
        {
            result.Add(("all_skill_gem_quality_+", D(quality.Groups[1].Value)));
            return;
        }
        // Gem-level wordings (item affixes, rune lines, ascendancy nodes). StatInterpreter routes each id
        // to a scope the gem's own tags must contain, so "+N to Level of all Spell Skills" only lifts
        // gems tagged "spell" — exactly PoB2's GemProperty keyword rule.
        if (GemLevelRequirement.Match(line) is { Success: true } requirement)
        {
            result.Add((requirement.Groups[2].Value.ToLowerInvariant() + "_skill_gem_level_+", D(requirement.Groups[1].Value)));
            return;
        }
        if (GemLevelTo.Match(line) is { Success: true } gemLevel)
        {
            string type = gemLevel.Groups[2].Value.ToLowerInvariant();
            result.Add((type.Length == 0 ? "all_skill_gem_level_+" : type + "_skill_gem_level_+", D(gemLevel.Groups[1].Value)));
            return;
        }
        // ---- curated ordering: most specific shapes first ----
        if (AllAttributes.Match(line) is { Success: true } attrs)
        {
            result.Add(("additional_all_attributes", D(attrs.Groups[1].Value)));
            return;
        }
        if (SingleAttribute.Match(line) is { Success: true } one)
        {
            result.Add((one.Groups[2].Value.ToLowerInvariant() switch
            {
                "strength" => "base_strength",
                "dexterity" => "base_dexterity",
                _ => "base_intelligence"
            }, D(one.Groups[1].Value)));
            return;
        }
        if (FlatPool.Match(line) is { Success: true } flat)
        {
            string pool = flat.Groups[2].Value.ToLowerInvariant();
            // A flat defence line on an item that prints that defence value is LOCAL: PoB2 feeds the
            // same "EnergyShield" BASE mod into the item's own armourData
            // (Classes/Item.lua: calcLocal(modList, "EnergyShield", "BASE", 0)), and the printed
            // "Energy Shield: 425" already contains it. Counting it in the character's global pool as
            // well doubles the item's contribution (two armour pieces inflated the pool by 118).
            // Life/Mana never appear as printed base values, so they are unaffected.
            if ((pool == "energy shield" && printed.EnergyShield is not null) ||
                (pool == "ward" && printed.Ward is not null)) return;
            result.Add((pool switch
            {
                "life" => "base_maximum_life",
                "mana" => "base_maximum_mana",
                "energy shield" => "base_maximum_energy_shield",
                _ => "base_maximum_ward"
            }, D(flat.Groups[1].Value)));
            return;
        }
        if (PercentPool.Match(line) is { Success: true } pct)
        {
            result.Add((pct.Groups[2].Value.ToLowerInvariant() switch
            {
                "life" => "maximum_life_+%",
                "mana" => "maximum_mana_+%",
                "energy shield" => "maximum_energy_shield_+%",
                _ => "maximum_ward_+%"
            }, D(pct.Groups[1].Value)));
            return;
        }
        // PoE2 prints an item-local defence increase without "maximum" ("77% increased Energy
        // Shield"), while a global one reads "increased maximum Energy Shield". The local ids are
        // routed by StatInterpreter: item context → that item's base values, no context → global.
        if (PercentMaxPool.Match(line) is { Success: true } maxPct)
        {
            result.Add((maxPct.Groups[2].Value.ToLowerInvariant() switch
            {
                "energy shield" => "local_energy_shield_+%",
                _ => "local_ward_+%"
            }, D(maxPct.Groups[1].Value)));
            return;
        }
        if (Resistance.Match(line) is { Success: true } res)
        {
            result.Add(("base_" + res.Groups[2].Value.ToLowerInvariant() + "_damage_resistance_%", D(res.Groups[1].Value)));
            return;
        }
        if (AllResistance.Match(line) is { Success: true } allRes)
        {
            result.Add(("base_resist_all_elements_%", D(allRes.Groups[1].Value)));
            return;
        }
        if (MaxResistance.Match(line) is { Success: true } maxRes)
        {
            result.Add(("maximum_" + maxRes.Groups[2].Value.ToLowerInvariant() + "_damage_resistance_%", D(maxRes.Groups[1].Value)));
            return;
        }
        if (DamageInc.Match(line) is { Success: true } dmg)
        {
            string scope = (dmg.Groups[2].Value ?? "").Trim();
            string id = scope.ToLowerInvariant() switch
            {
                "physical" => "physical_damage_+%",
                "fire" => "fire_damage_+%",
                "cold" => "cold_damage_+%",
                "lightning" => "lightning_damage_+%",
                "chaos" => "chaos_damage_+%",
                "elemental" => "elemental_damage_+%",
                "attack" => "attack_damage_+%",
                "spell" => "spell_damage_+%",
                _ => "damage_+%"
            };
            result.Add((id, D(dmg.Groups[1].Value)));
            return;
        }
        if (SpeedInc.Match(line) is { Success: true } speed)
        {
            string id = speed.Groups[2].Value.ToLowerInvariant() switch
            {
                "attack" => "attack_speed_+%",
                "cast" => "base_cast_speed_+%",
                _ => "skill_speed_+%"
            };
            result.Add((id, D(speed.Groups[1].Value)));
            return;
        }
        if (CritChanceInc.Match(line) is { Success: true } critChance)
        {
            result.Add(("critical_strike_chance_+%", D(critChance.Groups[1].Value)));
            return;
        }
        if (CritBonusTo.Match(line) is { Success: true } critBonus)
        {
            result.Add(("base_critical_strike_multiplier_+", D(critBonus.Groups[1].Value)));
            return;
        }
        if (CritBonusInc.Match(line) is { Success: true } critBonus2)
        {
            // PoB2 maps "N% increased Critical Damage Bonus" to CritMultiplier BASE (the game's own
            // stat id for this affix is base_critical_strike_multiplier_+), so it is a flat addition
            // to the critical damage bonus, not an increase.
            result.Add(("base_critical_strike_multiplier_+", D(critBonus2.Groups[1].Value)));
            return;
        }
        if (SpellCritBonusInc.Match(line) is { Success: true } spellCritBonusInc)
        {
            result.Add(("base_spell_critical_strike_multiplier_+", D(spellCritBonusInc.Groups[1].Value)));
            return;
        }
        if (AddedDamage.Match(line) is { Success: true } added)
        {
            string type = added.Groups[3].Value.ToLowerInvariant();
            result.Add(("attack_minimum_added_" + type + "_damage", D(added.Groups[1].Value)));
            result.Add(("attack_maximum_added_" + type + "_damage", D(added.Groups[2].Value)));
            return;
        }
        if (LifeRegenPercent.Match(line) is { Success: true } regenPct)
        {
            result.Add(("life_regeneration_percent_per_second", D(regenPct.Groups[1].Value)));
            return;
        }
        if (LifeRegenFlat.Match(line) is { Success: true } regenFlat)
        {
            result.Add(("base_life_regeneration_rate_per_minute", D(regenFlat.Groups[1].Value) * 60m));
            return;
        }
        if (GainAsExtra.Match(line) is { Success: true } gain)
        {
            string source = gain.Groups[2].Value.ToLowerInvariant();
            string dest = gain.Groups[3].Value.ToLowerInvariant();
            result.Add(("non_skill_base_" + source + "_damage_%_to_gain_as_" + dest, D(gain.Groups[1].Value)));
            return;
        }
        if (CritChanceTo.Match(line) is { Success: true } critTo)
        {
            result.Add(("critical_strike_chance_+", D(critTo.Groups[1].Value)));
            return;
        }
        if (AllElementsGain.Match(line) is { Success: true } allGain)
        {
            decimal value = D(allGain.Groups[1].Value);
            foreach (string element in new[] { "fire", "cold", "lightning" })
                result.Add(("non_skill_base_all_damage_%_to_gain_as_" + element, value));
            return;
        }
        if (DamagePerEnemyAilment.Match(line) is { Success: true } perAilment)
        {
            decimal value = D(perAilment.Groups[1].Value);
            // PoB2 conditions the line on each enemy ailment separately; the three the imported config
            // can report are emitted, and the calculator counts only the active ones.
            result.Add(("conditional_damage_+%_enemy_ignited", value));
            result.Add(("conditional_damage_+%_enemy_chilled", value));
            result.Add(("conditional_damage_+%_enemy_shocked", value));
            return;
        }
        // No deterministic pattern matched: consult the pinned reverse stat-translation table.
        if (ReverseStatTextMatcher.TryMatch(line) is { } reverse)
        {
            result.AddRange(reverse);
            return;
        }
    }

    /// <summary>Resolves the game's range notation ("+(10-20) to Strength") to its maximum roll — the
    /// same convention the pinned implicits use. Needed when a unique's lines come from PoB2's own data
    /// instead of an imported item, whose text already carries the concrete roll.</summary>
    public static string ResolveRanges(string line) => RangeNotation.Replace(line, match => match.Groups[2].Value);

    /// <summary>Modifier-looking lines of an item's text that the parser could not turn into a stat id.
    /// The item's name, its base type and socketed rune names are not modifiers and are skipped; every
    /// other line without an id is reported so nothing is dropped silently.</summary>
    public static IReadOnlyList<string> UnmappedLines(string? text)
    {
        var unmapped = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return unmapped;
        var mapped = new HashSet<string>(ParseModsDetailed(text).Select(entry => entry.Line), StringComparer.Ordinal);
        int titles = 0;
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            string raw = rawLine.Trim();
            if (raw.Length == 0) continue;
            if (HeaderLine.IsMatch(raw) || BaseValueLine.IsMatch(raw) || RuneNameLine.IsMatch(raw) || SocketCountLine.IsMatch(raw)) continue;
            string line = Normalise(raw);
            if (line.Length == 0 || HeaderLine.IsMatch(line) || BaseValueLine.IsMatch(line)) continue;
            if (mapped.Contains(line)) continue;
            // The first two non-modifier lines of an item's text are its name and base type.
            if (titles < 2) { titles++; continue; }
            unmapped.Add(line);
        }
        return unmapped;
    }
}