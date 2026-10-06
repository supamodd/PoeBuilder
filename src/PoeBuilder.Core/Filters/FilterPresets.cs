namespace PoeBuilder.Core.Filters;

/// <summary>Starting points for a filter. Each is a short, working rule set the player edits rather than an
/// empty page: a filter that catches nothing is hard to judge, and one that catches everything is worse.
/// <para>
/// The rules are written in the game's own item-class names, which the pinned catalog uses unchanged, so a
/// class named here is a class the client really knows.
/// </para></summary>
public static class FilterPresets
{
    /// <summary>One preset: its id, both names, and what it is for.</summary>
    public sealed record Preset(string Id, string Name, string Description, Func<LootFilterDocument> Build);
    /// <summary>Red money and gems stand out, everything else that is merely normal or magic is hidden.
    /// This is the smallest useful filter and the one most players start from.</summary>
    public static LootFilterDocument SemiStrict() => new LootFilterDocument { Name = "Полустрогий" } with
    {
        Minimap = true,
        Blocks =
        [
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Валюта — зелёный",
                Conditions = [new() { Keyword = "ItemClass", Value = "Currency" }],
                Style = new() { FontColor = [170, 230, 110, 255] }
            },
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Самоцветы",
                Conditions = [new() { Keyword = "ItemClass", Value = "Jewel" }],
                Style = new() { FontColor = [170, 230, 110, 255] }
            },
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Уникальные предметы — жёлтый, со звуком",
                Conditions = [new() { Keyword = "Rarity", Value = "Unique" }],
                Style = new() { FontColor = [255, 215, 60, 255], Sound = "IdRes3", MinimapIcon = 1, MinimapIconName = "YellowStar" }
            },
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Редкие предметы",
                Conditions = [new() { Keyword = "Rarity", Value = "Rare" }],
                Style = new() { FontColor = [255, 255, 255, 255] }
            },
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Волшебные сокела — красный",
                Conditions = [new() { Keyword = "Rarity", Value = "Magic" }],
                Style = new() { FontColor = [200, 60, 60, 255], BorderColor = [200, 60, 60, 255], BorderWidth = 1 }
            },
            new()
            {
                Kind = FilterBlockKind.Hide, Comment = "Обычные предметы скрыты",
                Conditions = [new() { Keyword = "Rarity", Value = "Normal" }]
            }
        ]
    };

    /// <summary>Everything SemiStrict keeps, plus the bases a build usually wants and the campaign currency,
    /// with a sound on the top tier.</summary>
    public static LootFilterDocument Strict()
    {
        var document = new LootFilterDocument { Name = "Строгий" } with
        {
            Minimap = true,
            Blocks =
            [
                new()
                {
                    Kind = FilterBlockKind.Show, Comment = "Валюта — зелёный",
                    Conditions = [new() { Keyword = "ItemClass", Value = "Currency" }],
                    Style = new() { FontColor = [170, 230, 110, 255] }
                },
                new()
                {
                    Kind = FilterBlockKind.Show, Comment = "Самоцветы",
                    Conditions = [new() { Keyword = "ItemClass", Value = "Jewel" }],
                    Style = new() { FontColor = [170, 230, 110, 255] }
                },
                new()
                {
                    Kind = FilterBlockKind.Show, Comment = "Сюжеты и квесты",
                    Conditions = [new() { Keyword = "ItemClass", Value = "Quest" }],
                    Style = new() { FontColor = [175, 96, 37, 255] }
                },
                new()
                {
                    Kind = FilterBlockKind.Show, Comment = "Камни умений — со звуком",
                    Conditions =
                    [
                        new() { Keyword = "ItemClass", Value = "Gem" },
                        new() { Keyword = "DropLevel", IsRange = true, Min = 1, Max = 20 }
                    ],
                    Style = new() { FontColor = [170, 230, 110, 255], Sound = "IdRes2" }
                },
                new()
                {
                    Kind = FilterBlockKind.Show, Comment = "Уникальные предметы — жёлтый, со звуком",
                    Conditions = [new() { Keyword = "Rarity", Value = "Unique" }],
                    Style = new() { FontColor = [255, 215, 60, 255], Sound = "IdRes3", MinimapIcon = 1, MinimapIconName = "YellowStar" }
                },
                new()
                {
                    Kind = FilterBlockKind.Show, Comment = "Редкие предметы",
                    Conditions = [new() { Keyword = "Rarity", Value = "Rare" }],
                    Style = new() { FontColor = [255, 255, 255, 255] }
                },
                new()
                {
                    Kind = FilterBlockKind.Show, Comment = "Волшебные сокела — красный",
                    Conditions = [new() { Keyword = "Rarity", Value = "Magic" }],
                    Style = new() { FontColor = [200, 60, 60, 255], BorderColor = [200, 60, 60, 255], BorderWidth = 1 }
                },
                new()
                {
                    Kind = FilterBlockKind.Hide, Comment = "Обычные предметы скрыты",
                    Conditions = [new() { Keyword = "Rarity", Value = "Normal" }]
                }
            ]
        };
        return document;
    }


    /// <summary>Soft is SemiStrict without the "normal hidden" rule and with a low-level hide instead.
    /// Most players start here.</summary>
    public static LootFilterDocument Soft()
    {
        var document = SemiStrict();
        // Remove the "Обычные предметы скрыты" block so normal items show again (SemiStrict already hides them).
        return document with
        {
            Blocks = document.Blocks
                .Where(b => b.Comment != "Обычные предметы скрыты")
                .Append(new()
                {
                    Kind = FilterBlockKind.Hide,
                    Comment = "Предметы уровня не выше 10 скрыты",
                    Conditions = [new() { Keyword = "DropLevel", IsRange = true, Min = 0, Max = 10 }]
                })
                .ToList()
        };
    }

    /// <summary>The default, balanced starting point.</summary>
    public static LootFilterDocument Normal() => SemiStrict();

    /// <summary>Very strict: currency and uniques keep their colours, everything else is hidden.</summary>
    public static LootFilterDocument VeryStrict()
    {
        var document = SemiStrict();
        var withoutBaseHide = document.Blocks
            .Where(b => b.Comment != "Обычные предметы скрыты")
            .ToList();
        return document with
        {
            Blocks = withoutBaseHide
                .Append(new()
                {
                    Kind = FilterBlockKind.Hide,
                    Comment = "Всё, кроме ценных классов, скрыто",
                    Conditions =
                    [
                        new() { Keyword = "Rarity", Value = "Normal" },
                        new() { Keyword = "Rarity", Value = "Magic" }
                    ]
                })
                .ToList()
        };
    }

    /// <summary>Uber strict: only the most valuable currency, uniques, and few classes remain.</summary>
    public static LootFilterDocument UberStrict() => new LootFilterDocument { Name = "Ультра строгий" } with
    {
        Minimap = false,
        Blocks =
        [
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Валюта — зелёный",
                Conditions = [new() { Keyword = "ItemClass", Value = "Currency" }],
                Style = new() { FontColor = [170, 230, 110, 255], Sound = "IdRes1" }
            },
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Уникальные предметы — жёлтый, со звуком",
                Conditions = [new() { Keyword = "Rarity", Value = "Unique" }],
                Style = new() { FontColor = [255, 215, 60, 255], Sound = "IdRes3", MinimapIcon = 1, MinimapIconName = "YellowStar" }
            },
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Камни умений с уровнем кап 20",
                Conditions =
                [
                    new() { Keyword = "ItemClass", Value = "Gem" },
                    new() { Keyword = "DropLevel", IsRange = true, Min = 1, Max = 20 }
                ],
                Style = new() { FontColor = [170, 230, 110, 255], Sound = "IdRes2" }
            },
            new()
            {
                Kind = FilterBlockKind.Hide, Comment = "Всё остальное скрыто",
                Conditions = [new() { Keyword = "DropLevel", IsRange = true, Min = 0, Max = 1000 }]
            }
        ]
    };

    /// <summary>All presets in the order they appear in the picker: from soft to uber strict.</summary>
    public static IReadOnlyList<Preset> All { get; } =
    [
        new("soft", "Мягкий",
            "Показывает почти всё, кроме совсем низкоуровневого мусора. Хорошая отправная точка для новичка.",
            Soft),
        new("regular", "Обычный",
            "Стандартный набор: валюта и самоцветы подсвечены, обычные предметы скрыты. Разумный баланс.",
            Normal),
        new("semi", "Полустрогий",
            "Валюта и самоцветы подсвечены, уникальные предметы — со звуком. Обычные скрыты.",
            SemiStrict),
        new("strict", "Строгий",
            "Полустрогий плюс камни умений и квестовые. Редкие и магические предметы скрыты жёстче.",
            Strict),
        new("very", "Очень строгий",
            "Полустрогий, но обычные и магические предметы скрыты. Остаётся только самое важное.",
            VeryStrict),
        new("uber", "Ультра строгий",
            "Максимальная фильтрация: только валюта, уникальные предметы и камни умений до 20 уровня.",
            UberStrict)
    ];
}