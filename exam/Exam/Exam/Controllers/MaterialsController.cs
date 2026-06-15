using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Dapper;
using System;
using System.IO;
using System.Threading.Tasks;
using Exam.Models;

namespace Exam.Controllers
{
    [Authorize]
    public class MaterialsController : Controller
    {
        private readonly string _connectionString;

        public MaterialsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // Ensure Materials table exists and has PosterPath column
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
                    END");

                var materials = await conn.QueryAsync<Material>("SELECT * FROM Materials ORDER BY UploadedAt DESC");
                return View(materials);
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
