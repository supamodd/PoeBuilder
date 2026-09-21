using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.App.Services;

/// <summary>Honest per-import outcome: what matched, what did not, and why.</summary>
public sealed record ImportReport(int PassivesMatched, int PassivesUnknown, int SkillsMatched, int SupportsMatched,
    int GemsUnknown, int AscendancyNodesMatched, string Ascendancy, IReadOnlyList<string> UnknownIds, string Notes,
    int EquipmentMatched = 0, int EquipmentSkippedLines = 0, int JewelsImported = 0, int UniquesImported = 0);

public sealed record ImportedBuild(BuildDocument Document, ImportReport Report);

/// <summary>
/// Interoperability with PUBLIC build exchange formats, no network calls:
///  - official Build Planner "*.build" JSON (GGG developer docs, Version 1, experimental) — also what
///    poe.ninja-style guides use: stable passive ids ("strength89") plus gem metadata paths;
///  - Path of Building share codes: URL-safe base64 → zlib → XML (decoded locally).
/// Our export writes the same Version 1 JSON so files drop into the in-game BuildPlanner folder.
/// Item/inventory data of foreign formats is deliberately out of scope and reported as such.
/// </summary>
public static class BuildInterop
{
    public const int AttributeChoice = 26297;

    // ------------------------------ official JSON v1 / poe.ninja ------------------------------
    public static ImportedBuild ParseBuildJson(string json, GameCatalog catalog, TreeCatalog tree)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "Импорт" : "Импорт";
        string ascendancyText = root.TryGetProperty("ascendancy", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";

        var stable = StableMap(tree);
        var unknown = new List<string>();
        var notes = new List<string>();

        var (classIndex, className, definition) = ResolveAscendancy(ascendancyText, tree);
        if (definition is null && ascendancyText.Length > 0)
            notes.Add("восхождение «" + ascendancyText + "» не распознано; главный класс взят по умолчанию");

        // ---- passives ----
        var engine = new PassiveTreeEngine(tree);
        var plan = new PassiveTreePlan { DatasetId = tree.DatasetId, ClassIndex = definition?.ClassIndex ?? classIndex, PointLimit = 0 };
        int passivesMatched = 0, passivesUnknown = 0;
        var ascEngine = definition is null ? null : new PassiveTreeEngine(definition.Graph);
        var ascPlan = definition is null ? null : new AscendancyPlan { Id = definition.Id, PointLimit = 8 };
        int ascMatched = 0;
        var mainIds = new List<int>();
        var ascIds = new List<int>();
        foreach (var id in ReadIds(root, "passives"))
        {
            if (stable.TryGetValue(id, out int nodeId)) mainIds.Add(nodeId);
            else if (ascEngine is not null && definition!.Graph.Nodes.Values.FirstOrDefault(x => x.StableId == id) is { } ascNode) ascIds.Add(ascNode.Id);
            else { unknown.Add(id); passivesUnknown++; }
        }
        // Foreign lists arrive in tree-walk order, so allocate in file order and retry until no progress:
        // a node whose path parent comes later in the list succeeds on a retry.
        var failed = new List<int>();
        foreach (var nodeId in mainIds)
        {
            try { plan = engine.Allocate(plan, nodeId, AttributeChoice); passivesMatched++; }
            catch (TreeRuleException) { failed.Add(nodeId); }
        }
        bool progressed = true;
        while (progressed && failed.Count > 0)
        {
            progressed = false;
            var stillFailed = new List<int>();
            foreach (var nodeId in failed)
            {
                try { plan = engine.Allocate(plan, nodeId, AttributeChoice); passivesMatched++; progressed = true; }
                catch (TreeRuleException) { stillFailed.Add(nodeId); }
            }
            failed = stillFailed;
        }
        foreach (var nodeId in failed)
        {
            unknown.Add(tree.Nodes.TryGetValue(nodeId, out var nn) ? nn.StableId : "node " + nodeId);
            passivesUnknown++;
        }
        if (ascEngine is not null && ascPlan is not null)
        {
            var unknownAsc = new List<string>();
            (ascMatched, var implied, var unsupportedAsc) = ChainAscendancy(ascEngine, definition!, ref ascPlan, ascIds, unknownAsc);
            passivesUnknown += unknownAsc.Count;
            unknown.AddRange(unknownAsc);
            if (implied > ascMatched) notes.Add("восхождение достроено до связного пути: добавлены соединяющие узлы восхождения (+" + (implied - ascMatched) + ")");
            if (unsupportedAsc > 0) notes.Add("часть узлов восхождения — механика выбора/особые узлы: планировщик пока не поддерживает их ни вручную, ни через импорт — они перечислены в списке");
        }
        if (ascPlan is not null) plan = plan with { Ascendancy = ascPlan };

        // ---- skills ----
        var skillPlan = new SkillPlan();
        var groups = new List<SkillGroup>();
        int skillsMatched = 0, supportsMatched = 0, gemsUnknown = 0;
        foreach (var element in ReadElements(root, "skills"))
        {
            string gemId = element.GetProperty("id").GetString() ?? "";
            if (!catalog.Gems.TryGetValue(gemId, out var gem))
            {
                // Some sources ship singular/plural or case drift in the metadata path.
                var alt = catalog.Gems.Keys.FirstOrDefault(k => k.Replace("/Gems/", "/Gem/").Equals(gemId.Replace("/Gems/", "/Gem/"), StringComparison.OrdinalIgnoreCase));
                if (alt is null || !catalog.Gems.TryGetValue(alt, out gem)) { gemsUnknown++; unknown.Add(gemId); continue; }
            }
            skillsMatched++;
            int level = 1;
            if (element.TryGetProperty("level_interval", out var interval) && interval.ValueKind == JsonValueKind.Array && interval.GetArrayLength() > 0)
                level = Math.Clamp(interval[0].GetInt32(), 1, 40);
            if (!gem.Levels.Contains(level)) level = gem.Levels[0];
            var supports = new List<GemSelection>();
            if (element.TryGetProperty("support_skills", out var sup))
                foreach (var s in sup.EnumerateArray())
                {
                    string sid = s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : s.GetProperty("id").GetString() ?? "";
                    if (!catalog.Gems.TryGetValue(sid, out var support))
                    {
                        var alt = catalog.Gems.Keys.FirstOrDefault(k => k.Replace("/Gems/", "/Gem/").Equals(sid.Replace("/Gems/", "/Gem/"), StringComparison.OrdinalIgnoreCase));
                        if (alt is null || !catalog.Gems.TryGetValue(alt, out support)) { gemsUnknown++; unknown.Add(sid); continue; }
                    }
                    if (supports.Count < 5) { supports.Add(new() { GemId = support.Id, Level = support.Levels.Contains(1) ? 1 : support.Levels[0] }); supportsMatched++; }
                    else { gemsUnknown++; unknown.Add(sid); notes.Add("у «" + gem.Name + "» больше 5 поддержек — лишние пропущены"); }
                }
            string groupName = gem.Name;
            for (int copy = 2; groups.Any(g => g.Name == groupName); copy++) groupName = gem.Name + " " + copy;
            groups.Add(new() { Name = groupName, Active = new() { GemId = gem.Id, Level = level }, Supports = [.. supports] });
        }
        skillPlan = skillPlan with { Groups = [.. groups] };

        if (passivesUnknown > 0) notes.Add("часть пассивов не найдена в закреплённом дереве 0.5.5 — см. список");
        notes.Add("уровень камней: взято начало уровня_интервала гайда; качество не задаётся форматом — стоит 0");
        notes.Add("предметы и инвентарь чужого формата не переносятся");

        var build = BuildDocument.Create(name) with
        {
            CharacterClass = className,
            Tree = plan,
            Skills = skillPlan,
            GameVersion = "0.5.5c",
            Notes = "Импорт (JSON Build Planner v1) · " + DateTime.Now.ToString("yyyy-MM-dd")
        };
        notes.Add("формат JSON v1 несёт только слоты unique_name — снаряжение из него не переносится");
        var report = new ImportReport(passivesMatched, passivesUnknown, skillsMatched, supportsMatched, gemsUnknown, ascMatched, ascendancyText, unknown, string.Join(" · ", notes));
        return new(build, report);
    }

