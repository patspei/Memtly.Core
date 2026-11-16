using Memtly.Core.EntityFramework;
using Memtly.Core.EntityFramework.Models;
using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class GalleryMediaCleanupTests
{
    [Test]
    public async Task DryRunAndRepeatedCleanupAreConfinedToGalleryTwoAndCascadeReferences()
    {
        var root = Directory.CreateTempSubdirectory("memtly-cleanup-test-").FullName;
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            await using var db = new CoreDbContext(new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var professional = new Gallery { Id = 2, Name = "Unsere Hochzeit", SecretKey = "keep-code" };
            var guests = new Gallery { Id = 3, Name = "Eure Hochzeitsfotos" };
            var privateGallery = new Gallery { Id = 4, Name = "Nur für Julia & Patrick", Type = GalleryType.Drop };
            db.Galleries.AddRange(professional, guests, privateGallery);
            db.Users.Add(new User { Id = 1, Username = "admin", EmailAddress = "test@example.invalid" });
            db.GallerySettings.Add(new GallerySetting { GalleryId = 2, Value = "keep-setting" });
            db.GalleryHistory.Add(new GalleryHistory { GalleryId = 2, UserId = 1, SecretKey = "keep-code" });
            db.GalleryShare.Add(new GalleryShare { GalleryId = 2, UserId = 1 });
            for (var id = 2; id <= 4; id++)
            {
                db.GalleryItems.Add(new GalleryItem { Id = id, GalleryId = id, Title = "storage.JPG" });
                db.GalleryLikes.Add(new GalleryLike { GalleryItemId = id, UserId = 1 });
                db.GalleryComments.Add(new GalleryComment { GalleryItemId = id, UserId = 1, Value = "comment" });
            }
            await db.SaveChangesAsync();
            var ownPaths = new List<string>();
            foreach (var gallery in new[] { professional, guests, privateGallery })
            foreach (var prefix in new[] { "", "wwwroot" })
            foreach (var directory in new[] { "uploads", "uploads/Pending", "uploads/Rejected", "thumbnails", "thumbnails/Secure" })
            {
                var parts = directory.Split('/');
                var target = parts[0] == "uploads" && parts.Length == 2
                    ? Path.Combine(root, prefix, "uploads", gallery.Identifier, parts[1])
                    : Path.Combine(root, prefix, directory, gallery.Identifier);
                Directory.CreateDirectory(target);
                var file = Path.GetFullPath(Path.Combine(target, "file.bin"));
                await File.WriteAllTextAsync(file, "keep other galleries");
                if (gallery.Id == 2) ownPaths.Add(file);
            }
            var helper = new GalleryMediaCleanup(db, root);
            var beforeFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            var plan = await helper.Run();
            Assert.That(plan.Executed, Is.False);
            Assert.That(plan.Media, Is.EqualTo(1));
            Assert.That(plan.Likes, Is.EqualTo(1));
            Assert.That(plan.Comments, Is.EqualTo(1));
            Assert.That(plan.Files, Is.EqualTo(ownPaths.Count));
            Assert.That(await db.GalleryItems.CountAsync(), Is.EqualTo(3));
            Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories), Is.EquivalentTo(beforeFiles));
            Assert.ThrowsAsync<InvalidOperationException>(() => helper.Run("wrong confirmation"));
            await helper.Run(GalleryMediaCleanup.Confirmation);
            Assert.That(await db.GalleryItems.Select(i => i.GalleryId).ToListAsync(), Is.EquivalentTo(new[] { 3, 4 }));
            Assert.That(await db.GalleryLikes.CountAsync(), Is.EqualTo(2));
            Assert.That(await db.GalleryComments.CountAsync(), Is.EqualTo(2));
            Assert.That(await db.Galleries.CountAsync(), Is.EqualTo(3));
            Assert.That(await db.Users.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.GallerySettings.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.GalleryHistory.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.GalleryShare.CountAsync(), Is.EqualTo(1));
            Assert.That((await db.Galleries.AsNoTracking().SingleAsync(g => g.Id == 2)).SecretKey, Is.EqualTo("keep-code"));
            Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories), Is.EquivalentTo(beforeFiles.Except(ownPaths)));
            var again = await helper.Run(GalleryMediaCleanup.Confirmation);
            Assert.That(again.Media, Is.Zero);
            Assert.That(again.Files, Is.Zero);
            professional.Name = "Different gallery";
            await db.SaveChangesAsync();
            Assert.ThrowsAsync<InvalidOperationException>(() => helper.Run(GalleryMediaCleanup.Confirmation));
        }
        finally { Directory.Delete(root, true); }
    }
}
