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

                // Auto-generate daily attendance sessions if StartDate is provided (default 2 weeks, 9:00 AM - 12:00 PM, excluding Fridays)
                if (wave.StartDate.HasValue)
                {
                    try
                    {
                        var startDate = wave.StartDate.Value.Date;
                        var endDate = (wave.EndDate.HasValue && wave.EndDate.Value.Date >= startDate)
                            ? wave.EndDate.Value.Date
                            : startDate.AddDays(13); // Default 2 weeks (14 calendar days)

                        var sessionStartTime = new TimeSpan(9, 0, 0); // 09:00 AM
                        var sessionEndTime = new TimeSpan(12, 0, 0);   // 12:00 PM (3 hours duration)
                        int sessionIndex = 1;

                        for (var d = startDate; d <= endDate; d = d.AddDays(1))
                        {
                            if (d.DayOfWeek == DayOfWeek.Friday) continue; // Skip Fridays (إجازة)

                            await connection.ExecuteAsync(@"
                                IF NOT EXISTS (SELECT 1 FROM dbo.AttendanceSessions WHERE WaveId = @WaveId AND SessionDate = @SessionDate)
                                BEGIN
                                    INSERT INTO dbo.AttendanceSessions (WaveId, SessionDate, SessionName, StartTime, EndTime, CreatedAt)
                                    VALUES (@WaveId, @SessionDate, @SessionName, @StartTime, @EndTime, GETDATE())
                                END",
                                new {
                                    WaveId = newWaveId,
                                    SessionDate = d,
                                    SessionName = $"Session {sessionIndex}",
                                    StartTime = sessionStartTime,
                                    EndTime = sessionEndTime
                                }
                            );
                            sessionIndex++;
                        }
                    }
                    catch (Exception)
                    {
                        // Log or handle gracefully without breaking wave creation
                    }
                }

                return Ok(new { NewWaveId = newWaveId });
            }
        }

        [HttpPost]
        public async Task<IActionResult> GenerateWaveAttendanceSessions(int waveId, DateTime? startDate, DateTime? endDate)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var start = startDate;
                var end = endDate;

                if (!start.HasValue)
                {
                    var wave = await connection.QueryFirstOrDefaultAsync<dynamic>("SELECT StartDate, EndDate FROM dbo.TrainingWaves WHERE Id = @Id", new { Id = waveId });
                    if (wave != null)
                    {
                        DateTime? wStart = wave.StartDate;
                        DateTime? wEnd = wave.EndDate;
                        if (wStart.HasValue)
                        {
                            start = wStart.Value;
                            end = wEnd;
                        }
                    }
                }

                if (!start.HasValue) return BadRequest(new { success = false, message = "Start date is required." });

                var sDate = start.Value.Date;
                var eDate = (end.HasValue && end.Value.Date >= sDate) ? end.Value.Date : sDate.AddDays(13);

                var sessionStartTime = new TimeSpan(9, 0, 0);
                var sessionEndTime = new TimeSpan(12, 0, 0);
                int sessionIndex = 1;
                int createdCount = 0;

                for (var d = sDate; d <= eDate; d = d.AddDays(1))
                {
                    if (d.DayOfWeek == DayOfWeek.Friday) continue;

                    int affected = await connection.ExecuteAsync(@"
                        IF NOT EXISTS (SELECT 1 FROM dbo.AttendanceSessions WHERE WaveId = @WaveId AND SessionDate = @SessionDate)
                        BEGIN
                            INSERT INTO dbo.AttendanceSessions (WaveId, SessionDate, SessionName, StartTime, EndTime, CreatedAt)
                            VALUES (@WaveId, @SessionDate, @SessionName, @StartTime, @EndTime, GETDATE())
                        END",
                        new {
                            WaveId = waveId,
                            SessionDate = d,
                            SessionName = $"Session {sessionIndex}",
                            StartTime = sessionStartTime,
                            EndTime = sessionEndTime
                        }
                    );
                    if (affected > 0) createdCount++;
                    sessionIndex++;
                }

                return Ok(new { success = true, createdCount });
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

        [HttpGet]
        public async Task<IActionResult> SmartWaveProposal()
        {
            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Select(c => c.Value).ToList();
            bool hasAccess = User.IsInRole("Admin") || User.IsInRole("HR") || User.IsInRole("Human Resources") || userRoles.Contains("Admin") 
                || await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "SmartWaveProposal", "view")
                || await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "view");
            if (!hasAccess)
                return Forbid();

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var branches = (await conn.QueryAsync<BranchDto>(
                "SELECT CAST(Id AS NVARCHAR(50)) AS Id, BranchName FROM Branches ORDER BY BranchName")).ToList();

            var roles = (await conn.QueryAsync<string>(
                @"SELECT DISTINCT r.Name 
                  FROM AspNetRoles r
                  WHERE r.Name IN ('Pharmacist', 'Assistant', 'Trainee') 
                     OR r.Name LIKE N'%صيدل%' OR r.Name LIKE N'%مساعد%'
                  ORDER BY r.Name")).ToList();

            if (!roles.Any())
            {
                roles = new List<string> { "Pharmacist", "Assistant" };
            }

            var recentWaves = (await conn.QueryAsync<WaveDto>(
                "SELECT TOP 20 Id, WaveName, StartDate, EndDate, IsActive FROM TrainingWaves ORDER BY Id DESC")).ToList();

            ViewBag.Branches = branches;
            ViewBag.Roles = roles;
            ViewBag.RecentWaves = recentWaves;
            ViewBag.DefaultWaveName = $"Batch {recentWaves.Count + 1} - Proposed";

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GenerateWaveCandidates([FromBody] WaveProposalFilterRequest filter)
        {
            try
            {
                filter ??= new WaveProposalFilterRequest();
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                DateTime? dateFrom = filter.DateFrom;
                DateTime? dateTo = filter.DateTo;

                if (filter.Days.HasValue && filter.Days.Value > 0)
                {
                    dateFrom = DateTime.Today.AddDays(-filter.Days.Value);
                    dateTo = DateTime.Today.AddDays(1).AddTicks(-1);
                }

                // 1. Fetch Fresh Candidates (Never enrolled in ANY wave)
                var freshSql = @"
                    WITH UserRoles AS (
                        SELECT UR.UserId,
                               MAX(R.Name) as RoleName
                        FROM AspNetUserRoles UR
                        JOIN AspNetRoles R ON UR.RoleId = R.Id
                        GROUP BY UR.UserId
                    )
                    SELECT 
                        u.Id AS UserId,
                        ISNULL(u.FullName, u.UserName) AS StudentName,
                        u.UserName,
                        ISNULL(u.UserCode, '--') AS UserCode,
                        ISNULL(u.Email, '--') AS Email,
                        ISNULL(u.PhoneNumber, '--') AS PhoneNumber,
                        u.CreatedAt,
                        u.BranchId,
                        ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
                        ISNULL(s.ShiftName, '--') AS ShiftName,
                        COALESCE(ur.RoleName, N'User') AS RoleName,
                        'New' AS CandidateType,
                        0 AS PreviousWaveCount,
                        CAST(NULL AS DECIMAL(18,2)) AS LastExamScore,
                        '' AS JoinedWaveNames
                    FROM AspNetUsers u WITH (NOLOCK)
                    LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                    LEFT JOIN Shifts s WITH (NOLOCK) ON u.ShiftId = s.Id
                    LEFT JOIN UserRoles ur ON ur.UserId = u.Id
                    WHERE (@ActiveOnly = 0 OR u.IsActive = 1)
                      AND (@DateFrom IS NULL OR u.CreatedAt >= @DateFrom)
                      AND (@DateTo IS NULL OR u.CreatedAt <= @DateTo)
                      AND NOT EXISTS (SELECT 1 FROM dbo.UserWaves uw WITH (NOLOCK) WHERE uw.UserId = u.Id)
                    ORDER BY u.CreatedAt DESC";

                var allFresh = (await conn.QueryAsync<WaveCandidateDto>(freshSql, new
                {
                    ActiveOnly = filter.ActiveOnly,
                    DateFrom = dateFrom,
                    DateTo = dateTo
                })).ToList();

                // 2. Fetch Uncertified Resitters (Enrolled in waves before, but no certificate / score < 75%)
                var resitterSql = @"
                    WITH UserRoles AS (
                        SELECT UR.UserId,
                               MAX(R.Name) as RoleName
                        FROM AspNetUserRoles UR
                        JOIN AspNetRoles R ON UR.RoleId = R.Id
                        GROUP BY UR.UserId
                    )
                    SELECT 
                        u.Id AS UserId,
                        ISNULL(u.FullName, u.UserName) AS StudentName,
                        u.UserName,
                        ISNULL(u.UserCode, '--') AS UserCode,
                        ISNULL(u.Email, '--') AS Email,
                        ISNULL(u.PhoneNumber, '--') AS PhoneNumber,
                        u.CreatedAt,
                        u.BranchId,
                        ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
                        ISNULL(s.ShiftName, '--') AS ShiftName,
                        COALESCE(ur.RoleName, N'User') AS RoleName,
                        'Resitter' AS CandidateType,
                        (SELECT COUNT(1) FROM dbo.UserWaves uw2 WITH (NOLOCK) WHERE uw2.UserId = u.Id) AS PreviousWaveCount,
                        (SELECT MAX(wc.Score) FROM dbo.UserWaveCertificates wc WITH (NOLOCK) WHERE wc.UserId = u.Id) AS LastExamScore,
                        (
                            SELECT STRING_AGG(tw.WaveName, N', ') WITHIN GROUP (ORDER BY tw.MinWaveId ASC)
                            FROM (
                                SELECT tw_inner.WaveName, MIN(tw_inner.Id) AS MinWaveId
                                FROM dbo.UserWaves uw_prev WITH (NOLOCK)
                                INNER JOIN dbo.TrainingWaves tw_inner WITH (NOLOCK) ON uw_prev.WaveId = tw_inner.Id
                                WHERE uw_prev.UserId = u.Id
                                GROUP BY tw_inner.WaveName
                            ) tw
                        ) AS JoinedWaveNames
                    FROM AspNetUsers u WITH (NOLOCK)
                    LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                    LEFT JOIN Shifts s WITH (NOLOCK) ON u.ShiftId = s.Id
                    LEFT JOIN UserRoles ur ON ur.UserId = u.Id
                    WHERE (@ActiveOnly = 0 OR u.IsActive = 1)
                      AND EXISTS (SELECT 1 FROM dbo.UserWaves uw WITH (NOLOCK) WHERE uw.UserId = u.Id)
                      AND NOT EXISTS (SELECT 1 FROM dbo.UserWaveCertificates wc WITH (NOLOCK) WHERE wc.UserId = u.Id AND wc.Score >= 75)
                    ORDER BY u.CreatedAt ASC";

                var allResitters = (await conn.QueryAsync<WaveCandidateDto>(resitterSql, new
                {
                    ActiveOnly = filter.ActiveOnly
                })).ToList();

                // Filter by Roles if specified
                if (filter.Roles != null && filter.Roles.Any() && !filter.Roles.Contains("All"))
                {
                    allFresh = allFresh.Where(u => filter.Roles.Any(r => 
                        string.Equals(u.RoleName, r, StringComparison.OrdinalIgnoreCase) ||
                        (r.Equals("Pharmacist", StringComparison.OrdinalIgnoreCase) && (u.RoleName.ToLower().Contains("pharmacist") || u.RoleName.Contains("صيدل"))) ||
                        (r.Equals("Assistant", StringComparison.OrdinalIgnoreCase) && (u.RoleName.ToLower().Contains("assistant") || u.RoleName.Contains("مساعد")))
                    )).ToList();

                    allResitters = allResitters.Where(u => filter.Roles.Any(r => 
                        string.Equals(u.RoleName, r, StringComparison.OrdinalIgnoreCase) ||
                        (r.Equals("Pharmacist", StringComparison.OrdinalIgnoreCase) && (u.RoleName.ToLower().Contains("pharmacist") || u.RoleName.Contains("صيدل"))) ||
                        (r.Equals("Assistant", StringComparison.OrdinalIgnoreCase) && (u.RoleName.ToLower().Contains("assistant") || u.RoleName.Contains("مساعد")))
                    )).ToList();
                }

                // Filter by Branches if specified
                if (filter.BranchIds != null && filter.BranchIds.Any())
                {
                    allFresh = allFresh.Where(u => u.BranchId.HasValue && filter.BranchIds.Contains(u.BranchId.Value)).ToList();
                    allResitters = allResitters.Where(u => u.BranchId.HasValue && filter.BranchIds.Contains(u.BranchId.Value)).ToList();
                }

                int capacity = filter.Capacity > 0 ? filter.Capacity : 50;
                int resitterPct = Math.Clamp(filter.ResitterPercentage, 0, 100);

                int targetResitters = (int)Math.Round(capacity * (resitterPct / 100.0));
                int targetFresh = capacity - targetResitters;

                // Select from both pools
                var selectedResitters = allResitters.Take(targetResitters).ToList();
                int remainingSpots = capacity - selectedResitters.Count;

                var selectedFresh = allFresh.Take(remainingSpots).ToList();
                
                // If fresh candidates didn't fill all remaining spots, take more resitters if available
                if (selectedFresh.Count < remainingSpots && allResitters.Count > selectedResitters.Count)
                {
                    int extraNeeded = remainingSpots - selectedFresh.Count;
                    var extraResitters = allResitters.Skip(selectedResitters.Count).Take(extraNeeded).ToList();
                    selectedResitters.AddRange(extraResitters);
                }

                var finalCandidates = new List<WaveCandidateDto>();
                finalCandidates.AddRange(selectedFresh);
                finalCandidates.AddRange(selectedResitters);

                // Sort by BranchName, then RoleName, then StudentName for clean presentation
                finalCandidates = finalCandidates.OrderBy(c => c.BranchName).ThenBy(c => c.RoleName).ThenBy(c => c.StudentName).ToList();

                var distinctBranches = finalCandidates.Where(c => !string.IsNullOrEmpty(c.BranchName) && c.BranchName != "--").Select(c => c.BranchName).Distinct().Count();

                return Json(new
                {
                    success = true,
                    totalCandidates = finalCandidates.Count,
                    freshCount = selectedFresh.Count,
                    resitterCount = selectedResitters.Count,
                    branchesCount = distinctBranches,
                    availableFreshPool = allFresh.Count,
                    availableResittersPool = allResitters.Count,
                    candidates = finalCandidates
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error generating candidates: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ExportWaveProposalToExcel([FromBody] WaveProposalExportRequest request)
        {
            try
            {
                if (request == null || request.SelectedUserIds == null || !request.SelectedUserIds.Any())
                {
                    return BadRequest("No candidates selected for export.");
                }

                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    WITH UserRoles AS (
                        SELECT UR.UserId,
                               MAX(R.Name) as RoleName
                        FROM AspNetUserRoles UR
                        JOIN AspNetRoles R ON UR.RoleId = R.Id
                        GROUP BY UR.UserId
                    )
                    SELECT 
                        u.Id AS UserId,
                        ISNULL(u.FullName, u.UserName) AS StudentName,
                        ISNULL(u.UserCode, '--') AS UserCode,
                        ISNULL(u.Email, '--') AS Email,
                        ISNULL(u.PhoneNumber, '--') AS PhoneNumber,
                        u.CreatedAt,
                        u.BranchId,
                        ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
                        ISNULL(s.ShiftName, '--') AS ShiftName,
                        COALESCE(ur.RoleName, N'User') AS RoleName,
                        CASE 
                            WHEN EXISTS (SELECT 1 FROM dbo.UserWaves uw WHERE uw.UserId = u.Id) THEN N'إعادة فرصة (لم يحصل على شهادة)'
                            ELSE N'جديد لأول مرة'
                        END AS CandidateType,
                        (SELECT COUNT(1) FROM dbo.UserWaves uw2 WHERE uw2.UserId = u.Id) AS PreviousWaveCount,
                        (
                            SELECT STRING_AGG(tw.WaveName, N', ') WITHIN GROUP (ORDER BY tw.MinWaveId ASC)
                            FROM (
                                SELECT tw_inner.WaveName, MIN(tw_inner.Id) AS MinWaveId
                                FROM dbo.UserWaves uw_prev WITH (NOLOCK)
                                INNER JOIN dbo.TrainingWaves tw_inner WITH (NOLOCK) ON uw_prev.WaveId = tw_inner.Id
                                WHERE uw_prev.UserId = u.Id
                                GROUP BY tw_inner.WaveName
                            ) tw
                        ) AS JoinedWaveNames
                    FROM AspNetUsers u WITH (NOLOCK)
                    LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                    LEFT JOIN Shifts s WITH (NOLOCK) ON u.ShiftId = s.Id
                    LEFT JOIN UserRoles ur ON ur.UserId = u.Id
                    WHERE u.Id IN @UserIds
                    ORDER BY ISNULL(b.BranchName, N'ZZZ') ASC, ur.RoleName ASC, ISNULL(u.FullName, u.UserName) ASC";

                var candidates = (await conn.QueryAsync<dynamic>(sql, new { UserIds = request.SelectedUserIds })).ToList();

                using (var workbook = new XLWorkbook())
                {
                    // Sheet 1: Candidates Roster
                    var ws = workbook.Worksheets.Add("كشف المرشحين للدفعة");
                    ws.RightToLeft = true; // Arabic layout

                    // Title Header Block
                    ws.Range("A1:K1").Merge();
                    ws.Cell("A1").Value = "مجموعة صيدليات الطرشوبي - أكاديمية التدريب والتطوير";
                    ws.Cell("A1").Style.Font.Bold = true;
                    ws.Cell("A1").Style.Font.FontSize = 16;
                    ws.Cell("A1").Style.Font.FontColor = XLColor.White;
                    ws.Cell("A1").Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A"); // Navy
                    ws.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell("A1").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    ws.Row(1).Height = 35;

                    ws.Range("A2:K2").Merge();
                    ws.Cell("A2").Value = $"كشف المرشحين المقترحين لـ: {(!string.IsNullOrWhiteSpace(request.ProposedWaveName) ? request.ProposedWaveName : "الدفعة التدريبية القادمة")}";
                    ws.Cell("A2").Style.Font.Bold = true;
                    ws.Cell("A2").Style.Font.FontSize = 13;
                    ws.Cell("A2").Style.Font.FontColor = XLColor.White;
                    ws.Cell("A2").Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E"); // Teal
                    ws.Cell("A2").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Row(2).Height = 25;

                    // Info summary bar
                    ws.Cell("A3").Value = "تاريخ الدفعة المقترح:";
                    ws.Cell("A3").Style.Font.Bold = true;
                    ws.Cell("B3").Value = request.ProposedStartDate.HasValue ? request.ProposedStartDate.Value.ToString("yyyy-MM-dd") : "يحدد لاحقاً";

                    ws.Cell("D3").Value = "إجمالي المرشحين:";
                    ws.Cell("D3").Style.Font.Bold = true;
                    ws.Cell("E3").Value = candidates.Count;

                    ws.Cell("G3").Value = "تاريخ التصدير:";
                    ws.Cell("G3").Style.Font.Bold = true;
                    ws.Cell("H3").Value = DateTime.Now.ToString("yyyy-MM-dd hh:mm tt");

                    ws.Range("A3:K3").Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
                    ws.Range("A3:K3").Style.Font.FontSize = 10;
                    ws.Row(3).Height = 22;

                    // Instructions to Branch Managers
                    ws.Range("A4:K4").Merge();
                    ws.Cell("A4").Value = "⚠️ السادة مديري الفروع المحترمين: نرفق لسيادتكم أسماء الزملاء المرشحين بالدفعة من فرعكم الموقر. يرجى التنسيق المسبق والتأكيد على جاهزية وتفريغ الزملاء لحضور المحاضرات وخوض الاختبارات المقررة في مواعيدها.";
                    ws.Cell("A4").Style.Font.Italic = true;
                    ws.Cell("A4").Style.Font.FontSize = 9.5;
                    ws.Cell("A4").Style.Font.FontColor = XLColor.FromHtml("#1E293B");
                    ws.Cell("A4").Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF3C7"); // Warm Amber
                    ws.Cell("A4").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Row(4).Height = 24;

                    // Table Headers on Row 6
                    int row = 6;
                    string[] headers = new[]
                    {
                        "م",
                        "الفرع",
                        "كود الموظف",
                        "اسم الزميل / الزميلة",
                        "الوظيفة / التخصص",
                        "حالة الترشيح",
                        "تاريخ التسجيل بالمنصة",
                        "الوردية",
                        "رقم الهاتف",
                        "البريد الإلكتروني",
                        "الدفعة السابقة / ملاحظات مدير الفرع"
                    };

                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = ws.Cell(row, i + 1);
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Font.FontSize = 10.5;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B"); // Slate 800
                        cell.Style.Font.FontColor = XLColor.White;
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    }
                    ws.Row(row).Height = 28;

                    int idx = 1;
                    foreach (var c in candidates)
                    {
                        row++;
                        ws.Cell(row, 1).Value = idx++;
                        ws.Cell(row, 2).Value = Convert.ToString(c.BranchName);
                        ws.Cell(row, 3).Value = Convert.ToString(c.UserCode);
                        ws.Cell(row, 4).Value = Convert.ToString(c.StudentName);
                        ws.Cell(row, 5).Value = Convert.ToString(c.RoleName);

                        string cType = Convert.ToString(c.CandidateType);
                        var cellType = ws.Cell(row, 6);
                        cellType.Value = cType;
                        cellType.Style.Font.Bold = true;
                        if (cType.Contains("جديد"))
                        {
                            cellType.Style.Font.FontColor = XLColor.FromHtml("#059669"); // Emerald
                        }
                        else
                        {
                            cellType.Style.Font.FontColor = XLColor.FromHtml("#D97706"); // Amber
                        }

                        DateTime? dt = c.CreatedAt != null ? (DateTime?)c.CreatedAt : null;
                        ws.Cell(row, 7).Value = dt.HasValue ? dt.Value.ToString("yyyy-MM-dd") : "--";
                        ws.Cell(row, 8).Value = Convert.ToString(c.ShiftName);
                        ws.Cell(row, 9).Value = Convert.ToString(c.PhoneNumber);
                        ws.Cell(row, 10).Value = Convert.ToString(c.Email);

                        string joinedWaves = Convert.ToString(c.JoinedWaveNames);
                        var cellNote = ws.Cell(row, 11);
                        if (!string.IsNullOrWhiteSpace(joinedWaves))
                        {
                            cellNote.Value = joinedWaves;
                            cellNote.Style.Font.Bold = true;
                            cellNote.Style.Font.FontColor = XLColor.FromHtml("#B45309"); // Amber 700
                        }
                        else
                        {
                            cellNote.Value = ""; // Manager note space
                        }

                        // Style data row
                        var dataRange = ws.Range(row, 1, row, headers.Length);
                        dataRange.Style.Font.FontSize = 10;
                        dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                        if (idx % 2 == 0)
                        {
                            dataRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                        }
                        ws.Row(row).Height = 22;
                    }

                    // Apply borders
                    var tableRange = ws.Range(6, 1, row, headers.Length);
                    tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    tableRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#CBD5E1");
                    tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                    tableRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#94A3B8");

                    // Alignments
                    ws.Column(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    ws.Column(5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Column(10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                    ws.Column(11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    ws.Columns().AdjustToContents();
                    ws.Column(11).Width = 30; // give plenty of room for branch manager sign

                    // Sheet 2: Summary by Branch (ملخص الفروع)
                    var wsSummary = workbook.Worksheets.Add("ملخص أعداد الفروع");
                    wsSummary.RightToLeft = true;

                    wsSummary.Range("A1:E1").Merge();
                    wsSummary.Cell("A1").Value = "توزيع المرشحين حسب الفروع";
                    wsSummary.Cell("A1").Style.Font.Bold = true;
                    wsSummary.Cell("A1").Style.Font.FontSize = 14;
                    wsSummary.Cell("A1").Style.Font.FontColor = XLColor.White;
                    wsSummary.Cell("A1").Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E");
                    wsSummary.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    wsSummary.Row(1).Height = 30;

                    int sRow = 3;
                    string[] sHeaders = { "الفرع", "عدد الصيادلة", "عدد المساعدين", "أدوار أخرى", "إجمالي مرشحي الفرع" };
                    for (int i = 0; i < sHeaders.Length; i++)
                    {
                        var sc = wsSummary.Cell(sRow, i + 1);
                        sc.Value = sHeaders[i];
                        sc.Style.Font.Bold = true;
                        sc.Style.Fill.BackgroundColor = XLColor.FromHtml("#334155");
                        sc.Style.Font.FontColor = XLColor.White;
                        sc.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                    wsSummary.Row(sRow).Height = 24;

                    var branchGroups = candidates.GroupBy(c => Convert.ToString(c.BranchName))
                                                .OrderBy(g => g.Key);

                    int totalPharm = 0, totalAsst = 0, totalOther = 0;
                    foreach (var bg in branchGroups)
                    {
                        sRow++;
                        string bName = bg.Key;
                        int pharmCount = bg.Count(c => Convert.ToString(c.RoleName).ToLower().Contains("pharmacist") || Convert.ToString(c.RoleName).Contains("صيدل"));
                        int asstCount = bg.Count(c => Convert.ToString(c.RoleName).ToLower().Contains("assistant") || Convert.ToString(c.RoleName).Contains("مساعد"));
                        int otherCount = bg.Count() - (pharmCount + asstCount);

                        totalPharm += pharmCount;
                        totalAsst += asstCount;
                        totalOther += otherCount;

                        wsSummary.Cell(sRow, 1).Value = bName;
                        wsSummary.Cell(sRow, 2).Value = pharmCount;
                        wsSummary.Cell(sRow, 3).Value = asstCount;
                        wsSummary.Cell(sRow, 4).Value = otherCount;
                        wsSummary.Cell(sRow, 5).Value = bg.Count();

                        var sRange = wsSummary.Range(sRow, 1, sRow, 5);
                        sRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        if (sRow % 2 == 0) sRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                        wsSummary.Row(sRow).Height = 20;
                    }

                    // Total Row
                    sRow++;
                    wsSummary.Cell(sRow, 1).Value = "الإجمالي العام";
                    wsSummary.Cell(sRow, 2).Value = totalPharm;
                    wsSummary.Cell(sRow, 3).Value = totalAsst;
                    wsSummary.Cell(sRow, 4).Value = totalOther;
                    wsSummary.Cell(sRow, 5).Value = candidates.Count;
                    var totRange = wsSummary.Range(sRow, 1, sRow, 5);
                    totRange.Style.Font.Bold = true;
                    totRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
                    totRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    wsSummary.Row(sRow).Height = 24;

                    var sumTableRange = wsSummary.Range(3, 1, sRow, 5);
                    sumTableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    sumTableRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#CBD5E1");
                    sumTableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;

                    wsSummary.Columns().AdjustToContents();

                    using var stream = new MemoryStream();
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    string safeWaveName = !string.IsNullOrWhiteSpace(request.ProposedWaveName) 
                        ? string.Join("_", request.ProposedWaveName.Split(Path.GetInvalidFileNameChars())) 
                        : "Proposed_Wave";
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{safeWaveName}_Branch_Roster_{DateTime.Now:yyyyMMdd}.xlsx");
                }
            }
            catch (Exception ex)
            {
                return BadRequest("Error exporting to Excel: " + ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> ConfirmWaveProposalEnrollment([FromBody] WaveEnrollmentRequest request)
        {
            try
            {
                if (request == null || request.UserIds == null || !request.UserIds.Any())
                {
                    return Json(new { success = false, message = "لم يتم تحديد أي مرشحين للتسكين." });
                }

                int targetWaveId = 0;
                string waveName = "";

                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureWaveModeColumnAsync(conn);

                if (request.ExistingWaveId.HasValue && request.ExistingWaveId.Value > 0)
                {
                    targetWaveId = request.ExistingWaveId.Value;
                    waveName = await conn.QueryFirstOrDefaultAsync<string>("SELECT WaveName FROM TrainingWaves WHERE Id = @Id", new { Id = targetWaveId }) ?? "Wave " + targetWaveId;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(request.NewWaveName))
                    {
                        return Json(new { success = false, message = "يرجى إدخال اسم الويف الجديدة." });
                    }

                    waveName = request.NewWaveName.Trim();
                    var modeValue = string.IsNullOrWhiteSpace(request.Mode) ? "Offline in Main Branch" : request.Mode.Trim();
                    var startDate = request.NewStartDate ?? DateTime.Now.Date.AddDays(7);
                    var endDate = request.NewEndDate ?? startDate.AddDays(5);

                    targetWaveId = await conn.ExecuteScalarAsync<int>(@"
                        INSERT INTO dbo.TrainingWaves (WaveName, StartDate, EndDate, IsOnline, Mode, IsActive)
                        VALUES (@WaveName, @StartDate, @EndDate, @IsOnline, @Mode, 1);
                        SELECT SCOPE_IDENTITY();",
                        new { WaveName = waveName, StartDate = startDate, EndDate = endDate, IsOnline = modeValue.ToLower().Contains("online"), Mode = modeValue }
                    );
                }

                // Batch insert into UserWaves
                string sqlBatch = @"
                    INSERT INTO UserWaves (UserId, WaveId, JoinDate, IsActive)
                    SELECT Id, @WaveId, @JoinDate, 1
                    FROM AspNetUsers
                    WHERE Id IN @UserIds
                      AND Id NOT IN (SELECT UserId FROM UserWaves WHERE WaveId = @WaveId)";

                int enrolled = await conn.ExecuteAsync(sqlBatch, new
                {
                    WaveId = targetWaveId,
                    JoinDate = DateTime.Now,
                    UserIds = request.UserIds
                });

                // Auto-assign any active wave exams
                try
                {
                    var activeExams = (await conn.QueryAsync<dynamic>(
                        "SELECT Id, StartTime, EndTime FROM Exams WHERE WaveId = @WaveId AND IsActive = 1",
                        new { WaveId = targetWaveId })).ToList();

                    foreach (var exam in activeExams)
                    {
                        foreach (var uid in request.UserIds)
                        {
                            try
                            {
                                await _examService.AssignExamToStudentAsync((int)exam.Id, uid, (DateTime?)exam.StartTime, (DateTime?)exam.EndTime);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                return Json(new
                {
                    success = true,
                    waveId = targetWaveId,
                    waveName = waveName,
                    enrolledCount = enrolled,
                    message = $"تم بنجاح تسكين {enrolled} متدرب في الويف '{waveName}'."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "فشل التسكين: " + ex.Message });
            }
        }
    }
}
