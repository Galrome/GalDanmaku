using System.Globalization;
using System.Text;

namespace GalDanmaku.Core.Text;

public static class TextNormalizer
{
    public static string Normalize(string raw)
    {
        string canonical = raw.Normalize(NormalizationForm.FormC);
        var result = new StringBuilder(canonical.Length);
        bool pendingSpace = false;
        foreach (Rune rune in canonical.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { pendingSpace = result.Length > 0; continue; }
            // Do not remove all Format characters: ZWJ/ZWNJ and variation selectors carry meaning.
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control || rune.Value is 0xFEFF or 0x200B or 0x2060) continue;
            if (pendingSpace) result.Append(' ');
            result.Append(rune.ToString());
            pendingSpace = false;
        }
        return result.ToString();
    }
}
