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
                    SELECT Id, WaveName, StartDate, EndDate, ISNULL(IsOnline, 0) AS IsOnline, ISNULL(Mode, CASE WHEN IsOnline = 1 THEN 'Online' ELSE 'Off' END) AS Mode, ISNULL(IsActive, 1) AS IsActive
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
    }
}
