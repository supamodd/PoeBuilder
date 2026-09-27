param([string]$Path, [string]$Section = "all")
function Decode-Pob([string]$f) {
    $code = (Get-Content $f -Raw).Trim()
    if ($code.StartsWith("<?xml") -or $code.StartsWith("<PathOfBuilding")) { return $code }
    $b64 = $code.Replace('-', '+').Replace('_', '/')
    switch ($b64.Length % 4) { 2 { $b64 += '==' } 3 { $b64 += '=' } }
    $bytes = [Convert]::FromBase64String($b64)
    $ms = New-Object IO.MemoryStream(, $bytes)
    $ms.Position = 2
    $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Decompress)
    (New-Object IO.StreamReader($ds)).ReadToEnd()
}
function Attr([string]$a, [string]$n) { [regex]::Match($a, "$n=`"([^`"]*)`"").Groups[1].Value }
$xml = Decode-Pob $Path

if ($Section -eq 'head') {
    '=== head ==='
    ($xml -split "`n") | Select-Object -First 4 | ForEach-Object { $_.Trim() }
}
if ($Section -eq 'playerstats') {
    '=== PlayerStats ==='
    foreach ($m in [regex]::Matches($xml, '<PlayerStat\s+([^>]*?)/>')) {
        $a = $m.Groups[1].Value
        $name = Attr $a 'stat'; $value = Attr $a 'value'
        if ($name -match 'DPS|Hit|Speed|Crit|Damage|Total|Mana|Life|Resist|Energy|Armour|Evasion|Spirit|Regen|Recovery|Rage|Charge') {
            "  $name = $value"
        }
    }
}
if ($Section -eq 'config') {
    '=== Config inputs ==='
    foreach ($m in [regex]::Matches($xml, '<Input\s+([^>]*?)/>')) {
        $a = $m.Groups[1].Value
        $name = Attr $a 'name'
        if ($name -match '^condition|flameWall|arcLight|^multiplier|resistancePenalty') { "  $a" }
    }
}
if ($Section -eq 'slots') {
    '=== Slots ==='
    foreach ($m in [regex]::Matches($xml, '<Slot\s+([^>]*?)/>')) {
        $a = $m.Groups[1].Value
        "  slot=$(Attr $a 'name') itemId=$(Attr $a 'itemId') set=$(Attr $a 'id')" + $(if ((Attr $a 'inactive')) { " INACTIVE" })
    }
    '=== Build attrs ==='
    '  ' + [regex]::Match($xml, '<Build\s+([^>]*?)>').Groups[1].Value
}
if ($Section -eq 'items') {
    foreach ($m in [regex]::Matches($xml, '(?s)<Item id="(\d+)">(.*?)</Item>')) {
        $id = $m.Groups[1].Value
        $lines = ($m.Groups[2].Value -split "`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
        "=== item $id ==="
        $lines | ForEach-Object { '  ' + $_ }
    }
}
if ($Section -eq 'sections') {
    '=== Calc sections (PoB2 breakdown dump in the share code) ==='
    foreach ($m in [regex]::Matches($xml, '(?s)<Section\s+([^>]*?)>(.*?)</Section>')) {
        $head = $m.Groups[1].Value
        $body = $m.Groups[2].Value
        "--- section: " + $head
        $rows = [regex]::Matches($body, '(?s)<Row[^>]*>(.*?)</Row>')
        foreach ($row in $rows | Select-Object -First 40) {
            $text = ($row.Groups[1].Value -replace '<[^>]+>', ' ') -replace '\s+', ' '
            '    ' + $text.Trim()
        }
    }
}

if ($Section -eq 'skills') {
    '=== Skill groups (index order, disabled included) ==='
    $index = 0
    foreach ($m in [regex]::Matches($xml, '(?s)<Skill\s+([^>]*?)>(.*?)</Skill>')) {
        $index++
        $head = $m.Groups[1].Value
        $body = $m.Groups[2].Value
        $gems = [regex]::Matches($body, '<Gem\s+([^>]*?)\s*/>') | ForEach-Object {
            $a = $_.Groups[1].Value
            $n = Attr $a 'nameSpec'; if (-not $n) { $n = Attr $a 'skillId' }
            $l = Attr $a 'level'; $q = Attr $a 'quality'; $en = Attr $a 'enabled'
            ("$n" + $(if ($l) { "/$l" }) + $(if ($q -and $q -ne '0') { "q$q" }) + $(if ($en -eq 'false') { "(off)" }))
        }
        $mark = if ((Attr $head 'enabled') -eq 'false') { 'DISABLED' } else { 'on' }
        "  #$index [$mark] main=$(Attr $head 'mainActiveSkill') weaponSet=$(Attr $head 'weaponSet') :: " + ($gems -join ', ')
    }
}
