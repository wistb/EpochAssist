using Il2Cpp;

namespace EpochAssist.Core;

/// <summary>
/// One number an item's stat line shows, in the tooltip's units (12 for 12%, 0.3 for 0.3%), with its tier's range as
/// the game rolls it: Min at roll 0, Max at roll 255 (the tier's best, also when best is a lower number). Property,
/// type, tags and special tag say what the number is (for totals across lines).
/// </summary>
internal readonly record struct StatValue(int Property, int ModType, int Tags, byte SpecialTag, double Value, double Min, double Max)
{
    /// <summary>Where the value sits in its range, 0–1; 1 when the range is one value.</summary>
    public double Quality => Max == Min ? 1d : Math.Clamp((Value - Min) / (Max - Min), 0d, 1d);
}

/// <summary>An implicit or unique roll line: its numbers, a key that names the line across items, and its text with
/// the numbers left out ("#% Increased Stun Chance").</summary>
internal sealed record StatLine(string Key, string Text, StatValue[] Numbers);

internal static partial class GameData
{
    public const string Values = "data: values";

    private static readonly Dictionary<(int Id, int Tier, int Roll, float Modifier), StatValue[]> s_affixNumbers = new();
    private static readonly Dictionary<string, string> s_lineTexts = new();

    /// <summary>
    /// Every number of an affix at a tier and roll, in shown units, through the game's own value functions with the
    /// item's effect modifier (<see cref="AffixModifier"/>). Empty when the affix or tier is unknown or the values
    /// group is missing.
    /// </summary>
    public static StatValue[] AffixValues(int id, int tier, byte roll, float modifier)
    {
        if (!Has(Affixes) || !Has(Values)) return Array.Empty<StatValue>();
        var key = (id, tier, (int)roll, modifier);
        if (s_affixNumbers.TryGetValue(key, out var cached)) return cached;
        StatValue[] result;
        try
        {
            result = ReadAffixValues(id, tier, roll, modifier);
        }
        catch (Exception e)
        {
            Failed(DataKind.Affix, e);
            return Array.Empty<StatValue>();
        }
        GameReads++;
        s_affixNumbers[key] = result;
        return result;
    }

    private static StatValue[] ReadAffixValues(int id, int tier, byte roll, float modifier)
    {
        var list = AffixList.get();
        var affix = list?.GetAffix(id);
        if (affix?.tiers is not { } tiers || tier < 0 || tier >= tiers.Count) return Array.Empty<StatValue>();
        if (affix.TryCast<AffixList.SingleAffix>() is { } single)
        {
            float v = list!.GetSingleAffixValue(single, modifier, tier, roll, 1);
            float lo = list.GetSingleAffixValue(single, modifier, tier, byte.MinValue, 1);
            float hi = list.GetSingleAffixValue(single, modifier, tier, byte.MaxValue, 1);
            return new[] { Shown(single.GetPropertyInfo(0), single.property, single.modifierType, single.tags, single.specialTag, v, lo, hi) };
        }
        if (affix.TryCast<AffixList.MultiAffix>() is { } multi && multi.affixProperties is { } props)
        {
            var numbers = new StatValue[props.Count];
            for (int p = 0; p < props.Count; p++)
            {
                var ap = props[p];
                float v = list!.GetMultiAffixPropertyValue(multi, modifier, tier, roll, p, 1);
                float lo = list.GetMultiAffixPropertyValue(multi, modifier, tier, byte.MinValue, p, 1);
                float hi = list.GetMultiAffixPropertyValue(multi, modifier, tier, byte.MaxValue, p, 1);
                numbers[p] = Shown(multi.GetPropertyInfo(p), ap.property, ap.modifierType, ap.tags, ap.specialTag, v, lo, hi);
            }
            return numbers;
        }
        return Array.Empty<StatValue>();
    }

