using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LightStudio.LightPlayer.Services;

internal sealed class LocalizedGroupIndex(CultureInfo culture, bool useSimpleGrouping = false)
{
    private static readonly string[] PinyinLabels =
        ["A", "B", "C", "D", "E", "F", "G", "H", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "W", "X", "Y", "Z"];
    private static readonly string[] PinyinStarts =
        ["\u963f", "\u516b", "\u5693", "\u5491", "\u59b8", "\u53d1", "\u65ee", "\u94ea", "\u8ba5", "\u5494", "\u5783", "\u5638", "\u62cf", "\u5662", "\u5991", "\u4e03", "\u5465", "\u4ee8", "\u4ed6", "\u5c72", "\u5915", "\u4e2b", "\u5e00"];
    private const string HangulInitials = "\u3131\u3132\u3134\u3137\u3138\u3139\u3141\u3142\u3143\u3145\u3146\u3147\u3148\u3149\u314a\u314b\u314c\u314d\u314e";
    private readonly Dictionary<string, string> initials = new(StringComparer.Ordinal);

    public string Initial(string? name)
    {
        name = name?.TrimStart();
        if (string.IsNullOrEmpty(name))
        {
            return "#";
        }
        var first = StringInfo.GetNextTextElement(name);
        if (!useSimpleGrouping)
            first = first.Normalize(NormalizationForm.FormKC);
        if (initials.TryGetValue(first, out var cached))
        {
            return cached;
        }
        var result = GetInitial(first);
        initials.Add(first, result);
        return result;
    }

    public string NormalizeKey(string value) => useSimpleGrouping ? value : value.Normalize();

    private string GetInitial(string first)
    {
        if (!Rune.TryGetRuneAt(first, 0, out var rune) || !Rune.IsLetter(rune))
        {
            return "#";
        }
        var code = rune.Value;
        if (code is >= 0xac00 and <= 0xd7a3)
        {
            return HangulInitials[(code - 0xac00) / 588].ToString();
        }
        if (code is >= 0x30a1 and <= 0x30f6)
        {
            code -= 0x60;
        }
        code = code switch
        {
            0x3094 => 0x3046,
            0x3095 or 0x3096 => 0x304b,
            _ => code,
        };
        if (code is >= 0x3041 and <= 0x3096)
        {
            return code switch
            {
                <= 0x304a => "\u3042",
                <= 0x3054 => "\u304b",
                <= 0x305e => "\u3055",
                <= 0x3069 => "\u305f",
                <= 0x306e => "\u306a",
                <= 0x307d => "\u306f",
                <= 0x3082 => "\u307e",
                <= 0x3088 => "\u3084",
                <= 0x308d => "\u3089",
                _ => "\u308f",
            };
        }
        if (code is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af)
        {
            if (!useSimpleGrouping && culture.TwoLetterISOLanguageName == "zh" &&
                !culture.Name.Contains("TW", StringComparison.OrdinalIgnoreCase) &&
                !culture.Name.Contains("HK", StringComparison.OrdinalIgnoreCase) &&
                !culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase))
            {
                return PinyinInitial(first);
            }
            return culture.TwoLetterISOLanguageName == "ja" ? "#" : first;
        }

        if (useSimpleGrouping)
            return first.ToUpperInvariant();

        var upper = culture.TextInfo.ToUpper(first);
        var decomposed = upper.Normalize(NormalizationForm.FormD);
        var baseLetter = Rune.GetRuneAt(decomposed, 0).ToString();
        return culture.CompareInfo.Compare(upper, baseLetter, CompareOptions.IgnoreNonSpace) == 0
            ? baseLetter : upper;
    }

    private string PinyinInitial(string first)
    {
        var comparison = culture.CompareInfo;
        var hasIndexBoundaries = comparison.Compare("\u963f", "\ufdd0A", CompareOptions.None) >= 0 &&
            comparison.Compare("\u963f", "\ufdd0B", CompareOptions.None) < 0;
        var low = 0;
        var high = PinyinLabels.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var boundary = hasIndexBoundaries ? "\ufdd0" + PinyinLabels[middle] : PinyinStarts[middle];
            if (comparison.Compare(first, boundary, CompareOptions.None) >= 0)
                low = middle + 1;
            else
                high = middle;
        }
        return PinyinLabels[Math.Max(0, low - 1)];
    }
}
