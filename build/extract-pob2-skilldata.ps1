# Extracts per-skill data the pinned RePoE catalog does not carry: the attack baseMultiplier (the
# "% of base weapon damage" every attack skill uses), attackSpeedMultiplier, per-level costs,
# quality stats, and every effect variant's ordered stat-id list with its per-level values.
#
# Source: PathOfBuilding-PoE2-master/src/Data/Skills/*.lua (PoB2's own game-data export in the
# workspace). Output: src/PoeBuilder.App/Data/Game/skilldata.json (sha256 goes into manifest.json).
#
# Brace depth drives the parse because the Lua has two shapes:
#  * depth 1 fields belong to the skill (name, castTime, cooldown, qualityStats, altQualityStats);
#  * `[n] = {` at depth 1 opens a variant (label/stats/constantStats/levels at depth 2);
#  * a skill without variants carries `levels = {` directly at depth 1 (Twister does);
#  * level rows sit on one line: positional values in `stats` order plus named fields.
param(
    [string]$PobRoot = "$PSScriptRoot\..\PathOfBuilding-PoE2-master\src\Data\Skills",
    [string]$OutFile = "$PSScriptRoot\..\src\PoeBuilder.App\Data\Game\skilldata.json"
)
$ErrorActionPreference = 'Stop'

function Strings-Of([string]$s) {
    [regex]::Matches($s, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { $_.Groups[1].Value }
}
function Depth-Delta([string]$line) {
    $o = 0; $c = 0
    foreach ($ch in $line.ToCharArray()) { if ($ch -eq '{') { $o++ } elseif ($ch -eq '}') { $c++ } }
    return $o - $c
}
function New-Levels() { return [ordered]@{} }
function Parse-LevelRow([string]$body) {
    $posText = $body; $named = ''
    $marker = [regex]::Match($body, '[a-zA-Z_]+ = ')
    if ($marker.Success) {
        if ($marker.Index -gt 0) { $posText = $body.Substring(0, $marker.Index) } else { $posText = '' }
        $named = $body.Substring($marker.Index)
    }
    $entry = [ordered]@{}
    if ($posText -match '[0-9]') {
        $entry['values'] = @([regex]::Matches($posText, '-?[0-9]+(?:\.[0-9]+)?') | ForEach-Object { [double]$_.Value })
    }
    $costText = ''
    if ($named -match 'cost = \{ ([^}]*)\}') { $costText = $Matches[1] }
    $namedKeys = if ($costText) { $named.Replace('cost = { ' + $costText + ' }', '') } else { $named }
    foreach ($m in [regex]::Matches($namedKeys, '([a-zA-Z_]+) = (-?[0-9.]+)')) {
        if ($m.Groups[1].Value -in @('statInterpolation', 'cost')) { continue }
        $entry[$m.Groups[1].Value] = [double]$m.Groups[2].Value
    }
    if ($costText) {
        $cost = [ordered]@{}
        foreach ($c in [regex]::Matches($costText, '([A-Za-z]+) = ([0-9.]+)')) { $cost[$c.Groups[1].Value] = [double]$c.Groups[2].Value }
        if ($cost.Count -gt 0) { $entry['cost'] = $cost }
    }
    return $entry
}


function Add-GlobalStat([string]$id, [string]$spec, $mult) {
    $list = @()
    if ($script:skillStats.Contains($id)) { $list = @($script:skillStats[$id]) }
    $entry = [ordered]@{ spec = (($spec -replace '\s+', ' ').Trim()) }
    if ($null -ne $mult) { $entry['mult'] = $mult }
    $script:skillStats[$id] = @($list + $entry)
}

function Add-StatSpec([string]$id, [string]$spec, $mult) {
    # Appends one id -> raw mod spec to the current effect's statMap (mutating the ordered dictionary keeps
    # the JSON shape: statMap[<id>] = [ { spec = "mod(\"Damage\", \"MORE\", nil)", mult = -1 }, … ]).
    $target = if ($variant) { $variant } else { $skill }
    if (-not $target.Contains('statMap')) { $target['statMap'] = [ordered]@{} }
    $list = @()
    if ($target['statMap'].Contains($id)) { $list = @($target['statMap'][$id]) }
    $entry = [ordered]@{ spec = (($spec -replace '\s+', ' ').Trim()) }
    if ($null -ne $mult) { $entry['mult'] = $mult }
    $target['statMap'][$id] = @($list + $entry)
}

$skills = [ordered]@{}
$gems = [ordered]@{}
$skillStats = [ordered]@{}
# PoB2's GLOBAL skill-stat map (Data/SkillStatMap.lua): the mods shared by every skill whose stats are
# not defined in the skill's own table (e.g. "attacks_roll_crits_twice" -> flag("BifurcateCrit")).
$globalStatMap = Join-Path (Split-Path $PobRoot -Parent) 'SkillStatMap.lua'
if (Test-Path $globalStatMap) {
    $lines = Get-Content $globalStatMap
    $currentId = $null; $spec = ''; $specBalance = 0; $entryMult = $null
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\["([^"]+)"\] = \{\s*$') {
            if ($currentId -and $spec.Trim().Length -gt 0) { Add-GlobalStat $currentId $spec $entryMult }
            $currentId = $Matches[1]; $spec = ''; $specBalance = 0; $entryMult = $null
            continue
        }
        if (-not $currentId) { continue }
        if ($trimmed -match '^(mod|flag|min|max)\(') {
            $spec = if ($spec.Length -gt 0) { $spec + ' ' + $trimmed } else { $trimmed }
            $specBalance += ([regex]::Matches($trimmed, '\(')).Count - ([regex]::Matches($trimmed, '\)')).Count
            continue
        }
        if ($specBalance -gt 0 -and $trimmed.Length -gt 0) { $spec += ' ' + $trimmed; $specBalance += ([regex]::Matches($trimmed, '\(')).Count - ([regex]::Matches($trimmed, '\)')).Count; continue }
        if ($trimmed -match '^mult = (-?[0-9.]+)') { $entryMult = [double]$Matches[1]; continue }
        if ($trimmed -match '^\}') {
            if ($spec.Trim().Length -gt 0) { Add-GlobalStat $currentId $spec $entryMult }
            $currentId = $null; $spec = ''; $entryMult = $null
        }
    }
}

