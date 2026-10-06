<#
.SYNOPSIS
Reads the "DataBase mods" archive (ENGLISH /// RUSSIAN lines) and adds the modifier text to
Data/Game/locale-ru.json.

.DESCRIPTION
The tree, gems, bases and uniques are joined from poe2db by name. Modifiers are different: a mod is not a
page but a line inside a per-class page, so poe2db exposes no stable id for one and the previous scrape
found none. The archive in Data/DataBase mods already holds those lines as explicit EN/RU pairs, which
is the reliable join: a modifier is matched on ITS OWN ENGLISH TEMPLATE, not on its position in a page.

The archive writes the range separator as an em dash ("(1—2)") where our pinned catalog writes a
hyphen ("(1-2)"), and the archive capitalises some words the catalog does not ("Damage" vs "damage").
Both sides are therefore folded (dashes unified, case and whitespace collapsed) before they are compared,
and a pair is only kept when the folded English really equals the folded template we ship. A modifier
that does not match stays English rather than picking up somebody else's translation.
#>
[CmdletBinding()]
param(
    # $PSScriptRoot is not bound yet while the param block runs under -File, so these default to $null
    # here and are filled from $MyInvocation.MyCommand.Path just below.
    [string]$Archive,
    [string]$LocaleFile,
    [string]$Catalog
)

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Archive)    { $Archive    = Join-Path $here '..\src\PoeBuilder.App\Data\DataBase mods' }
if (-not $LocaleFile) { $LocaleFile = Join-Path $here '..\src\PoeBuilder.App\Data\Game\locale-ru.json' }
if (-not $Catalog)    { $Catalog    = Join-Path $here '..\src\PoeBuilder.App\Data\Game\catalog.json' }

$ErrorActionPreference = 'Stop'

function Fold {
    param([string]$Text)
    if ($null -eq $Text) { return '' }
    # All whitespace is dropped, not merely collapsed: the catalog writes "+(25-50)%" where the archive
    # writes "+(25—50) %", and that single space is the difference between a match and a miss. This folded
    # form is only ever a lookup key - the text that gets stored is taken verbatim from one side - so
    # squeezing spaces here cannot alter what is displayed.
    $t = $Text -replace '[\u2010-\u2015\u2212]', '-'
    $t = $t -replace '\s', ''
    return $t.ToLowerInvariant()
}

if (-not (Test-Path $Archive)) { throw "archive not found: $Archive" }
if (-not (Test-Path $LocaleFile)) { throw "locale file not found: $LocaleFile - run build/extract-poe2db-ru.ps1 first" }

# EN name / text pairs, keyed by the folded English template.
$pairs = @{}
$clauses = @{}
foreach ($file in Get-ChildItem $Archive -Filter '*.txt') {
    foreach ($line in [IO.File]::ReadAllLines($file.FullName, [Text.Encoding]::UTF8)) {
        if ($line -notmatch ' /// ') { continue }
        $parts = $line -split ' /// ', 2
        if ($parts.Count -ne 2) { continue }
        $en = $parts[0].Trim(); $ru = $parts[1].Trim()
        if (-not $en -or -not $ru) { continue }
        # "[Prefix, item level 1] Agile — +(11—18) to Evasion Rating" -> name "Agile", text after the dash.
        $m = [regex]::Match($en, '^\[[^\]]*\]\s*(?<name>.+?)\s+[\u2014\u2013-]\s+(?<text>.+)$')
        $name = if ($m.Success) { $m.Groups['name'].Value.Trim() } else { $en }
        $text = if ($m.Success) { $m.Groups['text'].Value.Trim() } else { '' }
        $key = Fold $text
        if (-not $key) { $key = "name:$((Fold $name))" }
        # The Russian half mirrors the English structure - "[Suffix, item level 3] аскетизма — (8—10) % ..." -
        # so the prefix is stripped here and what is stored is the Russian TEXT alone. Keeping the prefix would
        # put the tier in the shape and make an archive line look like a different sentence than ours.
        $ruText = $ru
        if ($ru -match '^[\s\[\]][^\]\u2014\u2013]*[\]\u2014\u2013]\s*.+?\s+[\u2014\u2013]\s+(?<t>.+)$') {
            $ruText = $Matches['t'].Trim()
        }
        # First writer wins: a modifier repeated across item classes carries the same wording, so a later
        # line cannot silently overwrite an earlier match with a different Russian form.
        if (-not $pairs.ContainsKey($key)) {
            $pairs[$key] = [ordered]@{ en = $name; ru = $ruText; text = $text }
        }
    }
}
Write-Host "archive pairs: $($pairs.Count)"

