<#
.SYNOPSIS
  Reduces PoB2's full-resolution class and ascendancy backdrops to the sizes the application ships.

.DESCRIPTION
  The backdrop art is extracted once from PoB2's TreeData/0_5 atlases — ascendancy-background_1500_1500
  holds the eight base classes and the twenty-three ascendancies, ascendancy-background_4000_4000 holds
  BGTree and BGTreeActive — with the BC7 decoder described in docs/VALIDATION.md. This script is the
  second half of that step: it downscales each full-resolution PNG into Data/Tree/Art/class at the sizes
  the renderer draws with.

  - 512 for a base class: its art covers 1500 world units at the tree centre, which is the picture a
    player looks at when the build's own class is on screen;
  - 1024 for BGTree and BGTreeActive: the ring is drawn at 2000 world units over the centre art and its
    ornamentation is the finest thing on the tree;
  - 320 for an ascendancy: it is a circle around the ring AND the whole background of that ascendancy's
    own graph view, both of which are watched at more than one zoom.

  These are the sizes the test "Tree art: class and ascendancy backdrops are shipped and placed like
  PoB2" pins, so a run that changes them has to update that test and docs/VALIDATION.md in the same commit.

.EXAMPLE
  pwsh build/extract-tree-art.ps1 -Source C:\work\pb-art\classfull
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Destination,
    [int]$ClassSize = 512,
    [int]$RingSize = 1024,
    [int]$AscendancySize = 320
)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $root 'src\PoeBuilder.App\Data\Tree\Art\class' }
if (-not (Test-Path $Source)) { throw "source folder not found: $Source" }
$dataPath = Join-Path $root 'src\PoeBuilder.App\Data\Tree\data.json'
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# Which sprites are a BASE class comes from the pinned tree itself: a class entry whose art really is in
# the source folder is drawn as a centre backdrop, everything else is a circle around the ring.
$data = Get-Content $dataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$baseClasses = @($data.classes |
    ForEach-Object { 'Classes' + $_.name } |
    Where-Object { Test-Path (Join-Path $Source ($_ + '.png')) })

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
foreach ($file in (Get-ChildItem $Source -Filter '*.png' | Sort-Object Name)) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $kind = if ($name -like 'BGTree*') { 'ring' } elseif ($baseClasses -contains $name) { 'class' } else { 'ascendancy' }
    $size = switch ($kind) { 'ring' { $RingSize } 'class' { $ClassSize } default { $AscendancySize } }
    $out = Join-Path $Destination ($name + '.png')
    Save-Resized $file.FullName $out $size
    $rows += [pscustomobject]@{ Name = $name; Kind = $kind; Size = $size; KB = [math]::Round((Get-Item $out).Length / 1KB) }
}

$rows | Format-Table -AutoSize
$rows | Group-Object Kind | ForEach-Object {
    "{0}: {1} file(s), {2} KB" -f $_.Name, $_.Count, ($_.Group | Measure-Object KB -Sum).Sum
}
"total: {0} file(s), {1:0.0} MB in {2}" -f $rows.Count, (($rows | Measure-Object KB -Sum).Sum / 1024), $Destination
