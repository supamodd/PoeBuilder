using System.Collections.ObjectModel;
using PoeBuilder.Core.Equipment;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

/// <summary>One row of the Quest Rewards tab: a quest from PoB2's own table with the reward it grants.
/// A choice quest ("Options" in the data) shows its selectable lines instead of one fixed reward.</summary>
public sealed class QuestRewardRow : Observable
{
    private bool _checked;
    private string _chosen;
    public QuestRewardRow(QuestReward reward, string line, bool isChecked)
    {
        Act = reward.Act; Area = reward.Area; Info = reward.Info; AreaLevel = reward.AreaLevel;
        Options = reward.Options; QuestPoints = reward.QuestPoints; ConfigDriven = reward.UseConfig;
        Stat = reward.Stat;
        _chosen = Options.Count > 0 && Options.Contains(line, StringComparer.Ordinal) ? line : Options.FirstOrDefault() ?? "";
        _checked = isChecked;
    }
    public int Act { get; }
    public string Area { get; }
    public string Info { get; }
    public int AreaLevel { get; }
    public int QuestPoints { get; }
    public bool ConfigDriven { get; }
    public IReadOnlyList<string> Options { get; }
    /// <summary>The quest's single reward line; empty for a choice quest, which uses <see cref="Options"/>.</summary>
    public string Stat { get; }
    public bool IsChoice => Options.Count > 0;
    public string Source => Position + " · " + Info;
    public string Position => "Act " + Act + " · " + Area;
    public string LevelLabel => "lvl " + AreaLevel;
    public IEnumerable<string> ChoiceOptions => Options;
    /// <summary>The chosen line of a choice quest. Picking one enables the row, exactly like clicking the
    /// checkbox does for a reward with no options.</summary>
    public string Chosen
    {
        get => _chosen;
        set { if (Set(ref _chosen, value) && value.Length > 0 && !IsChecked) IsChecked = true; }
    }
    public bool IsChecked
    {
        get => _checked;
        set { if (Set(ref _checked, value)) Changed?.Invoke(); }
    }
    /// <summary>The line this row contributes to the build (empty while disabled).</summary>
    public string Line => !IsChecked ? "" : IsChoice ? _chosen : Stat;
    public string Display => IsChoice ? string.Join(" / ", Options) : Stat;
    public event Action? Changed;
}

/// <summary>The Quest Rewards tab: PoB2's quest-reward table (its own
/// <c>src/Data/QuestRewards.lua</c>), one row per quest, ticked for the rewards the build has. The ticked
/// rows are what the calculator applies, so a build carries exactly what the player picked — including
/// the choice quests, where PoB2 stores the chosen option and leaves the rest at "None".</summary>
public sealed class QuestRewardsViewModel : Observable
{
    public Localization L { get; }
    private BuildEditor? _editor;
    private QuestRewardIndex _index = QuestRewardIndex.Empty;
    private string[]? _imported;
    private string _status = "";
    private bool _suppress;

    public QuestRewardsViewModel(Localization l)
    {
        L = l;
        AllCommand = new ActionCommand(_ => SetAll(true), () => CanEdit);
        NoneCommand = new ActionCommand(_ => SetAll(false), () => CanEdit);
        ImportedCommand = new ActionCommand(_ => Restore(), () => CanEdit && _imported is not null);
    }

    public ObservableCollection<QuestRewardRow> Rows { get; } = [];
    public System.Windows.Input.ICommand AllCommand { get; }
    public System.Windows.Input.ICommand NoneCommand { get; }
    public System.Windows.Input.ICommand ImportedCommand { get; }
    public bool CanEdit => _editor is not null && _index.Count > 0;
    public string Warning => _editor is null ? L["NoBuildText"] : _index.Count == 0 ? L["CatalogMissing"] : "";
    public bool HasWarning => Warning.Length > 0;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Summary => L.Format("QuestRewardSummary", Rows.Count(r => r.IsChecked), Rows.Count);

    /// <summary>PoB2's table, published by the app once the catalog is loaded.</summary>
    public void SetIndex(QuestRewardIndex index)
    {
        _index = index;
        Reload();
    }

    public void BindEditor(BuildEditor? editor)
    {
        _editor = editor;
        _imported = editor?.QuestRewardsSnapshot;
        Reload();
    }

    /// <summary>Rebuilds the rows from PoB2's table, ticking the lines the build already carries. A build
    /// that carries none starts with PoB2's own default set — every entry PoB2 drives from its config
    /// ("useConfig"), which is what a levelled character has.</summary>
    private void Reload()
    {
        Rows.Clear();
        if (_editor is null || _index.Count == 0) { RaiseSummary(); return; }
        var current = _editor.QuestRewardsSnapshot;
        bool hasLines = current is { Length: > 0 };
        var lines = hasLines ? current! : [.. _index.ConfigLines()];
        _suppress = true;
        foreach (var reward in _index.Rewards)
        {
            string line = lines.FirstOrDefault(l => Matches(l, reward)) ?? "";
            QuestRewardRow row = new(reward, line, line.Length > 0);
            row.Changed += Push;
            Rows.Add(row);
        }
        _suppress = false;
        // A hand-made build starts with PoB2's defaults, so they become its stored rewards right away.
        if (!hasLines) Push();
        RaiseSummary();
    }

    private static bool Matches(string line, QuestReward reward) => reward.Stat.Length > 0
        ? string.Equals(line, reward.Stat, StringComparison.Ordinal)
        : reward.Options.Any(option => string.Equals(QuestRewardIndex.NormaliseLine(line),
            QuestRewardIndex.NormaliseLine(option), StringComparison.Ordinal));

    private void SetAll(bool on)
    {
        _suppress = true;
        foreach (var row in Rows)
        {
            // A choice quest still needs a pick, so "all" leaves it as it is (PoB2 keeps it at "None").
            if (on && row.IsChoice) continue;
            row.IsChecked = on;
        }
        _suppress = false;
        Push();
    }

    /// <summary>Restores the lines the build was imported with, so an experiment can be undone.</summary>
    private void Restore()
    {
        _editor?.SetQuestRewards(_imported);
        Reload();
    }

    private void Push()
    {
        if (_suppress || _editor is null) { RaiseSummary(); return; }
        var lines = Rows.Where(r => r.Line.Length > 0).Select(r => r.Line).ToArray();
        _editor.SetQuestRewards(lines);
        Status = L.Format("QuestRewardApplied", lines.Length);
        RaiseSummary();
    }

    private void RaiseSummary()
    {
        Raise(nameof(Summary)); Raise(nameof(CanEdit)); Raise(nameof(Warning)); Raise(nameof(HasWarning));
    }
}
