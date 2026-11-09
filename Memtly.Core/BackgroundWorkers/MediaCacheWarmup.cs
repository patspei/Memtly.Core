using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Models;
using Memtly.Core.Models.Database;

namespace Memtly.Core.BackgroundWorkers;

/// <summary>One background pass after startup. Persistent cache hits never decode again.</summary>
public sealed class MediaCacheWarmup(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime,
    IConfiguration configuration, ILogger<MediaCacheWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!WeddingOptions.Read(configuration).Enabled) return;
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
            await ready.Task.WaitAsync(stoppingToken);
            await PrepareExistingImages(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Cache preparation must not stop the application. Authorized requests can retry.
            logger.LogWarning(ex, "Gallery image cache warmup did not complete");
        }
    }

    public async Task PrepareExistingImages(CancellationToken cancellationToken)
    {
        var wedding = WeddingOptions.Read(configuration);
        if (!wedding.Enabled) return;
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IDatabaseHelper>();
        var images = scope.ServiceProvider.GetRequiredService<IMediaThumbnailHelper>();
        var count = 0;
        logger.LogInformation("Preparing existing wedding gallery thumbnails and previews");
        foreach (var gallery in (await database.GetGalleries()).Where(g => g.Id > 0 && wedding.IsWedding(g) && wedding.ValidType(g)))
        {
            const int pageSize = 100;
            for (var page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = await database.GetGalleryItems(new GalleryItemSearch
                {
                    GalleryIds = [gallery.Id], Page = page, Limit = pageSize, MediaType = MediaType.Image,
                    OrderBy = GalleryOrder.Ascending,
                    ItemState = new() { Approved = ItemOwner.All, Pending = ItemOwner.All, Rejected = ItemOwner.All }
                });
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.GalleryId != gallery.Id || item.MediaType != MediaType.Image) continue;
                    await images.PrepareImages(gallery, item);
                    count++;
                    // Yield between files so foreground upload/view requests can use the bounded decoder.
                    await Task.Delay(100, cancellationToken);
                }
                if (items.Count < pageSize) break;
            }
        }
        logger.LogInformation("Finished preparing image cache for {Count} wedding gallery images", count);
    }
}