$data = Get-Content $Catalog -Raw | ConvertFrom-Json
$locale = Get-Content $LocaleFile -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $locale.mods) { $locale | Add-Member -NotePropertyName mods -NotePropertyValue ([ordered]@{}) }
$mods = [ordered]@{}
foreach ($p in $locale.mods.PSObject.Properties) { $mods[$p.Name] = $p.Value }

# Second pass: many archive lines are TWO modifiers that the game draws on one item, and the pinned catalog
# stores them as two separate entries. The archive writes
#     "[Prefix, item level 13] Impairing — (61—68) % increased Life Recovered; Removes 15 % of Life Recovered
#      from Mana when used"
# while the catalog holds "Adds ... IncreasedLifeRecovered" and "Removes ... LifeRecoveredFromMana" apart, so
# neither ever equals the whole line. Splitting on ';' fixes that, but only where both halves really do have
# the same number of parts: 12668 of the 12677 archive lines do, and a line where a ';' is mid-sentence ("gains
# maximum added Lightning damage equal to; the enemy's Power") must NOT be split, or the two Russian pieces
# would be welded onto the wrong English one.
foreach ($p in $pairs.Values) {
    # The pinned catalog keeps a two-stat modifier as ONE entry with a newline between the stats, while the
    # archive writes the very same two stats on one line separated by ";". Both separators are handled, so
    # "(21-26) % increased Evasion and Energy Shield\n+(17-20) to maximum Mana" meets its Russian halves.
    # The pinned catalog joins a modifier's stats with " + " ("(6-13) % increased Armour + +(5-9) to Armour"),
    # which is a separator of its own and is by far the most common one left unmatched: the archive writes the
    # same two stats separated by "; " and nothing else. Split on both.
    # The same modifier is split into its stats differently on each side: the archive uses "; ", the pinned
    # catalog a newline, and the two may even list the stats in the other order ("Instant Recovery" then
    # "50 % reduced Amount Recovered" here, the reverse there). Both orders and both separators are indexed,
    # because an entry only matches when the WHOLE set of stats lines up - a half-translated modifier would
    # lose one of its stats, which the number validation below would then catch.
    foreach ($sep in @(';', "`n")) {
        $enParts = @($p.text -split $sep); $ruParts = @($p.ru -split $sep)
        if ($enParts.Count -ne $ruParts.Count -or $enParts.Count -lt 2) { continue }
        foreach ($order in @($false, $true)) {
            $enTrim = @($enParts | ForEach-Object { $_.Trim() })
            $ruTrim = @($ruParts | ForEach-Object { $_.Trim() })
            if ($order) { [array]::Reverse($enTrim); [array]::Reverse($ruTrim) }
            foreach ($keySep in @('; ', "`n")) {
                $key = Fold ($enTrim -join $keySep)
                if (-not $pairs.ContainsKey($key) -and -not $clauses.ContainsKey($key)) {
                    $clauses[$key] = $ruTrim -join $keySep
                }
            }
        }
    }
}
Write-Host "joined pairs: $($clauses.Count)"

# Finally, index EVERY individual stat line, so a modifier the archive never spells out in full can still be
# translated stat by stat. The pinned catalog has three-stat modifiers - "(6-13) % increased Armour and Energy
# Shield + +(3-5) to Armour + +(2-4) to maximum Energy Shield" - that appear nowhere in the archive as one
# entry, but each of their three stats is an ordinary stat line that is. Rebuilding the Russian sentence from
# those three is exact rather than a guess, and the number validation below refuses the result unless every
# number of the English entry comes back in the Russian one, in order.
$statRu = @{}
foreach ($p in $pairs.Values) {
    $ruParts = @($p.ru -split ';') + @($p.ru -split "`n")
    $i = 0
    foreach ($part in @($p.text -split ';') + @($p.text -split "`n")) {
        $t = $part.Trim()
        if (-not $t -or $i -ge $ruParts.Count) { continue }
        $key = Fold $t
        if (-not $statRu.ContainsKey($key)) { $statRu[$key] = $ruParts[$i].Trim() }
        $i++
    }
}
Write-Host "stat lines: $($statRu.Count)"

function Get-StatRussian {
    param([string]$English, [string]$Glue)
    $out = @()
    # The catalog writes its stats "…Energy Shield\n+(8-13) to Stun Threshold": the separator is a newline
    # and the space that precedes the following stat's own "+" sign, so the stat list is split on the newline
    # or on " + ", and never on a bare "+" (which is far too common inside a stat to mean a separator).
    # A fourth spelling appears on the corrupted-mod entries: "(6-10) % increased Accuracy Rating | (6-10) %
    # increased Critical Hit Chance". All of them are split here; a bare "+" is never one, because it is far too
    # common inside a single stat to mean a separator.
    foreach ($part in @($English -split "`n" | ForEach-Object { $_ -split ' \+ ' } | ForEach-Object { $_ -split ' \| ' })) {
        $t = $part.Trim()
        if (-not $t) { continue }
        $key = Fold $t
        if (-not $statRu.ContainsKey($key)) { return $null }
        $out += $statRu[$key]
    }
    if ($out.Count -eq 0) { return $null }
    return ($out -join $Glue)
}


