using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Memtly.Core.Controllers;
using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Helpers.Notifications;
using Memtly.Core.Middleware;
using Memtly.Core.Models.Database;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Memtly.Core.UnitTests.Tests.Controllers;

/// <summary>Real Kestrel, cookies, MVC, range requests and ZIP bytes. All data is
/// isolated under a temporary directory; the production database is never used.</summary>
[NonParallelizable]
public class MediaSecurityTests
{
    private IWebHost host = null!;
    private string root = null!;
    private Uri baseUrl = null!;
    private readonly List<GalleryItemModel> items = [];
    private Dictionary<int, GalleryModel> galleries = null!;
    private IDatabaseHelper database = null!;
    private ISettingsHelper settings = null!;
    private ILogger<GalleryController> downloadLogger = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        root = Path.Combine(Path.GetTempPath(), "memtly-media-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        galleries = new()
        {
            [-1] = new() { Id = -1, Identifier = "all", Name = "All", Type = GalleryType.Basic },
            [1] = new() { Id = 1, Identifier = new string('a', 32), Name = "Public", Owner = 10, Type = GalleryType.Basic },
            [2] = new() { Id = 2, Identifier = new string('b', 32), Name = "Protected", Owner = 11, SecretKey = "child-key", Type = GalleryType.Basic },
            [3] = new() { Id = 3, Identifier = new string('c', 32), Name = "Private drop", Owner = 10, Type = GalleryType.Drop },
            [4] = new() { Id = 4, Identifier = new string('d', 32), Name = "Collection", Owner = 12, SecretKey = "collection-key", Type = GalleryType.Collection }
        };
        AddItem(1, 1, GalleryItemState.Approved, "approved.jpg", "APPROVED-PUBLIC");
        AddItem(2, 1, GalleryItemState.Pending, "pending.jpg", "PRIVATE-PENDING");
        AddItem(3, 1, GalleryItemState.Rejected, "rejected.jpg", "PRIVATE-REJECTED");
        AddItem(4, 3, GalleryItemState.Approved, "drop.jpg", "PRIVATE-DROP");
        AddItem(5, 2, GalleryItemState.Approved, "protected.jpg", "PRIVATE-KEY");
        AddItem(6, 3, GalleryItemState.Approved, "own.jpg", "OWN-DROP", 20);
        AddItem(7, 1, GalleryItemState.Pending, "own-pending.jpg", "OWN-PENDING", 20);
        AddItem(9, 1, GalleryItemState.Pending, "approved.png", "COLLIDING-PENDING");
        File.WriteAllText(Path.Combine(root, "uploads", galleries[1].Identifier, "Rejected", "legacy-rejected.jpg"), "PRIVATE-LEGACY");
        items.Add(new() { Id = 8, GalleryId = 1, Title = "../pending.jpg", State = GalleryItemState.Approved });
        // A publicly reachable legacy copy really exists: the middleware must
        // block it rather than merely relying on a missing test file.
        var legacy = Path.Combine(root, "wwwroot", "uploads", galleries[1].Identifier);
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "pending.jpg"), "LEGACY-PRIVATE");
        var rclLegacy = Path.Combine(root, "wwwroot", "_content", "Memtly.Core", "uploads", galleries[1].Identifier);
        Directory.CreateDirectory(rclLegacy);
        await File.WriteAllTextAsync(Path.Combine(rclLegacy, "pending.jpg"), "LEGACY-PRIVATE");

        database = Substitute.For<IDatabaseHelper>();
        database.GetGallery(Arg.Any<int>()).Returns(call => galleries.GetValueOrDefault(call.Arg<int>()));
        database.GetGalleryItem(Arg.Any<int>()).Returns(call => items.FirstOrDefault(i => i.Id == call.Arg<int>()));
        // Deliberately return too much: the authorization layer must intersect
        // the collection and status scope even if a repository query is broad.
        database.GetGalleryItems(Arg.Any<GalleryItemSearch>()).Returns(_ => items.ToList());
        database.GetGalleriesByCollectionId(4).Returns(new List<GalleryModel> { galleries[1], galleries[2], galleries[3] });
        database.GetGalleries().Returns(galleries.Values.ToList());
        settings = Substitute.For<ISettingsHelper>();
        downloadLogger = Substitute.For<ILogger<GalleryController>>();
        settings.GetOrDefault(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>()).Returns(true);
        var localizer = Substitute.For<IStringLocalizer<Localization.Translations>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var images = Substitute.For<IImageHelper>();
        images.GenerateThumbnail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(async call =>
        {
            await File.WriteAllTextAsync(call.ArgAt<string>(1), "THUMB-" + await File.ReadAllTextAsync(call.ArgAt<string>(0)));
            return true;
        });

        host = WebHost.CreateDefaultBuilder()
            .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
            .UseContentRoot(root).UseWebRoot("wwwroot")
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
                services.AddControllersWithViews().AddApplicationPart(typeof(GalleryController).Assembly);
                services.AddAuthorization();
                services.AddDistributedMemoryCache(); services.AddSession();
                services.AddSingleton(database); services.AddSingleton(settings);
                services.AddSingleton(localizer);
                services.AddSingleton(downloadLogger);
                services.AddSingleton<IIdentityHelper, IdentityHelper>();
                services.AddScoped<IMediaAccessHelper, MediaAccessHelper>();
                services.AddSingleton<IMediaFileStore>(new MediaFileStore(root));
                services.AddScoped<GalleryDownloadHelper>();
                services.AddScoped<IMediaThumbnailHelper, MediaThumbnailHelper>();
                services.AddSingleton(Substitute.For<IFileHelper>());
                services.AddSingleton(Substitute.For<IDeviceDetector>());
                services.AddSingleton(images);
                services.AddSingleton(Substitute.For<INotificationHelper>());
                services.AddSingleton(Substitute.For<IEncryptionHelper>());
                services.AddSingleton(Substitute.For<Memtly.Core.Helpers.IUrlHelper>());
            })
            .Configure(app =>
            {
                app.UseDeveloperExceptionPage();
                app.UseMiddleware<PrivateMediaMiddleware>();
                app.UseStaticFiles();
                // Test authentication exists only in this isolated test host.
                app.Use(async (context, next) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                    {
                        var parts = user.ToString().Split(':');
                        context.User = new ClaimsPrincipal(new ClaimsIdentity([
                            new Claim(ClaimTypes.Sid, parts[0]), new Claim(ClaimTypes.Role, parts[1] + "|None")
                        ], "Fixture"));
                    }
                    await next();
                });
                app.UseRouting(); app.UseAuthorization(); app.UseSession();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllerRoute("default", "{controller}/{action=Index}/{id?}");
                    // Emulate successful credential entry without rendering the
                    // entire gallery; grant and cookie storage are the real code.
                    endpoints.MapGet("/grant/{id:int}", async context =>
                    {
                        var gallery = galleries[int.Parse(context.Request.RouteValues["id"]!.ToString()!)];
                        var access = context.RequestServices.GetRequiredService<IMediaAccessHelper>();
                        context.Response.StatusCode = await access.CanAccessGallery(context, gallery, context.Request.Query["key"])
                            ? 204 : 403;
                    });
                });
            }).Build();
        await host.StartAsync();
        baseUrl = new Uri(host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    private void AddItem(int id, int gallery, GalleryItemState state, string title, string data, int user = 10)
    {
        var item = new GalleryItemModel { Id = id, GalleryId = gallery, State = state, Title = title,
            UserId = user, MediaType = MediaType.Image, UploadedBy = "Test", UploadedDate = DateTimeOffset.UtcNow };
        items.Add(item);
        var folder = Path.Combine(root, "uploads", galleries[gallery].Identifier);
        if (state != GalleryItemState.Approved) folder = Path.Combine(folder, state.ToString());
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, title), data);
        folder = Path.Combine(root, "thumbnails", galleries[gallery].Identifier);
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, Path.GetFileNameWithoutExtension(title) + ".webp"), "THUMB-" + data);
    }

    private HttpClient Client(string? user = null)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
            { BaseAddress = baseUrl };
        if (user != null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    private static async Task<Dictionary<string, string>> Zip(HttpResponseMessage response)
    {
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var result = new Dictionary<string, string>();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName.Replace('\\', '/')] = await reader.ReadToEndAsync();
        }
        return result;
    }

    private static Task<HttpResponseMessage> Download(HttpClient client, int id = 1, params (string Key, string Value)[] extra) =>
        client.PostAsync("/Gallery/DownloadGallery", new FormUrlEncodedContent(
            new[] { new KeyValuePair<string, string>("id", id.ToString()) }
                .Concat(extra.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)))));

    [TestCase(1, HttpStatusCode.OK)]
    [TestCase(2, HttpStatusCode.NotFound)]
    [TestCase(3, HttpStatusCode.NotFound)]
    [TestCase(4, HttpStatusCode.NotFound)]
    [TestCase(5, HttpStatusCode.NotFound)]
    public async Task SingleDownloadMetadataUsesDownloadAuthorization(int id, HttpStatusCode status)
    {
        using var client = Client();
        var response = await client.GetAsync($"/Media/DownloadInfo/{id}");
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        if (status == HttpStatusCode.OK)
        {
            var info = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.That(info.GetProperty("count").GetInt32(), Is.EqualTo(1));
            Assert.That(info.GetProperty("bytes").GetInt64(), Is.EqualTo("APPROVED-PUBLIC".Length));
        }
    }

    [Test]
    public async Task ConfiguredPrivateGalleryCannotBecomePublicThroughWrongGalleryType()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Wedding:Enabled"] = "true", ["Wedding:PrivateGallery"] = "Public" }).Build();
        var access = new MediaAccessHelper(database, new IdentityHelper(), settings, configuration);
        var context = new DefaultHttpContext();
        Assert.That(await access.CanRead(context, items.Single(item => item.Id == 1)), Is.False);
        Assert.That(await access.CanDownload(context, items.Single(item => item.Id == 1)), Is.False);
    }

    [Test]
    public async Task ArchiveMetadataAndFilteredZipHaveTheSameAuthorizedFiles()
    {
        using var client = Client();
        var response = await client.PostAsync("/Gallery/DownloadInfo", new FormUrlEncodedContent(new Dictionary<string,string> { ["id"] = "1", ["fileFilter[0]"] = "approved.jpg", ["fileFilter[1]"] = "pending.jpg" }));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var info = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var zip = await Zip(await Download(client, 1, ("fileFilter[0]", "approved.jpg"), ("fileFilter[1]", "pending.jpg")));
        Assert.That(info.GetProperty("count").GetInt32(), Is.EqualTo(zip.Count));
        Assert.That(info.GetProperty("bytes").GetInt64(), Is.EqualTo(zip.Values.Sum(value => value.Length)));
    }

    [TestCase(2, HttpStatusCode.Forbidden)]
    [TestCase(3, HttpStatusCode.Forbidden)]
    public async Task ArchiveMetadataDoesNotRevealProtectedOrDropContents(int id, HttpStatusCode status)
    {
        using var client = Client();
        var response = await client.PostAsync("/Gallery/DownloadInfo", new FormUrlEncodedContent(new Dictionary<string,string> { ["id"] = id.ToString() }));
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("bytes"));
    }

    [Test]
    public async Task AnonymousZipContainsOnlyApprovedEvenWhenShowPendingUploadsIsTrue()
    {
        using var client = Client();
        var zip = await Zip(await Download(client));
        Assert.That(zip, Has.Count.EqualTo(1));
        Assert.That(zip.Values.Single(), Is.EqualTo("APPROVED-PUBLIC"));
        Assert.That(zip.Keys.Single(), Does.EndWith("approved.jpg"));
    }

    [TestCase("pending.jpg")]
    [TestCase("rejected.jpg")]
    [TestCase("own-pending.jpg")]
    public async Task FilenameFilterCannotForceUnauthorizedStatus(string file)
    {
        using var client = Client();
        var response = await Download(client, 1, ("fileFilter[0]", file));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("PRIVATE"));
    }

    [TestCase("0|anything|Pending", HttpStatusCode.NotFound)]
    [TestCase("0|anything|Rejected", HttpStatusCode.NotFound)]
    [TestCase("3|Image|Approved", HttpStatusCode.OK)]
    [TestCase("garbage", HttpStatusCode.BadRequest)]
    [TestCase("0|anything|987", HttpStatusCode.BadRequest)]
    public async Task GroupFiltersOnlyNarrowTheAuthorizedSet(string group, HttpStatusCode status)
    {
        using var client = Client();
        var response = await Download(client, 1, ("group", group));
        Assert.That(response.StatusCode, Is.EqualTo(status));
        if (status == HttpStatusCode.OK)
            Assert.That((await Zip(response)).Values, Is.EquivalentTo(new[] { "APPROVED-PUBLIC" }));
    }

    [Test]
    public async Task AdministratorZipRetainsAuthorizedApprovedPendingAndRejected()
    {
        using var client = Client("1:Admin");
        var zip = await Zip(await Download(client));
        Assert.That(zip.Values, Is.EquivalentTo(new[] { "APPROVED-PUBLIC", "PRIVATE-PENDING", "PRIVATE-REJECTED", "OWN-PENDING", "PRIVATE-LEGACY", "COLLIDING-PENDING" }));
    }

    [Test]
    public async Task AuthorizedOwnerRetainsManagementAccess()
    {
        using var client = Client("10:Paid");
        Assert.That((await Zip(await Download(client))).Values, Does.Contain("PRIVATE-REJECTED"));
    }

    [Test]
    public async Task AnonymousDropZipAndApprovedOriginalAreDenied()
    {
        using var client = Client();
        Assert.That((await Download(client, 3)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await client.GetAsync("/Media/Original/4")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await client.GetAsync("/Media/Thumbnail/4")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await client.GetAsync("/MediaViewer/GalleryItem?id=4")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AllGalleriesIsReservedForAuthorizedAdministrators()
    {
        using var guest = Client();
        Assert.That((await Download(guest, -1)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var admin = Client("1:Admin");
        var zip = await Zip(await Download(admin, -1));
        Assert.That(zip.Values, Does.Contain("PRIVATE-DROP"));
        Assert.That(zip.Values, Does.Contain("PRIVATE-KEY"));
    }

    [Test]
    public async Task CollectionOwnerCannotManageAnUnrelatedChildGallery()
    {
        using var owner = Client("12:Paid");
        var zip = await Zip(await Download(owner, 4));
        Assert.That(zip.Values, Is.EquivalentTo(new[] { "APPROVED-PUBLIC" }));
    }

    [Test]
    public async Task RegisteredUploaderCanOnlyDownloadTheirOwnApprovedDropUpload()
    {
        using var client = Client("20:Paid");
        var zip = await Zip(await Download(client, 3));
        Assert.That(zip.Values, Is.EquivalentTo(new[] { "OWN-DROP" }));
        Assert.That((await client.GetAsync("/Media/Original/4")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task InvalidGalleryKeyIsDeniedWithoutLeakingTheCredential()
    {
        using var client = Client();
        const string secret = "DO-NOT-LOG-THIS-KEY";
        var response = await Download(client, 2, ("secretKey", secret));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain(secret));
        var logged = string.Join("\n", downloadLogger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "Log").Select(call => call.GetArguments()[2]?.ToString()));
        Assert.That(logged, Does.Not.Contain(secret));
    }

    [Test]
    public async Task DownloadSettingGatesZipAndAttachmentButDoesNotBreakAuthorizedViewing()
    {
        settings.GetOrDefault(Memtly.Core.Constants.MemtlyConfiguration.Gallery.Download, true, 1).Returns(false);
        try
        {
            using var guest = Client();
            Assert.That((await Download(guest)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That((await guest.GetAsync("/Media/Original/1?download=true")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await guest.GetStringAsync("/Media/Original/1"), Is.EqualTo("APPROVED-PUBLIC"));
            using var owner = Client("10:Paid");
            Assert.That((await Download(owner)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally { settings.GetOrDefault(Memtly.Core.Constants.MemtlyConfiguration.Gallery.Download, true, 1).Returns(true); }
    }

    [Test]
    public async Task ReviewRoleDoesNotImplicitlyGrantPendingZipDownloads()
    {
        using var reviewer = Client("30:Reviewer");
        Assert.That((await reviewer.GetAsync("/Media/Original/2")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Download(reviewer, 1, ("fileFilter[0]", "pending.jpg"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ChangingPublicGalleryToDropRevokesAnonymousMediaAccess()
    {
        using var guest = Client();
        Assert.That((await guest.GetAsync("/Media/Original/1")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        try
        {
            galleries[1].Type = GalleryType.Drop;
            Assert.That((await guest.GetAsync("/Media/Original/1")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally { galleries[1].Type = GalleryType.Basic; }
    }

    [Test]
    public async Task CollectionCredentialNeverGrantsProtectedOrDropChildren()
    {
        using var client = Client();
        var zip = await Zip(await Download(client, 4, ("secretKey", "collection-key")));
        Assert.That(zip.Values, Is.EquivalentTo(new[] { "APPROVED-PUBLIC" }));
        var filtered = await Download(client, 4, ("secretKey", "collection-key"), ("fileFilter[0]", "protected.jpg"));
        Assert.That(filtered.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await client.GetAsync("/Media/Original/5")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task IndependentlyAuthorizedChildCanBeDownloadedThroughCollection()
    {
        using var client = Client();
        Assert.That((await client.GetAsync("/grant/2?key=child-key")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var zip = await Zip(await Download(client, 4, ("secretKey", "collection-key")));
        Assert.That(zip.Values, Is.EquivalentTo(new[] { "APPROVED-PUBLIC", "PRIVATE-KEY" }));
    }

    [TestCase(2)]
    [TestCase(3)]
    [TestCase(7)]
    public async Task DirectPreviewOriginalHeadAndRangeNeverExposeNonApprovedMedia(int id)
    {
        using var client = Client();
        foreach (var path in new[] { $"/Media/Original/{id}", $"/Media/Thumbnail/{id}" })
        {
            var response = await client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            using var request = new HttpRequestMessage(HttpMethod.Head, path);
            Assert.That((await client.SendAsync(request)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            using var range = new HttpRequestMessage(HttpMethod.Get, path);
            range.Headers.Range = new RangeHeaderValue(0, 3);
            Assert.That((await client.SendAsync(range)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }

    [Test]
    public async Task ApprovedOriginalSingleDownloadAndVideoStyleRangesWork()
    {
        using var client = Client();
        var original = await client.GetAsync("/Media/Original/1?download=true");
        Assert.That(await original.Content.ReadAsStringAsync(), Is.EqualTo("APPROVED-PUBLIC"));
        Assert.That(original.Content.Headers.ContentDisposition?.FileNameStar ?? original.Content.Headers.ContentDisposition?.FileName,
            Does.Contain("approved.jpg"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Media/Original/1");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var range = await client.SendAsync(request);
        Assert.That(range.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
        Assert.That(await range.Content.ReadAsStringAsync(), Is.EqualTo("APPR"));
        Assert.That(await client.GetStringAsync("/Media/Thumbnail/1"), Is.EqualTo("THUMB-APPROVED-PUBLIC"));
    }

    [Test]
    public async Task KeyProtectedMediaRequiresGrantAndGrantIsRevokedOnKeyChange()
    {
        using var client = Client();
        Assert.That((await client.GetAsync("/Media/Original/5")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await client.GetAsync("/grant/2?key=child-key");
        Assert.That(await client.GetStringAsync("/Media/Original/5"), Is.EqualTo("PRIVATE-KEY"));
        try
        {
            galleries[2].SecretKey = "rotated-key";
            Assert.That((await client.GetAsync("/Media/Original/5")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally { galleries[2].SecretKey = "child-key"; }
    }

    [TestCase("/uploads/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pending.jpg")]
    [TestCase("//uploads/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pending.jpg")]
    [TestCase("/%75ploads/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pending.jpg")]
    [TestCase("/UPLOADS/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pending.jpg")]
    [TestCase("/thumbnails/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pending.webp")]
    [TestCase("/temp/Export/Uploads.bak")]
    [TestCase("/_content/Memtly.Core/uploads/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pending.jpg")]
    [TestCase("/_content/Memtly.Core/thumbnails/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/pending.webp")]
    public async Task LegacyStaticUrlsAreBlockedBeforeStaticFiles(string path)
    {
        using var client = Client();
        // Preserve a double slash as a path rather than an authority-relative URI.
        var response = await client.GetAsync(baseUrl.GetLeftPart(UriPartial.Authority) + path);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("LEGACY-PRIVATE"));
    }

    [TestCase("../pending.jpg")]
    [TestCase("..\\pending.jpg")]
    [TestCase("folder/approved.jpg")]
    public async Task TraversalAndPathFiltersAreRejected(string name)
    {
        using var client = Client("1:Admin");
        Assert.That((await Download(client, 1, ("fileFilter[0]", name))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await client.GetAsync("/Media/Original/8")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (host != null) { await host.StopAsync(); host.Dispose(); }
        if (root != null) Directory.Delete(root, true);
    }
}
