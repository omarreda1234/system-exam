using ClosedXML.Excel;
using Exam.Services;
using Exam.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using Exam.Hubs;
using System.Data;
using Exam.DTOs;
using System;

namespace Exam.Controllers
{
    public partial class AdminController
    {
        private async Task EnsureWaveModeColumnAsync(SqlConnection connection)
        {
            try
            {
                await connection.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TrainingWaves') AND name = 'Mode')
                    BEGIN
                        ALTER TABLE dbo.TrainingWaves ADD Mode NVARCHAR(100) NULL;
                    END
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TrainingWaves') AND name = 'EndDate')
                    BEGIN
                        ALTER TABLE dbo.TrainingWaves ADD EndDate DATETIME NULL;
                    END
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TrainingWaves') AND name = 'IsActive')
                    BEGIN
                        ALTER TABLE dbo.TrainingWaves ADD IsActive BIT NOT NULL CONSTRAINT DF_TrainingWaves_IsActive DEFAULT 1;
                    END
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TrainingWaves') AND name = 'CustomEmailSubject')
                    BEGIN
                        ALTER TABLE dbo.TrainingWaves ADD CustomEmailSubject NVARCHAR(500) NULL;
                    END
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TrainingWaves') AND name = 'CustomEmailBody')
                    BEGIN
                        ALTER TABLE dbo.TrainingWaves ADD CustomEmailBody NVARCHAR(MAX) NULL;
                    END");
            }
            catch { }
        }

        public async Task<IActionResult> Waves()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                await EnsureWaveModeColumnAsync(connection);

                var waves = await connection.QueryAsync<Exam.DTOs.WaveDto>(@"
                    SELECT Id, WaveName, StartDate, EndDate, ISNULL(IsOnline, 0) AS IsOnline, ISNULL(Mode, CASE WHEN IsOnline = 1 THEN 'Online' ELSE 'Off' END) AS Mode, ISNULL(IsActive, 1) AS IsActive, CustomEmailSubject, CustomEmailBody
                    FROM dbo.TrainingWaves
                    ORDER BY Id DESC"
                );

                var distinctModes = await connection.QueryAsync<string>(@"
                    SELECT DISTINCT Mode
                    FROM dbo.TrainingWaves
                    WHERE Mode IS NOT NULL AND TRIM(Mode) <> ''
                    ORDER BY Mode"
                );
                ViewBag.DistinctModes = distinctModes.ToList();