    /// <summary>
    /// The item's implicit lines. The game hands an item's implicits back already rolled, so the shown value is the
    /// line's own value; the range is the base type's implicit at roll 0 and 255.
    /// </summary>
    public static StatLine[] ImplicitValues(ItemDataUnpacked item)
    {
        if (!Has(Values) || !Game.Alive(item)) return Array.Empty<StatLine>();
        try
        {
            var items = ItemList.get();
            var implicits = items?.GetItemImplicits(item);
            if (implicits == null || implicits.Count == 0) return Array.Empty<StatLine>();
            var props = PropertyList.get();
            var lines = new StatLine[implicits.Count];
            for (int i = 0; i < implicits.Count; i++)
            {
                var im = implicits[i];
                var b = items!.GetItemImplicit(item.itemType, item.subType, i);
                float lo = b != null ? b.GetValue(byte.MinValue) : im.implicitValue;
                float hi = b != null ? b.GetValue(byte.MaxValue) : im.implicitValue;
                var value = Shown(props?.GetPropertyInfo(im.property, im.tags, im.specialTag), im.property, im.type, im.tags, im.specialTag, im.implicitValue, lo, hi);
                string key = $"{(int)im.property}:{(int)im.type}:{(int)im.tags}:{im.specialTag}";
                lines[i] = new StatLine(key, LineText(key, () => TooltipItemManager.ImplicitFormatter(item, im.property, im.type, im.implicitValue, im.tags, im.specialTag,
                    im.extraTag, TooltipMode.DefaultInfo, TooltipItemManager.SlotType.STASH, i, false, ValueRangeFormat.None)), new[] { value });
            }
            GameReads++;
            return lines;
        }
        catch (Exception e)
        {
            Failed(DataKind.Item, e);
            return Array.Empty<StatLine>();
        }
    }

    /// <summary>
    /// A unique or set item's rolled lines: each mod the game rolls (its roll from the item's unique rolls by the mod's
    /// roll id), keyed "uniqueId:modIndex". Fixed mods and mods the tooltip hides are left out.
    /// </summary>
    public static StatLine[] UniqueRollValues(ItemDataUnpacked item)
    {
        if (!Has(Values) || !Game.Alive(item)) return Array.Empty<StatLine>();
        try
        {
            if (!item.isUniqueSetOrLegendary()) return Array.Empty<StatLine>();
            var entry = UniqueList.getUnique(item.uniqueID);
            if (entry?.mods == null) return Array.Empty<StatLine>();
            var lines = new List<StatLine>();
            for (int i = 0; i < entry.mods.Count; i++)
            {
                var m = entry.mods[i];
                if (m == null || !m.canRoll || m.hideInTooltip) continue;
                byte roll = item.getUniqueRoll(m.rollID);
                float v = m.getValue(roll);
                var value = Shown(m.GetPropertyInfo(), m.property, m.type, m.tags, m.specialTag, v, m.getValue(byte.MinValue), m.getValue(byte.MaxValue));
                string key = $"{item.uniqueID}:{i}";
                int index = i;
                lines.Add(new StatLine(key, LineText(key, () => TooltipItemManager.UniqueBasicModFormatter(item, m.property, m.type, v, m.tags, m.specialTag, m.extraTag,
                    TooltipMode.DefaultInfo, TooltipItemManager.SlotType.STASH, index, false)), new[] { value }));
            }
            GameReads++;
            return lines.ToArray();
        }
        catch (Exception e)
        {
            Failed(DataKind.Item, e);
            return Array.Empty<StatLine>();
        }
    }

