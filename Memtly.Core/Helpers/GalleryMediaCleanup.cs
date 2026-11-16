using Memtly.Core.EntityFramework;
using Memtly.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Memtly.Core.Helpers;

public sealed record GalleryCleanupReport(int GalleryId, string GalleryName, string Identifier,
    int Media, int Likes, int Comments, int Files, long Bytes, bool Executed);

/// <summary>Offline maintenance only. No web endpoint. Intentionally pinned to
/// the professional wedding gallery; never deletes galleries or their settings.</summary>
public sealed class GalleryMediaCleanup(CoreDbContext database, string applicationRoot)
{
    public const string Confirmation = "DELETE GALLERY 2 MEDIA";

    public async Task<GalleryCleanupReport> Run(string? confirmation = null)
    {
        if (confirmation != null && confirmation != Confirmation)
            throw new InvalidOperationException("Invalid cleanup confirmation.");
        var gallery = await database.Galleries.AsNoTracking().SingleAsync(g => g.Id == 2);
        if (gallery.Name != "Unsere Hochzeit" || gallery.Type != GalleryType.Basic ||
            !GalleryHelper.IsValidGalleryIdentifier(gallery.Identifier))
            throw new InvalidOperationException("Gallery 2 does not match the expected professional wedding gallery.");

        var root = Path.GetFullPath(applicationRoot);
        var paths = new List<string>();
        // Include legacy public copies, retained rejected files and all cache versions.
        foreach (var prefix in new[] { "", "wwwroot" })
        foreach (var folder in new[] { "uploads", "thumbnails", Path.Combine("thumbnails", "Secure") })
        {
            var target = Path.GetFullPath(Path.Combine(root, prefix, folder, gallery.Identifier));
            var relative = Path.GetRelativePath(root, target);
            if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Cleanup path escaped the application root.");
            var component = root;
            CheckLink(component);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                component = Path.Combine(component, segment);
                CheckLink(component);
            }
            Scan(target, paths);
        }

        var media = database.GalleryItems.Where(i => i.GalleryId == 2);
        var ids = media.Select(i => i.Id);
        var report = new GalleryCleanupReport(2, gallery.Name, gallery.Identifier, await media.CountAsync(),
            await database.GalleryLikes.CountAsync(l => ids.Contains(l.GalleryItemId ?? 0)),
            await database.GalleryComments.CountAsync(c => ids.Contains(c.GalleryItemId ?? 0)),
            paths.Count, paths.Sum(p => new FileInfo(p).Length), confirmation != null);
        if (confirmation == null) return report;

        // FK cascades remove likes/comments. Commit before removing files: if a
        // filesystem operation fails, another offline run cleans the remainder.
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            await media.ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
        foreach (var path in paths) File.Delete(path);
        return report;
    }

    private static void CheckLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Cleanup refuses symlinks and junctions.");
    }

    private static void Scan(string directory, List<string> paths)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            CheckLink(path);
            if (Directory.Exists(path)) Scan(path, paths);
            else paths.Add(path);
        }
    }
}
