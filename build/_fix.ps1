$path = 'C:\Users\supamodd\source\repos\PoeBuilder\src\PoeBuilder.Core\Filters\FilterReader.cs'
$lines = [System.IO.File]::ReadAllLines($path)
$kept = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt $lines.Length; $i++)
{
    $line = $lines[$i]
    if ($line -match '</new_text>|</new_string>') { continue }
    # The dead `else { blocks.Add(block); }` left over from the earlier three-branch form.
    if ($i -ge 2 -and $kept[$kept.Count - 1].Trim() -eq 'minimap = false;' -and $kept[$kept.Count - 2].Trim() -eq '}' -and $line.Trim() -eq 'else') { continue }
    if ($i -ge 1 -and $kept[$kept.Count - 1].Trim() -eq '}' -and $line.Trim() -eq 'else') { continue }
    $kept.Add($line)
}
[System.IO.File]::WriteAllLines($path, $kept, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("before={0} after={1}" -f $lines.Length, $kept.Count)