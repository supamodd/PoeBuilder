<#
.SYNOPSIS
Downloads the Russian game text for PoeBuilder from poe2db and writes Data/Game/locale-ru.json.

.DESCRIPTION
The pinned Game data is English only, so every name in the catalog and the tree stays EN even with a
Russian UI. This script fetches the Russian side from poe2db and joins it onto OUR ids, so the application
can show real Russian text while every calculation, import and save keeps using the English ids.

Joining is by identity, never by position:
  * tree - poe2db's per-language trees are keyed by the same node ids we ship (verified 5153 = 5153),
    so a node joins on its id and its stat lines pair up on the [StatId|Text] prefix.
  * names - poe2db URLs carry the English name as a slug (/ru/Firebolt), which is the only join key the
    site exposes. A name is therefore joined on its English text, normalised.
Anything that does not join is reported, never guessed.

.NOTES
PoE2DB is an independent community wiki, NOT GGG. Its Russian text is not guaranteed to match the Russian
client word for word. The output records provenance so this stays visible instead of being assumed.
#>
[CmdletBinding()]
param(
    # $PSScriptRoot is not bound yet while the param block runs under -File, so these default to $null
    # here and are filled from $MyInvocation.MyCommand.Path just below.
    [string]$CacheDir,
    [string]$OutFile,
    [string]$TreeVersion = '4.5',
    [int]$ThrottleMs = 900,
    # -TreeOnly writes the tree and the list-page names and skips the ~3 700 per-base requests, so the
    # Russian tree can land without waiting for the long crawl. A later full run merges the bases into
    # the same file.
    [switch]$TreeOnly
)

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $CacheDir) { $CacheDir = Join-Path $here 'cache' }
if (-not $OutFile)  { $OutFile  = Join-Path $here '..\src\PoeBuilder.App\Data\Game\locale-ru.json' }

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

function Get-Cached {
    <# Fetches a URL through an on-disk cache so a re-run costs no traffic and a failure is recoverable. #>
    param([string]$Url, [string]$Name, [int]$TimeoutSec = 120)
    $file = Join-Path $CacheDir $Name
    if (Test-Path $file) {
        $age = (Get-Date) - (Get-Item $file).LastWriteTime
        if ($age.TotalDays -lt 7) { return Get-Content $file -Raw -Encoding UTF8 }
        Write-Host "  cache stale ($([int]$age.TotalDays)d): $Name"
    }
    Write-Host "  GET $Url"
    # Read the bytes and decode them as UTF-8 ourselves. Letting Invoke-WebRequest decode first gives
    # the console's OEM codepage here, which mangles every Russian string before we ever see it.
    $tmp = Join-Path $CacheDir ($Name + '.part')
    (New-Object Net.WebClient).DownloadFile($Url, $tmp)
    $body = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($tmp))
    Remove-Item $tmp -Force
    Set-Content -Path $file -Value $body -Encoding UTF8 -NoNewline
    Start-Sleep -Milliseconds $ThrottleMs
    return $body
}

function ConvertTo-Key {
    <# Normalises a name into the slug form poe2db uses for its URLs, which is the join key. #>
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $t = $Text.Trim().ToLowerInvariant()
    $t = $t -replace "'", ''            # "Ancestor's Cry" and "Ancestors Cry" are one name on the site
    $t = $t -replace '[^a-z0-9]+', ''
    if ($t.Length -eq 0) { return $null }
    return $t
}

$names = [ordered]@{}   # en-normalised -> {en, ru}
$missing = [ordered]@{} # bucket -> list of names

function Add-NamePair {
    param([string]$En, [string]$Ru)
    if ([string]::IsNullOrWhiteSpace($Ru) -or $Ru -match '^[\x00-\x7F]+$') { return }  # untranslated
    $k = ConvertTo-Key $En
    if (-not $k) { return }
    if (-not $names.Contains($k)) { $names[$k] = [ordered]@{ en = $En; ru = $Ru } }
}

function Add-Missing {
    param([string]$Name, [string]$Bucket)
    if (-not $missing.Contains($Bucket)) { $missing[$Bucket] = New-Object 'System.Collections.Generic.List[string]' }
    if (-not $missing[$Bucket].Contains($Name)) { $missing[$Bucket].Add($Name) }
}