# Third pass, for the modifiers whose numbers do not match any archive line even though the wording does.
# The pinned catalog and the archive disagree on some ranges (the catalog says "(10-8) % reduced Charm
# Charges used", which the archive spells "(8-10) %" - the two sides even order the bounds differently), and
# on which tiers a class offers. So an exact match is too strict and loses real modifiers.
#
# The shape is the sentence with every number replaced by "#". Two lines share a shape only when they differ
# in nothing but their numbers, and for those the Russian wording is the same sentence with the same numbers
# replaced, so the catalog's own numbers are substituted back in. A shape whose Russian side is ambiguous -
# two different Russian sentences behind one shape - is left alone rather than guessed at.
$ruByShape = @{}
foreach ($p in $pairs.Values) {
    $shape = Fold ($p.text -replace '\d+(\.\d+)?', '#')
    if (-not $ruByShape.ContainsKey($shape)) { $ruByShape[$shape] = @{} }
    if (-not $ruByShape[$shape].ContainsKey($p.ru)) { $ruByShape[$shape][$p.ru] = $true }
}
$cleanShapes = @{}
foreach ($shape in $ruByShape.Keys) {
    if ($ruByShape[$shape].Count -ne 1) { Write-Host "  skipping ambiguous shape: $shape"; continue }
    $cleanShapes[$shape] = @($ruByShape[$shape].Keys)[0]
}
Write-Host "clean shapes: $($cleanShapes.Count)"

# Fourth pass: a handful of catalog entries store their bounds the wrong way round - "(10-8) % reduced Charm
# Charges used" where the game and the archive both say "(8-10) %". That is a defect in the pinned catalog,
# not a missing translation, so the range is flipped for the lookup and our own (wrong) order is written back
# unchanged: the numbers a player sees still come from the catalog, exactly as before this script ran.
function Flip-Range {
    param([string]$Text)
    return [regex]::Replace($Text, '\((\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)\)', {
        param($m)
        $lo = [double]$m.Groups[1].Value; $hi = [double]$m.Groups[2].Value
        if ($lo -gt $hi) { '(' + $m.Groups[2].Value + '-' + $m.Groups[1].Value + ')' } else { $m.Value }
    })
}

function Get-Russian {
    param([string]$English)
    $numbers = [regex]::Matches($English, '\d+(?:\.\d+)?') | ForEach-Object { $_.Value }
    $shape = Fold ($English -replace '\d+(\.\d+)?', '#')
    if (-not $cleanShapes.ContainsKey($shape)) { return $null }
    $ru = $cleanShapes[$shape]
    # The Russian template's numbers are the archive's; ours are the catalog's, and they are the ones that
    # belong on this item, so they are written back in the order both sentences place them.
    $ruNumbers = [regex]::Matches($ru, '\d+(?:\.\d+)?') | ForEach-Object { $_.Value }
    if ($ruNumbers.Count -ne $numbers.Count) { return $null }
    # The replacement is written out by hand rather than with a Replace callback: a scriptblock passed to
    # [regex]::Replace runs in its own scope, so a counter incremented inside it never comes back, and every
    # number in the line would come out as the first one - which is exactly what the validation caught.
    $sb = New-Object Text.StringBuilder
    $last = 0; $i = 0
    foreach ($match in [regex]::Matches($ru, '\d+(?:\.\d+)?')) {
        [void]$sb.Append($ru.Substring($last, $match.Index - $last))
        [void]$sb.Append($numbers[$i]); $i++
        $last = $match.Index + $match.Length
    }
    [void]$sb.Append($ru.Substring($last))
    return $sb.ToString()
}

