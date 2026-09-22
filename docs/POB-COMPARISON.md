# PoB comparison

This document records reproducible comparisons against the real PoB2 fixture in `tests/PoeBuilder.Tests/Fixtures/pob-real.txt`.

## Sources

- PoB2 share-code fixture: `tests/PoeBuilder.Tests/Fixtures/pob-real.txt`
- Existing recorded PoB value: `README.md` and `docs/VALIDATION.md`
- PoB source reference: https://github.com/PathOfBuildingCommunity/PathOfBuilding

## Current comparison

| Metric | PoB reference | PoeBuilder | Delta | Status |
|---|---:|---:|---:|---|
| Flameblast DPS with imported gear | 3206.0/s | 3281.0/s | +75.0 / +2.34% | within 5% regression tolerance |

The automated comparison is in `tests/PoeBuilder.Tests/InteropTests.cs` and prints the imported build's current defensive summary.

Current PoeBuilder defensive output for this fixture:

- Life: 1166
- Energy Shield: 378
- Armour: 221
- Evasion: 447
- Fire resistance: 75
- Cold resistance: 46
- Lightning resistance: 74
- Chaos resistance: 0

These defensive values are not marked as PoB matches yet because the repository does not contain a corresponding PoB detailed-calculation snapshot. They are diagnostic output, not golden expectations.

## Remaining work for exact parity

- Export the same fixture's PoB offence and defence panel values into a reviewed golden file.
- Compare hit damage, attack/cast rate, crit, conversion split, resistance layers and EHP independently.
- Record active buffs, curses, charges, enemy configuration and conditional states.
- Replace the broad 5% DPS gate with per-metric tolerances after the golden values are reviewed.
