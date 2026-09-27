using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PoeBuilder.Core.Calculation;
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
        // a node whose path parent comes later in the list succeeds on a retry. Imported lists are
        // trimmed to manually-picked nodes, so shortest-path reconstruction is required; the pinned
        // graph reproduces the same edge choices the game builds from that list.
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
        // Build Planner v1 ships no level or equipment; be explicit so the user does not mistake a
        // level-1 baseline for their real numbers (the full build comes from a PoB share code).
        int buildLevel = 1;
        if (root.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number)
            buildLevel = Math.Clamp(levelEl.GetInt32(), 1, 100);
        else
            notes.Add("в источнике нет уровня персонажа — взят 1 (для полного расчёта нужен код PoB)");

        var build = BuildDocument.Create(name) with
        {
            CharacterClass = className,
            Level = buildLevel,
            Tree = plan,
            Skills = skillPlan,
            GameVersion = "0.5.5c",
            Notes = "Импорт (JSON Build Planner v1) · " + DateTime.Now.ToString("yyyy-MM-dd")
        };
        notes.Add("формат Build Planner v1 не содержит снаряжение — для полного переноса используйте код Path of Building (кнопка «PoB» на poe.ninja или pobb.in)");
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

        // Items are parsed BEFORE the tree is allocated: a unique jewel that opens another class's
        // starting point ("Split Personality") must be known up front. Otherwise the cross-class
        // cluster is reached by routing a long path across the whole tree, which both invents nodes
        // the build never allocated and draws the wrong path on the tree view.
        // PoB2 keeps tree jewels outside the <Slot> list and references them only from <Socket itemId>,
        // so that reference is what tells a tree jewel apart from an unslotted item of another set.
        var socketElements = root.Descendants("Socket").ToList();
        var socketedJewelIds = new HashSet<string>(socketElements
            .Select(element => (string?)element.Attribute("itemId"))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!), StringComparer.Ordinal);
        var pobItems = ParsePobItems(root.Element("Items"), catalog, socketedJewelIds);
        int alternateSkipped = 0;
        var alternateStartIds = PobAlternateStartIds(pobItems, tree, socketElements, ref alternateSkipped);
        // Radius jewels ("From Nothing": "Passives in radius of Resonance can be Allocated without being
        // connected to your tree") are read BEFORE the allocation, because the nodes they reach must not be
        // routed through the tree: PoB2 reaches them from the jewel, so a path would invent passives the
        // build never took. The set is computed from the same geometry PoB2 precomputes per socket band
        // (Classes/PassiveTree.lua:331-354), against the keystone the jewel names.
        var socketMap = PobSocketMap(pobItems, tree, socketElements, out int socketsSkipped);
        var radiusRules = PobRadiusRules(pobItems, socketMap);

        var engine = new PassiveTreeEngine(tree);
        var plan = new PassiveTreePlan
        {
            DatasetId = tree.DatasetId,
            ClassIndex = definition?.ClassIndex ?? tree.Classes.FirstOrDefault(c => c.Name == className)?.Index ?? 0,
            PointLimit = 0,
            AlternateStartNodes = alternateStartIds
        };
        // The centres of the imported rules, so the deferred nodes can be recognised before either the
        // sockets or the keystone are in the plan.
        var radiusCentres = radiusRules
            .Select(p => (Centre: p.Value.FromKeystone ? engine.KeystoneId(p.Value.KeystoneName) : p.Key, p.Value.RadiusIndex))
            .Where(p => p.Centre != 0)
            .ToArray();
        var radiusCandidates = engine.RadiusAt(radiusCentres);
        int passivesMatched = 0, passivesUnknown = 0;
        // PoB ships integer node ids; the main graph ids and ascendancy graph ids live in one list.
        var allNodes = new List<int>();
        var nodesAttr = (string?)spec?.Attribute("nodes");
        if (!string.IsNullOrWhiteSpace(nodesAttr))
            foreach (var part in nodesAttr.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int parsed)) allNodes.Add(parsed);
        var ascEngine = definition is not null ? new PassiveTreeEngine(definition.Graph) : null;
        var ascPlan = definition is not null ? new AscendancyPlan { Id = definition.Id, PointLimit = 0 } : null;
        // Imported lists are trimmed to the node ids the source stores (clicked notables plus, for
        // PoB2, the nodes granted by unique jewels). PoB2's list is the COMPLETE allocated set, so the
        // import allocates it verbatim: inventing intermediates would reroute the build through
        // passives it never took and draw a wrong path on the tree view.
        // Nodes granted free by unique jewels ("Allocates X") live in the list but are NOT connected
        // main-tree passives: routing them would run a path across other class areas (the game never
        // does). They are skipped here and placed at zero cost by ApplyPobJewels below.
        // Class starts (the character's own and any opened by a unique jewel) are roots, not
        // allocations, exactly like the class start the game never charges for.
        var grantedIds = PobItemGrantedIds(root.Element("Items"), tree);
        int classStart = engine.Start(plan);
        var failed = new List<int>();
        // Nodes a radius jewel reaches are deferred to the second pass: until the socket and the keystone
        // are allocated the rule reaches nothing, and routing them instead would add passives the build
        // never took. PoB2 does the same thing — it computes those dependencies after the whole tree is in.
        // A radius node that also HAS an edge to the allocated set is taken normally: connectivity and the
        // radius are two legal reasons for the same node, and the shorter one wins.
        var deferred = new List<int>();
        foreach (var nodeId in allNodes)
        {
            if (ascEngine is not null && definition!.Graph.Nodes.ContainsKey(nodeId)) continue;
            if (nodeId == classStart || plan.AlternateStartNodes.Contains(nodeId)) { passivesMatched++; continue; }
            if (grantedIds.Contains(nodeId)) { passivesMatched++; continue; }
            try { plan = engine.AllocateVerbatim(plan, nodeId, AttributeChoice); passivesMatched++; }
            catch (TreeRuleException) when (radiusCandidates.Contains(nodeId)) { deferred.Add(nodeId); }
            catch (TreeRuleException) { failed.Add(nodeId); }
        }
        bool progressed = true;
        while (progressed && failed.Count > 0)
        {
            progressed = false;
            var stillFailed = new List<int>();
            foreach (var nodeId in failed)
            {
                try { plan = engine.AllocateVerbatim(plan, nodeId, AttributeChoice); passivesMatched++; progressed = true; }
                catch (TreeRuleException) { stillFailed.Add(nodeId); }
            }
            failed = stillFailed;
        }
        // A node that is still unreachable means the source list is not self-connected under our graph
        // (older tree snapshot, or a mechanic we do not model). It is connected with a shortest path and
        // REPORTED, never silently rerouted — this pass runs AFTER the jewels are placed, because the
        // remaining candidates are exactly the ones a radius rule could not account for.
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

        // PoB2 stores weapon-set allocations as child elements of the spec (<WeaponSet1 nodes="…"/>,
        // Classes/PassiveSpec.lua:272-277). A node allocated for one set only contributes while that set
        // is active, so the mode travels with the build and the calculator applies it. The modes are applied
        // AFTER the radius pass, because a node a radius jewel reaches is allocated there and an entry for
        // it would otherwise look like a node the tree does not have.
        var weaponSets = new Dictionary<int, int>();
        if (spec is not null)
            foreach (var setElement in spec.Elements())
            {
                string setKey = setElement.Name.LocalName;
                if (setKey.Length != 10 || !setKey.StartsWith("WeaponSet", StringComparison.Ordinal) || setKey[9] is not ('1' or '2')) continue;
                string? ids = (string?)setElement.Attribute("nodes");
                if (string.IsNullOrWhiteSpace(ids)) continue;
                foreach (var part in ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(part, out int setNode)) weaponSets[setNode] = setKey[9] - '0';
            }

        // ---- skills: PoB stores <SkillSet><Skill><Gem nameSpec="..." skillId="..." level=".."/></Skill></SkillSet> ----
        var groups = new List<SkillGroup>();
        int skillsMatched = 0, supportsMatched = 0, gemsUnknown = 0, corruptedGems = 0;
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
                    // A corrupted gem keeps its level bonus in its own attribute (corrupted="true"
                    // corruptLevel="1"); PoB2 adds it to the gem's level, so the effective level is folded
                    // in here (Arc in the reference build: 20 + 1 = 21 before the global +10).
                    if (int.TryParse((string?)gemEl.Attribute("corruptLevel"), out int corruptLevel) && corruptLevel > 0)
                    {
                        gemLevel = Math.Clamp(gemLevel + corruptLevel, 1, 40);
                        corruptedGems++;
                    }
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
        if (corruptedGems > 0) notes.Add("осквернённых камней с бонусом к уровню: " + corruptedGems);

        if (gemsUnknown > 0) notes.Add("часть камней переименовывалась между версиями игры — нераспознанные показаны в списке");
        // ---- items: PoB carries full gear text; bases and affix lines are matched against the pinned catalog ----
        // (pobItems was already parsed above, before the tree, so alternate class starts were known.)
        var (treeWithJewels, socketsPlaced, nodesGranted, grantsSkipped) =
            ApplyPobJewels(plan, pobItems, tree, socketElements, alternateSkipped, socketMap, radiusRules);
        // Second pass: with the sockets and their rules in the plan, the deferred nodes are reachable with
        // no edge at all (PoB2 spends the node's own point, nothing else). A node the rule does NOT reach is
        // still reported and only then connected by a path, never silently dropped.
        var radiusAllocated = 0;
        var pending = new List<int>(deferred);
        bool radiusProgress = true;
        while (radiusProgress && pending.Count > 0)
        {
            radiusProgress = false;
            var stillPending = new List<int>();
            foreach (var nodeId in pending)
            {
                // A node BEYOND the radius (an ordinary neighbour of a radius node) only becomes takeable
                // once its neighbour is in, so this is a fixed-point loop, not a single pass.
                try { treeWithJewels = engine.AllocateVerbatim(treeWithJewels, nodeId, AttributeChoice); passivesMatched++; radiusProgress = true; }
                catch (TreeRuleException) { stillPending.Add(nodeId); }
            }
            radiusAllocated += pending.Count - stillPending.Count;
            pending = stillPending;
        }
        failed.AddRange(pending);
        foreach (var nodeId in failed)
        {
            try { treeWithJewels = engine.Allocate(treeWithJewels, nodeId, AttributeChoice); passivesMatched++; }
            catch (TreeRuleException) { unknown.Add("node " + nodeId); passivesUnknown++; }
        }
        if (radiusAllocated > 0)
            notes.Add("радиус-самоцветы («From Nothing»/«Intuitive Leap») дают взять " + radiusAllocated +
                " узлов(а) без связи с деревом — как в игре, путь к ним не строится");
        // Weapon-set modes, now that every allocated node is known (see the collection above).
        if (weaponSets.Count > 0)
        {
            // Only ids the plan actually allocated: an entry for a node unknown to the pinned tree must
            // not enter the plan, or its structure validation rejects the whole build.
            foreach (var unknownSetNode in weaponSets.Keys.Where(id => !treeWithJewels.AllocatedNodes.Contains(id)).ToList())
                weaponSets.Remove(unknownSetNode);
            treeWithJewels = treeWithJewels with { WeaponSetNodes = weaponSets };
            notes.Add("ноды, привязанные к набору оружия: " + weaponSets.Count(entry => entry.Value == 1) + " на набор 1 и " +
                weaponSets.Count(entry => entry.Value == 2) + " на набор 2 — в расчёт идут только ноды активного набора");
        }
        // PoE2 generic attribute nodes ("+5 to any Attribute") remember which attribute the player
        // picked. PoB2 exports that choice as
        //   <Overrides><AttributeOverride strNodes=".." dexNodes=".." intNodes=".."/></Overrides>
        // Without it every such node silently defaults to Strength, which inflates Strength, starves
        // Intelligence and drags Life/Mana along with it. Applied last so jewel-granted attribute
        // nodes are covered too.
        treeWithJewels = ApplyPobAttributeOverride(treeWithJewels, spec, tree, ref notes);

        var skillPlan = new SkillPlan { Groups = [.. groups] };
        // Quest rewards and the elemental resistance penalty are part of PoB2's config; both are
        // resolved here so the imported build carries the same baseline the reference shows.
        var (progressStage, penalty, penaltyExplicit) = PobProgressStage(root);
        var questRewards = ResolvePobQuestRewards(root, catalog.QuestRewards);
        var build = BuildDocument.Create(string.IsNullOrWhiteSpace(pobClassName) ? "PoB импорт" : "PoB · " + pobClassName) with
        {
            CharacterClass = className,
            Level = level,
            Tree = treeWithJewels,
            Skills = skillPlan,
            Equipment = pobItems.Plan,
            GameVersion = "0.5.5c",
            ProgressStage = progressStage,
            QuestRewards = questRewards,
            LowLife = PobLowLife(root),
            Conditions = PobConditions(root),
            Notes = "Импорт из кода Path of Building · " + DateTime.Now.ToString("yyyy-MM-dd")
        };
        if (questRewards.Length > 0) notes.Add("квестовые награды перенесены из конфига PoB: " + questRewards.Length);
        if (penaltyExplicit && penalty != -60m)
            notes.Add("штраф резистов в коде PoB = " + penalty + "%: наша модель знает только «стартер» (0) и «эндгейм» (−60), выбрана ближайшая");
        if (socketsPlaced > 0) notes.Add("самоцветы вставлены в гнёзда дерева как в коде PoB: " + socketsPlaced);
        if (nodesGranted > 0) notes.Add("узлы от уникальных самоцветов («Allocates …») аллоцированы бесплатно, без пути: " + nodesGranted);
        if (treeWithJewels.AlternateStartNodes.Length > 0)
            notes.Add("уникальный(-ые) самоцвет(-ы) открыл(и) стартовую зону другого класса: " + string.Join(", ", treeWithJewels.AlternateStartNodes));
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
        string[] AllocatedNames, IReadOnlyDictionary<int, Guid> PobIdMap, string[] AlternateClassStarts,
        IReadOnlyDictionary<Guid, string> ItemTexts);

    private static PobItemsResult ParsePobItems(XElement? itemsEl, GameCatalog catalog, IReadOnlySet<string> socketedJewelIds)
    {
        var plan = new EquipmentPlan();
        if (itemsEl is null) return new PobItemsResult(plan, 0, 0, 0, 0, [], new Dictionary<int, Guid>(), [], new Dictionary<Guid, string>());
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
        var alternateClassStarts = new List<string>();
        var itemTexts = new Dictionary<Guid, string>();  // our GearItem id -> PoB item text
        var referencedPobItemIds = new HashSet<string>(StringComparer.Ordinal);

        // PoB2 stores slots inside ItemSet and Items/@activeItemSet identifies the selected set.
        // Older exports may place Slot elements directly under Items, so retain that fallback.
        var itemSets = itemsEl.Elements("ItemSet").ToArray();
        string? activeItemSetId = (string?)itemsEl.Attribute("activeItemSet");
        var selectedItemSet = itemSets.FirstOrDefault(set =>
            string.Equals((string?)set.Attribute("id"), activeItemSetId, StringComparison.OrdinalIgnoreCase))
            ?? itemSets.FirstOrDefault();
        var slotElements = selectedItemSet is null ? itemsEl.Elements("Slot") : selectedItemSet.Descendants("Slot");
        foreach (var slotEl in slotElements.GroupBy(x => ((string?)x.Attribute("name")) ?? "").Select(g => g.First()))
        {
            var slotName = ((string?)slotEl.Attribute("name")) ?? "";
            var itemId = (string?)slotEl.Attribute("itemId");
            if (itemId is null || ((string?)slotEl.Attribute("inactive")) == "true") continue;
            referencedPobItemIds.Add(itemId);
            if (!texts.TryGetValue(itemId, out var text)) continue;
            var parsed = ParsePobItemText(text, catalog, matcher, ref skippedLines);
            if (parsed is null) { skippedLines++; continue; }
            var (item, isJewel, isUnique, allocs) = parsed.Value;
            items.Add(item);
            itemTexts[item.Id] = text;
            if (int.TryParse(itemId, out int pobNum)) pobIdMap.TryAdd(pobNum, item.Id);
            if (allocs.Length > 0) itemAllocates.Add(allocs);
            if (AlternateClassStart(text) is string altType) alternateClassStarts.Add(altType);
            if (isUnique) uniquesCount++;
            if (isJewel) { jewels++; continue; } // jewels are placed into tree sockets via <Socket itemId nodeId>
            var slot = MapPobSlot(slotName);
            if (slot is not null && item.BaseId.Length > 0) { slots[slot] = item.Id; matched++; }
        }
        // Jewels live outside the selected slot list in PoB2 exports: import every unreferenced jewel item.
        foreach (var (iid, text) in texts)
        {
            if (referencedPobItemIds.Contains(iid)) continue;
            // A <Socket itemId> reference is the authoritative "this item is a tree jewel" signal: PoE2's
            // newer jewel bases (Time-Lost Sapphire and friends) are not in the pinned base table and do
            // not always appear in the jewel base-name list either.
            bool socketedJewel = socketedJewelIds.Contains(iid);
            var parsed = ParsePobItemText(text, catalog, matcher, ref skippedLines, socketedJewel);
            if (parsed is null) continue;
            var (item, isJewel, isUnique, allocs) = parsed.Value;
            if (!isJewel) continue; // unslotted non-jewels belong to other weapon sets / stash: out of scope
            items.Add(item);
            itemTexts[item.Id] = text;
            if (int.TryParse(iid, out int pobNum2)) pobIdMap.TryAdd(pobNum2, item.Id);
            if (allocs.Length > 0) itemAllocates.Add(allocs);
            if (AlternateClassStart(text) is string altType2) alternateClassStarts.Add(altType2);
            jewels++;
            if (isUnique) uniquesCount++;
        }
        plan = plan with { Items = [.. items], Slots = slots, WeaponSet = PobWeaponSet(itemsEl, selectedItemSet) };
        return new PobItemsResult(plan, matched, skippedLines, jewels, uniquesCount, itemAllocates.SelectMany(a => a).ToArray(), pobIdMap, alternateClassStarts.Distinct().ToArray(), itemTexts);
    }

    /// <summary>PoB2 unique jewels can open another class's starting area: "Can Allocate Passive Skills
    /// from the {Class}'s starting point". The class name is resolved against the pinned tree's class
    /// list during apply so the exact spelling lives in one place.</summary>
    private static string? AlternateClassStart(string text)
    {
        const string marker = "Can Allocate Passive Skills from the ";
        int startAt = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (startAt < 0) return null;
        int nameStart = startAt + marker.Length;
        int nameEnd = text.IndexOf("'s starting point", nameStart, StringComparison.OrdinalIgnoreCase);
        if (nameEnd <= nameStart) return null;
        return text[nameStart..nameEnd].Trim();
    }

    /// <summary>Merges tree jewel sockets and jewel-granted ("Allocates X") nodes into the tree plan.
    /// Jewel-granted notables are allocated without path cost and are exempt from the connectivity
    /// rule — exactly how the game treats them. Unique jewels that open another class's starting
    /// point ("Can Allocate Passive Skills from the {Class}'s starting point") add that class start
    /// as an alternate root so imported cross-class node clusters stay connected, exactly like PoB2.
    /// Sockets are taken from PoB's <Socket itemId nodeId>.</summary>
    internal static (PassiveTreePlan Tree, int SocketsPlaced, int NodesGranted, int GrantsSkipped) ApplyPobJewels(
        PassiveTreePlan tree, PobItemsResult items, TreeCatalog treeCatalog, IEnumerable<XElement> socketElements,
        int alternateSkipped, IReadOnlyDictionary<int, Guid> socketMap, IReadOnlyDictionary<int, RadiusAllocationRule> radiusRules)
    {
        var free = new List<int>();
        int skipped = 0;
        foreach (var name in items.AllocatedNames.Distinct())
        {
            // "Allocates X" also covers anoints and enchant grants: the amulet's "Allocates Paragon"
            // hands over the Delirium node "+5 to all Attributes / +5% to Quality of all Skills", which
            // the tree data marks anoint-only (no edges). It is granted free, so it needs no path.
            var hits = treeCatalog.Nodes.Values.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && n.CanBeGranted && !n.IsJewel).ToList();
            if (hits.Count == 1) free.Add(hits[0].Id);
            else skipped++;
        }
        var socketed = new Dictionary<int, Guid>(socketMap);
        var alternate = new List<int>();
        // Alternate class starts are resolved from the socketed jewels' own texts as well as from the
        // first pass, so a jewel that PoB2 stores only inside <Socket> (never inside <Slot>) still
        // opens its class start. Without it the tree would draw a path from the class start across
        // the whole tree to reach those clusters — the "wrong path" an imported build used to show.
        alternate.AddRange(PobAlternateStartIds(items, treeCatalog, socketElements, ref alternateSkipped));
        int skippedTotal = skipped + alternateSkipped;
        var extra = free.Concat(socketed.Keys).Where(id => !tree.AllocatedNodes.Contains(id)).ToHashSet();
        // Only a rule whose socket really is allocated can apply — the same law Validate enforces, checked
        // here so a malformed socket reference is reported instead of producing an invalid plan.
        var rules = radiusRules.Where(p => extra.Contains(p.Key) || tree.AllocatedNodes.Contains(p.Key)).ToDictionary();
        skippedTotal += radiusRules.Count - rules.Count;
        var merged = tree with
        {
            AllocatedNodes = tree.AllocatedNodes.Concat(extra).Order().ToArray(),
            JewelAllocatedNodes = tree.JewelAllocatedNodes.Concat(free).Distinct().Order().ToArray(),
            Jewels = socketed,
            RadiusJewels = rules,
            AlternateStartNodes = tree.AlternateStartNodes.Concat(alternate).Distinct().Order().ToArray()
        };
        return (merged, socketed.Count, free.Distinct().Count(), skippedTotal);
    }

    /// <summary>The tree jewel sockets PoB's &lt;Socket itemId nodeId&gt; elements place, as plan sockets
    /// (socket node id → our jewel item id). Shared by the pre-allocation pass and <see cref="ApplyPobJewels"/>
    /// so both read the same map.</summary>
    private static Dictionary<int, Guid> PobSocketMap(PobItemsResult items, TreeCatalog treeCatalog,
        IEnumerable<XElement> socketElements, out int skipped)
    {
        var socketed = new Dictionary<int, Guid>();
        skipped = 0;
        foreach (var se in socketElements)
        {
            var itemIdAttr = (string?)se.Attribute("itemId");
            var nodeIdAttr = (string?)se.Attribute("nodeId");
            if (itemIdAttr is null || nodeIdAttr is null || !int.TryParse(nodeIdAttr, out int nodeId)) continue;
            if (!treeCatalog.Nodes.TryGetValue(nodeId, out var node) || !node.IsJewel) { skipped++; continue; }
            if (!int.TryParse(itemIdAttr, out int pobItemId) || !items.PobIdMap.TryGetValue(pobItemId, out var guid)) { skipped++; continue; }
            socketed[nodeId] = guid;
        }
        return socketed;
    }

    /// <summary>The allocation rules the socketed jewels state ("From Nothing": "Passives in radius of
    /// Resonance can be Allocated without being connected to your tree"; "Intuitive Leap": "Passives in
    /// radius can be allocated without being connected to your tree"). PoB2 reads the line off the item
    /// (Modules/ModParser.lua:5507-5511), so this is a per-socket reading of the jewel's own text.</summary>
    private static Dictionary<int, RadiusAllocationRule> PobRadiusRules(PobItemsResult items, IReadOnlyDictionary<int, Guid> socketMap)
    {
        var rules = new Dictionary<int, RadiusAllocationRule>();
        foreach (var (socket, guid) in socketMap)
        {
            if (!items.ItemTexts.TryGetValue(guid, out var text)) continue;
            if (JewelRadius.AllocationRule(text) is { } rule) rules[socket] = rule;
        }
        return rules;
    }

    /// <summary>Start nodes of the classes opened by imported unique jewels
    /// ("Can Allocate Passive Skills from the {Class}'s starting point"). The class name is resolved
    /// against the pinned tree's class list; an unknown name is counted as skipped, never guessed.
    /// Both the first item pass and every &lt;Socket&gt; jewel are inspected, because PoB2 keeps
    /// socketed jewels outside the &lt;Slot&gt; list.</summary>
    private static int[] PobAlternateStartIds(PobItemsResult items, TreeCatalog tree, IEnumerable<XElement> socketElements, ref int skipped)
    {
        var names = new List<string>(items.AlternateClassStarts);
        foreach (var element in socketElements)
        {
            if (!int.TryParse((string?)element.Attribute("itemId"), out int pobItemId)) continue;
            if (!items.PobIdMap.TryGetValue(pobItemId, out var guid)) continue;
            if (!items.ItemTexts.TryGetValue(guid, out var text)) continue;
            if (AlternateClassStart(text) is string className) names.Add(className);
        }
        var ids = new List<int>();
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ResolveClassStartNode(tree, name) is int startNodeId) ids.Add(startNodeId);
            else skipped++;
        }
        return ids.Distinct().Order().ToArray();
    }

    /// <summary>Resolves a class name from a PoB jewel to its tree start node. PoE2 keeps the legacy
    /// PoE1 start nodes, so several class names share one node (Templar/Druid, Marauder/Warrior,
    /// Duelist/Mercenary …). Playable classes come from <see cref="TreeCatalog.Classes"/>; a legacy
    /// name such as "Templar" is resolved by the start node's own name instead of being dropped,
    /// otherwise the imported cross-class cluster would still be routed across the whole tree.</summary>
    private static int? ResolveClassStartNode(TreeCatalog tree, string className)
    {
        var definition = tree.Classes.FirstOrDefault(c => c.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
        if (definition is not null) return definition.StartNodeId;
        var node = tree.Nodes.Values.FirstOrDefault(n => n.IsStart && n.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
        return node?.Id;
    }

    /// <summary>PoB2's <c>&lt;AttributeOverride strNodes=".." dexNodes=".." intNodes=".."/&gt;</c> lists
    /// every allocated generic attribute node under the attribute the player picked. The pinned tree
    /// exposes those three choices as the skill overrides 26297 (Strength), 14927 (Dexterity) and
    /// 57022 (Intelligence). Nodes the file does not mention keep the game default (Strength).</summary>
    private static PassiveTreePlan ApplyPobAttributeOverride(PassiveTreePlan plan, XElement? spec, TreeCatalog tree, ref List<string> notes)
    {
        var overrideElement = spec?.Element("Overrides")?.Element("AttributeOverride")
            ?? spec?.Element("AttributeOverride");
        if (overrideElement is null) return plan;
        var selections = new Dictionary<int, int>(plan.AttributeSelections);
        int applied = 0;
        foreach (var (attributeName, choice) in new[] { ("strNodes", 26297), ("dexNodes", 14927), ("intNodes", 57022) })
        {
            string? list = (string?)overrideElement.Attribute(attributeName);
            if (string.IsNullOrWhiteSpace(list)) continue;
            foreach (var part in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, out int nodeId)) continue;
                if (!tree.Nodes.TryGetValue(nodeId, out var node) || !node.IsAttribute) continue;
                if (!tree.Variants.ContainsKey(choice)) continue;
                selections[nodeId] = choice;
                applied++;
            }
        }
        if (applied == 0) return plan;
        notes.Add("выбор атрибутов из кода PoB перенесён: " + applied + " узлов «+5 к любому атрибуту»");
        return plan with { AttributeSelections = selections };
    }

    /// <summary>Resolves the quest rewards a PoB2 build actually has, from PoB2's own quest table
    /// (<see cref="QuestRewardIndex"/>, extracted from PathOfBuilding-PoE2-master
    /// src/Data/QuestRewards.lua). PoB2 turns every entry with <c>useConfig = true</c> into a config
    /// checkbox whose default state is true, so a levelled character has every reward; entry with
    /// "Options" (a choice quest) defaults to nothing until the build picks one. The config variable
    /// PoB2 writes into the share code is <c>"quest" + Description + Area + Info</c>, which is the key
    /// used here to match the <c>&lt;Input name="quest…"/&gt;</c> entries.</summary>
    private static string[] ResolvePobQuestRewards(XElement root, QuestRewardIndex rewards)
    {
        var overrides = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var input in root.Descendants("Input"))
        {
            string? name = (string?)input.Attribute("name");
            if (name is null || !name.StartsWith("quest", StringComparison.Ordinal)) continue;
            overrides[name] = input;
        }
        var lines = new List<string>();
        foreach (var reward in rewards.Rewards)
        {
            if (!reward.UseConfig) continue;
            string key = "quest" + reward.Description + reward.Area + reward.Info;
            if (overrides.TryGetValue(key, out var input))
            {
                string? chosen = (string?)input.Attribute("string");
                if (chosen is not null)
                {
                    // A multi-choice quest: "None" means the build picked nothing.
                    if (chosen.Equals("None", StringComparison.Ordinal)) continue;
                    string normalised = string.Join("\n", chosen.Replace("\r", "").Split('\n')
                        .Select(l => l.Trim()).Where(l => l.Length > 0));
                    // Known options and unknown wording alike are stored as written; the calculator
                    // reports anything it cannot model instead of guessing.
                    if (normalised.Length > 0) lines.Add(normalised);
                    continue;
                }
                if ((string?)input.Attribute("boolean") == "false") continue; // checkbox switched off
            }
            // An option quest contributes nothing until the build picks a line.
            if (reward.Stat.Length > 0) lines.Add(reward.Stat);
        }
        // No Distinct(): two different quests can grant the identical reward line (Act 1 Freythorn and
        // Act 3 Azak Bog both give "+30 to Spirit"), and both must count.
        return [.. lines];
    }

    /// <summary>Progress stage of an imported build. PoB2 (<c>Modules/CalcSetup.lua:681-683</c>) uses
    /// <c>env.configInput.resistancePenalty or -60</c>, and <c>ConfigOptions.lua</c> offers
    /// 0 (Act 1) … -60 (Endgame): the default is Endgame. Our document model has two stages, so a
    /// zero penalty maps to "starter" and anything else to "endgame"; the exact value is reported in
    /// the import notes when it is neither.</summary>
    private static (string Stage, decimal Penalty, bool Explicit) PobProgressStage(XElement root)
    {
        var input = root.Descendants("Input").FirstOrDefault(i => (string?)i.Attribute("name") == "resistancePenalty");
        string? raw = (string?)input?.Attribute("number") ?? (string?)input?.Attribute("string");
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal penalty))
            return (penalty == 0 ? "starter" : "endgame", penalty, true);
        return ("endgame", -60m, false);
    }

    /// <summary>Low Life state of an imported build. PoB2 derives it from the unreserved Life
    /// percentage (<c>data.misc.LowPoolThreshold</c> = 35% of maximum Life) and writes that resolved
    /// percentage into the share code as a PlayerStat. Our own reservation model cannot resolve
    /// skill reservations yet, so this is the honest source for the condition; when the code has no
    /// such value the state stays unknown and the calculator derives it from the reservation plan.</summary>
    private static bool? PobLowLife(XElement root)
    {
        foreach (var stat in root.Descendants("PlayerStat"))
        {
            if ((string?)stat.Attribute("stat") != "LifeUnreservedPercent") continue;
            if (decimal.TryParse((string?)stat.Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal percent))
                return percent < 35m;
        }
        return null;
    }

    /// <summary>Active weapon set of an imported build. PoB2 stores it on the item set
    /// (<c>&lt;Items useSecondWeaponSet="true"&gt;</c> / <c>&lt;ItemSet useSecondWeaponSet="true"&gt;</c>),
    /// and the calculator must use that set's weapons — a build played with the swap weapons gets the
    /// swap weapon's damage, base attack time and local modifiers (the Twister reference build switches
    /// to "The Ordained, Grand Spear" that way). 1 is the default set.</summary>
    private static int PobWeaponSet(XElement itemsEl, XElement? itemSet)
    {
        static bool Second(XElement? element) =>
            string.Equals((string?)element?.Attribute("useSecondWeaponSet"), "true", StringComparison.OrdinalIgnoreCase);
        return Second(itemSet) || Second(itemsEl) ? 2 : 1;
    }

    /// <summary>Booleans set in the imported build's PoB2 config (&lt;Config&gt;/&lt;ConfigSet&gt;
    /// inputs). PoB2 writes only the inputs a build changed, so an absent key means "default"; the
    /// mapping below is an explicit, one-to-one reading of PoB2's own variable names.</summary>
    private static BuildConditions PobConditions(XElement root)
    {
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var sets = root.Descendants("ConfigSet").ToArray();
        var inputs = sets.Length > 0
            ? sets.SelectMany(s => s.Elements("Input"))
            : root.Descendants("Config").SelectMany(c => c.Elements("Input"));
        foreach (var input in inputs)
        {
            string? name = (string?)input.Attribute("name");
            if (string.IsNullOrEmpty(name)) continue;
            if ((string?)input.Attribute("boolean") is string boolean)
            {
                flags[name] = boolean.Equals("true", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if ((string?)input.Attribute("number") is string number &&
                decimal.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                numbers[name] = value;
        }
        bool On(string pobName) => flags.GetValueOrDefault(pobName);
        decimal? Num(string pobName) => numbers.TryGetValue(pobName, out var v) ? v : null;
        return new BuildConditions
        {
            Moving = On("conditionMoving"),
            CritRecently = On("conditionCritRecently"),
            BeenHitRecently = On("conditionBeenHitRecently"),
            EnemyChilled = On("conditionEnemyChilled"),
            EnemyIgnited = On("conditionEnemyIgnited"),
            EnemyBleeding = On("conditionEnemyBleeding"),
            EnemyShocked = On("conditionEnemyShocked"),
            EnemyFireExposure = On("conditionEnemyFireExposure"),
            EnemyColdExposure = On("conditionEnemyColdExposure"),
            EnemyLightningExposure = On("conditionEnemyLightningExposure"),
            FlameWallAddedDamage = On("flameWallAddedDamage"),
            ArcLightningInfused = On("arcLightningInfused"),
            EnemyFireResist = Num("enemyFireResist"),
            EnemyColdResist = Num("enemyColdResist"),
            EnemyLightningResist = Num("enemyLightningResist"),
            EnemyChaosResist = Num("enemyChaosResist"),
            EnemyArmour = Num("enemyArmour"),
            EnemyLevel = Num("enemyLevel"),
            EnemyPhysicalDamageReduction = Num("enemyPhysicalDamageReduction")
        };
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
            "weapon 1" or "weapon" => "Main1",
            // PoB2's slot names: "Weapon 1"/"Weapon 2" are the MAIN HAND and OFF HAND of the active
            // weapon set; the "… Swap" pair is the second set (ImportTab.lua slotMap).
            "weapon 2" or "offhand" => "Off1",
            "weapon 1 swap" or "weapon2" => "Main2",
            "weapon 2 swap" or "offhand2" => "Off2",
            _ => null
        };
    }

    private static readonly HashSet<string> PobJewelBases = new(StringComparer.OrdinalIgnoreCase)
    { "Diamond", "Ruby", "Sapphire", "Emerald", "Time-Lost Diamond", "Time-Lost Ruby", "Time-Lost Sapphire", "Time-Lost Emerald" };

    /// <summary>Node ids granted free by unique jewels ("Allocates X" lines in imported item text).
    /// These nodes live in the imported build's node list but are NOT connected main-tree passives;
    /// the importer must skip them during verbatim allocation (ApplyPobJewels places them at zero
    /// cost afterwards).</summary>
    private static HashSet<int> PobItemGrantedIds(XElement? itemsEl, TreeCatalog tree)
    {
        var granted = new HashSet<int>();
        if (itemsEl is null) return granted;
        foreach (var it in itemsEl.Elements("Item"))
        {
            foreach (var line in it.Value.Replace("\r", "").Split('\n'))
            {
                var text = line.Trim();
                if (!text.StartsWith("Allocates ", StringComparison.OrdinalIgnoreCase)) continue;
                string name = text["Allocates ".Length..].Trim();
                var hits = tree.Nodes.Values.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && n.CanBeGranted && !n.IsJewel).ToList();
                if (hits.Count == 1) granted.Add(hits[0].Id);
            }
        }
        return granted;
    }

    private static (GearItem Item, bool IsJewel, bool IsUnique, string[] Allocates)? ParsePobItemText(string text, GameCatalog catalog, ModLineMatcher matcher, ref int skippedLines, bool socketedJewel = false)
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
            || socketedJewel
            || (itemName is not null && catalog.Uniques.TryGetValue(itemName, out var uid) && uid.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase));
        int itemLevel = 80, quality = 0;
        var modLines = new List<string>();
        int implicitsPending = 0;
        foreach (var raw in lines.Skip(idx))
        {
            // PoB item text carries display markup in front of a line ({enchant}, {rune}, {crafted},
            // {fractured}…); every leading tag is stripped so the wording can be matched.
            var line = System.Text.RegularExpressions.Regex.Replace(raw, @"^(?:\{[^}]*\}\s*)+", "").Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Implicits:", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                _ = int.TryParse(digits, out implicitsPending);
                continue; // base implicits already come from the pinned base data
            }
            // PoB2 writes the rune, enchant and implicit lines as one contiguous block and counts all of
            // them in "Implicits: N" (Classes/Item.lua), so every line of the block is consumed here —
            // "Allocates X" included. It is still captured below, because a granted passive is a real
            // effect; it just must not shift the block.
            bool insideImplicits = implicitsPending > 0;
            if (insideImplicits) implicitsPending--;
            // "Allocates X" (unique jewels, anoint enchants) must be captured even when listed after
            // "Implicits: N".
            if (line.StartsWith("Allocates ", StringComparison.OrdinalIgnoreCase)) { allocates.Add(line["Allocates ".Length..].Trim()); continue; }
            if (insideImplicits) continue;
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
        // The pinned base table carries no jewels, so a baseless RARE item can only be a jewel — a magic
        // item with an unresolved base is a flask or charm, whose whole printed name is a single line and
        // which is imported through its <Slot> instead. Without the rule, rare jewels whose base name is
        // missing from PobJewelBases (Time-Lost and other PoE2 jewel bases) were dropped at parse time
        // and never reached the tree sockets or the Jewels tab.
        if (!isJewel && baseId.Length == 0 && rarity == "rare") isJewel = true;
        // Scope the affix matcher to the mods this item's own class can roll. The pinned catalog words
        // several mods identically for different classes, so an unscoped tie-break mis-attributes lines
        // (a ring's "+208 to maximum Mana" was attributed to a two-handed-weapon mod). Jewels keep the
        // unrestricted matcher on purpose: their affixes may legitimately come from the item pool too.
        IReadOnlySet<string>? modPool = null;
        if (!isJewel && baseId.Length != 0 && catalog.Bases.TryGetValue(baseId, out var itemBase) &&
            catalog.Data.ModPools.TryGetValue(itemBase.ModPool, out var poolIds) && poolIds.Length > 0)
            modPool = new HashSet<string>(poolIds, StringComparer.Ordinal);
        var rolls = new List<ModRoll>();
        if (rarity != "unique")
        {
            foreach (var line in modLines)
            {
                var match = matcher.Match(line, modPool);
                if (match is null) { skippedLines++; continue; }
                if (rolls.Any(r => r.Id == match.Value.roll.Id)) { skippedLines++; continue; }
                rolls.Add(match.Value.roll);
            }
            // Without a pinned base only jewels can be validated (they use the jewel affix pool).
            if (baseId.Length == 0 && !isJewel) return null;
            if (baseId.Length == 0 && rarity == "normal") return null;
            // A jewel whose affixes the pinned pool cannot place is still a real item: it is socketed in
            // the tree and its own text carries effects the calculator reads (radius grants, implicits),
            // so only a text-less shell is rejected.
            if (baseId.Length == 0 && rarity != "unique" && rolls.Count == 0 && modLines.Count == 0) return null; // shell
        }
        // Every imported item keeps its full PoB text. For uniques it is the only source of their
        // modifiers; for rare/magic items it carries the rune/enchant bonuses and — importantly — the
        // item's own printed defence values ("Energy Shield: 243"), which are the level-scaled numbers
        // the game shows and the pinned base table does not export.
        string notes = text.Length > 9999 ? text[..9999] : text;
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

    // ------------------------------ build links ------------------------------
    /// <summary>Where a pasted build link has to be fetched from, and what it is expected to carry.
    /// <paramref name="Kind"/> is "pob" for a link that returns a Path of Building share code,
    /// "ninja" for a poe.ninja character model and "auto" for anything else (JSON or code, detected
    /// after the download). <paramref name="Host"/> is used for the status line.</summary>
    public sealed record ImportLink(string FetchUrl, string Kind, string Host);

    private static readonly string[] KnownBuildHosts = ["pobb.in", "www.pobb.in", "poe.ninja", "www.poe.ninja"];

    /// <summary>True when the text is a link we can fetch (with or without its scheme).</summary>
    public static bool LooksLikeBuildLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains('\n')) return false;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return true;
        return KnownBuildHosts.Any(host => trimmed.StartsWith(host + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a pasted build link into the URL that actually carries the build.
    /// <para>
    ///  - pobb.in keeps the share code behind <c>&lt;link&gt;/raw</c>; the short link itself is a
    ///    JavaScript page, which is why pasting one used to fail as "does not decode".
    ///  - a poe.ninja character page is rendered in the browser from
    ///    <c>/&lt;game&gt;/api/profile/characters/&lt;account&gt;/&lt;league&gt;/&lt;character&gt;/model/0</c>,
    ///    whose <c>charModel.pathOfBuildingExport</c> is the same Path of Building share code.
    ///  - every other link is fetched as-is and auto-detected.
    /// </para>
    /// Returns null when the text is not a link at all.</summary>
    public static ImportLink? ResolveImportLink(string? text)
    {
        if (!LooksLikeBuildLink(text)) return null;
        string trimmed = text!.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string host = uri.Host.ToLowerInvariant();
        string authority = uri.GetLeftPart(UriPartial.Authority);
        if (host is "pobb.in" or "www.pobb.in")
        {
            // https://pobb.in/<id> and https://pobb.in/<id>/raw both point at the share code.
            string? id = segments.LastOrDefault(segment => !segment.Equals("raw", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(id)) return null;
            return new(authority + "/" + id + "/raw", "pob", host);
        }
        if (host.EndsWith("poe.ninja", StringComparison.Ordinal))
        {
            // https://poe.ninja/<game>/profile/<account>/<league>/character/<name>
            int profile = Array.FindIndex(segments, segment => segment.Equals("profile", StringComparison.OrdinalIgnoreCase));
            if (profile >= 1 && segments.Length >= profile + 5 && segments[0].StartsWith("poe", StringComparison.OrdinalIgnoreCase) &&
                segments[profile + 3].Equals("character", StringComparison.OrdinalIgnoreCase))
                return new(authority + "/" + segments[0] + "/api/profile/characters/" +
                    Uri.EscapeDataString(segments[profile + 1]) + "/" + Uri.EscapeDataString(segments[profile + 2]) + "/" +
                    Uri.EscapeDataString(segments[profile + 4]) + "/model/0", "ninja", host);
            return new(trimmed, "auto", host);
        }
        return new(trimmed, "auto", host);
    }

    /// <summary>pobb.in ids are short URL-safe tokens; a Path of Building share code is thousands of
    /// characters long, so a short token pasted on its own can only be a bare pobb.in id.</summary>
    public static bool LooksLikePobbId(string? text)
    {
        string trimmed = (text ?? "").Trim();
        return trimmed.Length is >= 6 and <= 24 && trimmed.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }

    /// <summary>The share code carried by a fetched payload. A poe.ninja model JSON keeps it as
    /// <c>pathOfBuildingExport</c>; any other payload (HTML page, JSON, plain text) may embed the code
    /// itself. Returns null when there is none, so the caller can fall back to a JSON import.</summary>
    public static string? ExtractPobCode(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        string text = payload.Trim();
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try { if (FindPobCode(JsonDocument.Parse(text).RootElement) is string fromJson) return fromJson; }
            catch (JsonException) { /* not JSON after all: fall through to the text scan */ }
        }
        if (LooksLikePobCode(text)) return text;
        return FindEmbeddedPobCode(text);
    }

    /// <summary>Walks a JSON document for a string that decodes as a Path of Building share code.
    /// The search is by shape, not by field name, so a renamed or nested export field still works;
    /// only base64url-looking strings of a plausible length are tried, which keeps a 365 KB character
    /// model cheap to scan.</summary>
    private static string? FindPobCode(JsonElement element, int depth = 0)
    {
        if (depth > 6) return null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (FindPobCode(property.Value, depth + 1) is string found) return found;
            return null;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FindPobCode(item, depth + 1) is string found) return found;
            return null;
        }
        if (element.ValueKind != JsonValueKind.String) return null;
        string value = element.GetString() ?? "";
        return LooksLikePobCode(value) ? value.Trim() : null;
    }

    // A share code is base64url, so it may end in '=' padding; the whole run is taken and the decode
    // check decides whether it really is a code.
    private static readonly System.Text.RegularExpressions.Regex PobCodeShape = new(
        @"[A-Za-z0-9_\-+/=]{200,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A share code embedded in a page (a script block, an attribute, a query string).</summary>
    private static string? FindEmbeddedPobCode(string text)
    {
        int tried = 0;
        foreach (System.Text.RegularExpressions.Match match in PobCodeShape.Matches(text))
        {
            if (++tried > 8) break;
            if (LooksLikePobCode(match.Value)) return match.Value;
        }
        return null;
    }

    /// <summary>True when the text decodes as a Path of Building envelope (URL-safe base64 + zlib).</summary>
    public static bool LooksLikePobCode(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length < 100 || trimmed.Length > 200_000) return false;
        foreach (char c in trimmed)
            if (!char.IsLetterOrDigit(c) && c is not ('-' or '_' or '+' or '/' or '=')) return false;
        try { return DecodePobEnvelope(trimmed).Length > 0; }
        catch (Exception e) when (e is FormatException or InvalidDataException or NotSupportedException or ArgumentException) { return false; }
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
        uint adler = Adler32(payload);
        outp.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
        return Convert.ToBase64String(outp.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Inflate(byte[] data, int offset)
    {
        if (offset == 2)
        {
            if (data.Length < 6) throw new InvalidDataException("неполный zlib envelope");
            int header = (data[0] << 8) | data[1];
            if ((data[0] & 0x0F) != 8 || header % 31 != 0)
                throw new InvalidDataException("недопустимый zlib header");
        }
        using var source = new MemoryStream(data, offset, data.Length - offset);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        if (output.Length == 0) throw new InvalidDataException("пустой поток");
        var payload = output.ToArray();
        if (offset == 2)
        {
            uint expected = ((uint)data[^4] << 24) | ((uint)data[^3] << 16) | ((uint)data[^2] << 8) | data[^1];
            uint actual = Adler32(payload);
            if (expected != actual) throw new InvalidDataException("Adler-32 checksum mismatch");
        }
        return payload;
    }

    private static uint Adler32(ReadOnlySpan<byte> payload)
    {
        uint a = 1, b = 0;
        foreach (byte value in payload) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
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
