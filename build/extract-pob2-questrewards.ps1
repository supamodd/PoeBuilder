# Extracts PoB2's Quest Rewards table (PathOfBuilding-PoE2-master/src/Data/QuestRewards.lua) into the
# format the app reads. Each entry is one quest reward: which act/area grants it, the reward line as
# PoB2 stores it ("+10% to Cold Resistance"), how many weapon-set skill points it grants, and whether
# PoB2 applies it through the build's config ("useConfig").
#
# Output: src/PoeBuilder.App/Data/Game/questrewards.json (its sha256 belongs in manifest.json).
param(
    [string]$PobFile = "$PSScriptRoot\..\PathOfBuilding-PoE2-master\src\Data\QuestRewards.lua",
    [string]$OutFile = "$PSScriptRoot\..\src\PoeBuilder.App\Data\Game\questrewards.json"
)
$ErrorActionPreference = 'Stop'

$text = Get-Content $PobFile -Raw
$entries = [System.Collections.Generic.List[object]]::new()

# Entries are flat Lua tables: `{ "Key" = value, … }`, one key per line, with an optional
# `"Options" = { "line", … }` array (a quest where the player picks one reward; its strings may carry
# "\n"/"\t" escapes, which become real characters because that is what PoB2's config value holds).
# The leading indentation decides what a closing brace ends (one tab = entry, two tabs = Options), so
# the reader tracks it instead of trimming it away.
function Unescape([string]$s) { $s.Replace('\n', "`n").Replace('\t', "`t") }
$entry = $null
$options = $null
foreach ($line in ($text -split "`n")) {
    $trimmed = $line.Trim()
    if ($line -match '^\t\{$') { $entry = [ordered]@{}; $options = $null; continue }
    if ($line -match '^\t\},?$') {
        if ($null -ne $entry -and $entry.Count -gt 0) { $entries.Add($entry) }
        $entry = $null; $options = $null; continue
    }
    if ($null -eq $entry) { continue }
    if ($null -ne $options) {
        if ($line -match '^\t\t\},?$') {
            if ($options.Count -gt 0) { $entry['Options'] = $options.ToArray() }
            $options = $null; continue
        }
        if ($trimmed -match '^"(.*)",?$') { $options.Add((Unescape $Matches[1])) }
        continue
    }
    if ($line -match '^\t\t\["Options"\]\s*=\s*\{') { $options = [System.Collections.Generic.List[string]]::new(); continue }
    if ($trimmed -match '^\["([^"]+)"\]\s*=\s*(.+?),?$') {
        $key = $Matches[1]; $raw = $Matches[2].Trim()
        if ($raw -match '^"(.*)"$') { $entry[$key] = Unescape $Matches[1] }
        elseif ($raw -match '^(true|false)$') { $entry[$key] = ($raw -eq 'true') }
        elseif ($raw -match '^-?[0-9.]+$') { $entry[$key] = [double]$raw }
    }
}

$json = [ordered]@{
    provenance = [ordered]@{
        source = "PathOfBuilding-PoE2-master/src/Data/QuestRewards.lua (PoB2's own data, workspace copy)"
        generator = "build/extract-pob2-questrewards.ps1"
        note = "One entry per quest reward: Act/Description/Area/Info identity, the reward line (Stat), the weapon-set skill points it grants (questPoints) and whether PoB2 applies it from the config (useConfig)."
    }
    rewards = $entries
}
$jsonText = ($json | ConvertTo-Json -Depth 5 -Compress)
[System.IO.File]::WriteAllText($OutFile, $jsonText, (New-Object System.Text.UTF8Encoding($false)))
"quest rewards extracted: $($entries.Count)"
"output: $OutFile"
"sha256: " + (Get-FileHash -Path $OutFile -Algorithm SHA256).Hash.ToLower()
