using System.Security.Cryptography;
using System.Text;
using Memtly.Core.Constants;
using Memtly.Core.Models.Database;

namespace Memtly.Core.Helpers;

public interface IMediaThumbnailHelper
{
    Task<string?> GetPath(GalleryModel gallery, GalleryItemModel item);
    Task<string?> GetPreviewPath(GalleryModel gallery, GalleryItemModel item);
}

/// <summary>Build from the authorized original, never a shared basename thumbnail
/// that might have been overwritten by a pending file with another extension.</summary>
public sealed class MediaThumbnailHelper(IMediaFileStore files, IImageHelper images, ISettingsHelper settings) : IMediaThumbnailHelper
{
    // Bounded process-wide locks also coordinate separate request-scoped helpers.
    private static readonly SemaphoreSlim[] GenerationLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    public async Task<string?> GetPath(GalleryModel gallery, GalleryItemModel item)
        => await GetVariant(gallery, item, false);

    public async Task<string?> GetPreviewPath(GalleryModel gallery, GalleryItemModel item)
        => item.MediaType == Enums.MediaType.Image ? await GetVariant(gallery, item, true) : null;

    private async Task<string?> GetVariant(GalleryModel gallery, GalleryItemModel item, bool preview)
    {
        var original = files.GetPath(gallery, item);
        if (original == null) return null;
        var size = preview ? 1920 : Math.Clamp(await settings.GetOrDefault(MemtlyConfiguration.Gallery.Thumbnails.Size, 1200, gallery.Id), 100, 4096);
        var variant = preview ? "preview-v1-q84" : null;
        var generationLock = GenerationLocks[(uint)StringComparer.Ordinal.GetHashCode(original) % GenerationLocks.Length];
        await generationLock.WaitAsync();
        try
        {
            // Resolve again after waiting: moderation can move the source file.
            if (files.GetPath(gallery, item) != original) return null;
            return await Generate(gallery, item, original, size, variant, preview);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        finally { generationLock.Release(); }
    }

    private static string Version(GalleryItemModel item, string original, int size, string? variant)
    {
        var info = new FileInfo(original);
        var key = $"{item.Id}|{item.State}|{item.Title}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{size}";
        // Preserve existing thumbnail cache keys; previews have their own version and quality.
        if (variant != null) key += $"|{variant}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private async Task<string?> Generate(GalleryModel gallery, GalleryItemModel item, string original, int size, string? variant, bool preview)
    {
        var version = Version(item, original, size, variant);
        var target = files.GetThumbnailCachePath(gallery, preview ? $"{item.Id}-{variant}-{version}.webp" : $"{item.Id}-{version}.webp");
        if (target == null) return null;
        if (File.Exists(target)) return target;
        var temporary = files.GetThumbnailCachePath(gallery, $"{Guid.NewGuid():N}.webp");
        if (temporary == null) return null;
        try
        {
            var success = preview ? await images.GeneratePreview(original, temporary) : await images.GenerateThumbnail(original, temporary, size);
            if (!success || !File.Exists(temporary) || files.GetPath(gallery, item) != original ||
                Version(item, original, size, variant) != version) return null;
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target)) { /* Another request completed first. */ }
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
