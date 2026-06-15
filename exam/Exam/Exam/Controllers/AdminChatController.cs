using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Dapper;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Exam.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminChatController : Controller
    {
        private readonly string _connectionString;

        public AdminChatController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        private async Task EnsureTableExists()
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AdminChatMessages]') AND type in (N'U'))
                    BEGIN
                        CREATE TABLE [dbo].[AdminChatMessages] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SenderName] NVARCHAR(250) NOT NULL,
                            [Message] NVARCHAR(MAX) NULL,
                            [FilePath] NVARCHAR(MAX) NULL,
                            [FileType] NVARCHAR(50) NULL,
                            [CreatedAt] DATETIME NOT NULL DEFAULT GETDATE()
                        )
                    END");
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetMessages()
        {
            await EnsureTableExists();

            using (var conn = new SqlConnection(_connectionString))
            {
                var messages = await conn.QueryAsync(@"
                    SELECT TOP 100 * FROM AdminChatMessages 
                    ORDER BY CreatedAt ASC");
                
                return Json(new { success = true, messages });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return Json(new { success = false, message = "Message is empty." });

            await EnsureTableExists();

            var senderName = User.Identity?.Name ?? "Admin";

            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO AdminChatMessages (SenderName, Message, CreatedAt)
                    VALUES (@SenderName, @Message, GETDATE())",
                    new { SenderName = senderName, Message = message });
            }

            return Json(new { success = true });
        }

        [HttpPost]
        [RequestSizeLimit(104857600)] // 100 MB limit
        public async Task<IActionResult> UploadFile(Microsoft.AspNetCore.Http.IFormFile file, string fileType)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "No file selected." });

            await EnsureTableExists();

            try
            {
                var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "adminchat");
                if (!Directory.Exists(uploadsDir))
                    Directory.CreateDirectory(uploadsDir);

                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (fileType == "audio" && string.IsNullOrEmpty(ext)) ext = ".webm"; // default for voice records

                var uniqueFileName = Guid.NewGuid().ToString() + ext;
                var filePath = Path.Combine(uploadsDir, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var relativePath = "/uploads/adminchat/" + uniqueFileName;
                var senderName = User.Identity?.Name ?? "Admin";

                using (var conn = new SqlConnection(_connectionString))
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO AdminChatMessages (SenderName, Message, FilePath, FileType, CreatedAt)
                        VALUES (@SenderName, @Message, @FilePath, @FileType, GETDATE())",
                        new { SenderName = senderName, Message = file.FileName, FilePath = relativePath, FileType = fileType });
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}
