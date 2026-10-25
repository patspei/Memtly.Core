using Memtly.Core.EntityFramework;
using Memtly.Core.EntityFramework.Models;
using Memtly.Core.Enums;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Models.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class GalleryRepositorySecurityTests
{
    [Test]
    public async Task RejectedIsOptInAndCollectionExpansionUsesCollectionIdRatherThanUserId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CoreDbContext(new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var gallery = new Gallery { Id = 1, Identifier = "default", Name = "Public", Type = GalleryType.Basic };
        var collection = new Gallery { Id = 5, Name = "Collection", Type = GalleryType.Collection };
        db.Galleries.AddRange(gallery, collection);
        db.GalleryCollections.Add(new GalleryCollection { CollectionId = 5, GalleryId = 1 });
        db.GalleryItems.AddRange(
            new GalleryItem { GalleryId = 1, Title = "approved.jpg", State = GalleryItemState.Approved },
            new GalleryItem { GalleryId = 1, Title = "pending.jpg", State = GalleryItemState.Pending },
            new GalleryItem { GalleryId = 1, Title = "rejected.jpg", State = GalleryItemState.Rejected });
        await db.SaveChangesAsync();
        var repository = new EFDatabaseHelper(db, NullLogger<EFDatabaseHelper>.Instance);
        var publicSearch = new GalleryItemSearch { GalleryIds = [1], ItemState = new() { Approved = ItemOwner.All } };
        Assert.That((await repository.GetGalleryItems(publicSearch)).Select(i => i.Title), Is.EquivalentTo(new[] { "approved.jpg" }));
        var managementSearch = new GalleryItemSearch
        {
            CollectionIds = [5], ItemState = new() { Approved = ItemOwner.All, Pending = ItemOwner.All, Rejected = ItemOwner.All }
        };
        Assert.That((await repository.GetGalleryItems(managementSearch)).Select(i => i.Title),
            Is.EquivalentTo(new[] { "approved.jpg", "pending.jpg", "rejected.jpg" }));
        var counts = await repository.GetGalleryItemCount(managementSearch);
        Assert.That(counts["Rejected"], Is.EqualTo(1));
    }
}
