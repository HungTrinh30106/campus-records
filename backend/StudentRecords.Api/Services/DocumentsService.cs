using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Bson;
using MongoDB.Driver;
using static StudentRecords.Api.RecordHelpers;

namespace StudentRecords.Api;

public sealed class DocumentsService(ApiSupport s, Database db, IConfiguration config)
{
    public async Task<IResult> List(string id)
    {
        await s.Student(id);
        return Results.Ok((await db.Documents.Find(x => x.StudentId == id).SortByDescending(x => x.UploadedAt).ToListAsync()).Select(Dto.Document));
    }

    public async Task<IResult> Upload(string id, HttpRequest request)
    {
        await s.Student(id);
        if (!request.HasFormContentType)
            throw new ApiException(400, "Cần gửi tệp multipart.");
        var form = await request.ReadFormAsync();
        if (form.Files.Count != 1 || form.Files[0].Name != "file")
            throw new ApiException(400, "Chọn một tệp.");
        var file = form.Files[0];
        if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
            throw new ApiException(400, "Tệp tối đa 5 MiB.");
        var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (name.Length > 180 || name.Any(char.IsControl) || name.Contains('<') || name.Contains('>') || string.IsNullOrWhiteSpace(name))
            throw new ApiException(400, "Tên tệp không hợp lệ.");
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var mime = ext switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => ""
        };
        if (mime == "" || file.ContentType != mime)
            throw new ApiException(400, "Chỉ chấp nhận PDF, PNG hoặc JPEG.");
        using var stream = file.OpenReadStream();
        var head = new byte[8];
        var n = await stream.ReadAsync(head);
        var valid = ext switch
        {
            ".pdf" => n >= 5 && head.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
            ".png" => n == 8 && head.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            _ => n >= 3 && head[0] == 255 && head[1] == 216 && head[2] == 255
        };
        if (!valid)
            throw new ApiException(400, "Nội dung tệp không khớp định dạng.");
        var root = Storage(config);
        Directory.CreateDirectory(root);
        var d = new Document
        {
            StudentId = id,
            OriginalName = name,
            ContentType = mime,
            Size = file.Length,
            UploadedBy = s.User.Id,
            StorageName = Guid.NewGuid().ToString("N") + ext
        };
        var path = Path.Combine(root, d.StorageName);
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(head.AsMemory(0, n));
                await stream.CopyToAsync(output);
            }

            await db.Documents.InsertOneAsync(d);
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        await s.Audit("UploadDocument", d.Id);
        return Results.Created("/api/documents/" + d.Id + "/download", Dto.Document(d));
    }

    public async Task<IResult> Download(string id)
    {
        ApiSupport.Id(id);
        var d = await db.Documents.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Không tìm thấy dữ liệu.");
        await s.Student(d.StudentId);
        var path = Path.Combine(Storage(config), d.StorageName);
        if (!File.Exists(path))
            throw new ApiException(404, "Không tìm thấy dữ liệu.");
        await s.Audit("DownloadDocument", id);
        return Results.File(path, d.ContentType, d.OriginalName, enableRangeProcessing: false);
    }

    public async Task<IResult> Delete(string id)
    {
        ApiSupport.Id(id);
        var d = await db.Documents.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Không tìm thấy dữ liệu.");
        await s.Student(d.StudentId);
        File.Delete(Path.Combine(Storage(config), d.StorageName));
        await db.Documents.DeleteOneAsync(x => x.Id == id);
        await s.Audit("DeleteDocument", id);
        return Results.NoContent();
    }
}
