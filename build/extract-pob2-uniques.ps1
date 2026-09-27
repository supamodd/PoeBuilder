# Extracts every unique item PoB2 ships (name, base type, item class, variant list, implicits count
# and the modifier lines with their {variant:N}/{tags:...} filters) into pinned data, because the
# pinned RePoE export carries unique identities only (name/class/icon, no modifiers).
#
# Source: PathOfBuilding-PoE2-master/src/Data/Uniques/*.lua (PoB2's own game-data export, workspace copy).
# Output: src/PoeBuilder.App/Data/Game/uniques.json  (+ sha256 recorded in manifest.json)
#
# The Lua files are one flat table of `[[ ... ]]` blocks, one per unique:
#   <name>
#   <base type>
#   [Source: ... / League: ... / Variant: ... / Requires Level N / Implicits: N]
#   [{variant:N,M}][{tags:a,b}]<modifier line>
# A following line that starts lowercase continues the previous modifier (PoB2 joins them when the
# first line alone does not parse: Classes/Item.lua).
param(
    [string]$PobRoot = "$PSScriptRoot\..\PathOfBuilding-PoE2-master\src\Data\Uniques",
    [string]$OutFile = "$PSScriptRoot\..\src\PoeBuilder.App\Data\Game\uniques.json"
)
$ErrorActionPreference = 'Stop'

$classByFile = @{
    amulet = 'Amulet'; belt = 'Belt'; body = 'Body Armour'; boots = 'Boots'; bow = 'Bow'; claw = 'Claw'
    crossbow = 'Crossbow'; dagger = 'Dagger'; flail = 'Flail'; flask = 'Flask'; focus = 'Focus'
    gloves = 'Gloves'; helmet = 'Helmet'; jewel = 'Jewel'; mace = 'Mace'; quiver = 'Quiver'; ring = 'Ring'
    sceptre = 'Sceptre'; shield = 'Shield'; spear = 'Spear'; staff = 'Staff'; sword = 'Sword'
    talisman = 'Amulet'; tincture = 'Tincture'; wand = 'Wand'; fishing = 'Fishing Rod'
    soulcore = 'Soul Core'; incursionlimb = 'Incursion Limb'; traptool = 'Trap Tool'; race = 'Race Reward'
}

$uniques = @()
$withVariants = 0
foreach ($file in Get-ChildItem -Path $PobRoot -Filter '*.lua' | Sort-Object Name) {
    $text = Get-Content $file.FullName -Raw
    $base = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $itemClass = if ($classByFile.ContainsKey($base)) { $classByFile[$base] } else { $base }
    foreach ($block in [regex]::Matches($text, '(?s)\[\[(.*?)\]\]')) {
        $lines = @($block.Groups[1].Value -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        if ($lines.Count -lt 2) { continue }
        $variants = @()
        $implicits = 0
        $requiresLevel = 0
        $source = ''
        $selectedVariant = 0
        $mods = @()
        foreach ($raw in $lines[2..($lines.Count - 1)]) {
            if ($raw -match '^Variant:\s*(.*)$') { $variants += $Matches[1].Trim(); continue }
            if ($raw -match '^Selected Variant:\s*(\d+)') { $selectedVariant = [int]$Matches[1]; continue }
            if ($raw -match '^Implicits:\s*(\d+)') { $implicits = [int]$Matches[1]; continue }
            if ($raw -match '^Requires Level\s*(\d+)') { $requiresLevel = [int]$Matches[1]; continue }
            if ($raw -match '^(Source|League|DropLevel|Note):\s*(.*)$') { $source = $Matches[2].Trim(); continue }
            # Headers that are not modifiers: variant bookkeeping (PoB2 keeps an alt-variant list per
            # item), socket counts and the item's own labels.
            if ($raw -match '^(Has (Alt )?Variant|Selected Alt Variant|Sockets:|Limited to:|Corrupted$|Twice Corrupted$|Item Class:|Rarity:|LevelReq:|Upgraded|Second Modifier:|This item can be anointed|Unmodifiable)') { continue }
            $line = $raw
            $lineVariants = @()
            while ($line -match '^\{variant:([0-9,]+)\}') {
                $lineVariants += @($Matches[1] -split ',' | ForEach-Object { [int]$_ })
                $line = $line.Substring($Matches[0].Length)
            }
            $tags = @()
            if ($line -match '^\{tags:([^}]*)\}') {
                $tags = @($Matches[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
                $line = $line.Substring($Matches[0].Length)
            }
            if ($line.Trim().Length -eq 0) { continue }
            $mods += [ordered]@{ line = $line.Trim(); variants = @($lineVariants | Sort-Object -Unique); tags = $tags }
        }
        $joined = @()
        foreach ($mod in $mods) {
            $firstChar = $mod.line.Substring(0, 1)
            if ($joined.Count -gt 0 -and $firstChar -cmatch '[a-z]') {
                $previous = $joined[$joined.Count - 1]
                $previous.line = $previous.line + ' ' + $mod.line
                if ($previous.variants.Count -eq 0) { $previous.variants = $mod.variants }
                continue
            }
            $joined += $mod
        }
        $uniques += [ordered]@{
            name = $lines[0]; baseType = $lines[1]; itemClass = $itemClass; file = $file.Name
            variants = @($variants); selectedVariant = $selectedVariant; implicits = $implicits; requiresLevel = $requiresLevel
            source = $source; mods = @($joined)
        }
        if ($variants.Count -gt 0) { $withVariants++ }
    }
}

$json = [ordered]@{
    provenance = [ordered]@{
        source = "PathOfBuilding-PoE2-master/src/Data/Uniques/*.lua (PoB2's own game-data export, workspace copy)"
        extractedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        note = "Every unique identity with its base type, variant list and modifier lines (variant/tags filters kept). The pinned RePoE catalog ships identities only, so this is what makes unique modifiers available to the calculator and the item editors."
    }
    uniques = @($uniques)
}
$jsonText = ($json | ConvertTo-Json -Depth 10 -Compress)
[System.IO.File]::WriteAllText($OutFile, $jsonText, (New-Object System.Text.UTF8Encoding($false)))
"uniques extracted: $($uniques.Count)"
"with variants: $withVariants"
"output: $OutFile ($([Math]::Round($jsonText.Length / 1MB, 2)) MB)"
"sha256: " + (Get-FileHash -Path $OutFile -Algorithm SHA256).Hash.ToLower()
