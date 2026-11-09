using Memtly.Core.BackgroundWorkers;
using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Models.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class MediaCacheWarmupTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task ExistingImagesArePreparedInPagesOnlyForConfiguredWeddingGalleries(bool enabled)
    {
        var database = Substitute.For<IDatabaseHelper>();
        var images = Substitute.For<IMediaThumbnailHelper>();
        database.GetGalleries().Returns(new List<GalleryModel>
        {
            new() { Id = 1, Name = "Unsere Hochzeit" },
            new() { Id = 2, Name = "Nur f\u00fcr Julia & Patrick", Type = GalleryType.Drop },
            new() { Id = 3, Name = "Unrelated" },
            new() { Id = 4, Name = "Eure Hochzeitsfotos", Type = GalleryType.Collection }
        });
        database.GetGalleryItems(Arg.Any<GalleryItemSearch>()).Returns(call =>
        {
            var search = call.Arg<GalleryItemSearch>();
            Assert.That(search.MediaType, Is.EqualTo(MediaType.Image));
            Assert.That(search.Limit, Is.EqualTo(100));
            Assert.That(search.ItemState.Rejected, Is.EqualTo(ItemOwner.All));
            if (search.GalleryIds.Single() == 2) return new List<GalleryItemModel>
            {
                new() { Id = 102, GalleryId = 2, MediaType = MediaType.Image, State = GalleryItemState.Pending }
            };
            if (search.Page == 2) return new List<GalleryItemModel>
            {
                new() { Id = 101, GalleryId = 1, MediaType = MediaType.Image, State = GalleryItemState.Approved }
            };
            // Broad repository results must not warm videos or files from another gallery.
            var page = Enumerable.Range(1, 98).Select(i => new GalleryItemModel { Id = i, GalleryId = 1, MediaType = MediaType.Video }).ToList();
            page.Add(new() { Id = 99, GalleryId = 3, MediaType = MediaType.Image });
            page.Add(new() { Id = 100, GalleryId = 1, MediaType = MediaType.Image, State = GalleryItemState.Rejected });
            return page;
        });
        var prepared = new List<int>();
        images.PrepareImages(Arg.Any<GalleryModel>(), Arg.Any<GalleryItemModel>()).Returns(async call =>
        {
            await Task.Yield();
            prepared.Add(call.Arg<GalleryItemModel>().Id);
        });
        using var services = new ServiceCollection().AddSingleton(database).AddSingleton(images).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Wedding:Enabled"] = enabled.ToString() }).Build();
        using var worker = new MediaCacheWarmup(services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHostApplicationLifetime>(), configuration, NullLogger<MediaCacheWarmup>.Instance);
        await worker.PrepareExistingImages(CancellationToken.None);
        Assert.That(prepared, Is.EqualTo(enabled ? new[] { 100, 101, 102 } : Array.Empty<int>()));
        await database.DidNotReceive().AddGalleryItem(Arg.Any<GalleryItemModel>());
        await database.DidNotReceive().EditGalleryItem(Arg.Any<GalleryItemModel>());
    }

    [Test]
    public async Task ShutdownStopsBeforeProcessingMoreFiles()
    {
        var database = Substitute.For<IDatabaseHelper>();
        database.GetGalleries().Returns(new List<GalleryModel> { new() { Id = 1, Name = "Unsere Hochzeit" } });
        var images = Substitute.For<IMediaThumbnailHelper>();
        using var services = new ServiceCollection().AddSingleton(database).AddSingleton(images).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Wedding:Enabled"] = "true" }).Build();
        using var worker = new MediaCacheWarmup(services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHostApplicationLifetime>(), configuration, NullLogger<MediaCacheWarmup>.Instance);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await worker.PrepareExistingImages(cancelled.Token));
        await images.DidNotReceive().PrepareImages(Arg.Any<GalleryModel>(), Arg.Any<GalleryItemModel>());
    }
}
