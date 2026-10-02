<#
.SYNOPSIS
  Cuts PoB2's class and ascendancy backdrops out of its BC7 atlases at full resolution.

.DESCRIPTION
  First half of the conversion step described in docs/VALIDATION.md: this script runs the
  build/tree-art-decoder tool (a self-contained BC7 decoder) over PoB2's
  ascendancy-background_1500_1500 and ascendancy-background_4000_4000 atlases and writes
  Data/Tree/Art/class at the sizes the renderer draws with:

  - 1500 for a base class and for every ascendancy: the slice IS the art — 1500 world units, no
    downscale at all;
  - 2000 for BGTree and BGTreeActive: the ring art is a 4000x4000 slice, and PoB2 halves it when it
    draws (Classes/PassiveTreeView.lua:609-615), so 2000 is exactly what the renderer shows.

  The sprite-to-slice mapping is read from the atlas's own ddsCoords table in TreeData/<ver>/tree.lua
  (1-based slice numbers), which is what PoB2 itself uses, so a new PoB2 refresh needs no script edit.

  These are the sizes the test "Tree art: class and ascendancy backdrops are shipped and placed like
  PoB2" pins, so a run that changes them has to update that test and docs/VALIDATION.md in the same commit.

.PARAMETER TreeData
  PoB2's TreeData version folder (the one holding ascendancy-background_1500_1500_BC7.dds.zst,
  ascendancy-background_4000_4000_BC7.dds.zst and tree.lua).

.EXAMPLE
  pwsh build/extract-tree-art.ps1 -TreeData <PoB2>\src\TreeData\0_5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TreeData,
    [string]$Destination
)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $root 'src\PoeBuilder.App\Data\Tree\Art\class' }
if (-not (Test-Path $TreeData)) { throw "TreeData folder not found: $TreeData" }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# Which sprites ship: the base class backdrops PoB2 itself draws (tree.json's classes[] — the same
# set Core/Tree/TreeClassArt.cs mirrors), plus every ascendancy circle the pinned tree has.
$treeJson = Get-Content (Join-Path $TreeData 'tree.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$dataPath = Join-Path $root 'src\PoeBuilder.App\Data\Tree\data.json'
$data = Get-Content $dataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$wanted = @('BGTree', 'BGTreeActive')
$wanted += $treeJson.classes | ForEach-Object { 'Classes' + $_.name }
$wanted += $data.classes | ForEach-Object { $_.ascendancies } |
    Where-Object { $_.name } | ForEach-Object { 'Classes' + $_.name } | Sort-Object -Unique

# ---- sprite -> slice mapping, straight from PoB2's ddsCoords table (1-based slice numbers)
function Get-AtlasMapping([string]$LuaPath, [string]$AtlasName) {
    $text = Get-Content $LuaPath -Raw
    $bs = [char]92
    $dq = [char]34
    $lb = [char]91
    $rb = [char]93
    $nameEsc = [regex]::Escape($AtlasName)
    $atlasRe = $bs + $lb + $dq + $nameEsc + $dq + $rb + $bs + 's*' + $bs + 's*' + '=' + $bs + '{(?s)(.*?)' + $bs + '},'
    $match = [regex]::Match($text, $atlasRe)
    if (-not $match.Success) { throw "atlas $AtlasName not found in ddsCoords: $LuaPath" }
    $entryRe = '(?:' + $bs + $lb + $dq + '?([A-Za-z0-9_ .' + $bs + '-]+)' + $dq + '?' + $bs + $rb + '|([A-Za-z0-9_]+))' + $bs + 's*' + '=' + $bs + 's*' + '(' + $bs + 'd+)'
    $map = @{}
    foreach ($entry in [regex]::Matches($match.Groups[1].Value, $entryRe)) {
        $name = $entry.Groups[1].Value
        if ($name -eq '') { $name = $entry.Groups[2].Value }
        $map[$name] = [int]$entry.Groups[3].Value
    }
    if ($map.Count -eq 0) { throw "no entries parsed for $AtlasName" }
    return $map
}

$luaPath = Join-Path $TreeData 'tree.lua'
$map1500 = Get-AtlasMapping $luaPath 'ascendancy-background_1500_1500_BC7.dds.zst'
$map4000 = Get-AtlasMapping $luaPath 'ascendancy-background_4000_4000_BC7.dds.zst'

$missing = $wanted | Where-Object { -not $map1500.ContainsKey($_) -and -not $map4000.ContainsKey($_) }
if ($missing) { throw "no slice in the atlases for: $($missing -join ', ')" }

# ---- the tree-art-decoder tool (built once; it unzstds the .dds.zst and decodes the BC7 slices)
$decoderCsproj = Join-Path $PSScriptRoot 'tree-art-decoder\tree-art-decoder.csproj'
$decoder = Join-Path (Split-Path $decoderCsproj -Parent) 'bin\Debug\net10.0\tree-art-decoder.dll'
if (-not (Test-Path $decoder)) {
    dotnet build $decoderCsproj -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "tree-art-decoder build failed" }
}

# ---- extract: 1500 slices ship as-is, 4000 slices are halved to the 2000 PoB2 draws the ring at
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("tree-art-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$spec1500 = ($wanted | Where-Object { $map1500.ContainsKey($_) } |
    ForEach-Object { "{0}={1}" -f $_, ($map1500[$_] - 1) }) -join ';'
$spec4000 = ($wanted | Where-Object { $map4000.ContainsKey($_) } |
    ForEach-Object { "{0}={1}" -f $_, ($map4000[$_] - 1) }) -join ';'
& dotnet $decoder 'extract' (Join-Path $TreeData 'ascendancy-background_1500_1500_BC7.dds.zst') $work $spec1500
if ($LASTEXITCODE -ne 0) { throw "1500 atlas extraction failed" }
& dotnet $decoder 'extract' (Join-Path $TreeData 'ascendancy-background_4000_4000_BC7.dds.zst') $work $spec4000
if ($LASTEXITCODE -ne 0) { throw "4000 atlas extraction failed" }

function Save-Resized([string]$In, [string]$Out, [int]$Size) {
    $image = New-Object System.Drawing.Bitmap($In)
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.DrawImage($image, 0, 0, $Size, $Size)
    $graphics.Dispose()
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose(); $image.Dispose()
}

$rows = @()
foreach ($name in ($wanted | Sort-Object -Unique)) {
    $out = Join-Path $Destination ($name + '.png')
    if ($map4000.ContainsKey($name)) { Save-Resized (Join-Path $work ($name + '.png')) $out 2000 }
    else { Copy-Item (Join-Path $work ($name + '.png')) $out -Force }
    $kind = if ($map4000.ContainsKey($name)) { 'ring' } elseif ($treeJson.classes.name -contains ($name -replace '^Classes', '')) { 'class' } else { 'ascendancy' }
    $rows += [pscustomobject]@{ Name = $name; Kind = $kind; KB = [math]::Round((Get-Item $out).Length / 1KB) }
}
Remove-Item $work -Recurse -Force

$rows | Format-Table -AutoSize
$rows | Group-Object Kind | ForEach-Object {
    "{0}: {1} file(s), {2} KB" -f $_.Name, $_.Count, ($_.Group | Measure-Object KB -Sum).Sum
}
"total: {0} file(s), {1:0.0} MB in {2}" -f $rows.Count, (($rows | Measure-Object KB -Sum).Sum / 1024), $Destination
