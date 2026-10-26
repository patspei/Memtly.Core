using System.Globalization;

namespace Memtly.Core.Helpers;

public static class UploaderIdentity
{
    public static bool TryNormalize(string? name, out string normalized)
    {
        normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0) { normalized = "Anonymous"; return true; }
        return normalized.Length <= 80 && !normalized.Any(c => char.IsControl(c) || c is '<' or '>') &&
            !normalized.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format);
    }
}
