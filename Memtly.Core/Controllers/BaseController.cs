using Memtly.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Memtly.Core.Controllers
{
    public class BaseController : Controller
    {
        public BaseController()
            : base()
        {
        }

        protected async Task<IActionResult> ZipFileResponse(string filename, ZipListing content)
        {
            return await ZipFileResponse(filename, new List<ZipListing>() { content });
        }

        protected async Task<IActionResult> ZipFileResponse(string filename, IEnumerable<ZipListing> contentsList)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filename) && contentsList != null && contentsList.Count() > 0)
                {
                    Response.ContentType = "application/zip";
                    var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
                    disposition.SetHttpFileName(Memtly.Core.Helpers.MediaFileName.Normalize(filename) ?? "download.zip");
                    Response.Headers.ContentDisposition = disposition.ToString();
                    await Memtly.Core.Helpers.ZipArchiveWriter.Write(Response.BodyWriter.AsStream(true), contentsList);
                }
            }
            catch (Exception ex)
            {
                var logger = HttpContext.RequestServices.GetService<ILogger<BaseController>>();
                if (logger != null)
                { 
                    var localizer = HttpContext.RequestServices.GetService<IStringLocalizer<Localization.Translations>>();
                    if (localizer != null)
                    { 
                        logger.LogWarning(ex, $"{localizer["Unexpected_Error_Occurred"].Value} - {ex?.Message}");
                    }
                }
            }

            return new EmptyResult();
        }

        protected async Task<IActionResult> ErrorResponse(int errorCode)
        {
            var isAjaxRequest = Request.Headers.ContainsKey("X-Requested-With") && string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
            if (isAjaxRequest)
            {
                var localizer = HttpContext.RequestServices.GetService<IStringLocalizer<Localization.Translations>>();
                if (localizer != null)
                { 
                    return new JsonResult(new { success = false, error_title = localizer[$"{errorCode}_Error_Title"].Value, error_message = localizer[$"{errorCode}_Error_Message"].Value });
                }
            }

            return new RedirectToActionResult("Index", "Error", new { Reason = errorCode }, false);
        }
    }
}