    /// <summary>The value as the tooltip shows it: the game's rounding, then percent (×100) for percentages, increased
    /// and more, and a tenth for properties shown as a tenth of their value.</summary>
    private static StatValue Shown(BasePropertyInfo? info, SP property, BaseStats.ModType type, AT tags, byte specialTag, float value, float min, float max)
    {
        double scale = 1d;
        if (info != null)
        {
            value = info.GetValueAfterRounding(type, specialTag, value);
            min = info.GetValueAfterRounding(type, specialTag, min);
            max = info.GetValueAfterRounding(type, specialTag, max);
            bool added = type == BaseStats.ModType.ADDED;
            if (!added || info.displayAddedAsPercentage) scale = type == BaseStats.ModType.QUOTIENT && !info.displayAddedAsPercentage ? 1d : 100d;
            if (added && info.displayAddedAsTenthOfValue) scale *= 0.1d;
        }
        else if (type is BaseStats.ModType.INCREASED or BaseStats.ModType.MORE) scale = 100d;
        return new StatValue((int)property, (int)type, (int)tags, specialTag, Tidy(value * scale), Tidy(min * scale), Tidy(max * scale));
    }

    // Float noise off (0.58 × 100 = 57.99999…): four decimals is finer than any shown rounding.
    private static double Tidy(double v) => Math.Round(v, 4);

    // A line's text from the game's formatter, numbers replaced by "#", kept per line key.
    private static string LineText(string key, Func<string?> format)
    {
        if (s_lineTexts.TryGetValue(key, out var text)) return text;
        string raw;
        try
        {
            raw = format() ?? string.Empty;
        }
        catch
        {
            raw = string.Empty;
        }
        raw = System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]+>", string.Empty);
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"^\s*\[[^\]]*\]\s*", string.Empty);
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"[+-]?\d+(\.\d+)?", "#").Replace("\n", " ").Trim();
        s_lineTexts[key] = raw;
        return raw;
    }

    private static readonly Dictionary<int, (string Label, string Unit, string Form)[]> s_affixShapes = new();

    /// <summary>
    /// What each number of an affix is: a label (its property's name for a multi affix, the affix's name for a single
    /// one), its unit ("%" when shown as a percentage, else empty) and its form ("added", "increased", "more",
    /// "quotient"). Empty when unknown.
    /// </summary>
    public static (string Label, string Unit, string Form)[] AffixNumbers(int id)
    {
        if (!Has(Affixes) || !Has(Values)) return Array.Empty<(string, string, string)>();
        if (s_affixShapes.TryGetValue(id, out var cached)) return cached;
        (string, string, string)[] result;
        try
        {
            var affix = AffixList.get()?.GetAffix(id);
            if (affix?.TryCast<AffixList.SingleAffix>() is { } single)
                result = new[] { (Affix(id).Name ?? $"Affix {id}", Unit(single.GetPropertyInfo(0), single.modifierType), single.modifierType.ToString().ToLowerInvariant()) };
            else if (affix?.TryCast<AffixList.MultiAffix>() is { } multi && multi.affixProperties is { } props)
            {
                result = new (string, string, string)[props.Count];
                for (int p = 0; p < props.Count; p++)
                {
                    var ap = props[p];
                    string label = ap.getModDisplayName();
                    if (string.IsNullOrWhiteSpace(label)) label = PropertyList.get()?.GetPropertyInfo(ap.property, ap.tags, ap.specialTag)?.getPropertyName(ap.specialTag) ?? ap.property.ToString();
                    result[p] = (label.Trim(), Unit(multi.GetPropertyInfo(p), ap.modifierType), ap.modifierType.ToString().ToLowerInvariant());
                }
            }
            else result = Array.Empty<(string, string, string)>();
        }
        catch (Exception e)
        {
            Failed(DataKind.Affix, e);
            return Array.Empty<(string, string, string)>();
        }
        s_affixShapes[id] = result;
        return result;
    }

    private static string Unit(BasePropertyInfo? info, BaseStats.ModType type) =>
        type != BaseStats.ModType.ADDED || info != null && info.displayAddedAsPercentage ? "%" : string.Empty;

    private static void ClearValues()
    {
        s_affixShapes.Clear();
        s_affixNumbers.Clear();
        s_lineTexts.Clear();
    }
}
