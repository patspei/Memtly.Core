using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Claims;
using Memtly.Core.Constants;
using Memtly.Core.Controllers;
using Memtly.Core.EntityFramework;
using Memtly.Core.EntityFramework.Models;
using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Helpers.Notifications;
using Memtly.Core.Models;
using Memtly.Core.Models.Database;
using Memtly.Core.UnitTests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class OriginalFileNameTests
{
    private string temporary = null!;
    private string root = null!;
    private string identifier = null!;
    private ISettingsHelper settings = null!;
    private ServiceProvider services = null!;
    private readonly MockSession session = new();

    private CoreDbContext Database() => new(new DbContextOptionsBuilder<CoreDbContext>()
        .UseSqlite($"Data Source={Path.Combine(temporary, "test.db")};Foreign Keys=True").Options);

    [SetUp]
    public async Task Setup()
    {
        temporary = Directory.CreateTempSubdirectory("memtly-names-").FullName;
        root = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
        identifier = Guid.NewGuid().ToString("N");
        settings = Substitute.For<ISettingsHelper>();
        settings.GetOrDefault(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>()).Returns(c => c.ArgAt<string>(1));
        settings.GetOrDefault(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int?>()).Returns(c => c.ArgAt<long>(1));
        settings.GetOrDefault(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>()).Returns(c => c.ArgAt<int>(1));
        settings.GetOrDefault(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>()).Returns(c => c.ArgAt<bool>(1));
        settings.GetOrDefault(MemtlyConfiguration.Gallery.RequireReview, true, 2).Returns(false);
        services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).BuildServiceProvider();
        session.SetString(SessionKey.Viewer.Identity, "Gast Ä & Gast B");
        await using var db = Database();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = 1, Username = "owner", EmailAddress = "owner@example.invalid", State = AccountState.Active });
        db.Galleries.Add(new Gallery { Id = 2, Identifier = identifier, Name = "Unsere Hochzeit", UserId = 1, Type = GalleryType.Basic });
        await db.SaveChangesAsync();
    }

    private DefaultHttpContext Context() => new() { Session = session, RequestServices = services };

    private async Task<GalleryItemModel> Upload(string name, byte[] bytes, int chunks = 1, bool reverse = false, bool authenticated = false)
    {
        await using var db = Database();
        var repository = new EFDatabaseHelper(db, NullLogger<EFDatabaseHelper>.Instance);
        var identity = new IdentityHelper();
        var access = new MediaAccessHelper(repository, identity, settings);
        var images = Substitute.For<IImageHelper>();
        images.GenerateThumbnail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(true);
        images.GetMediaType(Arg.Any<string>()).Returns(c => Path.GetExtension(c.Arg<string>()).ToLowerInvariant() is ".mov" or ".mp4" ? MediaType.Video : MediaType.Image);
        var localizer = Substitute.For<IStringLocalizer<Localization.Translations>>();
        localizer[Arg.Any<string>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>()));
        var controller = new GalleryController(settings, repository, new FileHelper(NullLogger<FileHelper>.Instance),
            Substitute.For<IDeviceDetector>(), images, Substitute.For<INotificationHelper>(), Substitute.For<IEncryptionHelper>(),
            Substitute.For<Memtly.Core.Helpers.IUrlHelper>(), identity, NullLogger<GalleryController>.Instance, localizer, access,
            new GalleryDownloadHelper(repository, access, new MediaFileStore(root), identity, settings), Substitute.For<IMediaThumbnailHelper>());
        controller.ControllerContext.HttpContext = Context();
        if (authenticated)
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Sid, "1"), new Claim(ClaimTypes.Role, "Admin|None")], "Tests"));
        var uploadId = Guid.NewGuid().ToString();
        var checksum = Guid.NewGuid().ToString();
        foreach (var index in (reverse ? Enumerable.Range(0, chunks).Reverse() : Enumerable.Range(0, chunks)))
        {
            var start = bytes.Length * index / chunks;
            var part = bytes[start..(bytes.Length * (index + 1) / chunks)];
            var result = (JsonResult)await controller.UploadFileChunk(new MediaUploadRequest
            {
                RequestId = "parallel-batch", UploadId = uploadId, GalleryId = 2, CollectionId = 0,
                ChunkIndex = index, TotalChunks = chunks, FileSize = bytes.Length, FileChecksum = checksum,
                File = new FormFile(new MemoryStream(part), 0, part.Length, "File", name)
            });
            var response = (MediaUploadResponse)result.Value!;
            Assert.That(response.Success, Is.True, string.Join(";", response.Errors ?? []));
        }
        db.ChangeTracker.Clear();
        return (await repository.GetGalleryItemByChecksum(2, checksum))!;
    }

    private static byte[] Photo(SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var surface = SKSurface.Create(new SKImageInfo(20, 30));
        surface.Canvas.Clear(SKColors.Green);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UploadPersistsOriginalNameAcrossAllRepositoryReadsAndEdits(bool authenticated)
    {
        var bytes = Photo();
        var item = await Upload("DSC_0001.JPG", bytes, authenticated: authenticated);
        Assert.That(item.OriginalFileName, Is.EqualTo("DSC_0001.JPG"));
        Assert.That(item.Title, Is.Not.EqualTo(item.OriginalFileName));
        await using var db = Database();
        Assert.That((await db.GalleryItems.SingleAsync()).OriginalFileName, Is.EqualTo("DSC_0001.JPG"));
        var repository = new EFDatabaseHelper(db, NullLogger<EFDatabaseHelper>.Instance);
        var listed = await repository.GetGalleryItems(new GalleryItemSearch { GalleryIds = [2], ItemState = new() { Approved = ItemOwner.All } });
        Assert.That(listed.Single().OriginalFileName, Is.EqualTo("DSC_0001.JPG"));
        item.UploadedBy = "Changed uploader";
        Assert.That((await repository.EditGalleryItem(item))!.OriginalFileName, Is.EqualTo("DSC_0001.JPG"));
        var access = new MediaAccessHelper(repository, new IdentityHelper(), settings);
        var controller = new MediaController(repository, access, new MediaFileStore(root), new IdentityHelper(), Substitute.For<IMediaThumbnailHelper>());
        controller.ControllerContext.HttpContext = Context();
        var download = (PhysicalFileResult)await controller.Original(item.Id, true);
        Assert.That(download.FileDownloadName, Is.EqualTo("DSC_0001.JPG"));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(download.FileName)), Is.EqualTo(SHA256.HashData(bytes)));
    }

    [TestCase("DSC_0001.JPG", SKEncodedImageFormat.Jpeg)]
    [TestCase("Julia & Patrick – Grüße 你好.PNG", SKEncodedImageFormat.Png)]
    [TestCase("Aufnahme 9.WeBp", SKEncodedImageFormat.Webp)]
    [TestCase("unverändert.jpeg", SKEncodedImageFormat.Jpeg)]
    public async Task ParallelChunkedGuestUploadsKeepEachNameAndBytes(string name, SKEncodedImageFormat format)
    {
        var bytes = Photo(format);
        var uploaded = await Task.WhenAll(Upload(name, bytes, 3), Upload(name, bytes, 4, reverse: true));
        Assert.That(uploaded.Select(i => i.Title).Distinct().Count(), Is.EqualTo(2));
        Assert.That(uploaded.Select(i => i.OriginalFileName), Is.All.EqualTo(name));
        foreach (var item in uploaded)
            Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "uploads", identifier, item.Title))), Is.EqualTo(SHA256.HashData(bytes)));
    }

    [TestCase("Film Ä.MP4")]
    [TestCase("Film Ä.MoV")]
    public async Task VideoOriginalNameAndBytesSurviveChunkedUpload(string name)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "Assets", "DemoVideo.mp4"));
        var item = await Upload(name, bytes, 3);
        Assert.That(item.MediaType, Is.EqualTo(MediaType.Video));
        Assert.That(item.OriginalFileName, Is.EqualTo(name));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "uploads", identifier, item.Title))), Is.EqualTo(SHA256.HashData(bytes)));
    }

    [TestCase("Foto Ä.HEIC")]
    [TestCase("Foto Ä.HEIF")]
    public async Task LinuxHeifUploadPreservesOriginalNameAndBytes(string name)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Native HEIF fixture generation runs in Linux CI.");
        var source = Path.Combine(temporary, "fixture.heic");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("convert")
        { ArgumentList = { "-size", "30x20", "xc:green", source }, UseShellExecute = false });
        await process!.WaitForExitAsync();
        Assert.That(process.ExitCode, Is.Zero);
        var bytes = await File.ReadAllBytesAsync(source);
        var item = await Upload(name, bytes, 3);
        Assert.That(item.OriginalFileName, Is.EqualTo(name));
        Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "uploads", identifier, item.Title))), Is.EqualTo(SHA256.HashData(bytes)));
    }

    [Test]
    public async Task ZipCollisionsAlsoHandleCaseUnicodeNormalizationAndNestedBackupArchives()
    {
        var names = new[] { "IMG.JPG", "img.jpg", "IMG (2).JPG", "Ä.JPG", "A\u0308.JPG" };
        var mappings = new Dictionary<string, string>();
        for (var i = 0; i < names.Length; i++)
        {
            var path = Path.Combine(temporary, i + ".bin");
            await File.WriteAllTextAsync(path, i.ToString());
            mappings[path] = names[i];
        }
        using var output = new MemoryStream();
        await ZipArchiveWriter.Write(output, [new ZipListing(temporary, mappings.Keys, filename: "Uploads.bak", entryNames: mappings)]);
        output.Position = 0;
        using var outer = new ZipArchive(output, ZipArchiveMode.Read);
        using var innerStream = new MemoryStream();
        await outer.Entries.Single().Open().CopyToAsync(innerStream);
        innerStream.Position = 0;
        using var inner = new ZipArchive(innerStream, ZipArchiveMode.Read);
        Assert.That(inner.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "IMG.JPG", "img (3).jpg", "IMG (2).JPG", "Ä.JPG", "A\u0308 (2).JPG" }));
        Assert.That(inner.Entries.Select(e => e.FullName.Normalize().ToUpperInvariant()).Distinct().Count(), Is.EqualTo(names.Length));
        for (var i = 0; i < names.Length; i++)
        {
            using var reader = new StreamReader(inner.Entries[i].Open());
            Assert.That(await reader.ReadToEndAsync(), Is.EqualTo(i.ToString()));
        }
    }

    [Test]
    public async Task SelectedAndWholeGalleryZipsPreserveNamesSortAndBytesDeterministically()
    {
        var bytes = Photo();
        var names = new[] { "DSC_11.JPG", "DSC_9.JPG", "DSC_10.JPG", "IMG.JPG", "IMG.JPG", "IMG (2).JPG", "Grüße & 你好.JPG" };
        var uploaded = new List<GalleryItemModel>();
        foreach (var name in names) uploaded.Add(await Upload(name, bytes));
        await using var db = Database();
        var repository = new EFDatabaseHelper(db, NullLogger<EFDatabaseHelper>.Instance);
        var identity = new IdentityHelper();
        var helper = new GalleryDownloadHelper(repository, new MediaAccessHelper(repository, identity, settings), new MediaFileStore(root), identity, settings);
        foreach (var selected in new[] { false, true })
        {
            var plan = await helper.Build(Context(), 2, null, null, selected ? uploaded.Select(i => i.Title).ToList() : null);
            Assert.That(plan.StatusCode, Is.EqualTo(200));
            using var output = new MemoryStream();
            await ZipArchiveWriter.Write(output, plan.Files);
            output.Position = 0;
            using var archive = new ZipArchive(output, ZipArchiveMode.Read);
            Assert.That(archive.Entries.Select(e => e.FullName), Is.EqualTo(new[] {
                "Approved/DSC_9.JPG", "Approved/DSC_10.JPG", "Approved/DSC_11.JPG", "Approved/Grüße & 你好.JPG",
                "Approved/IMG (2).JPG", "Approved/IMG.JPG", "Approved/IMG (3).JPG" }));
            foreach (var entry in archive.Entries)
            {
                using var actual = new MemoryStream();
                await entry.Open().CopyToAsync(actual);
                Assert.That(SHA256.HashData(actual.ToArray()), Is.EqualTo(SHA256.HashData(bytes)));
            }
        }
        Assert.That(await db.GalleryItems.CountAsync(i => i.OriginalFileName == "IMG.JPG"), Is.EqualTo(2));
    }

    [TestCase("DSC_0042.JPG", "DSC_0042.JPG")]
    [TestCase("C:\\fakepath\\Grüße & 你好.JPG", "Grüße & 你好.JPG")]
    [TestCase("../../photo.JPG", "photo.JPG")]
    [TestCase("..", null)]
    [TestCase("photo\r\nInjected.JPG", "photo__Injected.JPG")]
    [TestCase("CON.jpg", "_CON.jpg")]
    [TestCase("special% #+()' ä.JPG", "special% #+()' ä.JPG")]
    [TestCase("Familie 👩‍👩‍👧.JPG", "Familie 👩‍👩‍👧.JPG")]
    public void OnlyUnsafeFilenamePartsAreNormalized(string value, string? expected) => Assert.That(MediaFileName.Normalize(value), Is.EqualTo(expected));

    [TestCase("../escape.jpg")]
    [TestCase("/absolute.jpg")]
    [TestCase("C:\\escape.jpg")]
    public void ZipMappingsCannotEscapeArchiveDirectory(string malicious)
    {
        using var output = new MemoryStream();
        var file = Path.Combine(temporary, "test.db");
        Assert.ThrowsAsync<InvalidDataException>(() => ZipArchiveWriter.Write(output,
            [new ZipListing(temporary, [file], entryNames: new Dictionary<string, string> { [file] = malicious })]));
    }

    [TearDown]
    public void Cleanup()
    {
        services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(temporary, true);
        foreach (var area in new[] { "uploads", "thumbnails" })
        {
            var directory = Path.Combine(root, area, identifier);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