$gemsFile = Join-Path (Split-Path $PobRoot -Parent) 'Gems.lua'
if (Test-Path $gemsFile) {
    $glines = Get-Content $gemsFile
    $cur = $null
    foreach ($line in $glines) {
        if ($line -match '^\s*\["([^"]+)"\] = \{\s*$') {
            if ($cur -and $cur.gameId) { $gems[$cur.gameId] = [ordered]@{ key = $cur.key; name = $cur.name; grantedEffectId = $cur.grantedEffectId } }
            $cur = [ordered]@{ key = $Matches[1]; name = ''; gameId = ''; grantedEffectId = '' }
            continue
        }
        if (-not $cur) { continue }
        if ($line -match '^\s*name = "([^"]*)"') { $cur.name = $Matches[1] }
        elseif ($line -match '^\s*gameId = "([^"]*)"') { $cur.gameId = $Matches[1] }
        elseif ($line -match '^\s*grantedEffectId = "([^"]*)"') { $cur.grantedEffectId = $Matches[1] }
        elseif ($line -match '^\}') {
            if ($cur.gameId) { $gems[$cur.gameId] = [ordered]@{ key = $cur.key; name = $cur.name; grantedEffectId = $cur.grantedEffectId } }
            $cur = $null
        }
    }
}
foreach ($file in Get-ChildItem -Path $PobRoot -Filter '*.lua' | Sort-Object Name) {
    $lines = Get-Content $file.FullName
    $skill = $null; $variant = $null; $depth = 0; $levelsDepth = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^skills\["([^"]+)"\] = \{$') {
            if ($skill) { $skills[$skill.id] = $skill }
            $skill = [ordered]@{ id = $Matches[1]; file = $file.Name; name = ''; castTime = $null; cooldown = $null
                qualityStats = @(); altQualityStats = @(); levels = $null; constants = $null; stats = @(); variants = [ordered]@{} }
            $variant = $null; $variantDepth = -1; $depth = 1; $levelsDepth = -1
            continue
        }
        if (-not $skill) { continue }
        $delta = Depth-Delta $line

        if ($levelsDepth -ge 0 -and $depth -eq $levelsDepth + 1 -and $line -match '^\t+\[(\d+)\] = \{(.*)\}\s*,?\s*$') {
            $entry = Parse-LevelRow $Matches[2]
            $target = if ($variant) { $variant.levels } else { $skill.levels }
            if (-not $target) { $target = New-Levels; if ($variant) { $variant.levels = $target } else { $skill.levels = $target } }
            $target[$Matches[1]] = $entry
            $depth += $delta
            continue
        }
        if ($line -match '^\t+levels = \{\s*$') { $levelsDepth = $depth; $depth += $delta; continue }
        if ($line -match '^\t+stats = \{') {
            $acc = @($line); $j = $i; $local = 0
            while ($j -lt $lines.Count) {
                $acc += if ($j -eq $i) { @() } else { @($lines[$j]) }
                $local += Depth-Delta $lines[$j]
                if ($local -le 0) { break }
                $j++
            }
            $ids = @(Strings-Of (($acc | Where-Object { $_ }) -join ' '))
            if ($ids.Count -gt 0) { if ($variant) { $variant.stats = $ids } else { $skill.stats = $ids } }
            $i = $j; continue
        }
        if ($line -match '^\t+constantStats = \{') {
            $j = $i
            while ($j -lt $lines.Count -and $lines[$j] -notmatch '^\t+\}') {
                if ($lines[$j] -match '\{\s*"([^"]+)"\s*,\s*(-?[0-9.]+)') {
                    $target = if ($variant) { $variant.constants } else { $skill.constants }
                    if (-not $target) { $target = [ordered]@{}; if ($variant) { $variant.constants = $target } else { $skill.constants = $target } }
                    $target[$Matches[1]] = [double]$Matches[2]
                }
                $j++
            }
            $i = $j; continue
        }

        if ($line -match '^\t+statMap = \{\s*$') {
            # PoB2's own stat -> mod mapping for this effect: ["<stat id>"] = { mod("Damage","MORE",…), flag(…), }
            # plus optional mult/base scalars. The raw spec text is kept (the C# side translates the names it
            # models and reports the rest), so nothing here is guessed. Brace nesting bounds the block, not
            # indentation, because PoB2 nests statMap at different depths in different files.
            $j = $i
            $local = 0
            $currentId = $null
            $spec = ''
            $balance = 0
            $entryMult = $null
            while ($j -lt $lines.Count) {
                $inner = $lines[$j]
                $local += Depth-Delta $inner
                if ($j -gt $i) {
                    $trimmed = $inner.Trim()
                    if ($trimmed -match '^\["([^"]+)"\] = \{\s*$') {
                        if ($currentId -and $spec.Trim().Length -gt 0) { Add-StatSpec $currentId $spec $entryMult }
                        $currentId = $Matches[1]; $spec = ''; $balance = 0; $entryMult = $null
                    }
                    elseif ($currentId) {
                        if ($trimmed -match '^mult = (-?[0-9.]+)') { $entryMult = [double]$Matches[1] }
                        elseif ($trimmed -match '^(mod|flag|min|max|base)\(') {
                            $spec = if ($spec.Length -gt 0) { $spec + ' ' + $trimmed } else { $trimmed }
                            $balance += ([regex]::Matches($trimmed, '\(')).Count - ([regex]::Matches($trimmed, '\)')).Count
                            while ($balance -gt 0 -and $j + 1 -lt $lines.Count) {
                                $j++
                                $cont = $lines[$j].Trim()
                                $local += Depth-Delta $lines[$j]
                                $spec += ' ' + $cont
                                $balance += ([regex]::Matches($cont, '\(')).Count - ([regex]::Matches($cont, '\)')).Count
                            }
                        }
                    }
                }
                if ($local -le 0) { break }
                $j++
            }
            if ($currentId -and $spec.Trim().Length -gt 0) { Add-StatSpec $currentId $spec $entryMult }
            $i = $j
            continue
        }

        # A multi-line `[n] = {` that is not a level row opens an effect variant (PoB2 puts them at
        # depth 1 for some skills and at depth 2 for others, so depth only sets the variant's own base).
        if ($levelsDepth -lt 0 -and $line -match '^\t+\[(\d+)\] = \{\s*$') {
            $variant = [ordered]@{ label = ''; stats = @(); constants = [ordered]@{}; levels = New-Levels }
            $variantDepth = $depth
            $skill.variants[[string]($skill.variants.Count + 1)] = $variant
            $depth += $delta
            continue
        }
        if ($variant -and $depth -eq $variantDepth + 1 -and $line -match '^\t+label = "([^"]*)"') {
            $variant.label = $Matches[1]
            $depth += $delta
            continue
        }
        if ($depth -eq 1) {
            if ($line -match '^\tname = "([^"]*)"') { $skill.name = $Matches[1]; $depth += $delta; continue }
            if ($line -match '^\tcastTime = ([0-9.]+)') { $skill.castTime = [double]$Matches[1]; $depth += $delta; continue }
            if ($line -match '^\tcooldown = ([0-9.]+)') { $skill.cooldown = [double]$Matches[1]; $depth += $delta; continue }
            foreach ($scalar in @('baseMultiplier', 'attackSpeedMultiplier', 'dpsMultiplier', 'hitCount', 'hitInterval')) {
                if ($line -match ('^\t' + $scalar + ' = (-?[0-9.]+)')) {
                    $skill[$scalar] = [double]$Matches[1]; $depth += $delta; $done = $true; break
                }
            }
            if ($done) { $done = $false; continue }
            if ($line -match '^\t(alt)?[qQ]ualityStats = \{') {
                $isAlt = [bool]$Matches[1]
                $acc = @($line); $j = $i
                while ($acc[-1] -notmatch '\}\s*,?\s*$' -and $j + 1 -lt $lines.Count) { $j++; $acc += $lines[$j] }
                $list = @()
                foreach ($m in [regex]::Matches(($acc -join ' '), '\{\s*"([^"]+)"\s*,\s*(-?[0-9.]+)')) {
                    $list += [ordered]@{ stat = $m.Groups[1].Value; value = [double]$m.Groups[2].Value }
                }
                if ($isAlt) { $skill.altQualityStats = $list } else { $skill.qualityStats = $list }
                $i = $j; $depth += (Depth-Delta ($acc -join "`n")); continue
            }
        }


        $depth += $delta
        if ($levelsDepth -ge 0 -and $depth -le $levelsDepth) { $levelsDepth = -1 }
        if ($variant -and $variantDepth -ge 0 -and $depth -le $variantDepth) { $variant = $null; $variantDepth = -1 }
    }
    if ($skill) { $skills[$skill.id] = $skill }
}

$json = [ordered]@{
    provenance = [ordered]@{
        source = "PathOfBuilding-PoE2-master/src/Data/Skills + src/Data/Gems.lua (PoB2's own game-data export, workspace copy)"
        extractedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        note = "Per-gem data the pinned RePoE catalog lacks: attack baseMultiplier/attackSpeedMultiplier, per-level costs, qualityStats, every effect variant's ordered stat-id list with per-level values, and the gem-item -> granted-effect join."
    }
    gems = $gems
    skillStats = $skillStats
    skills = $skills
}
$jsonText = ($json | ConvertTo-Json -Depth 14 -Compress)
[System.IO.File]::WriteAllText($OutFile, $jsonText, (New-Object System.Text.UTF8Encoding($false)))
"skills extracted: $($skills.Count)"
"output: $OutFile ($([Math]::Round($jsonText.Length / 1MB, 2)) MB)"
"sha256: " + (Get-FileHash -Path $OutFile -Algorithm SHA256).Hash.ToLower()

