namespace EpochAssist.Features.PlannerFormats;

/// <summary>
/// The one lz-string call the planners need: decompressFromEncodedURIComponent, written here from the format so no
/// package is added. Last Epoch Tools writes its item, affix and set ids with it.
/// </summary>
internal static class LzString
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-$";
    private const int BitsPerChar = 6;

    /// <summary>The text an id holds, or null when the input isn't valid encoded data.</summary>
    public static string? DecompressFromEncodedUriComponent(string? input)
    {
        if (input == null) return null;
        if (input.Length == 0) return string.Empty;
        // A browser can turn the plus sign into a space.
        input = input.Replace(' ', '+');
        var values = new int[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            int v = Alphabet.IndexOf(input[i]);
            if (v < 0) return null;
            values[i] = v;
        }
        return Decompress(values);
    }

    /// <summary>A Last Epoch Tools id split into its tag letter and the digits after it; digits are null when the id
    /// doesn't decode.</summary>
    public static (char Tag, string? Digits) DecodeLetId(string id)
    {
        if (string.IsNullOrEmpty(id)) return ('\0', null);
        string? digits = DecompressFromEncodedUriComponent(id.Substring(1));
        return (id[0], digits != null && digits.Length > 0 && digits.All(char.IsDigit) ? digits : null);
    }

    private static string? Decompress(int[] values)
    {
        var dictionary = new List<string> { "0", "1", "2" };
        int enlargeIn = 4, numBits = 3;
        int position = 1 << (BitsPerChar - 1), index = 1, value = values[0];

        // Bits come out of each 6-bit value high bit first, and are put together low bit first.
        int Read(int count)
        {
            int bits = 0;
            for (int power = 1, n = 0; n < count; n++, power <<= 1)
            {
                if ((value & position) != 0) bits |= power;
                position >>= 1;
                if (position == 0)
                {
                    position = 1 << (BitsPerChar - 1);
                    value = index < values.Length ? values[index] : 0;
                    index++;
                }
            }
            return bits;
        }

        string w;
        switch (Read(2))
        {
            case 0: w = ((char)Read(8)).ToString(); break;
            case 1: w = ((char)Read(16)).ToString(); break;
            default: return string.Empty;
        }
        dictionary.Add(w);
        var result = new System.Text.StringBuilder(w);
        while (true)
        {
            if (index > values.Length) return string.Empty;
            int c = Read(numBits);
            switch (c)
            {
                case 0:
                case 1:
                    dictionary.Add(((char)Read(c == 0 ? 8 : 16)).ToString());
                    c = dictionary.Count - 1;
                    enlargeIn--;
                    break;
                case 2:
                    return result.ToString();
            }
            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
            string entry;
            if (c < dictionary.Count) entry = dictionary[c];
            else if (c == dictionary.Count) entry = w + w[0];
            else return null;
            result.Append(entry);
            dictionary.Add(w + entry[0]);
            enlargeIn--;
            w = entry;
            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }
    }
}