function Read-NameRows {
    <# Gem, unique and base pages all list a row per entity, and the name is the link text. The unique
       page wraps it in <span class="uniqueName">, so that shape is read first and the plain link text is
       the fallback; the image-only link (no text) is skipped rather than stored as an empty name.
       The key is always the slug, which is the English name. #>
    param([string]$Html)
    $rows = [ordered]@{}
    foreach ($m in [regex]::Matches($Html, '<a[^>]*href="/(?:ru|us)/([^"/]+)"[^>]*>\s*<span class="uniqueName">(.*?)</span>', 'Singleline')) {
        $slug = $m.Groups[1].Value
        $text = [Net.WebUtility]::HtmlDecode(($m.Groups[2].Value -replace '<[^>]+>', '')).Trim()
        if ($text) { $rows[$slug] = $text }
    }
    foreach ($m in [regex]::Matches($Html, '<a[^>]*href="/(?:ru|us)/([^"/]+)"[^>]*>([^<]+)</a>')) {
        $slug = $m.Groups[1].Value
        if ($rows.Contains($slug)) { continue }
        $text = [Net.WebUtility]::HtmlDecode($m.Groups[2].Value).Trim()
        if ($text) { $rows[$slug] = $text }
    }
    # A base item has no list row anywhere: the name is the type line of its own page, a
    # <div class="itemName typeLine"> holding one <span class="lc">. Bases are reached one URL per base
    # (driven by Read-BasePage below), and the URL slug is the English name we already know.
    foreach ($m in [regex]::Matches($Html, '<div class="itemName typeLine">\s*<span class="lc">([^<]+)</span>', 'Singleline')) {
        $text = [Net.WebUtility]::HtmlDecode($m.Groups[1].Value).Trim()
        if ($text) { $rows["base:$text"] = $text }
    }
    return $rows
}
function Read-BasePage {
    <# A base item has no list row anywhere; the name is the title of its own page. poe2db addresses a
       base by the slugified English name, so the slug comes from OUR name and both locales are read
       from the same slug. Returns $null when the page is not a base (a hub redirect or a 404), which is
       counted as unmatched rather than guessed at. #>
    param([string]$Slug, [switch]$WithUs)
    $locales = if ($WithUs) { @('us','ru') } else { @('ru') }
    $title = $null
    foreach ($loc in $locales) {
        $rows = Read-NameRows (Get-Cached "https://poe2db.tw/$loc/$Slug" "base_${loc}_$Slug.html" 60)
        $titles = @($rows.Keys | Where-Object { $_ -like 'base:*' } | ForEach-Object { $_.Substring(5) })
        if ($titles.Count -eq 0) { return $null }
        if ($loc -eq 'us') { $usTitle = $titles[0] } else { $title = $titles[0] }
    }
    if ($WithUs) { return @{ ru = $title; us = $usTitle } }
    return $title
}
# ---------------------------------------------------------------- tree
# The only source in this task that joins on identity rather than on a name.
Write-Host '== passive skill tree'
$ruTree = (Get-Cached "https://poe2db.tw/data/passive-skill-tree/$TreeVersion/data_ru.json" 'tree_ru.json') | ConvertFrom-Json
$usTree = (Get-Cached "https://poe2db.tw/data/passive-skill-tree/$TreeVersion/data_us.json" 'tree_us.json') | ConvertFrom-Json

