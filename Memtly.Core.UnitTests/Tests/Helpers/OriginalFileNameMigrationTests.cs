using Memtly.Core.EntityFramework;
using Memtly.Core.EntityFramework.Models;
using Memtly.Core.Enums;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Models.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class OriginalFileNameMigrationTests
{
    [TestCase("sqlite")]
    [TestCase("mariadb")]
    public async Task AdditiveMigrationKeepsLegacyRowsAndPersistsUnicodeOriginalNames(string provider)
    {
        var temporary = Directory.CreateTempSubdirectory("memtly-migration-tests-").FullName;
        try
        {
            var options = new DbContextOptionsBuilder<CoreDbContext>();
            if (provider == "sqlite")
                options.UseSqlite($"Data Source={Path.Combine(temporary, "migration.db")}", x => x.MigrationsAssembly("Memtly.Core.Migrations.Sqlite"));
            else
            {
                var connection = Environment.GetEnvironmentVariable("MEMTLY_TEST_MARIADB");
                if (string.IsNullOrEmpty(connection)) Assert.Ignore("MariaDB integration runs against the isolated GitHub Actions service.");
                var config = new MySqlConnectionStringBuilder(connection);
                Assert.That(config.Server, Is.AnyOf("localhost", "127.0.0.1"));
                Assert.That(config.Database, Is.EqualTo("memtly_filename_tests"));
                options.UseMySql(connection, new MariaDbServerVersion(new Version(11, 4)), x => x.MigrationsAssembly("Memtly.Core.Migrations.MySql"));
            }
            await using var db = new CoreDbContext(options.Options);
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.FindIndex(m => m.EndsWith("_AddOriginalFileName", StringComparison.Ordinal));
            Assert.That(index, Is.GreaterThan(0));
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[index - 1]);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO GalleryItems (Title, UploadedBy, UploaderEmailAddress, Checksum, FileSize, State, Type, Orientation, CreatedAt) VALUES ('legacy.JPG', '', '', '', 1, 3, 2, 1, 0)");
            await migrator.MigrateAsync();
            Assert.That(db.Database.HasPendingModelChanges(), Is.False);
            var legacy = await db.GalleryItems.AsNoTracking().SingleAsync();
            Assert.That(legacy.Title, Is.EqualTo("legacy.JPG"));
            Assert.That(legacy.OriginalFileName, Is.Null);
            db.Galleries.Add(new Gallery { Id = 2, Name = "Unsere Hochzeit" });
            await db.SaveChangesAsync();
            var repository = new EFDatabaseHelper(db, NullLogger<EFDatabaseHelper>.Instance);
            var item = await repository.AddGalleryItem(new GalleryItemModel
            {
                GalleryId = 2, Title = "unique-storage.JPG", OriginalFileName = "DSC_0042 – Grüße 你好 📷.JPG",
                State = GalleryItemState.Approved, Checksum = "checksum"
            });
            db.ChangeTracker.Clear();
            Assert.That((await repository.GetGalleryItem(item!.Id))!.OriginalFileName, Is.EqualTo("DSC_0042 – Grüße 你好 📷.JPG"));
            // Cascades used by offline cleanup also hold on the real MariaDB schema.
            db.GalleryLikes.Add(new GalleryLike { GalleryItemId = item.Id });
            db.GalleryComments.Add(new GalleryComment { GalleryItemId = item.Id, Value = "comment" });
            await db.SaveChangesAsync();
            await repository.DeleteGalleryItem(item);
            Assert.That(await db.GalleryLikes.CountAsync(), Is.Zero);
            Assert.That(await db.GalleryComments.CountAsync(), Is.Zero);
            Assert.That((await db.GalleryItems.SingleAsync()).Id, Is.EqualTo(legacy.Id));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(temporary, true);
        }
    }
}
