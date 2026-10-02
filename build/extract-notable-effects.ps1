<#
.SYNOPSIS
  Regenerates Data/Tree/notable-effects.json: the effect art the game shows under notable and keystone
  nodes, which the upstream GGG export (data.json) names only for the masteries.

.DESCRIPTION
  The pinned reference tree (PathOfBuilding-PoE2-master/src/TreeData/0_5/tree.json) carries
  "activeEffectImage" on 632 nodes; the pinned upstream export carries it on the 368 masteries only.
  This script writes the exact diff (reference minus export) as a "nodeId -> art path" map, in the
  export's own path style (.png), so TreeCatalog.LoadPinned can merge it without touching the
  byte-pinned data.json.

  The script validates that every reference id exists in the pinned data.json, that no export node loses
  its own art to the overlay, and that every referenced sprite ships in Data/Tree/Art/effect.
  After a run, refresh the pinned hash in manifest.json (files -> notable-effects.json).

.EXAMPLE
  pwsh build/extract-notable-effects.ps1
#>
[CmdletBinding()]
param(
    [string]$PobRoot = "$PSScriptRoot\..\PathOfBuilding-PoE2-master\src\TreeData\0_5\tree.json",
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exportPath = Join-Path $root 'src\PoeBuilder.App\Data\Tree\data.json'
if (-not $Destination) { $Destination = Join-Path $root 'src\PoeBuilder.App\Data\Tree\notable-effects.json' }
$effectFolder = Join-Path $root 'src\PoeBuilder.App\Data\Tree\Art\effect'

$ref = Get-Content $PobRoot -Raw | ConvertFrom-Json
$exportRaw = Get-Content $exportPath -Raw
# Regex, not ConvertFrom-Json: the export's 5 000-record "nodes" table would be materialized as a
# hashtable there, and the diff must read the real property list.
$exportIds = [regex]::Matches($exportRaw, '"(\d+)":\s*\{(?:(?!"\d+":\s*\{).)*?"activeEffectImage"', 's') |
    ForEach-Object { $_.Groups[1].Value }
$exportSet = [Linq.Enumerable]::ToHashSet($exportIds)

$map = [ordered]@{}
foreach ($node in $ref.nodes.PSObject.Properties) {
    if ($node.Value.PSObject.Properties.Name -notcontains 'activeEffectImage') { continue }
    if ($exportSet.Contains($node.Name)) { continue }
    if ($exportRaw -notmatch ('"' + [regex]::Escape($node.Name) + '":\s*\{')) {
        throw "reference node $($node.Name) is not in the pinned export"
    }
    $map[$node.Name] = $node.Value.activeEffectImage + '.png'
}

$shipped = Get-ChildItem $effectFolder -Filter '*.png' | ForEach-Object { $_.BaseName }
$unknown = $map.Values | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) } |
    Where-Object { $_ -notin $shipped } | Sort-Object -Unique
if ($unknown) {
    throw ("sprites named by the overlay but not shipped in Art/effect: " +
        [string]::Join(', ', $unknown) + " (run the tree-art step first)")
}

$json = $map | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText($Destination, $json, [Text.UTF8Encoding]::new($false))
Write-Host ("wrote {0} entries ({1} bytes) to {2}" -f $map.Count, (Get-Item $Destination).Length, $Destination)