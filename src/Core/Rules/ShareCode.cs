using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>What reading a share code gave: the set (with the parts this build can't read listed) and the named
/// conditions it carries (null for a version 1 code), or why not.</summary>
internal sealed record ShareDecode(MacroSet? Set, string? Error, IReadOnlyList<string> Unknown)
{
    public ConditionDoc? Named { get; init; }

    public static ShareDecode Fail(string error) => new(null, error, Array.Empty<string>());
}

/// <summary>
/// One macro set as one line (contracts/share-code.md): "EAMACRO1:" + base64url(deflate(UTF-8 set JSON)) + "." + its
/// CRC-32 as 8 hex digits, at most 16 000 characters. When deflate isn't available the payload is "u" + base64url of the
/// JSON itself; decoding takes both. A code holds only the set as the library stores it: names given by the player,
/// classes, keys, conditions and steps, never character or account data. A set that uses named conditions is version 2:
/// {"set": …, "named": a condition document of the named conditions it reaches}; one that uses none stays version 1.
/// </summary>
internal static class ShareCode
{
    public const string Tag = "EAMACRO";
    /// <summary>The newest version this build reads.</summary>
    public const int Version = 2;
    public const int MaxLength = 16000;

    public const string NotACode = "not a share code";
    public const string Newer = "made by a newer Epoch Assist";
    public const string Damaged = "damaged or incomplete";

    /// <summary>Whether deflate worked the last time a code was made (false: codes are uncompressed).</summary>
    public static bool? Compressed { get; private set; }

    /// <summary>The set's code, with the named conditions it uses from named, or the reason it can't be shared ("set too
    /// large to share (N characters)").</summary>
    public static (string? Code, string? Error) Encode(MacroSet set, ConditionDoc? named = null)
    {
        var closure = named != null ? NamedConditions.Closure(named, MacroDoc.NamedIds(set)) : new HashSet<string>();
        int version = 1;
        string json = MacroDoc.SetJson(set)?.ToJsonString() ?? "{}";
        if (closure.Count > 0)
        {
            version = 2;
            var lists = new JsonObject();
            var stored = named!.ToJson()["lists"] as JsonObject;
            foreach (string key in named.Order)
            {
                string owner = key.EndsWith(NamedConditions.EndsSuffix, StringComparison.Ordinal) ? key[..^NamedConditions.EndsSuffix.Length] : key;
                if (closure.Contains(owner) && stored?[key] is { } list) lists[key] = ConditionDoc.Clone(list);
            }
            var both = new JsonObject { ["set"] = MacroDoc.SetJson(set), ["named"] = new JsonObject { ["v"] = ConditionDoc.FormatVersion, ["lists"] = lists } };
            json = both.ToJsonString();
        }
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string crc = Crc32(bytes).ToString("x8");
        string payload;
        try
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(bytes, 0, bytes.Length);
            payload = Base64Url(output.ToArray());
            Compressed = true;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("sharecode:deflate", $"Share code compression isn't available, codes are uncompressed: {e.Message}", 300);
            payload = "u" + Base64Url(bytes);
            Compressed = false;
        }
        string code = $"{Tag}{version}:{payload}.{crc}";
        return code.Length > MaxLength ? (null, $"set too large to share ({code.Length} characters)") : (code, null);
    }

    /// <summary>Reads a code: the set, or not a share code, made by a newer Epoch Assist, or damaged or incomplete.</summary>
    public static ShareDecode Decode(string text)
    {
        text = text.Trim();
        if (!text.StartsWith(Tag, StringComparison.Ordinal)) return ShareDecode.Fail(NotACode);
        int colon = text.IndexOf(':');
        if (colon < 0 || !int.TryParse(text[Tag.Length..colon], out int version)) return ShareDecode.Fail(NotACode);
        if (version > Version) return ShareDecode.Fail(Newer);
        if (version < 1) return ShareDecode.Fail(NotACode);
        string rest = text[(colon + 1)..];
        int dot = rest.LastIndexOf('.');
        if (dot < 0 || rest.Length - dot - 1 != 8) return ShareDecode.Fail(Damaged);
        string payload = rest[..dot], crcText = rest[(dot + 1)..];
        byte[] bytes;
        try
        {
            if (payload.StartsWith('u'))
            {
                bytes = FromBase64Url(payload[1..]);
            }
            else
            {
                using var input = new MemoryStream(FromBase64Url(payload));
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                bytes = output.ToArray();
            }
        }
        catch (Exception)
        {
            return ShareDecode.Fail(Damaged);
        }
        if (!uint.TryParse(crcText, System.Globalization.NumberStyles.HexNumber, null, out uint crc) || Crc32(bytes) != crc) return ShareDecode.Fail(Damaged);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception)
        {
            return ShareDecode.Fail(Damaged);
        }
        ConditionDoc? named = null;
        if (version == 2)
        {
            if (node is not JsonObject both || both["set"] is not JsonObject setNode) return ShareDecode.Fail(Damaged);
            named = ConditionDoc.Parse(both["named"], out _);
            if (named == null) return ShareDecode.Fail(Damaged);
            if (named.ReadOnly) return ShareDecode.Fail(Newer);
            node = setNode;
        }
        var set = MacroDoc.ParseSet(node);
        if (set.Raw != null) return ShareDecode.Fail(Damaged);
        return new ShareDecode(set, null, MacroDoc.UnknownParts(set)) { Named = named };
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        string b64 = text.Replace('-', '+').Replace('_', '/');
        b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new FormatException("bad length") };
        return Convert.FromBase64String(b64);
    }

    private static uint[]? s_crcTable;

    /// <summary>CRC-32 (IEEE, as zip uses).</summary>
    public static uint Crc32(byte[] bytes)
    {
        var table = s_crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in bytes) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
