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
        public async Task<IActionResult> Waves()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var waves = await connection.QueryAsync<Exam.DTOs.WaveDto>(
                    "dbo.sp_GetAllWaves",
                    commandType: System.Data.CommandType.StoredProcedure
                );

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
                "SELECT Id, WaveName, StartDate FROM TrainingWaves WHERE Id = @Id",
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
                var newWaveId = await connection.ExecuteScalarAsync<int>(
                    "dbo.sp_Admin_CreateWave",
                    new { wave.WaveName, wave.StartDate, wave.IsOnline },
                    commandType: System.Data.CommandType.StoredProcedure
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
            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
            if (!userRoles.Contains("Admin") && !await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "delete"))
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            await _examService.DeleteWaveAsync(waveid);
            return Json(new { success = true, Message = "Delete wave success" }); 
        }

        [HttpPost]
        public async Task<IActionResult> EditWave([FromBody] Exam.DTOs.WaveDto wave)
        {
            var userRoles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
            if (!userRoles.Contains("Admin") && !await _examService.HasSpecificPermissionAsync(userRoles, "Admin", "Waves", "edit"))
            {
                return Json(new { success = false, message = "Permission denied." });
            }

            if (wave == null || wave.Id <= 0 || string.IsNullOrWhiteSpace(wave.WaveName))
            {
                return Json(new { success = false, message = "Batch name is required." });
            }

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                int rows = await connection.ExecuteAsync(
                    "UPDATE dbo.TrainingWaves SET WaveName = @WaveName, StartDate = @StartDate, IsOnline = @IsOnline WHERE Id = @Id",
                    new { WaveName = wave.WaveName.Trim(), StartDate = wave.StartDate, IsOnline = wave.IsOnline, Id = wave.Id }
                );

                if (rows > 0)
                {
                    return Json(new { success = true, message = "Batch updated successfully." });
                }

                return Json(new { success = false, message = "Batch not found." });
            }
        }
    }
}
