$path = 'C:\Users\supamodd\source\repos\PoeBuilder\src\PoeBuilder.Core\Filters\FilterReader.cs'
$lines = [System.IO.File]::ReadAllLines($path)
for ($i = 52; $i -lt 78; $i++)
{
    Write-Host ("{0}: {1}" -f ($i + 1), $lines[$i])
}