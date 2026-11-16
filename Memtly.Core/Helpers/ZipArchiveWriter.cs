using System.IO.Compression;
using System.Text;
using Memtly.Core.Models;

namespace Memtly.Core.Helpers;

public static class ZipArchiveWriter
{
    private sealed record Entry(string Name, string? Path = null, ZipListing? Nested = null);

    public static async Task Write(Stream output, IEnumerable<ZipListing> listings)
    {
        var entries = new List<Entry>();
        foreach (var listing in listings)
        {
            var root = Path.GetFullPath(listing.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var files = listing.Files?.Select(Path.GetFullPath).Where(p => p.StartsWith(root, comparison)).ToList() ?? [];
            if (files.Count == 0) continue;
            if (!string.IsNullOrWhiteSpace(listing.FileName))
                entries.Add(new Entry(SafePath(listing.Directory, listing.FileName), Nested: new ZipListing(listing.SourcePath, files, entryNames: listing.EntryNames)));
            else
                foreach (var file in files)
                {
                    var name = listing.EntryNames != null && listing.EntryNames.TryGetValue(file, out var mapped)
                        ? mapped : Path.GetRelativePath(listing.SourcePath, file);
                    entries.Add(new Entry(SafePath(listing.Directory, name), file));
                }
        }

        // Reserve real names first: a duplicate IMG.JPG must not steal IMG (2).JPG.
        // Also avoid overwrites on case-insensitive / Unicode-normalizing extractors.
        static string Key(string name) => name.Normalize(NormalizationForm.FormC);
        var reserved = entries.Select(e => Key(e.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8);
        foreach (var entry in entries)
        {
            var name = entry.Name;
            if (!used.Add(Key(name)))
            {
                var slash = name.LastIndexOf('/') + 1;
                var directory = name[..slash];
                var extension = Path.GetExtension(name[slash..]);
                var stem = name[slash..(name.Length - extension.Length)];
                for (var suffix = 2; ; suffix++)
                {
                    name = $"{directory}{stem} ({suffix}){extension}";
                    if (!reserved.Contains(Key(name)) && used.Add(Key(name))) break;
                }
            }
            var zipEntry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            await using var destination = zipEntry.Open();
            if (entry.Nested != null)
            {
                var temporary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
                try
                {
                    await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                        await Write(stream, [entry.Nested]);
                    await using var input = File.OpenRead(temporary);
                    await input.CopyToAsync(destination);
                }
                finally { File.Delete(temporary); }
            }
            else
            {
                await using var input = File.OpenRead(entry.Path!);
                await input.CopyToAsync(destination);
            }
        }
    }

    private static string SafePath(string? directory, string name)
    {
        var path = string.IsNullOrEmpty(directory) ? name : directory + "/" + name;
        path = path.Replace('\\', '/');
        if (path.Split('/').Any(segment => string.IsNullOrEmpty(segment) || MediaFileName.Normalize(segment) != segment))
            throw new InvalidDataException("Unsafe ZIP entry path.");
        return path;
    }
}
