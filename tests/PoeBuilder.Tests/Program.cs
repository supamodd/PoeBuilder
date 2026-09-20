using System.Text.Json;
using System.Text.RegularExpressions;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Storage;
using PoeBuilder.Core.Calculation;
using PoeBuilder.App.Services;
using PoeBuilder.App.ViewModels;

var root = Path.Combine(Path.GetTempPath(), "PoeBuilder-Native-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0;
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
BuildRepository Repository() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));
try
{
    await Test("Create and round-trip Cyrillic build and notes", async () =>
    {
        var repo = Repository(); var source = BuildDocument.Create("Ёж — тестовый билд") with { CharacterClass = "Своя подпись", Notes = "Привет, мир!\nЁё Жж — 123\nEnglish too." };
        var saved = await repo.SaveAsync(source); var loaded = await BuildRepository.ReadDocumentAsync(repo.PathFor(source.Id));
        Assert(loaded.Name == source.Name && loaded.Notes == source.Notes && loaded.Id == source.Id);
        Assert(saved.UpdatedUtc >= source.UpdatedUtc);
    });
    await Test("Second and third saves keep a usable previous-version backup", async () =>
    {
        var repo = Repository(); var doc = await repo.SaveAsync(BuildDocument.Create("First"));
        await repo.SaveAsync(doc with { Name = "Second" }); await repo.SaveAsync(doc with { Name = "Third" });
        var backup = await BuildRepository.ReadDocumentAsync(repo.PathFor(doc.Id) + ".bak");
        Assert(backup.Name == "Second"); Assert((await BuildRepository.ReadDocumentAsync(repo.PathFor(doc.Id))).Name == "Third");
    });
    await Test("Import always creates a new identity without replacing existing build", async () =>
    {
        var repo = Repository(); var doc = await repo.SaveAsync(BuildDocument.Create("Original"));
        var imported = await repo.ImportAsNewAsync(repo.PathFor(doc.Id));
        Assert(imported.Id != doc.Id); Assert((await repo.ReadLibraryAsync()).Builds.Count == 2);
    });
    await Test("Duplicate preserves content and has its own ID", async () =>
    {
        var repo = Repository(); var doc = await repo.SaveAsync(BuildDocument.Create("A") with { Notes = "Моя заметка", Level = 76 });
        var copy = await repo.DuplicateAsync(doc, "B");
        Assert(copy.Id != doc.Id && copy.Name == "B" && copy.Level == 76 && copy.Notes == doc.Notes);
    });
    await Test("Deletion moves the saved file to recoverable local trash", async () =>
    {
        var repo = Repository(); var doc = await repo.SaveAsync(BuildDocument.Create("Trash test"));
        var trash = await repo.MoveToTrashAsync(doc.Id);
        Assert(File.Exists(trash) && !File.Exists(repo.PathFor(doc.Id)));
        Assert((await repo.ReadLibraryAsync()).Builds.Count == 0);
        Assert((await BuildRepository.ReadDocumentAsync(trash)).Name == doc.Name);
    });
    await Test("Path-like names do not control file-system destinations", async () =>
    {
        var repo = Repository(); var doc = await repo.SaveAsync(BuildDocument.Create("../a\\b: *?"));
        Assert(Path.GetDirectoryName(repo.PathFor(doc.Id)) == repo.RootDirectory);
        Assert(Path.GetFileName(repo.PathFor(doc.Id)) == doc.Id.ToString("N") + ".poebuild");
    });
    await Test("Empty names and out-of-range levels are rejected", async () =>
    {
        var repo = Repository();
        await Throws<BuildFormatException>(() => repo.SaveAsync(BuildDocument.Create(" ")));
        await Throws<BuildFormatException>(() => repo.SaveAsync(BuildDocument.Create("A") with { Level = 0 }));
        await Throws<BuildFormatException>(() => repo.SaveAsync(BuildDocument.Create("A") with { Level = 101 }));
    });
    await Test("Oversized notes are rejected", async () =>
    {
        await Throws<BuildFormatException>(() => Repository().SaveAsync(BuildDocument.Create("A") with { Notes = new string('x', 100001) }));
    });
    await Test("Unsupported schema and missing format marker are rejected", async () =>
    {
        var path = Path.Combine(root, "invalid.poebuild");
        await File.WriteAllTextAsync(path, "{\"format\":\"PoeBuilder.Native.Build\",\"schemaVersion\":99}");
        await Throws<BuildFormatException>(() => BuildRepository.ReadDocumentAsync(path));
        await File.WriteAllTextAsync(path, "{\"name\":\"Not our format\"}");
        await Throws<BuildFormatException>(() => BuildRepository.ReadDocumentAsync(path));
    });
    await Test("Unknown fields are rejected rather than silently discarded", async () =>
    {
        var doc = BuildDocument.Create("Unknown fields");
        var node = JsonSerializer.SerializeToNode(doc, BuildRepository.JsonOptions)!;
        node["futureGameMechanic"] = 42;
        var path = Path.Combine(root, "unknown-fields.poebuild");
        await File.WriteAllTextAsync(path, node.ToJsonString());
        await Throws<BuildFormatException>(() => BuildRepository.ReadDocumentAsync(path));
    });
    await Test("PoB XML is not silently treated as native JSON", async () =>
    {
        var path = Path.Combine(root, "pob.xml"); await File.WriteAllTextAsync(path, "<PathOfBuilding2/>");
        await Throws<BuildFormatException>(() => BuildRepository.ReadDocumentAsync(path));
    });
    await Test("Wrong JSON types produce a controlled format error", async () =>
    {
        var path = Path.Combine(root, "types.poebuild");
        await File.WriteAllTextAsync(path, "{\"format\":\"PoeBuilder.Native.Build\",\"schemaVersion\":\"one\"}");
        await Throws<BuildFormatException>(() => BuildRepository.ReadDocumentAsync(path));
    });
    await Test("Large imported files are rejected before JSON parsing", async () =>
    {
        var path = Path.Combine(root, "large.poebuild"); await File.WriteAllTextAsync(path, new string('x', BuildRepository.MaximumFileBytes + 1));
        await Throws<BuildFormatException>(() => BuildRepository.ReadDocumentAsync(path));
    });
    await Test("Library tolerates malformed files and leaves them unchanged", async () =>
    {
        var repo = Repository(); await repo.SaveAsync(BuildDocument.Create("Valid"));
        var path = Path.Combine(repo.RootDirectory, "broken.poebuild"); await File.WriteAllTextAsync(path, "broken");
        var result = await repo.ReadLibraryAsync();
        Assert(result.Builds.Count == 1 && result.UnreadableFiles.Count == 1 && await File.ReadAllTextAsync(path) == "broken");
    });
    await Test("Filename and internal ID mismatch is not added to library", async () =>
    {
        var repo = Repository(); var doc = await repo.SaveAsync(BuildDocument.Create("A"));
        File.Move(repo.PathFor(doc.Id), Path.Combine(repo.RootDirectory, "renamed.poebuild"));
        var result = await repo.ReadLibraryAsync(); Assert(result.Builds.Count == 0 && result.UnreadableFiles.Count == 1);
    });
    await Test("Export/import preserves the native format independently of library", async () =>
    {
        var path = Path.Combine(root, "export.poebuild"); var doc = BuildDocument.Create("Экспорт") with { Notes = "Русский текст" };
        await BuildRepository.WriteDocumentAsync(path, doc);
        Assert((await BuildRepository.ReadDocumentAsync(path)) == doc);
    });
    await Test("Invalid save does not overwrite a valid previous build", async () =>
    {
        var repo = Repository(); var doc = await repo.SaveAsync(BuildDocument.Create("Good"));
        await Throws<BuildFormatException>(() => repo.SaveAsync(doc with { Level = -1 }));
        Assert((await BuildRepository.ReadDocumentAsync(repo.PathFor(doc.Id))).Name == "Good");
    });
    await Test("Atomic writer cleans temporary files after a destination failure", async () =>
    {
        var directory = Path.Combine(root, "blocked.poebuild"); Directory.CreateDirectory(directory);
        await Throws<IOException>(() => BuildRepository.WriteDocumentAsync(directory, BuildDocument.Create("A")));
        Assert(Directory.GetFiles(root, "blocked.poebuild.*.tmp").Length == 0);
    });
    await Test("Russian is the default application language", async () =>
    {
        var read = await new SettingsRepository(Path.Combine(root, "no-settings.json")).ReadAsync();
        Assert(read.Settings.Language == "ru" && !read.RecoveredFromError);
    });
    await Test("Language preference persists independently of build files", async () =>
    {
        var settings = new SettingsRepository(Path.Combine(root, "settings.json"));
        await settings.SaveAsync(new() { Language = "en" });
        Assert((await settings.ReadAsync()).Settings.Language == "en");
        await settings.SaveAsync(new() { Language = "ru" });
        Assert((await settings.ReadAsync()).Settings.Language == "ru");
    });
    await Test("Corrupt settings produce defaults and are not erased on read", async () =>
    {
        var path = Path.Combine(root, "bad-settings.json"); await File.WriteAllTextAsync(path, "{bad}");
        var result = await new SettingsRepository(path).ReadAsync();
        Assert(result.RecoveredFromError && result.Settings.Language == "ru" && await File.ReadAllTextAsync(path) == "{bad}");
    });
    await Test("Localization switches live and raises WPF indexer notification", () =>
    {
        var l = new Localization(); bool notified = false;
        l.PropertyChanged += (_, e) => { if (e.PropertyName == "Item[]") notified = true; };
        Assert(l["Save"] == "Сохранить"); l.SetLanguage("en");
        Assert(l["Save"] == "Save" && notified);
        l.SetLanguage("ru"); Assert(l["Notes"] == "Заметки"); return Task.CompletedTask;
    });
    await Test("New editor is dirty; save acceptance clears state", () =>
    {
        var doc = BuildDocument.Create("Editor"); var editor = new BuildEditor(doc, true);
        Assert(editor.IsDirty && editor.IsValid); editor.AcceptSaved(doc); Assert(!editor.IsDirty);
        editor.Notes = "Ёж"; Assert(editor.IsDirty && editor.ToDocument().Notes == "Ёж"); return Task.CompletedTask;
    });
    await Test("Invalid level remains editable but cannot be converted to a build", async () =>
    {
        var editor = new BuildEditor(BuildDocument.Create("Editor")); editor.LevelText = "abc";
        Assert(!editor.IsValid); await Throws<BuildFormatException>(() => Task.FromResult(editor.ToDocument()));
        editor.LevelText = "100"; Assert(editor.IsValid);
    });
    await Test("Calculation placeholder never invents metrics", () =>
    {
        var engine = new UnavailableCalculationEngine(); var build = BuildDocument.Create("A");
        var result = engine.Calculate(build, null);
        Assert(result.Availability == CalculationAvailability.NoGameData && result.Metrics.Count == 0);
        var provided = engine.Calculate(build, new("0.5.5c", "ru", "not-yet-connected", ""));
        Assert(provided.Availability == CalculationAvailability.NotImplemented && provided.Metrics.Count == 0);
        return Task.CompletedTask;
    });
    await TreeTests.Run(Test, root);
    await TreePropertyTests.Run(Test);
    await AscendancyTests.Run(Test, root);
    await InteropTests.Run(Test);
    await EquipmentSkillsTests.Run(Test, root);
    await CalculationTests.Run(Test);
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
