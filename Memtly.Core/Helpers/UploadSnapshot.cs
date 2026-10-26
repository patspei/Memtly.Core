using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memtly.Core.Models;

namespace Memtly.Core.Helpers;

public sealed record UploadSnapshot(string Name, string Email, string Filename, int Size, int Chunks, string Checksum)
{
    public static string Prefix(HttpContext context, MediaUploadRequest request) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{context.Session.Id}|{request.GalleryId}|{request.CollectionId}|{request.UploadId}")));

    public static UploadSnapshot Capture(string directory, string prefix, MediaUploadRequest request, string name, string email)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, prefix + ".identity.json");
        var snapshot = new UploadSnapshot(name, email, request.File.FileName, request.FileSize, request.TotalChunks, request.FileChecksum);
        if (request.ChunkIndex == 0)
        {
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, snapshot);
            }
            catch (IOException) when (File.Exists(path)) { /* Retry keeps the original identity. */ }
        }
        var saved = JsonSerializer.Deserialize<UploadSnapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        if (saved.Filename != snapshot.Filename || saved.Size != snapshot.Size || saved.Chunks != snapshot.Chunks || saved.Checksum != snapshot.Checksum)
            throw new InvalidDataException("Upload metadata changed between chunks.");
        return saved;
    }
}