$treeOut = [ordered]@{}
$statJoined = 0; $statMissed = 0
foreach ($prop in $ruTree.nodes.PSObject.Properties) {
    $id = $prop.Name
    $ru = $prop.Value
    $usProp = $usTree.nodes.PSObject.Properties[$id]
    if (-not $usProp) { continue }
    $us = $usProp.Value
    $ruName = $ru.name; $usName = $us.name
    if ([string]::IsNullOrWhiteSpace($ruName)) { continue }

    $entry = [ordered]@{ name = $ruName }
    if ($usName) { $entry.en = $usName }
    Add-NamePair -En $usName -Ru $ruName

    # A stat line pairs up on the GGG stat ids it carries, not on the text around them. The ids sit in
    # "[Id|Text]" placeholders, but their POSITION differs between the locales: English leads with the
    # id of the stat the sentence is about, while Russian puts the number first and the id later in the
    # same line. So both sides are read as an ordered set of ids and the lines are paired on that key.
    if ($ru.stats -and $ru.stats.Count -gt 0) {
        function Get-StatKey { param([string]$Line) ([regex]::Matches($Line,'\[([A-Za-z0-9_]+)\|') | ForEach-Object { $_.Groups[1].Value }) -join '|' }
        $ruByKey = @{}
        foreach ($line in $ru.stats) {
            $key = Get-StatKey $line
            if ($key -and -not $ruByKey.ContainsKey($key)) { $ruByKey[$key] = $line }
        }
        $ruStats = New-Object 'System.Collections.Generic.List[string]'
        for ($i = 0; $i -lt $us.stats.Count; $i++) {
            $line = $us.stats[$i]
            $key = Get-StatKey $line
            $translated = $null
            if ($key -and $ruByKey.ContainsKey($key)) {
                # The Russian line is taken WHOLE. It is a complete translated sentence and it keeps the
                # same "[Id|Text]" placeholders the English one has, so nothing is lost by using it.
                # Splicing Russian fragments into the English line instead is what produced mixed lines
                # like "[Allies|Союзники] in your [Presence|присутствии] have 6% increased [Attack|атаки]
                # Speed": only the placeholder text was translated and the English words around it stayed.
                # These strings are display-only (the calculation keeps reading the pinned English), so
                # taking the sentence as the other locale wrote it is both correct and simpler.
                $translated = $ruByKey[$key]
                $statJoined++
            } elseif ($i -lt $ru.stats.Count -and $ru.stats.Count -eq $us.stats.Count -and $ru.stats[$i] -match '[\u0400-\u04FF]') {
                # No usable key: the line has no stat id at all ("Grants 1 Passive Skill Points"), or the
                # two locales name a different SET of ids for it. The two arrays hold one node's lines in
                # the same order, so the index is used - but only when both sides have the same number of
                # lines, so a short or reordered list can never shift a line onto the wrong stat.
                $translated = $ru.stats[$i]
                $statJoined++
            } else {
                $translated = $line; $statMissed++
            }
            $ruStats.Add($translated)
        }
        $entry.stats = $ruStats
    }
    $treeOut[$id] = $entry
}
Write-Host "  nodes: $($treeOut.Count)  stat lines: joined $statJoined, kept EN $statMissed"
# ---------------------------------------------------------------- names
# Both locales of a page are read with the same slug keys, so an English name and its Russian name
# pair up on that slug. Reading only the RU page would leave no key to join our own data on.
foreach ($page in @('Skill_Gems','Support_Gems','Spirit_Gems','Unique_item','Items','Modifiers')) {
    Write-Host "== $page"
    $ru = Read-NameRows (Get-Cached "https://poe2db.tw/ru/$page" "page_ru_$page.html")
    $us = Read-NameRows (Get-Cached "https://poe2db.tw/us/$page" "page_us_$page.html")
    $joined = 0
    foreach ($slug in $us.Keys) {
        if (-not $ru.Contains($slug)) { continue }
        $ruText = $ru[$slug]; $usText = $us[$slug]
        if ($ruText -eq $usText) { continue }
        if ($ruText -match '^[\x00-\x7F]+$') { continue }   # site served the English text back
        $k = ConvertTo-Key $usText
        if ($k -and -not $names.Contains($k)) { $names[$k] = [ordered]@{ en = $usText; ru = $ruText }; $joined++ }
    }
    Write-Host "  slug rows: ru=$($ru.Count) us=$($us.Count) newly joined $joined"
}

# ---------------------------------------------------------------- base items
# Bases are the one set with no list page: poe2db links them only from a class page's dropdowns, so
# each one is fetched by the slug built from its English name. That is ~1 800 requests per locale, which
# is why the cache is mandatory - a second run reads from disk and costs nothing.
Write-Host '== our catalog'
$catalog = Get-Content (Join-Path $here '..\src\PoeBuilder.App\Data\Game\catalog.json') -Raw | ConvertFrom-Json

# ---------------------------------------------------------------- mods
# A mod has no page of its own: it is one <span class="explicitMod"> row inside the Modifiers page, and the
# two locales render the SAME rows in the SAME order. So an English row and its Russian twin are paired by
# position - guarded twice: the two pages must hold the same number of rows, and a pair is only kept when
# the English half really equals OUR stored text. Without that check one shifted row would mistranslate a
# modifier, which is worse than leaving it English.
function Read-ModRows {
    param([string]$Html)
    $rows = New-Object 'System.Collections.Generic.List[string]'
    foreach ($m in [regex]::Matches($Html, '<span class="explicitMod">(.*?)</span>', 'Singleline')) {
        $text = $m.Groups[1].Value
        # Numbers stay as read (a range is "(2-3)" in our data and the site's n-dash is normalised to
        # that same hyphen); only the markup is stripped.
        $text = $text -replace '<span class=.mod-value.>', '' -replace '</span>', ''
        $text = $text -replace '<[^>]+>', ''
        $text = [Net.WebUtility]::HtmlDecode($text)
        $text = ($text -replace '[\u2013\u2014\u2012]', '-') -replace '\s+', ' '
        $text = $text.Trim()
        if ($text) { $rows.Add($text) }
    }
    return $rows
}

Write-Host '== modifier lines'
$modRu = Read-ModRows (Get-Cached 'https://poe2db.tw/ru/Modifiers' 'page_ru_Modifiers.html')
$modUs = Read-ModRows (Get-Cached 'https://poe2db.tw/us/Modifiers' 'page_us_Modifiers.html')
Write-Host "  rows: ru=$($modRu.Count) us=$($modUs.Count)"
$modPairs = @{}
if ($modRu.Count -ne $modUs.Count) {
    Write-Warning 'Modifier row counts differ between locales; mod text is left English rather than guessed.'
} else {
    for ($i = 0; $i -lt $modUs.Count; $i++) {
        $en = $modUs[$i]; $ru = $modRu[$i]
        if ($ru -match '^[\x00-\x7F]+$') { continue }
        $modPairs[$en] = $ru
    }
}
Write-Host "  mod pairs: $($modPairs.Count)"

