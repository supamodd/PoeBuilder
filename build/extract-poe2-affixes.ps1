# Extracts the full affix table the pinned RePoE snapshot states for every base — including the ones the
# shipped catalog's per-class pools do not reach — plus the jewel bases the catalog was built without.
#
# Why it is needed (measured against the same pinned snapshot, see docs/VALIDATION.md 0.9.24):
#   * catalog.json pools mods by the base's *item-class* tag, so a Strength body armour can never be given
#     "+(278-310) to Armour" (LocalIncreasedPhysicalDamageReductionRating11), although the snapshot spawns
#     that mod on str_armour bases — the mod is in the catalog, it is simply in no pool. The same gap hits
#     armour pieces, shields, foci, boots, gloves, helmets and the weapon classes.
#   * PoE2 jewels (Ruby/Emerald/Sapphire/Diamond, Timeless, Time-Lost …) are item_class "Jewel" and were
#     left out of catalog.json entirely, so the jewel editor had to treat jewels as baseless items with
#     affixes carrying no kind and no level.
#
# Filter (identical to the shape docs/THIRD-PARTY-NOTICES.md documents for the catalog): item domain,
# prefix/suffix generation, not essence-only, no granted effects, no added tags. English display markup
# "[Tag|Display]" is resolved to "Display", exactly like the catalog's own text.
#
# Pools are keyed by the snapshot's spawn tag, not by base: a base is offered the union of its own tags —
# which is RePoE's own rule — and the umbrella "default" tag is deliberately not used as a pool, because
# RePoE gives it a weight for mods whose concrete tags are absent (it would add every catch-all mod to
# every base). The shipped catalog pool stays in force on top, so nothing an older dataset allowed is lost.
#
# Output: src/PoeBuilder.App/Data/Game/affixes.json (+ sha256 recorded in manifest.json)
param(
    [string]$Snapshot = "$PSScriptRoot\catalog-pinned",
    [string]$Catalog = "$PSScriptRoot\..\src\PoeBuilder.App\Data\Game\catalog.json",
    [string]$OutFile = "$PSScriptRoot\..\src\PoeBuilder.App\Data\Game\affixes.json"
)
$ErrorActionPreference = 'Stop'

function Resolve-Markup([string]$text) {
    # "[Curse|Curses]" → "Curses" (the game shows the part after the pipe); "[Armour]" → "Armour".
    $resolved = [regex]::Replace($text, '\[([^\[\]|]*)\|([^\[\]]*)\]', '$2')
    return [regex]::Replace($resolved, '\[([^\[\]]*)\]', '$1')
}


$mods = (Get-Content -Raw (Join-Path $Snapshot 'mods.json') | ConvertFrom-Json).PSObject.Properties
$bases = (Get-Content -Raw (Join-Path $Snapshot 'base_items.json') | ConvertFrom-Json).PSObject.Properties

# Jewel affixes are not in the "item" domain in this snapshot (they answer to "misc" and are told apart
# by the tags they spawn on), so they are picked up by the tags of the jewel bases themselves.
$jewelSpawnTags = @()
foreach ($base in $bases) {
    if ($base.Value.item_class -ne 'Jewel') { continue }
    foreach ($tag in @($base.Value.tags)) { if ($tag -ne 'default' -and $jewelSpawnTags -notcontains $tag) { $jewelSpawnTags += $tag } }
}
function Test-JewelSpawn($mod) {
    foreach ($weight in @($mod.spawn_weights)) {
        if ([int]$weight.weight -le 0) { continue }
        if ($jewelSpawnTags -contains [string]$weight.tag) { return $true }
    }
    return $false
}

