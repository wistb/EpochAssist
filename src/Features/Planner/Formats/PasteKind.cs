using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EpochAssist.Features.PlannerFormats;

/// <summary>What a paste is.</summary>
internal enum PasteSource
{
    Other,
    MaxrollLink,
    LetLink,
    MaxrollExport,
    LetJson,
}

/// <summary>A recognised paste: its kind, the planner id and, for a link, the one URL its request may use.</summary>
internal sealed record Paste(PasteSource Kind, string Text)
{
    public string? Id { get; init; }
    /// <summary>A Maxroll link's 1-based #n.</summary>
    public int? Profile { get; init; }
    /// <summary>The planner page the link names, kept for the build's link back.</summary>
    public string? PageUrl { get; init; }
    /// <summary>The build data the request fetches.</summary>
    public string? RequestUrl { get; init; }

    public bool IsLink => Kind is PasteSource.MaxrollLink or PasteSource.LetLink;
}

/// <summary>Recognises a pasted planner link, Maxroll export or Last Epoch Tools build JSON, and builds the request URL
/// for a link. Only the two sites' build data addresses are ever built.</summary>
internal static class PasteKind
{
    /// <summary>Nothing larger is read: the sample Maxroll profile is 130 KB.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    public const string MaxrollRequest = "https://planners.maxroll.gg/profiles/le/";
    public const string LetRequest = "https://www.lastepochtools.com/api/public/build_data/";
    public const string LetPage = "https://www.lastepochtools.com/planner/";

    private static readonly Regex s_maxroll = new(@"^https?://(?:www\.)?maxroll\.gg/last-epoch/planner/([^/?#\s]+)/?(?:\?[^#\s]*)?(?:#(\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex s_let = new(@"^https?://(?:www\.)?lastepochtools\.com/planner/([^/?#\s]+)(?:[/?#]\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex s_id = new("^[A-Za-z0-9]{4,16}$", RegexOptions.CultureInvariant);

    public static Paste Recognise(string? text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxBytes) return new Paste(PasteSource.Other, text);

        if (s_maxroll.Match(text) is { Success: true } mx)
        {
            string id = mx.Groups[1].Value;
            if (!s_id.IsMatch(id)) return new Paste(PasteSource.Other, text);
            int? profile = mx.Groups[2].Success && int.TryParse(mx.Groups[2].Value, out int n) ? n : null;
            return new Paste(PasteSource.MaxrollLink, text) { Id = id, Profile = profile, PageUrl = MaxrollFormat.PageUrl + id, RequestUrl = MaxrollRequest + id };
        }
        if (s_let.Match(text) is { Success: true } let)
        {
            string code = let.Groups[1].Value;
            if (!s_id.IsMatch(code)) return new Paste(PasteSource.Other, text);
            return new Paste(PasteSource.LetLink, text) { Id = code, PageUrl = LetPage + code, RequestUrl = LetRequest + code };
        }
        if (text[0] != '{') return new Paste(PasteSource.Other, text);

        if (MaxrollFormat.Parse(text) is not JsonObject root) return new Paste(PasteSource.Other, text);
        if (root["data"] is JsonObject data && (data.ContainsKey("bio") || data.ContainsKey("charTree"))) return new Paste(PasteSource.LetJson, text);
        if (!root.ContainsKey("profiles") && (root.ContainsKey("passives") || root.ContainsKey("skillTrees"))) return new Paste(PasteSource.MaxrollExport, text);
        return new Paste(PasteSource.Other, text);
    }

    /// <summary>The address the player opens in their browser to copy a Last Epoch Tools build's data by hand.</summary>
    public static string LetDataUrl(string code) => LetRequest + code;
}
