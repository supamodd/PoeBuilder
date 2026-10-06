# Sidebar tab icons

Drop one PNG per sidebar tab here and it replaces that tab's built-in glyph. The file name must match the
tab key exactly (case-insensitive on Windows, but keep it as written below), with a `.png` extension:

| File | Tab |
| --- | --- |
| `Builds.png` | Builds — список сборок |
| `Tree.png` | Tree — дерево пассивок |
| `Items.png` | Items — экипировка |
| `Jewels.png` | Jewels — самоцветы (**пока не нарисован**) |
| `Skills.png` | Skills — навыки |
| `QuestRewards.png` | QuestRewards — награды за задания |
| `Configuration.png` | Configuration — конфигурация |
| `Character.png` | Character — персонаж |
| `Notes.png` | Notes — заметки |
| `Settings.png` | Settings — настройки |

## How to draw them

- **Square canvas, 64×64 px, transparent background.** The sidebar slot is 19×19 device-independent pixels,
  so 64×64 gives a 3.4× asset that stays sharp on a 200%-scaled display and costs nothing at 100%.
- **Keep the art inside a centred 19:19 area with even padding.** Anything reaching the edge of the canvas
  will be flush against the folded rail's edge (64 px wide) and look clipped.
- **One flat colour, no gradients.** The inline glyphs the app ships with are strokes tinted by the row's
  foreground: muted grey at rest, gold when selected. A PNG cannot be tinted, so bake in a light neutral
  (e.g. `#C8CDD4`) and the selected row will keep the same colour as the rest — see the note below.
- **Transparent background, fully opaque strokes.** Do not use a white or black plate: the panel background is
  `#10161C`.

## How the swap works

The project globs `assets\NavIcons\*.png` as WPF resources, and `NavItem` probes for `Assets/NavIcons/{Key}.png`
at startup. A tab with no PNG keeps its inline path artwork, so the sidebar never breaks and you can add the
icons one at a time.

### The colour caveat

The built-in glyphs follow the row's state (grey → gold on selection). A PNG is a fixed bitmap and does not
recolour. If you want the gold highlight on the selected tab, either draw the glyph in the neutral colour and
accept the flat look, or say so and I will add an opacity/tint pass over the image in the item template.
