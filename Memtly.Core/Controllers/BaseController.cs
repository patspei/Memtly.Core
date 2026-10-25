using System.IO.Compression;
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
                    HttpContext.Response.Headers.Append("Content-Type", "application/zip");
                    HttpContext.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{filename}\"");

                    var bodyStream = Response.BodyWriter.AsStream(true);

                    using (var zipArchive = new ZipArchive(bodyStream, ZipArchiveMode.Create, true))
                    {
                        foreach (var contents in contentsList.Where(x => !string.IsNullOrWhiteSpace(x.SourcePath)))
                        {
                            var sourceRoot = Path.GetFullPath(contents.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                            var files = contents.Files?.Select(Path.GetFullPath).Where(x => x.StartsWith(sourceRoot, comparison));
                            if (files != null && files.Any())
                            {
                                if (!string.IsNullOrWhiteSpace(contents?.FileName))
                                {
                                    var tempZipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");

                                    try
                                    {
                                        await using (var tempFileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
                                        {
                                            using (var archive = new ZipArchive(tempFileStream, ZipArchiveMode.Create, true))
                                            {
                                                foreach (var file in files)
                                                {
                                                    var path = Path.GetRelativePath(contents.SourcePath, file);
                                                    var archiveEntry = archive.CreateEntry(path.Replace('\\', '/'));

                                                    await using (var es = archiveEntry.Open())
                                                    await using (var fs = System.IO.File.OpenRead(file))
                                                    {
                                                        await fs.CopyToAsync(es);
                                                    }
                                                }
                                            }
                                        }

                                        var relativePath = contents.FileName.TrimStart('/');
                                        var zipEntry = zipArchive.CreateEntry((!string.IsNullOrWhiteSpace(contents.Directory) ? Path.Combine(contents.Directory, relativePath) : relativePath).Replace('\\', '/'));

                                        await using (var entryStream = zipEntry.Open())
                                        await using (var tempReadStream = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                                        {
                                            await tempReadStream.CopyToAsync(entryStream);
                                        }
                                    }
                                    finally
                                    {
                                        try
                                        {
                                            if (System.IO.File.Exists(tempZipPath))
                                            {
                                                System.IO.File.Delete(tempZipPath);
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                else
                                {
                                    foreach (var file in files)
                                    {
                                        var relativePath = Path.GetRelativePath(contents.SourcePath, file);
                                        var zipEntry = zipArchive.CreateEntry((!string.IsNullOrWhiteSpace(contents.Directory) ? Path.Combine(contents.Directory, relativePath) : relativePath).Replace('\\', '/'));

                                        using (var fs = System.IO.File.OpenRead(file))
                                        using (var entryStream = zipEntry.Open())
                                        {
                                            await fs.CopyToAsync(entryStream);
                                        }
                                    }
                                }
                            }
                        }
                    }
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