                return View(waves);
            }
        }

        [HttpPost]
        public async Task<IActionResult> CloneWave(int waveId, string newWaveName, System.DateTime? newStartDate)
        {
            if (waveId <= 0 || string.IsNullOrWhiteSpace(newWaveName))
                return Json(new { success = false, message = "Invalid parameters." });

            try
            {
                var newDate = newStartDate ?? System.DateTime.Now;
                int newWaveId = await _examService.CloneWaveAsync(waveId, newWaveName, newDate);
                return Json(new { success = true, newWaveId = newWaveId });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> WaveDetails(int id)
        {
            using var conn = new SqlConnection(_connectionString);

            // Get wave info
            var wave = await conn.QueryFirstOrDefaultAsync<Exam.DTOs.WaveDto>(
                "SELECT Id, WaveName, StartDate, EndDate, IsOnline, Mode FROM TrainingWaves WHERE Id = @Id",
                new { Id = id });

            if (wave == null)
                return NotFound();

            // Get users assigned to this wave
            var users = await _examService.GetUsersByWaveIdAsync(id);

            ViewBag.Wave = wave;
            return View(users);
        }

        [HttpGet]
        public async Task<IActionResult> GetWaveUserIds(int waveId)
        {
            var users = await _examService.GetUsersByWaveIdAsync(waveId);
            return Json(users.Select(u => u.Id));
        }

        [HttpGet]
        public async Task<IActionResult> GetUsersByWaveId(int waveId)
        {
            var users = await _examService.GetUsersByWaveIdAsync(waveId);
            return Json(users);
        }

        [HttpPost]
        public async Task<IActionResult> RemoveUserFromWave(int waveId, string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) || waveId <= 0)
                return Json(new { success = false, message = "Invalid parameters." });

            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
            if (!userRoles.Contains("Admin") && !await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "RemoveUserFromWave", "delete"))
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                var affected = await conn.ExecuteAsync(
                    "DELETE FROM UserWaves WHERE WaveId = @WaveId AND UserId = @UserId",
                    new { WaveId = waveId, UserId = userId });

                if (affected > 0)
                    return Json(new { success = true, message = "User removed from batch successfully." });
                else
                    return Json(new { success = false, message = "User was not found in this batch." });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateWave([FromBody] Exam.DTOs.WaveDto wave)
        {
            if (string.IsNullOrEmpty(wave.WaveName))
            {
                return BadRequest("Wave name is required.");
            }

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                await EnsureWaveModeColumnAsync(connection);

                var modeValue = string.IsNullOrWhiteSpace(wave.Mode) ? (wave.IsOnline ? "Online" : "Off") : wave.Mode.Trim();
                var newWaveId = await connection.ExecuteScalarAsync<int>(@"
                    INSERT INTO dbo.TrainingWaves (WaveName, StartDate, EndDate, IsOnline, Mode, IsActive)
                    VALUES (@WaveName, @StartDate, @EndDate, @IsOnline, @Mode, @IsActive);
                    SELECT SCOPE_IDENTITY();",
                    new { wave.WaveName, wave.StartDate, wave.EndDate, wave.IsOnline, Mode = modeValue, IsActive = wave.IsActive }
                );

                return Ok(new { NewWaveId = newWaveId });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AssignWaveToNewPharmacists(int waveId)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var assignedCount = await connection.ExecuteScalarAsync<int>(
                    "dbo.sp_Admin_AssignWaveToNewPharmacists",
                    new { WaveId = waveId },
                    commandType: System.Data.CommandType.StoredProcedure
                );

                return Ok(new { AssignedCount = assignedCount });
            }
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteWave(int waveid)
        {
            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Select(c => c.Value).ToList();
            bool hasPermission = User.IsInRole("Admin") || userRoles.Contains("Admin") || await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "delete");
            if (!hasPermission)
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            await _examService.DeleteWaveAsync(waveid);
            return Json(new { success = true, Message = "Delete wave success" }); 
        }

        [HttpPost]
        public async Task<IActionResult> EditWave([FromBody] Exam.DTOs.WaveDto wave)
        {
            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Select(c => c.Value).ToList();
            bool hasPermission = User.IsInRole("Admin") || userRoles.Contains("Admin") || userRoles.Contains("HR") || await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "edit");
            if (!hasPermission)
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            if (wave == null || wave.Id <= 0 || string.IsNullOrWhiteSpace(wave.WaveName))
            {
                return Json(new { success = false, message = "Batch name is required." });
            }

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    await EnsureWaveModeColumnAsync(connection);

                    var modeValue = string.IsNullOrWhiteSpace(wave.Mode) ? (wave.IsOnline ? "Online (ON)" : "Offline (Off)") : wave.Mode.Trim();
                    int rows = await connection.ExecuteAsync(
                        "UPDATE dbo.TrainingWaves SET WaveName = @WaveName, StartDate = @StartDate, EndDate = @EndDate, IsOnline = @IsOnline, Mode = @Mode, IsActive = @IsActive WHERE Id = @Id",
                        new { WaveName = wave.WaveName.Trim(), StartDate = wave.StartDate, EndDate = wave.EndDate, IsOnline = wave.IsOnline, Mode = modeValue, IsActive = wave.IsActive, Id = wave.Id }
                    );

                    if (rows > 0)
                    {
                        // Auto-regenerate and synchronize certificate codes for all students in this wave
                        string waveName = wave.WaveName.Trim();
                        DateTime waveDate = wave.StartDate ?? DateTime.Now;
                        string yearStr = waveDate.Year.ToString();
                        string waveNumStr = "001";
                        var digits = new string(waveName.Where(char.IsDigit).ToArray());
                        if (!string.IsNullOrEmpty(digits))
                        {
                            waveNumStr = digits.PadLeft(3, '0');
                        }
                        else
                        {
                            waveNumStr = wave.Id.ToString().PadLeft(3, '0');
                        }

                        string serialMode = ExtractModeForSerial(modeValue, wave.IsOnline);

                        var enrolledUsers = await connection.QueryAsync<dynamic>(@"
                            SELECT U.Id, U.UserCode
                            FROM dbo.UserWaves UW
                            INNER JOIN dbo.AspNetUsers U ON UW.UserId = U.Id
                            INNER JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = UW.WaveId
                            WHERE UW.WaveId = @WaveId AND UWC.CertificateCode IS NOT NULL AND TRIM(UWC.CertificateCode) <> '' AND TRIM(UWC.CertificateCode) <> '/'", new { WaveId = wave.Id });

                        foreach (var u in enrolledUsers)
                        {
                            string uid = (string)u.Id;
                            string uCode = (string)u.UserCode ?? "0000";
                            string newCertCode = $"WTTA-{yearStr}-{waveNumStr}-PB-{serialMode}-{uCode}";

                            await connection.ExecuteAsync(@"
                                UPDATE dbo.UserWaveCertificates
                                SET CertificateCode = @CertCode
                                WHERE UserId = @UserId AND WaveId = @WaveId",
                                new { CertCode = newCertCode, UserId = uid, WaveId = wave.Id });
                        }

                        return Json(new { success = true, message = "تم تعديل الويف وتحديث السيريال بنجاح." });
                    }

                    return Json(new { success = false, message = "Batch not found." });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error updating batch: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RenameWaveMode(string oldMode, string newMode)
        {
            if (string.IsNullOrWhiteSpace(oldMode) || string.IsNullOrWhiteSpace(newMode))
            {
                return Json(new { success = false, message = "اسم الحالة القديم والجديد كلاهما مطلوب." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureWaveModeColumnAsync(conn);

                int rows = await conn.ExecuteAsync(
                    "UPDATE dbo.TrainingWaves SET Mode = @NewMode WHERE Mode = @OldMode",
                    new { OldMode = oldMode.Trim(), NewMode = newMode.Trim() });

                // Synchronize all waves using this mode
                var affectedWaves = await conn.QueryAsync<dynamic>(
                    "SELECT Id, WaveName, StartDate, ISNULL(IsOnline, 0) AS IsOnline, Mode FROM dbo.TrainingWaves WHERE Mode = @NewMode",
                    new { NewMode = newMode.Trim() });

                foreach (var w in affectedWaves)
                {
                    int wId = (int)w.Id;
                    string wName = (string)w.WaveName ?? "";
                    DateTime wDate = w.StartDate ?? DateTime.Now;
                    string yearStr = wDate.Year.ToString();
                    string waveNumStr = "001";
                    var digits = new string(wName.Where(char.IsDigit).ToArray());
                    if (!string.IsNullOrEmpty(digits))
                    {
                        waveNumStr = digits.PadLeft(3, '0');
                    }
                    else
                    {
                        waveNumStr = wId.ToString().PadLeft(3, '0');
                    }

                    string serialMode = ExtractModeForSerial(newMode.Trim(), Convert.ToBoolean(w.IsOnline));

                    var enrolledUsers = await conn.QueryAsync<dynamic>(@"
                        SELECT U.Id, U.UserCode
                        FROM dbo.UserWaves UW
                        INNER JOIN dbo.AspNetUsers U ON UW.UserId = U.Id
                        INNER JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = UW.WaveId
                        WHERE UW.WaveId = @WaveId AND UWC.CertificateCode IS NOT NULL AND TRIM(UWC.CertificateCode) <> '' AND TRIM(UWC.CertificateCode) <> '/'", new { WaveId = wId });

                    foreach (var u in enrolledUsers)
                    {
                        string uid = (string)u.Id;
                        string uCode = (string)u.UserCode ?? "0000";
                        string newCertCode = $"WTTA-{yearStr}-{waveNumStr}-PB-{serialMode}-{uCode}";

                        await conn.ExecuteAsync(@"
                            UPDATE dbo.UserWaveCertificates
                            SET CertificateCode = @CertCode
                            WHERE UserId = @UserId AND WaveId = @WaveId",
                            new { CertCode = newCertCode, UserId = uid, WaveId = wId });
                    }
                }

                return Json(new { success = true, message = $"تم تعديل مسمى الحالة من '{oldMode}' إلى '{newMode}' وتحديث سيريال الشهادات الصادرة بنجاح.", count = rows });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء تعديل الحالة: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteWaveMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return Json(new { success = false, message = "اسم الحالة مطلوب للحذف." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureWaveModeColumnAsync(conn);

                int rows = await conn.ExecuteAsync(
                    "UPDATE dbo.TrainingWaves SET Mode = NULL WHERE Mode = @Mode",
                    new { Mode = mode.Trim() });

                return Json(new { success = true, message = $"تم حذف الحالة '{mode}' وإلغاء تخصيصها عن {rows} ويف بنجاح.", count = rows });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء حذف الحالة: " + ex.Message });
            }
        }
        [HttpPost]
        public async Task<IActionResult> ToggleWaveStatus(int id, bool isActive)
        {
            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Select(c => c.Value).ToList();
            bool hasPermission = User.IsInRole("Admin") || userRoles.Contains("Admin") || userRoles.Contains("HR") || await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "edit");
            if (!hasPermission)
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    await EnsureWaveModeColumnAsync(connection);

                    int rows = await connection.ExecuteAsync(
                        "UPDATE dbo.TrainingWaves SET IsActive = @IsActive WHERE Id = @Id",
                        new { IsActive = isActive, Id = id }
                    );

                    if (rows > 0)
                    {
                        return Json(new { success = true, isActive = isActive, message = isActive ? "تم تفعيل الويف بنجاح" : "تم تمييز الويف كمنتهي (Done)" });
                    }
                    return Json(new { success = false, message = "Wave not found." });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetWaveEmailTemplate(int waveId)
        {
            if (waveId <= 0) return BadRequest("Invalid wave ID");

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await EnsureWaveModeColumnAsync(conn);

            var wave = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT Id, WaveName, StartDate, EndDate, ISNULL(IsOnline, 0) AS IsOnline, Mode,
                       CustomEmailSubject, CustomEmailBody
                FROM dbo.TrainingWaves
                WHERE Id = @Id", new { Id = waveId });

            if (wave == null)
                return NotFound(new { success = false, message = "Batch not found." });

            string defaultSubject = $"Welcome to {wave.WaveName} - Registration Confirmed";
            string formattedDate = wave.StartDate != null ? ((DateTime)wave.StartDate).ToString("MMMM dd, yyyy - hh:mm tt") : "To be announced";
            bool isOnline = (wave.IsOnline == true) || (wave.Mode != null && wave.Mode.ToString().ToLower().Contains("online"));
            string location = isOnline ? "Online" : (!string.IsNullOrWhiteSpace(wave.Mode?.ToString()) ? wave.Mode.ToString() : "Offline in Main Branch");

            string defaultBody = @"Dear {Name},

Welcome to {WaveName} of Pharmacy Basics Program — we’re glad to have you with us!

Your registration has been successfully confirmed. Here are your session details:
📅 Date & Time: {StartDate}
📍 Location: {Location}

A quick note before we start:
• Please arrive 10–15 minutes early to ensure a smooth check-in.
• Keep an eye on your email for any further updates or announcements.

If you have any questions or face any issues, feel free to reach out to us.
Looking forward to seeing you and having a great Training Program together!

Best regards,
Eltarshoubi Training Academy Team";

            return Json(new {
                success = true,
                waveId = (int)wave.Id,
                waveName = (string)wave.WaveName,
                startDate = formattedDate,
                location = location,
                customSubject = (string?)wave.CustomEmailSubject ?? defaultSubject,
                customBody = !string.IsNullOrWhiteSpace((string?)wave.CustomEmailBody) ? (string)wave.CustomEmailBody : defaultBody,
                isCustomized = !string.IsNullOrWhiteSpace((string?)wave.CustomEmailBody)
            });
        }

        [HttpPost]
        public async Task<IActionResult> SaveWaveEmailTemplate(int waveId, [FromBody] Exam.DTOs.WaveEmailTemplateDto model)
        {
            if (waveId <= 0) return BadRequest(new { success = false, message = "Invalid wave ID." });

            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Select(c => c.Value).ToList();
            bool hasPermission = User.IsInRole("Admin") || userRoles.Contains("Admin") || userRoles.Contains("HR") || await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "edit");
            if (!hasPermission)
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureWaveModeColumnAsync(conn);

                await conn.ExecuteAsync(@"
                    UPDATE dbo.TrainingWaves 
                    SET CustomEmailSubject = @Subject, CustomEmailBody = @Body 
                    WHERE Id = @WaveId", 
                    new { Subject = model?.CustomSubject?.Trim(), Body = model?.CustomBody?.Trim(), WaveId = waveId });

                return Json(new { success = true, message = "تم حفظ قالب الإيميل بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendCustomEmailToWavePersonnel(int waveId, [FromBody] Exam.DTOs.SendWaveEmailDto model)
        {
            if (waveId <= 0) return BadRequest(new { success = false, message = "Invalid wave ID." });

            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Select(c => c.Value).ToList();
            bool hasPermission = User.IsInRole("Admin") || userRoles.Contains("Admin") || userRoles.Contains("HR") || await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "edit");
            if (!hasPermission)
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureWaveModeColumnAsync(conn);

                var wave = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT Id, WaveName, StartDate, EndDate, ISNULL(IsOnline, 0) AS IsOnline, Mode,
                           CustomEmailSubject, CustomEmailBody
                    FROM dbo.TrainingWaves
                    WHERE Id = @Id", new { Id = waveId });

                if (wave == null)
                    return NotFound(new { success = false, message = "Batch not found." });

                // If saveAsDefault, update TrainingWaves
                if (model?.SaveAsDefault == true && (!string.IsNullOrWhiteSpace(model?.CustomSubject) || !string.IsNullOrWhiteSpace(model?.CustomBody)))
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.TrainingWaves 
                        SET CustomEmailSubject = @Subject, CustomEmailBody = @Body 
                        WHERE Id = @WaveId", 
                        new { Subject = model.CustomSubject?.Trim(), Body = model.CustomBody?.Trim(), WaveId = waveId });
                }

                // Query target enrolled users
                IEnumerable<dynamic> targetUsers;
                if (model?.UserIds != null && model.UserIds.Any())
                {
                    targetUsers = await conn.QueryAsync<dynamic>(@"
                        SELECT U.Id, U.UserName, U.Email
                        FROM dbo.UserWaves UW
                        INNER JOIN dbo.AspNetUsers U ON UW.UserId = U.Id
                        WHERE UW.WaveId = @WaveId AND U.Id IN @UserIds AND U.Email IS NOT NULL AND TRIM(U.Email) <> ''",
                        new { WaveId = waveId, UserIds = model.UserIds });
                }
                else
                {
                    targetUsers = await conn.QueryAsync<dynamic>(@"
                        SELECT U.Id, U.UserName, U.Email
                        FROM dbo.UserWaves UW
                        INNER JOIN dbo.AspNetUsers U ON UW.UserId = U.Id
                        WHERE UW.WaveId = @WaveId AND U.Email IS NOT NULL AND TRIM(U.Email) <> ''",
                        new { WaveId = waveId });
                }

                var userList = targetUsers.ToList();
                if (!userList.Any())
                {
                    return Json(new { success = false, message = "لم يتم العثور على متدربين لديهم بريد إلكتروني مسجل لإرسال الرسائل إليهم." });
                }

                string waveName = wave.WaveName ?? "Batch";
                DateTime? startDate = wave.StartDate;
                string formattedDate = startDate.HasValue ? startDate.Value.ToString("MMMM dd, yyyy - hh:mm tt") : "To be announced";
                bool isOnline = (wave.IsOnline == true) || (wave.Mode != null && wave.Mode.ToString().ToLower().Contains("online"));
                string location = isOnline ? "Online" : (!string.IsNullOrWhiteSpace(wave.Mode?.ToString()) ? wave.Mode.ToString() : "Offline in Main Branch");
                string siteUrl = "http://41.33.149.186:5208";

                string subjectTemplate = !string.IsNullOrWhiteSpace(model?.CustomSubject)
                    ? model.CustomSubject.Trim()
                    : (!string.IsNullOrWhiteSpace((string?)wave.CustomEmailSubject)
                        ? (string)wave.CustomEmailSubject
                        : $"Welcome to {waveName} - Registration Confirmed");

                string? bodyTemplate = !string.IsNullOrWhiteSpace(model?.CustomBody)
                    ? model.CustomBody.Trim()
                    : (!string.IsNullOrWhiteSpace((string?)wave.CustomEmailBody)
                        ? (string)wave.CustomEmailBody
                        : null);

                // Dispatch emails in background
                _ = Task.Run(async () =>
                {
                    foreach (var u in userList)
                    {
                        try
                        {
                            string userEmail = (string)u.Email;
                            string fullName = (string)(u.UserName ?? "Trainee");
                            string firstName = fullName.Split(' ')[0];

                            string resolvedSubject = subjectTemplate
                                .Replace("{Name}", firstName)
                                .Replace("{FullName}", fullName)
                                .Replace("{UserName}", fullName)
                                .Replace("{WaveName}", waveName)
                                .Replace("{StartDate}", formattedDate)
                                .Replace("{Location}", location)
                                .Replace("{PortalLink}", siteUrl);

                            string siteButton = (!string.IsNullOrEmpty(siteUrl) ? $@"
                                <div style='text-align: center; margin: 30px 0;'>
                                    <a href='{siteUrl}' style='background-color: #10b981; color: white; padding: 14px 28px; text-decoration: none; border-radius: 8px; font-weight: bold; font-size: 16px; box-shadow: 0 4px 6px rgba(16, 185, 129, 0.2); display: inline-block;'>Go to Portal Instance</a>
                                </div>" : "");

                            string innerContentHtml;
                            if (!string.IsNullOrWhiteSpace(bodyTemplate))
                            {
                                string customized = bodyTemplate
                                    .Replace("{Name}", firstName)
                                    .Replace("{FullName}", fullName)
                                    .Replace("{UserName}", fullName)
                                    .Replace("{WaveName}", waveName)
                                    .Replace("{StartDate}", formattedDate)
                                    .Replace("{Location}", location)
                                    .Replace("{PortalLink}", siteUrl);

                                if (customized.Contains("<p>") || customized.Contains("<div>") || customized.Contains("<br>"))
                                {
                                    innerContentHtml = customized;
                                }
                                else
                                {
                                    var paragraphs = customized.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                                    var formattedParagraphs = paragraphs.Select(p => $"<p style='margin-bottom: 15px;'>{p.Replace("\r\n", "<br/>").Replace("\n", "<br/>")}</p>");
                                    innerContentHtml = string.Join("", formattedParagraphs);
                                }

                                if (!bodyTemplate.Contains("{PortalLink}") && !string.IsNullOrEmpty(siteUrl))
                                {
                                    innerContentHtml += siteButton;
                                }
                            }
                            else
                            {
                                innerContentHtml = $@"
                                    <p style='font-size: 18px;'>Dear <b>{firstName}</b>,</p>
                                    <p>Welcome to <b>{waveName}</b> of Pharmacy Basics Program — we’re glad to have you with us!</p>
                                    <p>Your registration has been successfully confirmed. Here are your session details:</p>
                                    <div style='background: #f9fafb; border: 1px solid #e5e7eb; border-radius: 12px; padding: 20px; margin: 25px 0;'>
                                        <p style='margin: 0 0 10px 0;'>📅 <b>Date & Time:</b> {formattedDate}</p>
                                        <p style='margin: 0;'>📍 <b>Location:</b> {location}</p>
                                    </div>
                                    {siteButton}
                                    <hr style='border: 0; border-top: 1px solid #eee; margin: 30px 0;'>
                                    <p style='font-weight: bold; color: #111827;'>A quick note before we start:</p>
                                    <ul style='padding-left: 20px;'>
                                        <li style='margin-bottom: 10px;'>Please arrive 10–15 minutes early to ensure a smooth check-in.</li>
                                        <li>Keep an eye on your email for any further updates or announcements.</li>
                                    </ul>
                                    <p>If you have any questions or face any issues, feel free to reach out to us.</p>
                                    <p>Looking forward to seeing you and having a great Training Program together.</p>
                                    <p style='margin-top: 40px; line-height: 1.2;'>
                                        Best regards,<br>
                                        <span style='color: #10b981; font-weight: bold;'>Eltarshoubi Training Academy Team</span>
                                    </p>";
                            }

                            string htmlBody = $@"
<div style='background-color: #f4f7fa; padding: 30px 15px; font-family: ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;'>
    <div style='max-width: 600px; margin: 0 auto; background: white; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.08); border: 1px solid #e2e8f0;'>
        <div style='background: linear-gradient(135deg, #10b981 0%, #059669 100%); padding: 28px; text-align: center;'>
            <h2 style='color: white; margin: 0; font-size: 22px; font-weight: 800; letter-spacing: 0.5px;'>Eltarshouby Training Academy</h2>
            <p style='color: #d1fae5; margin: 6px 0 0 0; font-size: 14px;'>{waveName}</p>
        </div>
        <div dir='auto' style='padding: 30px; color: #374151; line-height: 1.7; font-size: 15px;'>
            {innerContentHtml}
        </div>
        <div style='background: #f9fafb; padding: 20px; text-align: center; color: #9ca3af; font-size: 12px; border-top: 1px solid #f3f4f6;'>
            <p style='margin: 0;'>&copy; {DateTime.Now.Year} Eltarshouby Pharmacies Group. All rights reserved.</p>
        </div>
    </div>
</div>";

                            await _emailSender.SendEmailAsync(userEmail, resolvedSubject, htmlBody);
                        }
                        catch { }
                    }
                });

                return Json(new { success = true, count = userList.Count, message = $"جاري إرسال الإيميل في الخلفية إلى {userList.Count} متدرب بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }
    }
}
