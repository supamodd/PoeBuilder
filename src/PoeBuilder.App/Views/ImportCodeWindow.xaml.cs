using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PoeBuilder.App.Services;
using PoeBuilder.App.ViewModels;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.Views;

/// <summary>Paste-in import: a Path of Building share code (decoded locally, no network) or
/// poe.ninja-style Build Planner JSON — pasted directly, from a file, or fetched from a user-provided link
/// (an explicit user action, never an automatic call).</summary>
public partial class ImportCodeWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _main;
    private string _pobCode = "", _sourceUrl = "", _jsonText = "", _statusText = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    public Localization L => _main.L;
    public string PobCode { get => _pobCode; set { _pobCode = value; Raise(); } }
    public string SourceUrl { get => _sourceUrl; set { _sourceUrl = value; Raise(); } }
    public string JsonText { get => _jsonText; set { _jsonText = value; Raise(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; Raise(); } }

    /// <summary>What OnImport produced: "pob" or "json"; null payload means the user cancelled or the fetch failed.</summary>
    public string? JsonPayload { get; private set; }
    public string PayloadKind { get; private set; } = "json";
    public string SourceLabel { get; private set; } = "";

    public ImportCodeWindow(MainViewModel main)
    {
        _main = main;
        InitializeComponent();
        ThemedWindowChrome.ConstrainToWorkArea(this);
        DataContext = this;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        StatusText = "";
        string pob = _pobCode.Trim(), url = _sourceUrl.Trim(), json = _jsonText.Trim();

        // ---- a build link, pasted into either tab ----
        // pobb.in keeps the share code behind <link>/raw and a poe.ninja character page is served by its
        // own model API, so a pasted link is resolved before anything else is tried. A bare pobb.in id
        // (the short token the site shows in its own link) counts as a link too; a share code never does,
        // because a code is thousands of characters long while an id is a handful.
        string? link = BuildInterop.LooksLikeBuildLink(pob) ? pob
            : BuildInterop.LooksLikeBuildLink(url) ? url
            : pob.Length > 0 && BuildInterop.LooksLikePobbId(pob) && !BuildInterop.LooksLikePobCode(pob) ? "pobb.in/" + pob
            : null;
        if (link is not null)
        {
            var resolved = BuildInterop.ResolveImportLink(link);
            if (resolved is null) { StatusText = _main.L["ImportUrlBad"]; return; }
            string fetched;
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(25);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PoeBuilder/0.7)");
                fetched = await http.GetStringAsync(resolved.FetchUrl);
            }
            catch (Exception ex) { StatusText = _main.L["ImportFetchFailed"] + " " + ex.Message; return; }
            SourceLabel = resolved.Host + " · " + resolved.FetchUrl;
            // A poe.ninja model JSON carries "pathOfBuildingExport"; any other page may embed the code.
            if (BuildInterop.ExtractPobCode(fetched) is string linked)
            { JsonPayload = linked; PayloadKind = "pob"; DialogResult = true; return; }
            if (fetched.TrimStart().StartsWith('{'))
            { JsonPayload = fetched; PayloadKind = "json"; DialogResult = true; return; }
            StatusText = _main.L.Format("ImportLinkNoCode", resolved.Host);
            return;
        }

        // ---- the PoB-code tab ----
        if (pob.Length > 0)
        {
            // Fail loudly but locally: decode problems are reported without touching the network.
            try { BuildInterop.DecodePobEnvelope(pob); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or NotSupportedException)
            { StatusText = _main.L["ImportPobBad"] + " " + ex.Message; return; }
            JsonPayload = pob; PayloadKind = "pob"; SourceLabel = "PoB code";
            DialogResult = true; return;
        }

        // ---- the JSON tab ----
        if (json.Length == 0) { StatusText = _main.L["ImportNothing"]; return; }
        if (json.TrimStart().StartsWith('{'))
        {
            JsonPayload = json; PayloadKind = "json"; SourceLabel = url.Length > 0 ? url : "JSON";
            DialogResult = true; return;
        }
        // Not JSON: a share code, or text that embeds one (a page copied out of a browser).
        if (BuildInterop.ExtractPobCode(json) is string embedded)
        {
            JsonPayload = embedded; PayloadKind = "pob"; SourceLabel = "PoB code";
            DialogResult = true; return;
        }
        StatusText = _main.L["ImportNeither"];
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not Button)
            DragMove();
    }
}