$exact = 0; $byClause = 0; $byShape = 0; $byStat = 0; $miss = 0
$all = @($data.mods) + @($data.jewelMods)
foreach ($m in $all) {
    $ruText = $null
    $key = Fold $m.text
    # A catalog entry is normally one archive line; the joined map covers the same stats written with a different
    # separator ("(6-13) % increased Armour + +(5-9) to Armour"). Try the entry whole, then with its range bounds
    # the other way round, and only then fall back to looking it up part by part.
    $flippedKey = Fold (Flip-Range $m.text)
    if ($flippedKey -ne $key -and $pairs.ContainsKey($flippedKey)) { $key = $flippedKey; $flipped = $true } else { $flipped = $false }
    if ($pairs.ContainsKey($key)) { $ruText = $pairs[$key].ru; if ($flipped) { $byClause++ } else { $exact++ } }
    elseif ($clauses.ContainsKey($key)) { $ruText = $clauses[$key]; $byClause++ }
    else {
        $found = $false
        $parts = @($m.text -split "`n") + @($m.text -split ';') + @($m.text -split ' \+ ')
        foreach ($part in $parts) {
            $pk = Fold $part.Trim()
            if (-not $pk) { continue }
            if ($pairs.ContainsKey($pk)) { $ruText = $pairs[$pk].ru; $found = $true; break }
            if ($clauses.ContainsKey($pk)) { $ruText = $clauses[$pk]; $found = $true; break }
            # Same entry with its range bounds the right way round.
            $fk = Fold (Flip-Range $part.Trim())
            if ($pairs.ContainsKey($fk)) { $ruText = $pairs[$fk].ru; $found = $true; break }
            if ($clauses.ContainsKey($fk)) { $ruText = $clauses[$fk]; $found = $true; break }
        }
        # Every branch must FALL THROUGH to the assignment below rather than "continue": a "continue" here
        # silently skipped the store, so the mod was counted as joined and then never written out.
        if ($found) { $byClause++ }
        else {
            $ruText = Get-Russian $m.text
            if (-not $ruText) { $ruText = Get-Russian (Flip-Range $m.text) }
            if ($ruText) { $byShape++ }
            else {
                $ruText = Get-StatRussian $m.text ' + '
                if (-not $ruText) { $ruText = Get-StatRussian $m.text "`n" }
                if ($ruText) { $byStat++ } else { $miss++; continue }
            }
        }
    }
    $mods[$m.id] = [ordered]@{ en = $m.text; ru = $ruText }
}
Write-Host "mods joined: $($exact + $byClause + $byShape) of $($all.Count) (exact $exact, by clause $byClause, by shape $byShape, by stat $byStat, unmatched $miss)"

# Numbers must agree between the English template and the Russian line, in the same order and the same count.
# A range's bounds are compared as an unordered pair, because the flip pass above deliberately looks a
# modifier up the other way round: "(10-8) %" on our side against "(8-10) %" in the archive is the SAME modifier.
function Test-Numbers {
    param([string]$En, [string]$Ru)
    $norm = {
        param($s)
        [regex]::Replace($s, '\((\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)\)', {
            param($m)
            $a = [double]$m.Groups[1].Value; $b = [double]$m.Groups[2].Value
            if ($a -gt $b) { $t = $a; $a = $b; $b = $t }
            '(' + $a + '-' + $b + ')'
        })
    }
    $enNums = [regex]::Matches((& $norm $En), '\d+(?:\.\d+)?') | ForEach-Object { $_.Value }
    $ruNums = [regex]::Matches((& $norm $Ru), '\d+(?:\.\d+)?') | ForEach-Object { $_.Value }
    return ($enNums -join ',') -eq ($ruNums -join ',')
}

# Validation: a joined Russian line must carry OUR numbers, in OUR order, and no others. A shape match that
# silently shifted "+(20-30) % Fire" onto "+(30-20) % Cold" would be worse than no translation at all, so
# anything that fails here is dropped rather than written out.
$bad = 0
foreach ($id in @($mods.Keys)) {
    $en = $mods[$id].en; $ru = $mods[$id].ru
    if (-not (Test-Numbers $en $ru)) { $bad++; if ($bad -le 5) { Write-Host "  number mismatch, dropped: '$en' -> '$ru'" } }
    if ($ru -notmatch '[\u0400-\u04FF]') { $bad++; if ($bad -le 5) { Write-Host "  not Russian, dropped: '$ru'" } }
}
Write-Host "validation failures: $bad"
if ($bad -gt 0) {
    foreach ($id in @($mods.Keys)) {
        if (-not (Test-Numbers $mods[$id].en $mods[$id].ru) -or $mods[$id].ru -notmatch '[\u0400-\u04FF]') {
            $mods.Remove($id)
        }
    }
    Write-Host "dropped $bad entries"
}

$locale.mods = $mods
Write-Host "entries written: $($mods.Count) of $($all.Count) catalog modifiers"
$json = $locale | ConvertTo-Json -Depth 12 -Compress
[IO.File]::WriteAllText($LocaleFile, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "wrote $LocaleFile ($([math]::Round((Get-Item $LocaleFile).Length/1MB,2)) MB)"