    // ------------------------------ PoB share code ------------------------------
    public static ImportedBuild ParsePobCode(string code, GameCatalog catalog, TreeCatalog tree)
    {
        byte[] payload;
        string xml;
        try { payload = DecodePobEnvelope(code); xml = Encoding.UTF8.GetString(payload); }
        catch (Exception e) when (e is FormatException or InvalidDataException or NotSupportedException)
        { throw new InvalidDataException("Код не декодируется (ожидался base64url+zlib от Path of Building). " + e.Message); }
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (Exception e) { throw new InvalidDataException("Внутри кода не XML Path of Building: " + e.Message); }
        var root = document.Root;
        // PoB1 writes <PathOfBuilding>, PoB2 (Path of Exile 2) writes <PathOfBuilding2> — verified on a real share code.
        if (root is null || (root.Name.LocalName != "PathOfBuilding" && root.Name.LocalName != "PathOfBuilding2"))
            throw new InvalidDataException("XML не похож на экспорт Path of Building (корень " + root?.Name.LocalName + ").");

        var unknown = new List<string>();
        var notes = new List<string>();
        int level = 1;
        string? pobClassName = null, pobAscendancy = null;
        if (root.Element("Build") is var buildEl && buildEl is not null)
        {
            if (int.TryParse((string?)buildEl.Attribute("level"), out int lv)) level = Math.Clamp(lv, 1, 100);
            pobClassName = (string?)buildEl.Attribute("className");
            pobAscendancy = (string?)buildEl.Attribute("ascendClassName") ?? (string?)buildEl.Attribute("ascendancyClassName") ?? (string?)buildEl.Attribute("ascendancyId");
        }

        // The active spec carries authoritative GGG ids; PoB2 even stores ascendancyInternalId ("Mercenary3").
        int? activeSpec = null;
        var treeEl = root.Element("Tree");
        if (treeEl is not null && int.TryParse((string?)treeEl.Attribute("activeSpec"), out int parsedActive)) activeSpec = parsedActive;
        var specs = treeEl?.Descendants("Spec").ToList() ?? [];
        XElement? spec = specs.Count == 0 ? null
            : activeSpec is int a ? specs.FirstOrDefault(sp => (int?)sp.Attribute("id") == a) ?? specs[0] : specs[0];
        if ((string?)spec?.Attribute("ascendancyInternalId") is string internalId && internalId.Length > 0) pobAscendancy = internalId;
        if ((string?)spec?.Attribute("treeVersion") is string treeVersion && treeVersion.Length > 0)
            notes.Add("дерево кода: PoB " + treeVersion.Replace('_', '.') + " → импорт в наше закреплённое 0.5.5");

        var (_, className, definition) = ResolveAscendancy(pobAscendancy ?? "", tree);
        if (definition is null && !string.IsNullOrWhiteSpace(pobClassName))
        {
            var cls = tree.Classes.FirstOrDefault(c => c.Name.Equals(pobClassName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (cls is not null) className = cls.Name;
            else notes.Add("класс PoB «" + pobClassName + "» не найден в закреплённом дереве");
        }

        var engine = new PassiveTreeEngine(tree);
        var plan = new PassiveTreePlan { DatasetId = tree.DatasetId, ClassIndex = definition?.ClassIndex ?? tree.Classes.FirstOrDefault(c => c.Name == className)?.Index ?? 0, PointLimit = 0 };
        int passivesMatched = 0, passivesUnknown = 0;
        // PoB ships integer node ids; the main graph ids and ascendancy graph ids live in one list.
        var allNodes = new List<int>();
        var nodesAttr = (string?)spec?.Attribute("nodes");
        if (!string.IsNullOrWhiteSpace(nodesAttr))
            foreach (var part in nodesAttr.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int parsed)) allNodes.Add(parsed);
        var ascEngine = definition is not null ? new PassiveTreeEngine(definition.Graph) : null;
        var ascPlan = definition is not null ? new AscendancyPlan { Id = definition.Id, PointLimit = 0 } : null;
        // Main-tree ids: allocate in file order (foreign lists walk the tree) with retry passes for order hiccups.
        var failed = new List<int>();
        foreach (var nodeId in allNodes)
        {
            if (ascEngine is not null && definition!.Graph.Nodes.ContainsKey(nodeId)) continue;
            try { plan = engine.Allocate(plan, nodeId, AttributeChoice); passivesMatched++; }
            catch (TreeRuleException) { failed.Add(nodeId); }
        }
        bool progressed = true;
        while (progressed && failed.Count > 0)
        {
            progressed = false;
            var stillFailed = new List<int>();
            foreach (var nodeId in failed)
            {
                try { plan = engine.Allocate(plan, nodeId, AttributeChoice); passivesMatched++; progressed = true; }
                catch (TreeRuleException) { stillFailed.Add(nodeId); }
            }
            failed = stillFailed;
        }
        foreach (var nodeId in failed) { unknown.Add("node " + nodeId); passivesUnknown++; }
        // Ascendancy ids live in the same integer space; PoB2 lists only picked notables, so the
        // connecting ascendancy nodes are implied and allocated to form the path the game validates.
        int ascMatched = 0;
        if (ascEngine is not null && ascPlan is not null)
        {
            var requested = allNodes.Where(definition!.Graph.Nodes.ContainsKey).ToList();
            var unknownAsc = new List<string>();
            (ascMatched, var implied, var unsupportedAsc) = ChainAscendancy(ascEngine, definition, ref ascPlan, requested, unknownAsc);
            passivesMatched += ascMatched;
            passivesUnknown += unknownAsc.Count;
            unknown.AddRange(unknownAsc);
            if (implied > ascMatched) notes.Add("восхождение достроено до связного пути: добавлены соединяющие узлы восхождения (+" + (implied - ascMatched) + ")");
            if (unsupportedAsc > 0) notes.Add("часть узлов восхождения — механика выбора/особые узлы: планировщик пока не поддерживает их ни вручную, ни через импорт — они перечислены в списке");
        }
        if (ascPlan is not null && ascPlan.AllocatedNodes.Length > 0) plan = plan with { Ascendancy = ascPlan };

        // ---- skills: PoB stores <SkillSet><Skill><Gem nameSpec="..." skillId="..." level=".."/></Skill></SkillSet> ----
        var groups = new List<SkillGroup>();
        int skillsMatched = 0, supportsMatched = 0, gemsUnknown = 0;
        var skillsEl = root.Element("Skills");
        XElement? set = null;
        if (skillsEl is not null)
        {
            var sets = skillsEl.Elements("SkillSet").ToList();
            if (sets.Count > 0)
                set = activeSpec is int specId ? sets.FirstOrDefault(ss => (int?)ss.Attribute("id") == specId) ?? sets[0] : sets[0];
        }
        var skillHost = set ?? skillsEl;
        if (skillHost is not null)
            foreach (var skillEl in skillHost.Elements("Skill"))
            {
                Gem? active = null; int activeLevel = 1, activeQuality = 0;
                var supports = new List<GemSelection>();
                foreach (var gemEl in skillEl.Elements("Gem"))
                {
                    if (((string?)gemEl.Attribute("enabled")) == "false") continue;
                    string? nameSpec = (string?)gemEl.Attribute("nameSpec");
                    string? skillId = (string?)gemEl.Attribute("skillId");
                    var gem = MatchGem(catalog, nameSpec, skillId);
                    if (gem is null) { gemsUnknown++; unknown.Add(nameSpec ?? skillId ?? "?"); continue; }
                    int gemLevel = int.TryParse((string?)gemEl.Attribute("level"), out int gl) ? Math.Clamp(gl, 1, 40) : 1;
                    int gemQuality = int.TryParse((string?)gemEl.Attribute("quality"), out int q) ? Math.Clamp(q, 0, 20) : 0;
                    if (gem.Kind == "support")
                    {
                        if (supports.Count >= 5) { gemsUnknown++; unknown.Add(gem.Name); notes.Add("у «" + (active?.Name ?? nameSpec ?? "?") + "» больше 5 поддержек — лишние пропущены"); continue; }
                        supports.Add(new() { GemId = gem.Id, Level = NearestLevel(gem, gemLevel), Quality = gemQuality });
                        supportsMatched++;
                    }
                    else if (active is null) { active = gem; activeLevel = gemLevel; activeQuality = gemQuality; }
                    else if (supports.Count < 5)
                    {
                        // Mirrors the official game export: the Build Planner puts the extra active gem
                        // (e.g. Arc inside a Spell Totem group) into support_skills.
                        supports.Add(new() { GemId = gem.Id, Level = NearestLevel(gem, gemLevel), Quality = gemQuality });
                        supportsMatched++;
                        notes.Add("активный камень «" + gem.Name + "» добавлен в поддержки группы — как в официальном экспорте игры");
                    }
                    else { gemsUnknown++; unknown.Add(gem.Name); }
                }
                if (active is null) continue;
                string groupName = active.Name;
                for (int copy = 2; groups.Any(g => g.Name == groupName); copy++) groupName = active.Name + " " + copy;
                groups.Add(new() { Name = groupName, Active = new() { GemId = active.Id, Level = NearestLevel(active, activeLevel), Quality = activeQuality }, Supports = [.. supports] });
                skillsMatched++;
            }

        notes.Add("уровни и качество камней взяты из кода и приведены к допустимым значениям каталога");

        if (gemsUnknown > 0) notes.Add("часть камней переименовывалась между версиями игры — нераспознанные показаны в списке");
        // ---- items: PoB carries full gear text; bases and affix lines are matched against the pinned catalog ----
        var pobItems = ParsePobItems(root.Element("Items"), catalog);
        var (treeWithJewels, socketsPlaced, nodesGranted, grantsSkipped) =
            ApplyPobJewels(plan, pobItems, tree, root.Descendants("Socket"));

        var skillPlan = new SkillPlan { Groups = [.. groups] };
        var build = BuildDocument.Create(string.IsNullOrWhiteSpace(pobClassName) ? "PoB импорт" : "PoB · " + pobClassName) with
        {
            CharacterClass = className,
            Level = level,
            Tree = treeWithJewels,
            Skills = skillPlan,
            Equipment = pobItems.Plan,
            GameVersion = "0.5.5c",
            Notes = "Импорт из кода Path of Building · " + DateTime.Now.ToString("yyyy-MM-dd")
        };
        if (socketsPlaced > 0) notes.Add("самоцветы вставлены в гнёзда дерева как в коде PoB: " + socketsPlaced);
        if (nodesGranted > 0) notes.Add("узлы от уникальных самоцветов («Allocates …») аллоцированы бесплатно, без пути: " + nodesGranted);
        if (grantsSkipped > 0) notes.Add("часть «Allocates …»/гнёзд не сопоставлена с закреплённым деревом: " + grantsSkipped);
        if (pobItems.Uniques > 0) notes.Add("уники перенесены со своим полным текстом: закреплённый каталог не содержит их модификаторов, в расчёт они не влияют");
        if (pobItems.SkippedLines > 0) notes.Add("часть строк модов снаряжения не сопоставлена с закреплённым каталогом и не влияет на расчёт");
        var report = new ImportReport(passivesMatched, passivesUnknown, skillsMatched, supportsMatched, gemsUnknown, ascMatched, pobAscendancy ?? "", unknown, string.Join(" · ", notes),
            pobItems.Matched, pobItems.SkippedLines, pobItems.Jewels, pobItems.Uniques);
        return new(build, report);
    }

    /// <summary>Cascade match of a PoB gem: skillId key (exact tail, then containment) → display name (exact, then normalized).
    /// Gems renamed between game versions cannot be matched reliably and land in the honest unknown list.</summary>
    private static Gem? MatchGem(GameCatalog catalog, string? nameSpec, string? skillId)
    {
        var pob = PobGemKey(skillId);
        if (pob is not null)
        {
            var exact = BestGem(catalog.Gems.Values.Where(g => TailKey(g.Id) == pob));
            if (exact is not null) return exact;
            if (pob.Length >= 4)
            {
                var contains = BestGem(catalog.Gems.Values.Where(g =>
                {
                    var t = TailKey(g.Id);
                    return (t.Length >= 4 && t.Contains(pob, StringComparison.Ordinal)) || (pob.Length >= 4 && pob.Contains(t, StringComparison.Ordinal));
                }));
                if (contains is not null) return contains;
            }
        }
        if (!string.IsNullOrWhiteSpace(nameSpec))
        {
            var byName = catalog.Gems.Values.Where(g => g.Name.Trim().Equals(nameSpec.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0) return BestGem(byName);
            var key = GemKey(nameSpec);
            byName = catalog.Gems.Values.Where(g => GemKey(g.Name) == key).ToList();
            if (byName.Count > 0) return BestGem(byName);
        }
        return null;
    }

    private static Gem? BestGem(IEnumerable<Gem> gems)
    {
        Gem? best = null;
        foreach (var g in gems)
            if (best is null || Rank(g) < Rank(best)) best = g;
        return best;
        static int Rank(Gem g) => g.Id.Contains("Unique", StringComparison.OrdinalIgnoreCase) ? 1_000_000 + g.Id.Length : g.Id.Length;
    }

    private static string TailKey(string id)
    {
        var tail = id[(id.LastIndexOf('/') + 1)..];
        foreach (var p in new[] { "SkillGem", "SupportGem" })
            if (tail.StartsWith(p, StringComparison.Ordinal)) { tail = tail[p.Length..]; break; }
        return GemKey(tail);
    }

    private static string GemKey(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string? PobGemKey(string? skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return null;
        var s = skillId.Trim();
        foreach (var p in new[] { "SummonMeta", "Meta", "Support" })
            if (s.StartsWith(p, StringComparison.Ordinal)) { s = s[p.Length..]; break; }
        s = s.Replace("Player", "");
        var key = GemKey(s);
        return key.Length == 0 ? null : key;
    }

    private static int NearestLevel(Gem gem, int wanted)
    {
        if (gem.Levels.Contains(wanted)) return wanted;
        var sorted = gem.Levels.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return 1;
        foreach (var l in sorted.OrderByDescending(x => x))
            if (l <= wanted) return l;
        return sorted[0];
    }

    // ------------------------------ export (official JSON v1) ------------------------------
    public static string ExportBuildJson(BuildDocument doc, GameCatalog catalog, TreeCatalog tree)
    {
        var stable = StableMap(tree);
        string? ascendancy = null;
        var passives = new List<string>();
        if (doc.Tree is not null)
        {
            foreach (var id in doc.Tree.AllocatedNodes)
                if (tree.Nodes.TryGetValue(id, out var node) && !string.IsNullOrEmpty(node.StableId)) passives.Add(node.StableId);
            if (doc.Tree.Ascendancy is not null)
            {
                var definition = tree.Ascendancies.FirstOrDefault(a => a.Id == doc.Tree.Ascendancy.Id && a.ClassIndex == doc.Tree.ClassIndex);
                ascendancy = definition?.Id;
                if (definition is not null)
                    foreach (var id in doc.Tree.Ascendancy.AllocatedNodes)
                        if (definition.Graph.Nodes.TryGetValue(id, out var ascNode) && !string.IsNullOrEmpty(ascNode.StableId)) passives.Add(ascNode.StableId);
            }
        }
        var skills = new List<object>();
        foreach (var group in doc.Skills?.Groups ?? [])
        {
            if (!catalog.Gems.TryGetValue(group.Active.GemId, out var gem)) continue;
            var supportIds = group.Supports.Where(s => catalog.Gems.ContainsKey(s.GemId)).Select(s => catalog.Gems[s.GemId].Id).ToList();
            skills.Add(supportIds.Count == 0
                ? new Dictionary<string, object> { ["id"] = gem.Id }
                : new Dictionary<string, object> { ["id"] = gem.Id, ["support_skills"] = supportIds });
        }
        var payload = new Dictionary<string, object?>
        {
            ["name"] = string.IsNullOrWhiteSpace(doc.Name) ? "PoeBuilder" : doc.Name,
            ["author"] = "PoeBuilder",
            ["ascendancy"] = ascendancy,
            ["passives"] = passives,
            ["skills"] = skills
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }


    // ------------------------------ PoB items (equipment, jewels, uniques) ------------------------------
    internal sealed record PobItemsResult(EquipmentPlan Plan, int Matched, int SkippedLines, int Jewels, int Uniques,
        string[] AllocatedNames, IReadOnlyDictionary<int, Guid> PobIdMap);

    private static PobItemsResult ParsePobItems(XElement? itemsEl, GameCatalog catalog)
    {
        var plan = new EquipmentPlan();
        if (itemsEl is null) return new PobItemsResult(plan, 0, 0, 0, 0, [], new Dictionary<int, Guid>());
        var texts = new Dictionary<string, string>();
        foreach (var it in itemsEl.Elements("Item"))
        {
            var iid = (string?)it.Attribute("id");
            if (iid is not null) texts[iid] = it.Value;
        }
        var matcher = ModLineMatcher.Build(catalog);
        int matched = 0, skippedLines = 0, jewels = 0, uniquesCount = 0;
        var slots = new Dictionary<string, Guid>();
        var items = new List<GearItem>();
        var pobIdMap = new Dictionary<int, Guid>();  // PoB numeric item id -> our GearItem id
        var itemAllocates = new List<string[]>();    // per imported item, "Allocates X" names
        // PoB2 nests slots inside <ItemSet>; group by name so extra sets never win over the first.
        foreach (var slotEl in itemsEl.Descendants("Slot").GroupBy(x => ((string?)x.Attribute("name")) ?? "").Select(g => g.First()))
        {
            var slotName = ((string?)slotEl.Attribute("name")) ?? "";
            var itemId = (string?)slotEl.Attribute("itemId");
            if (itemId is null || ((string?)slotEl.Attribute("inactive")) == "true") continue;
            if (!texts.TryGetValue(itemId, out var text)) continue;
            var parsed = ParsePobItemText(text, catalog, matcher, ref skippedLines);
            if (parsed is null) { skippedLines++; continue; }
            var (item, isJewel, isUnique, allocs) = parsed.Value;
            items.Add(item);
            if (int.TryParse(itemId, out int pobNum)) pobIdMap.TryAdd(pobNum, item.Id);
            if (allocs.Length > 0) itemAllocates.Add(allocs);
            if (isUnique) uniquesCount++;
            if (isJewel) { jewels++; continue; } // jewels are placed into tree sockets via <Socket itemId nodeId>
            var slot = MapPobSlot(slotName);
            if (slot is not null && item.BaseId.Length > 0) { slots[slot] = item.Id; matched++; }
        }
        // Jewels live outside the slot list in PoB2 exports: import every unreferenced jewel item.
        var referenced = new HashSet<string>(slots.Values.Select(v => v.ToString()));
        referenced.UnionWith(items.Select(i => i.Id.ToString()));
        foreach (var (iid, text) in texts)
        {
            if (referenced.Contains(iid)) continue;
            var parsed = ParsePobItemText(text, catalog, matcher, ref skippedLines);
            if (parsed is null) continue;
            var (item, isJewel, isUnique, allocs) = parsed.Value;
            if (!isJewel) continue; // unslotted non-jewels belong to other weapon sets / stash: out of scope
            items.Add(item);
            if (int.TryParse(iid, out int pobNum2)) pobIdMap.TryAdd(pobNum2, item.Id);
            if (allocs.Length > 0) itemAllocates.Add(allocs);
            jewels++;
            if (isUnique) uniquesCount++;
        }
        plan = plan with { Items = [.. items], Slots = slots };
        return new PobItemsResult(plan, matched, skippedLines, jewels, uniquesCount, itemAllocates.SelectMany(a => a).ToArray(), pobIdMap);
    }

    /// <summary>Merges tree jewel sockets and jewel-granted ("Allocates X") nodes into the tree plan.
    /// Jewel-granted notables are allocated without path cost and are exempt from the connectivity
    /// rule — exactly how the game treats them. Sockets are taken from PoB's <Socket itemId nodeId>.</summary>
    internal static (PassiveTreePlan Tree, int SocketsPlaced, int NodesGranted, int GrantsSkipped) ApplyPobJewels(
        PassiveTreePlan tree, PobItemsResult items, TreeCatalog treeCatalog, IEnumerable<XElement> socketElements)
    {
        var free = new List<int>();
        int skipped = 0;
        foreach (var name in items.AllocatedNames.Distinct())
        {
            var hits = treeCatalog.Nodes.Values.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && n.IsSupported && !n.IsJewel && !n.IsAscendancy && !n.IsStart).ToList();
            if (hits.Count == 1) free.Add(hits[0].Id);
            else skipped++;
        }
        var socketed = new Dictionary<int, Guid>();
        foreach (var se in socketElements)
        {
            var itemIdAttr = (string?)se.Attribute("itemId");
            var nodeIdAttr = (string?)se.Attribute("nodeId");
            if (itemIdAttr is null || nodeIdAttr is null || !int.TryParse(nodeIdAttr, out int nodeId)) continue;
            if (!treeCatalog.Nodes.TryGetValue(nodeId, out var node) || !node.IsJewel) { skipped++; continue; }
            if (!int.TryParse(itemIdAttr, out int pobItemId) || !items.PobIdMap.TryGetValue(pobItemId, out var guid)) { skipped++; continue; }
            socketed[nodeId] = guid;
        }
        var extra = free.Concat(socketed.Keys).Where(id => !tree.AllocatedNodes.Contains(id)).ToHashSet();
        var merged = tree with
        {
            AllocatedNodes = tree.AllocatedNodes.Concat(extra).Order().ToArray(),
            JewelAllocatedNodes = tree.JewelAllocatedNodes.Concat(free).Distinct().Order().ToArray(),
            Jewels = socketed
        };
        return (merged, socketed.Count, free.Distinct().Count(), skipped);
    }

    private static string? MapPobSlot(string pobName)
    {
        string key = pobName.Trim().ToLowerInvariant().Replace("  ", " ");
        if (key.Contains("jewel")) return null;
        if (key.Contains("life flask")) return "LifeFlask";
        if (key.Contains("mana flask")) return "ManaFlask";
        if (key.Contains("charm")) { var digits = new string(key.Where(char.IsDigit).ToArray()); return "Charm" + (digits.Length > 0 ? digits[0] : "1"); }
        return key switch
        {
            "helmet" => "Helmet",
            "body armour" or "body" => "Body",
            "gloves" => "Gloves",
            "boots" => "Boots",
            "belt" => "Belt",
            "amulet" => "Amulet",
            "ring 1" or "ring1" => "Ring1",
            "ring 2" or "ring2" => "Ring2",
            "weapon 1" => "Main1",
            "weapon 1 swap" => "Off1",
            "weapon 2" => "Main2",
            "weapon 2 swap" => "Off2",
            _ => null
        };
    }

    private static readonly HashSet<string> PobJewelBases = new(StringComparer.OrdinalIgnoreCase)
    { "Diamond", "Ruby", "Sapphire", "Emerald" };

    private static (GearItem Item, bool IsJewel, bool IsUnique, string[] Allocates)? ParsePobItemText(string text, GameCatalog catalog, ModLineMatcher matcher, ref int skippedLines)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return null;
        string rarity = "rare";
        int idx = 0;
        if (lines[0].StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase))
        {
            var r = lines[0]["Rarity:".Length..].Trim().ToLowerInvariant();
            rarity = r switch { "normal" => "normal", "magic" => "magic", "rare" => "rare", "unique" => "unique", _ => "rare" };
            idx = 1;
        }
        string? itemName = null, baseName = null;
        if (idx < lines.Length)
        {
            var first = lines[idx++];
            if (rarity == "normal") baseName = first; // a normal item has just the base line
            else { itemName = first; if (idx < lines.Length) baseName = lines[idx++]; }
        }
        // PoE2 jewel bases have no entry in the pinned base list; recognize them by base name,
        // plus any unique whose pinned identity says item class Jewel (e.g. Megalomaniac).
        var allocates = new List<string>();
        bool isJewel = (baseName is not null && PobJewelBases.Contains(baseName))
            || (itemName is not null && catalog.Uniques.TryGetValue(itemName, out var uid) && uid.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase));
        int itemLevel = 80, quality = 0;
        var modLines = new List<string>();
        int implicitsPending = 0;
        foreach (var raw in lines.Skip(idx))
        {
            var line = System.Text.RegularExpressions.Regex.Replace(raw, @"^\{[^}]*\}", "").Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Implicits:", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                _ = int.TryParse(digits, out implicitsPending);
                continue; // base implicits already come from the pinned base data
            }
            // "Allocates X" (unique jewels) must be captured even when listed after "Implicits: N".
            if (line.StartsWith("Allocates ", StringComparison.OrdinalIgnoreCase)) { allocates.Add(line["Allocates ".Length..].Trim()); continue; }
            if (implicitsPending > 0) { implicitsPending--; continue; }
            if (line.StartsWith("Unique ID:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Item Level:", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out int il)) itemLevel = Math.Clamp(il, 1, 100);
                continue;
            }
            if (line.StartsWith("Level:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Requires ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Quality", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out int q)) quality = Math.Clamp(q, 0, 20);
                continue;
            }
            if (line == "Corrupted") continue;
            if (line.StartsWith("Allocates ", StringComparison.OrdinalIgnoreCase)) { allocates.Add(line["Allocates ".Length..].Trim()); continue; }
            if (line.StartsWith("Variant:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Source:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Upgraded", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Rune:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Second Modifier:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Has ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Limited to:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Sockets:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Charm Slots:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("LevelReq:", StringComparison.OrdinalIgnoreCase)) continue;
            // Base stat readouts ("Energy Shield: 243", "Attack Time: 0.65", ...) are label lines, not affixes.
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[A-Z][A-Za-z ]{0,30}: ")) continue;
            modLines.Add(line);
        }
        string baseId = "";
        if (baseName is not null)
        {
            var norm = baseName.Replace("'", "").Replace("’", "");
            var b = catalog.Bases.Values.FirstOrDefault(x => x.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                ?? catalog.Bases.Values.FirstOrDefault(x => x.Name.Replace("'", "").Replace("’", "").Equals(norm, StringComparison.OrdinalIgnoreCase));
            if (b is not null) baseId = b.Id;
        }
        var rolls = new List<ModRoll>();
        if (rarity != "unique")
        {
            foreach (var line in modLines)
            {
                var match = matcher.Match(line);
                if (match is null) { skippedLines++; continue; }
                if (rolls.Any(r => r.Id == match.Value.roll.Id)) { skippedLines++; continue; }
                rolls.Add(match.Value.roll);
            }
            // Without a pinned base only jewels can be validated (they use the jewel affix pool).
            if (baseId.Length == 0 && !isJewel) return null;
            if (baseId.Length == 0 && rarity == "normal") return null;
            if (baseId.Length == 0 && rarity != "unique" && rolls.Count == 0) return null; // shell
        }
        // Uniques: the pinned catalog has no unique modifiers, so the full user-provided text is kept verbatim.
        string notes = rarity == "unique" ? (text.Length > 9999 ? text[..9999] : text) : "";
        var item = new GearItem
        {
            BaseId = baseId,
            Name = itemName ?? baseName ?? "",
            Rarity = rarity,
            ItemLevel = itemLevel,
            Quality = quality,
            Mods = [.. rolls],
            Notes = notes
        };
        return (item, isJewel, rarity == "unique", allocates.ToArray());
    }

    /// <summary>Reverse translation of an English affix line to a pinned mod + integer rolls.
    /// Templates and item lines are normalized to words plus '#' value slots; a match requires
    /// identical shapes and the same number of value slots as the mod has stats.</summary>
    internal sealed class ModLineMatcher
    {
        private static readonly System.Text.RegularExpressions.Regex RangeOrNumber =
            new(@"\(?\s*-?\d+(?:\.\d+)?\s*(?:-\s*-?\d+(?:\.\d+)?)?\s*\)?", System.Text.RegularExpressions.RegexOptions.Compiled);
        private readonly List<(string Template, ItemMod Mod)> _templates;
        private ModLineMatcher(List<(string, ItemMod)> templates) => _templates = templates;

        public static ModLineMatcher Build(GameCatalog catalog)
        {
            var list = new List<(string, ItemMod)>();
            foreach (var m in catalog.Mods.Values)
            {
                if (m.Text.Length == 0 || m.Stats.Length == 0 || m.Stats.Length > 4) continue;
                var t = Normalize(m.Text);
                if (!t.Contains('#')) continue;
                list.Add((t, m));
            }
            foreach (var m in catalog.JewelMods)
            {
                if (m.Text.Length == 0 || m.Stats.Length == 0 || m.Stats.Length > 4) continue;
                var t = Normalize(m.Text);
                if (!t.Contains('#')) continue;
                list.Add((t, m));
            }
            return new(list);
        }

        public (ModRoll roll, ItemMod mod)? Match(string line)
        {
            var norm = Normalize(line);
            int slots = norm.Count(c => c == '#');
            if (slots == 0 || slots > 4) return null;
            (string Template, ItemMod Mod)? fallback = null;
            foreach (var (template, mod) in _templates)
            {
                if (template != norm || mod.Stats.Length != slots) continue;
                var numbers = System.Text.RegularExpressions.Regex.Matches(line, @"-?\d+(?:\.\d+)?")
                    .Select(m2 => decimal.TryParse(m2.Value, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Round(v, 0) : 0m)
                    .ToArray();
                if (numbers.Length != mod.Stats.Length) return null;
                // Prefer a template whose pinned range actually contains the rolled values.
                bool fits = numbers.Zip(mod.Stats).All(p => p.First >= p.Second.Min && p.First <= p.Second.Max);
                if (fits) return (new ModRoll { Id = mod.Id, Values = numbers }, mod);
                fallback ??= (template, mod);
            }
            // Text matched but the roll is outside every pinned range: keep the observed values,
            // validation accepts them for jewels and reports honestly elsewhere.
            return fallback is null ? null : (new ModRoll { Id = fallback.Value.Mod.Id, Values = System.Text.RegularExpressions.Regex.Matches(line, @"-?\d+(?:\.\d+)?")
                    .Select(m2 => decimal.TryParse(m2.Value, System.Globalization.CultureInfo.InvariantCulture, out var v2) ? Math.Round(v2, 0) : 0m)
                    .ToArray() }, fallback.Value.Mod);
        }

        internal static string Normalize(string s)
        {
            var lowered = s.ToLowerInvariant().Replace('’', '\'').Replace('–', '-').Replace('—', '-');
            lowered = RangeOrNumber.Replace(lowered, "#");
            var sb = new StringBuilder();
            foreach (var c in lowered)
            {
                if (char.IsWhiteSpace(c)) { if (sb.Length == 0 || sb[^1] == ' ') continue; sb.Append(' '); }
                else if (char.IsLetter(c) || c == '%' || c == '#') sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }

    // ------------------------------ codec ------------------------------
    /// <summary>PoB envelope: URL-safe base64 → zlib frame (2-byte header + deflate + adler32).</summary>
    public static byte[] DecodePobEnvelope(string code)
    {
        var base64 = code.Trim().Replace('-', '+').Replace('_', '/').Replace(" ", "");
        base64 = (base64.Length % 4) switch { 2 => base64 + "==", 3 => base64 + "=", 0 => base64, _ => throw new FormatException("длина кода не кратна 4") };
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length > 2) { try { return Inflate(bytes, 2); } catch (InvalidDataException) { } }
        return Inflate(bytes, 0);
    }

    /// <summary>Reverse of DecodePobEnvelope; used to round-trip the codec in tests.</summary>
    public static string EncodePobEnvelope(string xml)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        using var outp = new MemoryStream();
        outp.WriteByte(0x78); outp.WriteByte(0x9C);
        using (var deflate = new DeflateStream(outp, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(payload);
        uint a = 1, b = 0;
        foreach (var t in payload) { a = (a + t) % 65521; b = (b + a) % 65521; }
        uint adler = (b << 16) | a;
        outp.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
        return Convert.ToBase64String(outp.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Inflate(byte[] data, int offset)
    {
        using var source = new MemoryStream(data, offset, data.Length - offset);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        if (output.Length == 0) throw new InvalidDataException("пустой поток");
        return output.ToArray();
    }

    // ------------------------------ helpers ------------------------------

    /// <summary>Foreign formats list nodes in arbitrary order, but Allocate requires a connected path.
    /// Chain the nodes from the graph start, always picking one adjacent to what is already allocated;
    /// anything that stays disconnected goes to the honest unknown list.</summary>
    private static List<int> OrderForAllocation(TreeCatalog graph, List<int> ids, int? startId)
    {
        var pending = new HashSet<int>(ids);
        var ordered = new List<int>();
        var frontier = new List<int>();
        if (startId is int seed && graph.Nodes.ContainsKey(seed))
        {
            frontier.Add(seed);
            if (pending.Remove(seed)) ordered.Add(seed); // the start is a path seed; allocate only if the file lists it
        }
        while (pending.Count > 0)
        {
            int? next = null;
            foreach (var allocated in frontier)
            {
                if (!graph.Neighbors.TryGetValue(allocated, out var adjacent)) continue;
                foreach (var candidate in adjacent)
                    if (pending.Contains(candidate)) { next = candidate; break; }
                if (next is not null) break;
            }
            if (next is null) break;
            ordered.Add(next.Value);
            frontier.Add(next.Value);
            pending.Remove(next.Value);
        }
        return ordered;
    }

    private static int? GraphStart(TreeCatalog graph) =>
        graph.Nodes.Values.FirstOrDefault(n => n.IsStart || n.IsAscendancyStart)?.Id;


    /// <summary>Shortest path inside one graph via BFS; empty when unreachable.</summary>
    private static List<int> PathInGraph(TreeCatalog graph, int from, int to)
    {
        if (!graph.Nodes.ContainsKey(from) || !graph.Nodes.ContainsKey(to)) return [];
        var prev = new Dictionary<int, int> { [from] = from };
        var queue = new Queue<int>(); queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == to) break;
            if (!graph.Neighbors.TryGetValue(current, out var adjacent)) continue;
            foreach (var next in adjacent)
                if (!prev.ContainsKey(next)) { prev[next] = current; queue.Enqueue(next); }
        }
        if (!prev.ContainsKey(to)) return [];
        var path = new List<int>();
        var node = to;
        while (node != from) { path.Add(node); node = prev[node]; }
        path.Add(from);
        path.Reverse();
        return path;
    }

    /// <summary>Allocates ascendancy picks as a connected path: PoB2 lists only the picked notables,
    /// while the game validates the full route — so connecting nodes are allocated too and counted as implied.</summary>
    private static (int Matched, int Implied, int Unsupported) ChainAscendancy(PassiveTreeEngine engine, AscendancyDefinition definition,
        ref AscendancyPlan plan, IEnumerable<int> requested, List<string> unknownIds)
    {
        int matched = 0, implied = 0, unsupported = 0;
        var current = plan;
        // The engine owns the ascendancy graph start implicitly (Validate forbids listing it);
        // BFS paths run from that implicit start and only the intermediate nodes are allocated.
        int startId;
        try { startId = engine.Start(definition.ToGraphPlan(current)); }
        catch (TreeRuleException) { return (0, 0, 0); }
        var allocated = new HashSet<int>(current.AllocatedNodes) { startId };
        foreach (var nodeId in requested)
        {
            if (allocated.Contains(nodeId)) { matched++; continue; }
            var path = PathInGraph(definition.Graph, startId, nodeId);
            if (path.Count == 0) { unknownIds.Add(nodeId.ToString()); continue; }
            bool ok = true;
            foreach (var step in path)
            {
                if (allocated.Contains(step)) continue;
                try
                {
                    var graphPlan = engine.Allocate(definition.ToGraphPlan(current), step, AttributeChoice);
                    current = current with { AllocatedNodes = [.. graphPlan.AllocatedNodes] };
                    allocated.Add(step);
                    implied++;
                }
                catch (TreeRuleException) { ok = false; break; }
            }
            if (ok) { matched++; continue; }
            unsupported++;
            var failedNode = definition.Graph.Nodes.GetValueOrDefault(nodeId);
            unknownIds.Add(failedNode is null ? nodeId.ToString()
                : failedNode.StableId + " «" + failedNode.Name + "»");
        }
        plan = current;
        return (matched, implied, unsupported);
    }

    private static Dictionary<string, int> StableMap(TreeCatalog tree) => tree.Nodes.Values
        .Where(n => !string.IsNullOrEmpty(n.StableId)).GroupBy(n => n.StableId).ToDictionary(g => g.Key, g => g.First().Id);

    private static (int ClassIndex, string ClassName, AscendancyDefinition? Definition) ResolveAscendancy(string text, TreeCatalog tree)
    {
        if (text.Length > 0)
        {
            var definition = tree.Ascendancies.FirstOrDefault(a => a.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
                ?? tree.Ascendancies.FirstOrDefault(a => a.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (definition is not null)
            {
                var cls = tree.Classes.FirstOrDefault(c => c.Index == definition.ClassIndex);
                return (definition.ClassIndex, cls?.Name ?? "", definition);
            }
        }
        var first = tree.Classes.FirstOrDefault();
        return (first?.Index ?? 0, first?.Name ?? "", null);
    }

    private static IEnumerable<string> ReadIds(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) yield return item.GetString() ?? "";
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)) yield return id.GetString() ?? "";
        }
    }

    private static IEnumerable<JsonElement> ReadElements(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out _)) yield return item;
    }
}
