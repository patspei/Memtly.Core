using System.Web;
using Memtly.Core.Constants;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Memtly.Core.Attributes
{
    public class RequiresSecretKeyAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            try
            {
                int? galleryId = null;

                var request = filterContext.HttpContext.Request;
                var databaseHelper = filterContext.HttpContext.RequestServices.GetService<IDatabaseHelper>();
                var urlHelper = filterContext.HttpContext.RequestServices.GetService<Helpers.IUrlHelper>();

                if (databaseHelper != null)
                {
                    var galleryIdentifier = urlHelper!.ExtractQueryValue(request, "identifier", null)?.ToLower();
                    if (!string.IsNullOrWhiteSpace(galleryIdentifier))
                    {
                        galleryId = databaseHelper.GetGalleryId(galleryIdentifier).Result;
                    }

                    if (galleryId == null)
                    {
                        var galleryName = urlHelper!.ExtractQueryValue(request, "id", null)?.ToLower() ?? SystemGalleries.DefaultGallery.ToLower();
                        if (!string.IsNullOrWhiteSpace(galleryName))
                        { 
                            galleryId = (databaseHelper?.GetGalleryIdByName(galleryName)?.Result) ?? 1;
                        }
                    }

                    if (galleryId != null)
                    { 
                        var gallery = databaseHelper?.GetGallery(galleryId.Value).Result;
                        if (gallery != null)
                        { 
                            var wedding = Models.WeddingOptions.Read(filterContext.HttpContext.RequestServices.GetService<IConfiguration>());
                            if (wedding.IsWedding(gallery))
                            {
                                var access = filterContext.HttpContext.RequestServices.GetRequiredService<IMediaAccessHelper>();
                                if (!access.CanAccessGallery(filterContext.HttpContext, gallery).GetAwaiter().GetResult())
                                    filterContext.Result = new RedirectToActionResult("Login", "Wedding", new { returnUrl = $"/Gallery?identifier={Uri.EscapeDataString(gallery.Identifier)}" });
                                else if (request.Query.ContainsKey("key") || request.Query.ContainsKey("enc"))
                                    filterContext.Result = new RedirectResult($"/Gallery?identifier={Uri.EscapeDataString(gallery.Identifier)}");
                                return;
                            }
                            var encryptionHelper = filterContext.HttpContext.RequestServices.GetService<IEncryptionHelper>();
                            if (encryptionHelper != null)
                            {
                                var key = urlHelper!.ExtractQueryValue(request, "key", string.Empty);
                                var enc = urlHelper!.ExtractQueryValue(request, "enc", "false")!.ToLower();

                                var isEncrypted = bool.Parse(enc);
                                if (!isEncrypted && !string.IsNullOrWhiteSpace(key) && encryptionHelper.IsEncryptionEnabled())
                                {
                                    var queryString = HttpUtility.ParseQueryString(request.QueryString.ToString());
                                    queryString.Set("enc", "true");
                                    queryString.Set("key", encryptionHelper.Encrypt(key));

                                    filterContext.Result = new RedirectResult($"/Gallery?{queryString.ToString()}");
                                }
                                else if (!string.IsNullOrWhiteSpace(gallery.SecretKey))
                                {
                                    if (!string.IsNullOrWhiteSpace(key))
                                    {
                                        var secretKey = encryptionHelper.IsEncryptionEnabled() ? encryptionHelper.Encrypt(gallery.SecretKey) : gallery.SecretKey;
                                        if (!string.IsNullOrWhiteSpace(secretKey) && !string.Equals(secretKey, key))
                                        {
                                            var logger = filterContext.HttpContext.RequestServices.GetService<ILogger<RequiresSecretKeyAttribute>>();
                                            if (logger != null)
                                            {
                                                logger.LogWarning($"A request was made to an endpoint with an invalid secret key");
                                            }

                                            filterContext.Result = new RedirectToActionResult("Index", "Error", new { Reason = ErrorCode.InvalidSecretKey }, false);
                                        }
                                        else
                                        {
                                            filterContext.HttpContext.RequestServices.GetRequiredService<IMediaAccessHelper>()
                                                .GrantGallery(filterContext.HttpContext, gallery);
                                        }
                                    }
                                    else
                                    {
                                        var access = filterContext.HttpContext.RequestServices.GetRequiredService<IMediaAccessHelper>();
                                        if (!access.CanAccessGallery(filterContext.HttpContext, gallery).GetAwaiter().GetResult())
                                            filterContext.Result = new RedirectResult($"/Gallery/Login?identifier={gallery.Identifier}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                var logger = filterContext.HttpContext.RequestServices.GetService<ILogger<RequiresSecretKeyAttribute>>();
                if (logger != null)
                {
                    logger.LogError("Failed to validate gallery access credential");
                }
                filterContext.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            }
        }
    }
}
