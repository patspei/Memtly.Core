using System.Net;
using System.Security.Claims;
using Memtly.Core.Attributes;
using Memtly.Core.Controllers;
using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Models;
using Memtly.Core.Models.Database;
using Memtly.Core.UnitTests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class WeddingAccessTests
{
    private IDatabaseHelper database = null!;
    private ISettingsHelper settings = null!;
    private IConfiguration configuration = null!;
    private List<GalleryModel> galleries = null!;
    private DefaultHttpContext context = null!;
    private WeddingOptions options = null!;

    [SetUp]
    public void Setup()
    {
        configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Wedding:Enabled"] = "true" }).Build();
        options = WeddingOptions.Read(configuration);
        galleries = [new() { Id = 1, Name = options.ProfessionalGallery, SecretKey = "test-code", Owner = 10 },
            new() { Id = 2, Name = options.GuestGallery, SecretKey = "test-code", Owner = 10 },
            new() { Id = 3, Name = options.PrivateGallery, SecretKey = "test-code", Owner = 10, Type = GalleryType.Drop },
            new() { Id = 4, Name = "Unrelated", SecretKey = "test-code", Owner = 11 }];
        database = Substitute.For<IDatabaseHelper>();
        database.GetGalleries().Returns(galleries);
        database.GetGallery(Arg.Any<int>()).Returns(call => galleries.Single(g => g.Id == call.Arg<int>()));
        foreach (var gallery in galleries) database.GetGalleryId(gallery.Identifier).Returns(gallery.Id);
        settings = Substitute.For<ISettingsHelper>();
        settings.GetOrDefault(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>()).Returns(true);
        context = new() { Session = new MockSession() };
        context.Connection.RemoteIpAddress = new IPAddress(Guid.NewGuid().ToByteArray());
    }

    private MediaAccessHelper Access() => new(database, new IdentityHelper(), settings, configuration);
    private void Grant()
    {
        WeddingAccessHelper.Grant(context, options, galleries.Take(3).ToList(), Access());
        var cookie = context.Response.Headers.SetCookie.Single(value => value!.StartsWith(".Memtly.Wedding.Session="))!.Split(';')[0];
        context.Request.Headers.Cookie = cookie;
    }

    [Test]
    public async Task OneGrantOpensExactlyThreeWeddingGalleriesWithoutRawKeys()
    {
        Assert.That(await Access().CanAccessGallery(context, galleries[0], "test-code"), Is.False);
        Grant();
        foreach (var gallery in galleries.Take(3)) Assert.That(await Access().CanAccessGallery(context, gallery), Is.True);
        Assert.That(await Access().CanAccessGallery(context, galleries[3]), Is.False);
        var cookies = context.Response.Headers.SetCookie.ToString();
        Assert.That(cookies, Does.Not.Contain("test-code").And.Not.Contain("max-age").And.Not.Contain("expires="));
        Assert.That(cookies, Does.Contain("httponly"));
        context.Request.Headers.Cookie = "";
        Assert.That(await Access().CanAccessGallery(context, galleries[0]), Is.False, "Closing the browser ends wedding access even if the older Memtly session persists.");
    }

    [Test]
    public async Task RotationAndInvalidGalleryConfigurationRevokeTheWholeGrant()
    {
        Grant();
        galleries[1].SecretKey = "rotated";
        Assert.That(await Access().CanAccessGallery(context, galleries[0]), Is.False);
        galleries[1].SecretKey = "test-code";
        galleries[2].Type = GalleryType.Basic;
        Assert.That(await Access().CanAccessGallery(context, galleries[0]), Is.False);
    }

    [Test]
    public async Task DifferentKeysRequireExplicitServerSideCode()
    {
        galleries[1].SecretKey = "other";
        Assert.That(WeddingAccessHelper.Code(options, galleries.Take(3).ToList()), Is.Null);
        options.AccessCode = "separate-code";
        Assert.That(WeddingAccessHelper.Code(options, galleries.Take(3).ToList()), Is.EqualTo("separate-code"));
        galleries.Add(new() { Id = 5, Name = options.GuestGallery });
        Assert.That(await WeddingAccessHelper.Resolve(database, options), Is.Null, "Ambiguous names must fail closed.");
    }

    [TestCase(null)]
    [TestCase("20:Paid")]
    public async Task PrivateGrantNeverPermitsReadingEvenTheUploadersOwnFile(string? user)
    {
        Grant();
        if (user != null) context.User = User(20, "Paid");
        foreach (var state in new[] { GalleryItemState.Approved, GalleryItemState.Pending, GalleryItemState.Rejected })
        {
            var item = new GalleryItemModel { GalleryId = 3, UserId = 20, State = state };
            Assert.That(await Access().CanRead(context, item), Is.False);
            Assert.That(await Access().CanDownload(context, item), Is.False);
        }
        context.User = User(10, "Paid");
        Assert.That(await Access().CanDownload(context, new() { GalleryId = 3, State = GalleryItemState.Approved }), Is.True);
        context.User = User(1, "Admin");
        Assert.That(await Access().CanRead(context, new() { GalleryId = 3, State = GalleryItemState.Pending }), Is.True);
    }

    private static ClaimsPrincipal User(int id, string role) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.Sid, id.ToString()), new Claim(ClaimTypes.Role, role + "|None")], "Tests"));

    [Test]
    public void RequiresSecretKeyUsesCentralLoginAndSessionEvenWhenGalleryHasNoKey()
    {
        galleries[0].SecretKey = null;
        using var services = new ServiceCollection().AddSingleton(database).AddSingleton(configuration)
            .AddSingleton<Memtly.Core.Helpers.IUrlHelper>(new Memtly.Core.Helpers.UrlHelper(settings))
            .AddSingleton<IMediaAccessHelper>(Access()).BuildServiceProvider();
        context.RequestServices = services;
        context.Request.QueryString = new QueryString("?identifier=" + galleries[0].Identifier);
        var action = new ActionExecutingContext(new ActionContext(context, new RouteData(), new ActionDescriptor()), [], new Dictionary<string, object?>(), new object());
        new RequiresSecretKeyAttribute().OnActionExecuting(action);
        Assert.That(action.Result, Is.TypeOf<RedirectToActionResult>());
        Assert.That(((RedirectToActionResult)action.Result!).ControllerName, Is.EqualTo("Wedding"));
        options.AccessCode = "separate-code";
        configuration["Wedding:AccessCode"] = options.AccessCode;
        Grant();
        action.Result = null;
        new RequiresSecretKeyAttribute().OnActionExecuting(action);
        Assert.That(action.Result, Is.Null);
    }

    [Test]
    public void FailedAttemptsSurviveClearedSessionsAndSuccessDoesNotConsumeBudget()
    {
        for (var i = 0; i < 10; i++) { Assert.That(WeddingAccessHelper.TakeAttempt(context), Is.True); WeddingAccessHelper.ReleaseSuccessfulAttempt(context); }
        for (var i = 0; i < 5; i++) Assert.That(WeddingAccessHelper.TakeAttempt(context), Is.True);
        Assert.That(WeddingAccessHelper.TakeAttempt(context), Is.False);
        for (var i = 0; i < 25; i++) { context.Session = new MockSession(); Assert.That(WeddingAccessHelper.TakeAttempt(context), Is.True); }
        context.Session = new MockSession();
        Assert.That(WeddingAccessHelper.TakeAttempt(context), Is.False);
    }

    [TestCase("test-code", true)]
    [TestCase("wrong", false)]
    [TestCase(null, false)]
    public async Task CentralLoginValidatesCodeAndNeverEchoesItInRedirect(string? code, bool allowed)
    {
        var controller = new WeddingController(configuration, database, Access()) { ControllerContext = new() { HttpContext = context } };
        var result = await controller.Login(code, "/Gallery?identifier=" + galleries[0].Identifier + "&key=must-not-echo");
        if (allowed)
        {
            Assert.That(result, Is.TypeOf<LocalRedirectResult>());
            Assert.That(((LocalRedirectResult)result).Url, Is.EqualTo("/Gallery?identifier=" + galleries[0].Identifier));
        }
        else
        {
            Assert.That(result, Is.TypeOf<ViewResult>());
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain(".Memtly.Wedding.Session"));
        }
    }
}
