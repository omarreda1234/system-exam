using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Dapper;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Exam.Models;

namespace Exam.Controllers
{
    [Authorize]
    public class MaterialsController : Controller
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly string _connectionString;

        public MaterialsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        private const string DefaultProgramDashUrl = "https://sites.google.com/view/tarshoubiacademyweeklymessage";

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // Ensure Materials table and SystemSettings table exist
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Materials]') AND type in (N'U'))
                    BEGIN
                        CREATE TABLE [dbo].[Materials] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [Title] NVARCHAR(250) NOT NULL,
                            [Description] NVARCHAR(MAX) NULL,
                            [FilePath] NVARCHAR(MAX) NOT NULL,
                            [FileType] NVARCHAR(50) NOT NULL,
                            [UploadedAt] DATETIME NOT NULL,
                            [UploadedBy] NVARCHAR(250) NOT NULL,
                            [FileSizeBytes] BIGINT NOT NULL
                        )
                    END
                    
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Materials]') AND name = 'PosterPath')
                    BEGIN
                        ALTER TABLE [dbo].[Materials] ADD [PosterPath] NVARCHAR(MAX) NULL
                    END
                    
                    IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SystemSettings]') AND type in (N'U'))
                    BEGIN
                        CREATE TABLE [dbo].[SystemSettings] (
                            [SettingKey] NVARCHAR(100) NOT NULL PRIMARY KEY,
                            [SettingValue] NVARCHAR(MAX) NULL,
                            [UpdatedAt] DATETIME NULL
                        )
                    END");

                var portalUrl = await conn.QueryFirstOrDefaultAsync<string>(
                    "SELECT SettingValue FROM SystemSettings WHERE SettingKey = 'ProgramDashUrl'");

                if (string.IsNullOrWhiteSpace(portalUrl) || portalUrl.Contains("/preview?authuser="))
                {
                    portalUrl = DefaultProgramDashUrl;
                    await conn.ExecuteAsync(
                        "IF EXISTS (SELECT 1 FROM SystemSettings WHERE SettingKey = 'ProgramDashUrl') " +
                        "UPDATE SystemSettings SET SettingValue = @Url, UpdatedAt = GETDATE() WHERE SettingKey = 'ProgramDashUrl' " +
                        "ELSE INSERT INTO SystemSettings (SettingKey, SettingValue, UpdatedAt) VALUES ('ProgramDashUrl', @Url, GETDATE())",
                        new { Url = DefaultProgramDashUrl });
                }

                ViewBag.ProgramDashUrl = portalUrl;

                var materials = await conn.QueryAsync<Material>("SELECT * FROM Materials ORDER BY UploadedAt DESC");
                return View(materials);
            }
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateProgramDashUrl([FromForm] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return Json(new { success = false, message = "URL cannot be empty." });

            url = url.Trim();

            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SystemSettings]') AND type in (N'U'))
                    BEGIN
                        CREATE TABLE [dbo].[SystemSettings] (
                            [SettingKey] NVARCHAR(100) NOT NULL PRIMARY KEY,
                            [SettingValue] NVARCHAR(MAX) NULL,
                            [UpdatedAt] DATETIME NULL
                        )
                    END

                    IF EXISTS (SELECT 1 FROM SystemSettings WHERE SettingKey = 'ProgramDashUrl')
                    BEGIN
                        UPDATE SystemSettings SET SettingValue = @Url, UpdatedAt = GETDATE() WHERE SettingKey = 'ProgramDashUrl'
                    END
                    ELSE
                    BEGIN
                        INSERT INTO SystemSettings (SettingKey, SettingValue, UpdatedAt) VALUES ('ProgramDashUrl', @Url, GETDATE())
                    END", new { Url = url });
            }

            return Json(new { success = true, message = "Program Dash Academy URL updated successfully.", url });
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> PortalEmbed()
        {
            try
            {
                string targetUrl = DefaultProgramDashUrl;
                using (var conn = new SqlConnection(_connectionString))
                {
                    var dbUrl = await conn.QueryFirstOrDefaultAsync<string>(
                        "SELECT SettingValue FROM SystemSettings WHERE SettingKey = 'ProgramDashUrl'");
                    if (!string.IsNullOrWhiteSpace(dbUrl))
                    {
                        targetUrl = dbUrl.Trim();
                    }
                }

                var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");

                var response = await _httpClient.SendAsync(request);
                var html = await response.Content.ReadAsStringAsync();

                // Inject base tag so all assets, links, and fonts resolve back to Google Sites
                var baseTag = $"<base href=\"{targetUrl.TrimEnd('/')}/\" target=\"_blank\">";
                if (!html.Contains("<base ", StringComparison.OrdinalIgnoreCase))
                {
                    if (html.Contains("<head>", StringComparison.OrdinalIgnoreCase))
                    {
                        html = html.Replace("<head>", $"<head>\n    {baseTag}", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        html = baseTag + "\n" + html;
                    }
                }

                // Remove X-Frame-Options from our response so this iframe is permitted
                Response.Headers.Remove("X-Frame-Options");
                Response.Headers["X-Frame-Options"] = "SAMEORIGIN";

                return Content(html, "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                var fallback = $@"<!DOCTYPE html>
<html lang='ar' dir='rtl'>
<head><meta charset='utf-8'><title>منصة الأكاديمية</title>
<style>
body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; display:flex; align-items:center; justify-content:center; height:100vh; margin:0; background:#f8fafc; color:#1e293b; text-align:center; }}
.card {{ background:white; padding:40px; border-radius:24px; box-shadow:0 10px 25px -5px rgba(0,0,0,0.1); max-width:480px; }}
.btn {{ display:inline-block; margin-top:20px; background:#059669; color:white; padding:12px 28px; border-radius:14px; text-decoration:none; font-weight:bold; }}
</style>
</head>
<body>
<div class='card'>
    <h2>بوابة أكاديمية الطرشوبي للتدريب</h2>
    <p style='color:#64748b;font-size:14px;'>تعذر التحميل المباشر للمنصة: {System.Net.WebUtility.HtmlEncode(ex.Message)}</p>
    <a href='{DefaultProgramDashUrl}' target='_blank' class='btn'>فتح الرابط في نافذة جديدة</a>
</div>
</body>
</html>";
                return Content(fallback, "text/html; charset=utf-8");
            }
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [RequestSizeLimit(524288000)] // 500 MB in bytes
        [RequestFormLimits(MultipartBodyLengthLimit = 524288000)]
        public async Task<IActionResult> Upload(string title, string description, Microsoft.AspNetCore.Http.IFormFile file, Microsoft.AspNetCore.Http.IFormFile posterFile)
        {
            if (string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Title is required." });
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "Please select a file to upload." });

            try
            {
                // Determine file type
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                var fileType = "Other";
                if (ext == ".pdf") fileType = "PDF";
                else if (ext == ".xls" || ext == ".xlsx" || ext == ".csv") fileType = "Excel";
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp") fileType = "Image";
                else if (ext == ".mp4" || ext == ".webm" || ext == ".ogg" || ext == ".mov" || ext == ".avi" || ext == ".mkv") fileType = "Video";

                // Ensure uploads directory exists
                var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "materials");
                if (!Directory.Exists(uploadsDir))
                    Directory.CreateDirectory(uploadsDir);

                // Secure filename
                var uniqueFileName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(file.FileName);
                var filePath = Path.Combine(uploadsDir, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var relativePath = "/uploads/materials/" + uniqueFileName;

                // Handle Poster File if present
                string? posterRelativePath = null;
                if (posterFile != null && posterFile.Length > 0)
                {
                    var postersDir = Path.Combine(uploadsDir, "posters");
                    if (!Directory.Exists(postersDir))
                        Directory.CreateDirectory(postersDir);

                    var uniquePosterName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(posterFile.FileName);
                    var posterFilePath = Path.Combine(postersDir, uniquePosterName);

                    using (var stream = new FileStream(posterFilePath, FileMode.Create))
                    {
                        await posterFile.CopyToAsync(stream);
                    }
                    posterRelativePath = "/uploads/materials/posters/" + uniquePosterName;
                }

                using (var conn = new SqlConnection(_connectionString))
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO Materials (Title, Description, FilePath, FileType, UploadedAt, UploadedBy, FileSizeBytes, PosterPath)
                        VALUES (@Title, @Description, @FilePath, @FileType, @UploadedAt, @UploadedBy, @FileSizeBytes, @PosterPath)",
                        new
                        {
                            Title = title,
                            Description = description ?? "",
                            FilePath = relativePath,
                            FileType = fileType,
                            UploadedAt = DateTime.Now,
                            UploadedBy = User.Identity.Name ?? "System",
                            FileSizeBytes = file.Length,
                            PosterPath = posterRelativePath
                        });
                }

                return Json(new { success = true, message = "Material uploaded successfully!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error uploading file: " + ex.Message });
            }
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                using (var conn = new SqlConnection(_connectionString))
                {
                    var material = await conn.QueryFirstOrDefaultAsync<Material>("SELECT * FROM Materials WHERE Id = @Id", new { Id = id });
                    if (material == null)
                        return Json(new { success = false, message = "Material not found." });

                    // Delete main file from disk
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", material.FilePath.TrimStart('/'));
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }

                    // Delete poster file from disk if present
                    if (!string.IsNullOrWhiteSpace(material.PosterPath))
                    {
                        var posterFilePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", material.PosterPath.TrimStart('/'));
                        if (System.IO.File.Exists(posterFilePath))
                        {
                            System.IO.File.Delete(posterFilePath);
                        }
                    }

                    // Delete record from DB
                    await conn.ExecuteAsync("DELETE FROM Materials WHERE Id = @Id", new { Id = id });
                }

                return Json(new { success = true, message = "Material deleted successfully!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error deleting file: " + ex.Message });
            }
        }
    }
}
