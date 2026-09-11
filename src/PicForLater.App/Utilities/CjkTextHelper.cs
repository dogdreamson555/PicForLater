using System.Text;

namespace PicForLater.App.Utilities;

internal static class CjkTextHelper
{
    internal static bool IsPureCjkText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var containsHan = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            if (IsHanCharacter(rune.Value))
            {
                containsHan = true;
                continue;
            }

            if (IsCjkPunctuation(rune.Value))
            {
                continue;
            }

            return false;
        }

        return containsHan;
    }

    private static bool IsHanCharacter(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2FA1F or
            >= 0x30000 and <= 0x323AF;

    private static bool IsCjkPunctuation(int value) =>
        value is >= 0x3000 and <= 0x303F or
            >= 0xFE10 and <= 0xFE1F or
            >= 0xFE30 and <= 0xFE4F or
            >= 0xFF01 and <= 0xFF65;
}
