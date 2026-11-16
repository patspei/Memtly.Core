using System.Text;
using Memtly.Core.Models.Database;

namespace Memtly.Core.Helpers;

public static class MediaFileName
{
    // Keep case, Unicode spelling and spaces. Only remove path components and
    // normalize characters that cannot safely be extracted on common platforms.
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Replace('\\', '/').Split('/').Last();
        if (value.Length > 500) return null;
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { return null; }
        var name = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
            name.Append(Rune.IsControl(rune) || "<>:\"|?*".Contains(rune.ToString(), StringComparison.Ordinal) ? "_" : rune.ToString());
        var result = name.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(result)) return null;
        var stem = result.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            result = "_" + result;
        return result.Length <= 500 ? result : null;
    }

    public static string DownloadName(GalleryItemModel item) => Normalize(item.OriginalFileName) ?? Normalize(item.Title) ?? "media";
}

/// <summary>Culture-independent digit runs, without integer overflow or renaming.</summary>
public sealed class NaturalFileNameComparer : IComparer<string>
{
    public static NaturalFileNameComparer Instance { get; } = new();
    public int Compare(string? x, string? y)
    {
        if (x == null || y == null) return StringComparer.Ordinal.Compare(x, y);
        var a = 0; var b = 0;
        while (a < x.Length && b < y.Length)
        {
            if (char.IsAsciiDigit(x[a]) && char.IsAsciiDigit(y[b]))
            {
                var startA = a; var startB = b;
                while (a < x.Length && char.IsAsciiDigit(x[a])) a++;
                while (b < y.Length && char.IsAsciiDigit(y[b])) b++;
                while (startA < a && x[startA] == '0') startA++;
                while (startB < b && y[startB] == '0') startB++;
                var difference = (a - startA).CompareTo(b - startB);
                if (difference == 0) difference = x.AsSpan(startA, a - startA).SequenceCompareTo(y.AsSpan(startB, b - startB));
                if (difference != 0) return difference;
            }
            else
            {
                var difference = char.ToUpperInvariant(x[a++]).CompareTo(char.ToUpperInvariant(y[b++]));
                if (difference != 0) return difference;
            }
        }
        var remaining = (x.Length - a).CompareTo(y.Length - b);
        return remaining != 0 ? remaining : StringComparer.Ordinal.Compare(x, y);
    }
}
