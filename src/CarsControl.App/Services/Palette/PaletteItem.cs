using Wpf.Ui.Controls;

namespace CarsControl.App.Services.Palette;

/// <summary>Result groups, in the order they're listed when scores tie.</summary>
public enum PaletteGroup { Actions, Pages, Apps, Games, Files, Settings, Help }

/// <summary>One command palette result: what Enter does, and the extra actions → shows.</summary>
public sealed class PaletteItem
{
    /// <summary>Stable id for usage counts ("action:update-everything", "app:winget:Git.Git").</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }

    /// <summary>Extra words that should find it ("theme dark light" for Appearance).</summary>
    public string? Keywords { get; init; }

    public PaletteGroup Group { get; init; }
    public SymbolRegular Icon { get; init; } = SymbolRegular.ChevronRight24;
    public required Func<Task> Run { get; init; }
    public IReadOnlyList<PaletteItem> More { get; init; } = [];
    public bool HasMore => More.Count > 0;

    /// <summary>Only listed when something is typed (files would crowd the empty palette).</summary>
    public bool SearchOnly { get; init; }

    public int Score { get; set; }

    public override string ToString() => Subtitle is null ? Title : $"{Title}, {Subtitle}";
}

/// <summary>A row in the result list: a group header or an item.</summary>
public sealed record PaletteRow(PaletteItem? Item, string? Header)
{
    public bool IsHeader => Header is not null;
    public override string ToString() => Header ?? Item?.ToString() ?? "";
}