$affixes = @()
$tagPools = [ordered]@{}
$skipped = @{ essence = 0; effects = 0; tags = 0; generation = 0; domain = 0 }
foreach ($mod in $mods) {
    $v = $mod.Value
    $isItem = $v.domain -eq 'item'
    $isJewel = -not $isItem -and $v.domain -eq 'misc' -and (Test-JewelSpawn $v)
    if (-not $isItem -and -not $isJewel) { $skipped.domain++; continue }
    if ($v.generation_type -ne 'prefix' -and $v.generation_type -ne 'suffix') { $skipped.generation++; continue }
    if ($v.is_essence_only -eq $true) { $skipped.essence++; continue }
    if ($null -ne $v.grants_effects -and @($v.grants_effects).Count -gt 0) { $skipped.effects++; continue }
    if ($null -ne $v.adds_tags -and @($v.adds_tags).Count -gt 0) { $skipped.tags++; continue }
    $stats = @()
    foreach ($stat in @($v.stats)) {
        $stats += [pscustomobject][ordered]@{ id = $stat.id; min = $stat.min; max = $stat.max }
    }
    $affixes += [pscustomobject][ordered]@{
        id = $mod.Name
        name = [string]$v.name
        kind = [string]$v.generation_type
        level = [int]$v.required_level
        groups = @($v.groups)
        text = (Resolve-Markup ([string]$v.text))
        stats = @($stats)
    }
    foreach ($weight in @($v.spawn_weights)) {
        if ([int]$weight.weight -le 0) { continue }
        $tag = [string]$weight.tag
        if ($tag -eq 'default') { continue }
        if (-not $tagPools.Contains($tag)) { $tagPools[$tag] = @() }
        $tagPools[$tag] += $mod.Name
    }
}


$jewelBases = @()
$jewelTags = @()
foreach ($base in $bases) {
    $v = $base.Value
    if ($v.item_class -ne 'Jewel') { continue }
    foreach ($tag in @($v.tags)) { if ($jewelTags -notcontains $tag) { $jewelTags += $tag } }
    $jewelBases += [pscustomobject][ordered]@{
        id = $base.Name
        name = [string]$v.name
        itemClass = 'Jewel'
        className = 'Jewels'
        dropLevel = [int]$v.drop_level
        propertiesText = ''
        implicits = @()
        implicitStats = @()
        tags = @($v.tags)
        modPool = ''
        art = [string]$v.visual_identity.dds_file
    }
}

# How much of this the shipped catalog already carries (reported for the release notes, not used below).
$catalogIds = New-Object System.Collections.Generic.HashSet[string]
foreach ($m in (Get-Content -Raw $Catalog | ConvertFrom-Json).mods) { [void]$catalogIds.Add($m.id) }
$inCatalog = 0
foreach ($a in $affixes) { if ($catalogIds.Contains($a.id)) { $inCatalog++ } }

$json = [ordered]@{
    provenance = [ordered]@{
        source = 'build/catalog-pinned/{mods,base_items}.json (pinned RePoE PoE2 export b818b843, version 4.5.5.2)'
        generator = 'build/extract-poe2-affixes.ps1'
        extractedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        note = 'Every regular prefix/suffix affix of the pinned snapshot with the tags it may spawn on, plus the PoE2 jewel bases. Pools are keyed by spawn tag (the umbrella "default" tag is excluded); catalog.json pools remain in force on top.'
    }
    mods = @($affixes)
    tagPools = $tagPools
    jewelBases = @($jewelBases)
    jewelTags = @($jewelTags)
}
$jsonText = ($json | ConvertTo-Json -Depth 8 -Compress)
[System.IO.File]::WriteAllText($OutFile, $jsonText, (New-Object System.Text.UTF8Encoding($false)))

"affixes: $($affixes.Count) (prefix $((@($affixes | Where-Object { $_.kind -eq 'prefix' })).Count), suffix $((@($affixes | Where-Object { $_.kind -eq 'suffix' })).Count))"
"already in catalog.json: $inCatalog"
"tag pools: $($tagPools.Count), entries: $((($tagPools.Values | ForEach-Object { $_.Count }) | Measure-Object -Sum).Sum)"
"jewel bases: $($jewelBases.Count) (tags: $($jewelTags -join ', '))"
"skipped: domain $($skipped.domain), generation $($skipped.generation), essence $($skipped.essence), granted effects $($skipped.effects), adds tags $($skipped.tags)"
"output: $OutFile ($([Math]::Round($jsonText.Length / 1MB, 2)) MB)"
"sha256: " + (Get-FileHash -Path $OutFile -Algorithm SHA256).Hash.ToLower()
