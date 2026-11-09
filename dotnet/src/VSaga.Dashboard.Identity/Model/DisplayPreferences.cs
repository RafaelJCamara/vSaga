using System.Text.Json;

namespace VSaga.Dashboard.Identity.Model;

/// <summary>
/// The display choices a user keeps on their account (ADR 0015, part 4): the dashboard's theme and density. A
/// closed set of keys and values; a null member is "no account choice for it", so the browser's own stays.
/// </summary>
/// <remarks>
/// The store keeps this as one JSON text (<see cref="DashboardUser.DisplayPreferences"/>), so the next
/// preference is a new member here and not a new column. <see cref="Parse"/> is tolerant, because the text may
/// have been written by a newer image: it drops a key it does not know and a value it does not accept, rather
/// than failing a sign-in over a cosmetic setting. A client's request is the strict side: see
/// <see cref="IsValid"/>.
/// </remarks>
/// <param name="Theme">One of <see cref="Themes"/>, or null.</param>
/// <param name="Density">One of <see cref="Densities"/>, or null.</param>
public sealed record DisplayPreferences(string? Theme, string? Density)
{
    /// <summary>The longest text the store keeps; the column is this wide.</summary>
    public const int MaxJsonLength = 256;

    /// <summary>The themes the dashboard offers. <c>system</c> follows the OS and is a choice, not an absence.</summary>
    public static IReadOnlyList<string> Themes { get; } = ["system", "light", "dark", "high-contrast"];

    /// <summary>The densities the dashboard offers.</summary>
    public static IReadOnlyList<string> Densities { get; } = ["comfortable", "compact"];

    /// <summary>True when neither member is set: nothing to store.</summary>
    public bool IsEmpty => Theme is null && Density is null;

    /// <summary>True when every member that is set is one the dashboard offers.</summary>
    public bool IsValid => (Theme is null || Themes.Contains(Theme, StringComparer.Ordinal))
        && (Density is null || Densities.Contains(Density, StringComparer.Ordinal));

    /// <summary>
    /// The preferences in <paramref name="json"/>, or null when it holds none: absent, empty, not an object,
    /// not JSON, or holding nothing this version knows.
    /// </summary>
    public static DisplayPreferences? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var parsed = new DisplayPreferences(
                Choice(document.RootElement, "theme", Themes),
                Choice(document.RootElement, "density", Densities));
            return parsed.IsEmpty ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text the store keeps: only the members that are set, in a fixed order; null when none is.</summary>
    public string? ToJson()
    {
        if (IsEmpty)
            return null;

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Theme is not null)
            members["theme"] = Theme;
        if (Density is not null)
            members["density"] = Density;
        return JsonSerializer.Serialize(members);
    }

    private static string? Choice(JsonElement root, string name, IReadOnlyList<string> allowed) =>
        root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && allowed.Contains(text, StringComparer.Ordinal)
                ? text
                : null;
}