Write-Host '== base items'
$baseJoined = 0; $baseMissed = 0; $baseSeen = @{}
$baseList = if ($TreeOnly) { @() } else { $catalog.bases }
foreach ($b in $baseList) {
    if ($b.name -match '^\[DNT') { continue }            # dev-only names never exist on the site
    $slug = ($b.name -replace '[^A-Za-z0-9]+','_').Trim('_')
    if (-not $slug) { continue }
    if ($baseSeen.ContainsKey($slug)) { continue }       # the same base name is used by several items
    $baseSeen[$slug] = $true
    $pair = $null
    try { $pair = Read-BasePage $slug -WithUs } catch { $pair = $null }
    if (-not $pair -or -not $pair.ru -or -not $pair.us) { $baseMissed++; continue }
    if ($pair.ru -eq $pair.us -or $pair.ru -match '^[\x00-\x7F]+$') { $baseMissed++; continue }
    Add-NamePair -En $pair.us -Ru $pair.ru
    $baseJoined++
}
Write-Host "  bases: joined $baseJoined, unmatched $baseMissed"

# Mod text joins on OUR English template, so a modifier that poe2db words differently stays English
# rather than picking up a translation that belongs to some other modifier.
$modOut = [ordered]@{}
$modHit = 0
foreach ($m in $catalog.mods) { if ($modPairs.ContainsKey($m.text)) { $modOut[$m.id] = $modPairs[$m.text]; $modHit++ } }
foreach ($m in $catalog.jewelMods) { if ($modPairs.ContainsKey($m.text)) { $modOut[$m.id] = $modPairs[$m.text]; $modHit++ } }
Write-Host "  mods: joined $modHit of $($catalog.mods.Count + $catalog.jewelMods.Count)"

$hit = [ordered]@{ gems = 0; bases = 0; uniques = 0 }
$miss = [ordered]@{ gems = 0; bases = 0; uniques = 0 }
foreach ($g in $catalog.gems) {
    $k = ConvertTo-Key $g.name
    if ($k -and $names.Contains($k)) { $hit.gems++ } else { $miss.gems++; Add-Missing $g.name 'gems' }
}
foreach ($b in $catalog.bases) {
    $k = ConvertTo-Key $b.name
    if ($k -and $names.Contains($k)) { $hit.bases++ } else { $miss.bases++; Add-Missing $b.name 'bases' }
}
foreach ($u in $catalog.uniques) {
    $k = ConvertTo-Key $u.name
    if ($k -and $names.Contains($k)) { $hit.uniques++ } else { $miss.uniques++; Add-Missing $u.name 'uniques' }
}

# ---------------------------------------------------------------- write
$out = [ordered]@{
    provenance = [ordered]@{
        source        = 'https://poe2db.tw (RU locale)'
        treeSource    = "https://poe2db.tw/data/passive-skill-tree/$TreeVersion/data_ru.json"
        note          = 'Community wiki data, not a GGG export. Russian text is not guaranteed to match the Russian client word for word.'
        treeNodes       = $treeOut.Count
        statLinesJoined = $statJoined
        statLinesKeptEn = $statMissed
        namesJoined     = $names.Count
        mods             = $modHit
        modsTotal        = $catalog.mods.Count + $catalog.jewelMods.Count
        gems            = $hit.gems;      gemsUnmatched    = $miss.gems
        bases           = $hit.bases;     basesUnmatched   = $miss.bases
        uniques         = $hit.uniques;   uniquesUnmatched = $miss.uniques
    }
    tree  = $treeOut
    names = $names
    mods  = $modOut
}
$json = $out | ConvertTo-Json -Depth 12 -Compress
[IO.File]::WriteAllText($OutFile, $json, (New-Object Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "wrote $OutFile ($([math]::Round((Get-Item $OutFile).Length/1MB,2)) MB)"
Write-Host "  tree nodes : $($treeOut.Count)   stat lines joined $statJoined / kept EN $statMissed"
Write-Host "  names      : $($names.Count)"
Write-Host "  gems       : $($hit.gems)/$($catalog.gems.Count)"
Write-Host "  bases      : $($hit.bases)/$($catalog.bases.Count)"
Write-Host "  uniques    : $($hit.uniques)/$($catalog.uniques.Count)"
foreach ($bucket in $missing.Keys) {
    Write-Host "  MISSING $bucket : $($missing[$bucket].Count) (e.g. $(($missing[$bucket] | Select-Object -First 3) -join ', '))"
}