namespace EpochAssist.Core;

/// <summary>A min–max pair bound to values: two number fields where empty means no bound (a search's bounds).</summary>
internal sealed class ValueRange : MenuRow
{
    public ValueRange(string label, Func<(double? Min, double? Max)> get, Action<double?, double?> set)
    {
        Label = label;
        Get = get;
        Set = set;
    }

    public string Label { get; }
    public Func<(double? Min, double? Max)> Get { get; }
    public Action<double?, double?> Set { get; }
    /// <summary>Shown after each field ("%"); empty for none.</summary>
    public string Unit { get; init; } = string.Empty;
    /// <summary>Muted words beside the fields; null for none.</summary>
    public Func<string?>? Hint { get; init; }
    public string? Tip { get; init; }
    protected override string DefaultKey => "range." + MenuEntry.Slug(Label);

    /// <summary>"12–" / "–60" / "12–60" / "any".</summary>
    public static string Text(double? min, double? max) =>
        min == null && max == null ? "any" : $"{Format(min)}–{Format(max)}";

    public static string Format(double? v) => v?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>What a player typed as a number; null for empty or not a number.</summary>
    public static double? Parse(string text)
    {
        string digits = new(text.Trim().Replace(',', '.').Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
