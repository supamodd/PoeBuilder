using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
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
        DataContext = this;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        StatusText = "";
        var pob = _pobCode.Trim();
        var url = _sourceUrl.Trim();
        var json = _jsonText.Trim();
        if (pob.Length > 0)
        {
            // Fail loudly but locally: decode problems are reported without touching the network.
            try { BuildInterop.DecodePobEnvelope(pob); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or NotSupportedException)
            { StatusText = _main.L["ImportPobBad"] + " " + ex.Message; return; }
            JsonPayload = pob; PayloadKind = "pob"; SourceLabel = "PoB code";
            DialogResult = true; return;
        }
        if (url.Length > 0 && json.Length == 0)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            { StatusText = _main.L["ImportUrlBad"]; return; }
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(20);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PoeBuilder/0.7)");
                json = await http.GetStringAsync(uri);
            }
            catch (Exception ex) { StatusText = _main.L["ImportFetchFailed"] + " " + ex.Message; return; }
            SourceLabel = url;
        }
        else if (json.Length > 0) SourceLabel = "JSON";
        if (json.TrimStart().StartsWith("{"))
        {
            JsonPayload = json; PayloadKind = "json";
            DialogResult = true; return;
        }
        if (json.Length > 0)
        {
            // Not JSON: probably a PoB code pasted into the wrong tab — still accept it.
            try { BuildInterop.DecodePobEnvelope(json); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or NotSupportedException)
            { StatusText = _main.L["ImportNeither"]; return; }
            JsonPayload = json; PayloadKind = "pob"; SourceLabel = "PoB code";
            DialogResult = true; return;
        }
        StatusText = _main.L["ImportNothing"];
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
