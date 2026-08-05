using System.Reflection;
using System.Text;

namespace Intercom.App.Chat;

public static class ChatEmojiCatalog
{
    sealed record Entry(string Emoji, string SearchName, string Category);

    static readonly Entry[] Items = typeof(Emoji).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => new Entry((string)field.GetRawConstantValue()!, Normalize(field.Name), Categorize(field.Name)))
        .DistinctBy(entry => entry.Emoji)
        .ToArray();

    public static IReadOnlyList<string> Categories { get; } =
        ["All", "Smileys", "People", "Animals", "Food", "Travel", "Activities", "Objects", "Symbols", "Flags"];

    public static IReadOnlyList<string> Search(string? query, IReadOnlyList<string>? recent = null, string? category = null)
    {
        var normalized = Normalize(query ?? "");
        var matches = Items.Where(item => (string.IsNullOrEmpty(normalized) || item.SearchName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(category) || category == "All" || item.Category == category)).Select(item => item.Emoji);
        return (recent ?? []).Concat(matches).Distinct().ToArray();
    }

    static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static string Categorize(string name)
    {
        if (name.StartsWith("Flag", StringComparison.Ordinal) || name.Contains("Flag", StringComparison.Ordinal)) return "Flags";
        if (Contains(name, "Animal", "Bird", "Cat", "Dog", "Horse", "Fish", "Bug", "Plant", "Flower", "Tree")) return "Animals";
        if (Contains(name, "Food", "Fruit", "Vegetable", "Cake", "Drink", "Bread", "Rice", "Pizza", "Fork", "Cup")) return "Food";
        if (Contains(name, "Car", "Bus", "Train", "Plane", "Boat", "Travel", "Map", "Building", "House", "Mountain")) return "Travel";
        if (Contains(name, "Ball", "Game", "Sport", "Medal", "Trophy", "Music", "Party", "Art")) return "Activities";
        if (Contains(name, "Button", "Arrow", "Sign", "Mark", "Symbol", "Square", "Circle", "Heart", "Star")) return "Symbols";
        if (Contains(name, "Person", "Man", "Woman", "Child", "Hand", "Finger", "People", "Family")) return "People";
        if (Contains(name, "Face", "Smile", "Laugh", "Cry", "Angry", "Emotion")) return "Smileys";
        return "Objects";
    }

    static bool Contains(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}
