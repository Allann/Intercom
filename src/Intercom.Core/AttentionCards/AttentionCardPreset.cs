namespace Intercom.AttentionCards;

/// <summary>One composer preset: a purpose string paired with its default
/// emoji icon (intercom-shell-prototype.html's composer-preset select).
/// Choosing "Custom…" in the composer means neither this list's Purpose nor
/// Icon is used verbatim — the human types their own purpose text and picks
/// any icon from <see cref="AttentionCardPresets.IconChoices"/>.</summary>
public sealed record AttentionCardPreset(string Purpose, string Icon);

/// <summary>
/// The composer's fixed preset list and emoji icon set (issue #25 scope
/// item 3), lifted verbatim from intercom-shell-prototype.html's
/// <c>#composer-preset</c> options and <c>#emoji-grid</c> buttons so the
/// WinUI composer and any tests share one source of truth rather than two
/// independently hand-typed copies.
/// </summary>
public static class AttentionCardPresets
{
    /// <summary>The five named presets, in prototype order, each with a
    /// default icon — a starting point only; the composer's icon picker
    /// (<see cref="IconChoices"/>) can always override it, since the
    /// prototype's own preset &lt;select&gt; and emoji-grid are independent
    /// controls, not a hard-coded pairing. The first four defaults come
    /// straight from the prototype's static <c>va-shelf</c> demo cards
    /// (Dinner's ready/🍽️, Get the door?/🚪, Package arrived/📦, Call when
    /// free/☎️); "Need a hand" has no such demo card, so ❤️ (from the same
    /// emoji-grid set) was picked as a reasonable default rather than
    /// inventing an icon the prototype's grid doesn't actually offer.</summary>
    public static readonly IReadOnlyList<AttentionCardPreset> Presets =
    [
        new("Dinner's ready", "🍽️"),
        new("Get the door?", "🚪"),
        new("Package arrived", "📦"),
        new("Call when free", "☎️"),
        new("Need a hand", "❤️"),
    ];

    /// <summary>The full emoji icon picker set (intercom-shell-prototype.html's
    /// <c>#emoji-grid</c>) — every preset's own icon plus a handful of extra
    /// choices, offered for a Custom card's icon pick.</summary>
    public static readonly IReadOnlyList<string> IconChoices =
    [
        "🍽️", "🚪", "📦", "☎️", "❤️", "⏰", "🔥", "❓", "🎉", "🛏️",
    ];
}
