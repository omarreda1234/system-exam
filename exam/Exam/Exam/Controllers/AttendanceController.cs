using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using Exam.Models;
using Exam.DTOs;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using System;
using ClosedXML.Excel;
using System.IO;

namespace Exam.Controllers
{
    [Authorize(Roles = "Admin,HR,Reception,SoftSkills Specialist")]
    public class AttendanceController : Controller
    {
        private readonly string _connectionString;

        public AttendanceController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        [HttpGet]
        public async Task<IActionResult> ExportSessionAttendance(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var session = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT S.*, 
                       TW.WaveName,
                       C.Name as CompanyName
                FROM AttendanceSessions S
                LEFT JOIN TrainingWaves TW ON S.WaveId = TW.Id
                LEFT JOIN Companies C ON S.CompanyId = C.Id
                WHERE S.Id = @Id", new { Id = sessionId });
            
            if (session == null) return NotFound();

            string title = session.WaveId != null ? session.WaveName : session.CompanyName;
            string sessionName = session.SessionName;
            DateTime sessionDate = session.SessionDate;

            IEnumerable<dynamic> users;

            if (session.CompanyId != null)
            {
                users = await conn.QueryAsync<dynamic>(@"
                    SELECT CONCAT('CT_', U.Id) as Id, U.FullName as UserName, U.Email, U.UserCode, U.BranchName as BranchName,
                           ISNULL(UA.IsPresent, 0) as IsPresent,
                           UA.CheckInTime, UA.CheckOutTime
                    FROM CompanyTrainees U
                    LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.CompanyTraineeId = U.Id
                    WHERE U.CompanyId = @CompanyId
                    ORDER BY U.FullName", 
                    new { SessionId = sessionId, CompanyId = (int)session.CompanyId });
            }
            else
            {
                users = await conn.QueryAsync<dynamic>(@"
                    SELECT U.Id, U.UserName, U.Email, U.UserCode, B.BranchName,
                           ISNULL(UA.IsPresent, 0) as IsPresent,
                           UA.CheckInTime, UA.CheckOutTime
                    FROM AspNetUsers U
                    JOIN UserWaves UW ON U.Id = UW.UserId
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.UserId = U.Id
                    WHERE UW.WaveId = @WaveId AND U.IsActive = 1
                    ORDER BY U.UserName", 
                    new { SessionId = sessionId, WaveId = (int)session.WaveId });
            }

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Session Attendance");
                
                worksheet.Cell(1, 1).Value = $"Session Attendance: {title} - {sessionName}";
                worksheet.Cell(1, 1).Style.Font.Bold = true;
                worksheet.Cell(1, 1).Style.Font.FontSize = 14;

                worksheet.Cell(2, 1).Value = $"Date: {sessionDate:MMMM dd, yyyy}";
                worksheet.Cell(2, 1).Style.Font.Italic = true;

                var headerRow = 4;
                worksheet.Cell(headerRow, 1).Value = "Name / الاسم";
                worksheet.Cell(headerRow, 2).Value = "Code / الكود";
                worksheet.Cell(headerRow, 3).Value = "Branch / الفرع";
                worksheet.Cell(headerRow, 4).Value = "Status / الحالة";
                worksheet.Cell(headerRow, 5).Value = "Check In Time / وقت الحضور";
                worksheet.Cell(headerRow, 6).Value = "Check Out Time / وقت الانصراف";

                var headerRange = worksheet.Range(headerRow, 1, headerRow, 6);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                int rowIdx = 5;
                foreach (var u in users)
                {
                    worksheet.Cell(rowIdx, 1).SetValue(u.UserName?.ToString() ?? "");
                    worksheet.Cell(rowIdx, 2).SetValue(u.UserCode?.ToString() ?? "");
                    worksheet.Cell(rowIdx, 3).SetValue(u.BranchName?.ToString() ?? "");

                    bool isPresent = u.IsPresent != null && (u.IsPresent.ToString() == "True" || u.IsPresent.ToString() == "1" || u.IsPresent.ToString() == "true");
                    var statusCell = worksheet.Cell(rowIdx, 4);
                    if (isPresent)
                    {
                        statusCell.SetValue("Present / حاضر");
                        statusCell.Style.Font.FontColor = XLColor.Green;
                    }
                    else
                    {
                        statusCell.SetValue("Absent / غائب");
                        statusCell.Style.Font.FontColor = XLColor.Red;
                    }
                    statusCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    worksheet.Cell(rowIdx, 5).SetValue(u.CheckInTime?.ToString() ?? "-");
                    worksheet.Cell(rowIdx, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    worksheet.Cell(rowIdx, 6).SetValue(u.CheckOutTime?.ToString() ?? "-");
                    worksheet.Cell(rowIdx, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    rowIdx++;
                }

                worksheet.Columns().AdjustToContents();
                worksheet.Column(1).Width = 30;

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 
                        $"Attendance_{title.Replace(" ", "_")}_{sessionName.Replace(" ", "_")}_{sessionDate:yyyyMMdd}.xlsx");
                }
            }
        }

        public async Task<IActionResult> Analytics()
        {
            using var conn = new SqlConnection(_connectionString);

            // 1. Global Stats - Use Total possible across Waves and Companies
            var stats = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                DECLARE @TotalPossible INT = (
                    SELECT ISNULL(SUM(TotalCount), 0)
                    FROM (
                        SELECT 
                            CASE 
                                WHEN S.CompanyId IS NOT NULL THEN (SELECT COUNT(*) FROM CompanyTrainees CU WHERE CU.CompanyId = S.CompanyId)
                                ELSE (SELECT COUNT(*) FROM UserWaves UW WHERE UW.WaveId = S.WaveId AND UW.IsActive = 1)
                            END as TotalCount
                        FROM AttendanceSessions S
                    ) t
                );
                DECLARE @TotalPresent INT = (SELECT COUNT(*) FROM UserAttendance WHERE IsPresent = 1);
                
                SELECT 
                    (SELECT COUNT(DISTINCT Id) FROM AttendanceSessions) as TotalSessions,
                    @TotalPossible as TotalRecords,
                    @TotalPresent as PresentCount,
                    CASE WHEN @TotalPossible > @TotalPresent THEN @TotalPossible - @TotalPresent ELSE 0 END as AbsentCount");

            // 2. Attendance by Wave
            var waveStats = await conn.QueryAsync<dynamic>(@"
                SELECT 
                    W.WaveName,
                    (SELECT COUNT(*) FROM UserWaves UW JOIN AttendanceSessions S ON UW.WaveId = S.WaveId WHERE UW.WaveId = W.Id AND UW.IsActive = 1) as Total,
                    ISNULL((SELECT COUNT(*) FROM UserAttendance UA JOIN AttendanceSessions S ON UA.SessionId = S.Id WHERE S.WaveId = W.Id AND UA.IsPresent = 1), 0) as Present
                FROM TrainingWaves W");

            // 3. Attendance by Branch
            var branchStats = await conn.QueryAsync<dynamic>(@"
                SELECT 
                    B.BranchName,
                    (SELECT COUNT(*) FROM UserWaves UW JOIN AspNetUsers U ON UW.UserId = U.Id JOIN AttendanceSessions S ON UW.WaveId = S.WaveId WHERE U.BranchId = B.Id AND UW.IsActive = 1) as Total,
                    ISNULL((SELECT COUNT(*) FROM UserAttendance UA JOIN AspNetUsers U ON UA.UserId = U.Id WHERE U.BranchId = B.Id AND UA.IsPresent = 1), 0) as Present
                FROM Branches B");

            // 4. Last 10 Sessions Trend - Unified Waves & Companies
            var trendStats = await conn.QueryAsync<dynamic>(@"
                SELECT TOP 10
                    S.SessionName + ' (' + CONVERT(VARCHAR, S.SessionDate, 3) + ')' as Label,
                    CASE 
                        WHEN S.CompanyId IS NOT NULL THEN (SELECT COUNT(*) FROM CompanyTrainees CU WHERE CU.CompanyId = S.CompanyId)
                        ELSE (SELECT COUNT(*) FROM UserWaves UW WHERE UW.WaveId = S.WaveId AND UW.IsActive = 1)
                    END as Total,
                    ISNULL((SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1), 0) as Present
                FROM AttendanceSessions S
                ORDER BY S.SessionDate DESC");

            // 5. Detailed Attendance Log (both waves and companies, including absent/unrecorded)
            var attendanceLog = await conn.QueryAsync<dynamic>(@"
                SELECT 
                    U.Id as UserId,
                    ISNULL(NULLIF(U.FullName, ''), U.UserName) as FullName,
                    U.UserCode,
                    S.SessionName,
                    S.SessionDate,
                    N'Wave / دفعة' as SessionType,
                    W.WaveName as GroupName,
                    ISNULL(UA.IsPresent, 0) as IsPresent,
                    UA.CheckInTime,
                    UA.CheckOutTime
                FROM AttendanceSessions S
                JOIN TrainingWaves W ON S.WaveId = W.Id
                JOIN AspNetUsers U ON U.Id IN (SELECT UW.UserId FROM UserWaves UW WHERE UW.WaveId = S.WaveId AND UW.IsActive = 1)
                LEFT JOIN UserAttendance UA ON UA.SessionId = S.Id AND UA.UserId = U.Id
                WHERE U.IsActive = 1

                UNION ALL

                SELECT 
                    CAST(CT.Id as NVARCHAR) as UserId,
                    CT.FullName as FullName,
                    CT.UserCode,
                    S.SessionName,
                    S.SessionDate,
                    N'Company / شركة' as SessionType,
                    C.Name as GroupName,
                    ISNULL(UA.IsPresent, 0) as IsPresent,
                    UA.CheckInTime,
                    UA.CheckOutTime
                FROM AttendanceSessions S
                JOIN Companies C ON S.CompanyId = C.Id
                JOIN CompanyTrainees CT ON CT.CompanyId = S.CompanyId
                LEFT JOIN UserAttendance UA ON UA.SessionId = S.Id AND UA.CompanyTraineeId = CT.Id

                ORDER BY SessionDate DESC, SessionName ASC, FullName ASC");

            var allWavesQuery = await conn.QueryAsync<string>("SELECT WaveName FROM TrainingWaves");
            var allCompaniesQuery = await conn.QueryAsync<string>("SELECT Name FROM Companies");

            ViewBag.Stats = stats;
            ViewBag.WaveStats = waveStats;
            ViewBag.BranchStats = branchStats;
            ViewBag.TrendStats = trendStats.Reverse();
            ViewBag.AttendanceLog = attendanceLog;
            ViewBag.AllWaves = allWavesQuery.OrderBy(w => w).ToList();
            ViewBag.AllCompanies = allCompaniesQuery.OrderBy(c => c).ToList();

            return View();
        }

        [HttpGet]
        public async Task<IActionResult> ExportAllAttendanceToExcel()
        {
            using var conn = new SqlConnection(_connectionString);
            var logs = await conn.QueryAsync<dynamic>(@"
                SELECT 
                    ISNULL(NULLIF(U.FullName, ''), U.UserName) as FullName,
                    U.UserCode,
                    S.SessionName,
                    S.SessionDate,
                    'Wave' as SessionType,
                    W.WaveName as GroupName,
                    CASE WHEN ISNULL(UA.IsPresent, 0) = 1 THEN 'Present' ELSE 'Absent' END as Status,
                    UA.CheckInTime,
                    UA.CheckOutTime
                FROM AttendanceSessions S
                JOIN TrainingWaves W ON S.WaveId = W.Id
                JOIN AspNetUsers U ON U.Id IN (SELECT UW.UserId FROM UserWaves UW WHERE UW.WaveId = S.WaveId AND UW.IsActive = 1)
                LEFT JOIN UserAttendance UA ON UA.SessionId = S.Id AND UA.UserId = U.Id
                WHERE U.IsActive = 1
                
                UNION ALL
                
                SELECT 
                    CT.FullName as FullName,
                    CT.UserCode,
                    S.SessionName,
                    S.SessionDate,
                    'Company' as SessionType,
                    C.Name as GroupName,
                    CASE WHEN ISNULL(UA.IsPresent, 0) = 1 THEN 'Present' ELSE 'Absent' END as Status,
                    UA.CheckInTime,
                    UA.CheckOutTime
                FROM AttendanceSessions S
                JOIN Companies C ON S.CompanyId = C.Id
                JOIN CompanyTrainees CT ON CT.CompanyId = S.CompanyId
                LEFT JOIN UserAttendance UA ON UA.SessionId = S.Id AND UA.CompanyTraineeId = CT.Id

                ORDER BY SessionDate DESC, SessionName ASC, FullName ASC");

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Global Attendance Log");
                worksheet.Cell(1, 1).SetValue("Employee Name / الاسم");
                worksheet.Cell(1, 2).SetValue("Employee Code / الكود");
                worksheet.Cell(1, 3).SetValue("Group/Company Name / المجموعة/الشركة");
                worksheet.Cell(1, 4).SetValue("Type / النوع");
                worksheet.Cell(1, 5).SetValue("Session Name / اسم المحاضرة");
                worksheet.Cell(1, 6).SetValue("Date / التاريخ");
                worksheet.Cell(1, 7).SetValue("Status / الحالة");
                worksheet.Cell(1, 8).SetValue("Check-In Time / وقت الحضور");
                worksheet.Cell(1, 9).SetValue("Check-Out Time / وقت الانصراف");

                // Style header row
                var headerRange = worksheet.Range("A1:I1");
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#6366f1");
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                int currentRow = 1;
                foreach (var log in logs)
                {
                    currentRow++;
                    worksheet.Cell(currentRow, 1).SetValue(log.FullName ?? "");
                    worksheet.Cell(currentRow, 2).SetValue(log.UserCode ?? "");
                    worksheet.Cell(currentRow, 3).SetValue(log.GroupName ?? "");
                    worksheet.Cell(currentRow, 4).SetValue(log.SessionType ?? "");
                    worksheet.Cell(currentRow, 5).SetValue(log.SessionName ?? "");
                    worksheet.Cell(currentRow, 6).SetValue(log.SessionDate.ToString("yyyy-MM-dd") ?? "");
                    worksheet.Cell(currentRow, 7).SetValue(log.Status ?? "");
                    worksheet.Cell(currentRow, 8).SetValue(log.CheckInTime ?? "--");
                    worksheet.Cell(currentRow, 9).SetValue(log.CheckOutTime ?? "--");

                    // Status highlight
                    if (log.Status == "Present")
                    {
                        worksheet.Cell(currentRow, 7).Style.Font.FontColor = XLColor.Green;
                    }
                    else
                    {
                        worksheet.Cell(currentRow, 7).Style.Font.FontColor = XLColor.Red;
                    }
                }

                worksheet.Columns().AdjustToContents();

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 
                        $"Global_Attendance_Log_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                }
            }
        }

        public async Task<IActionResult> Index()
        {
            using var conn = new SqlConnection(_connectionString);
            var waves = await conn.QueryAsync<dynamic>("SELECT Id, WaveName FROM TrainingWaves");
            var companies = await conn.QueryAsync<dynamic>("SELECT Id, Name FROM Companies ORDER BY Name");

            var softSkillsTrack = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT Id FROM SkillTracks WHERE TrackName = 'SoftSkills Track'");
            if (softSkillsTrack != null)
            {
                ViewBag.SoftSkillSessions = await conn.QueryAsync<dynamic>(@"
                    SELECT S.*, 
                        (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id) as EnrolledCount,
                        (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1) as PresentCount
                    FROM AttendanceSessions S 
                    WHERE S.SkillTrackId = @Id 
                    ORDER BY S.SessionDate DESC", new { Id = softSkillsTrack.Id });
            }

            ViewBag.Companies = companies;
            return View(waves);
        }

        public async Task<IActionResult> ManageSessions(int? waveId, int? companyId)
        {
            using var conn = new SqlConnection(_connectionString);
            
            if (companyId.HasValue)
            {
                var company = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT Id, Name FROM Companies WHERE Id = @Id", new { Id = companyId.Value });
                if (company == null) return NotFound();

                var sessions = await conn.QueryAsync<dynamic>(@"
                    SELECT S.*, (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1) as PresentCount
                    FROM AttendanceSessions S 
                    WHERE S.CompanyId = @CompanyId 
                    ORDER BY S.SessionDate", new { CompanyId = companyId.Value });

                ViewBag.Company = company;
                ViewBag.IsCompany = true;
                return View(sessions);
            }
            else
            {
                var wave = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT Id, WaveName FROM TrainingWaves WHERE Id = @WaveId", new { WaveId = waveId });
                if (wave == null) return RedirectToAction(nameof(Index));

                var sessions = await conn.QueryAsync<dynamic>(@"
                    SELECT S.*, (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1) as PresentCount
                    FROM AttendanceSessions S 
                    WHERE S.WaveId = @WaveId 
                    ORDER BY S.SessionDate", new { WaveId = waveId });

                ViewBag.Wave = wave;
                ViewBag.IsCompany = false;
                return View(sessions);
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateSession(int? waveId, int? companyId, DateTime sessionDate, string sessionName)
        {
            using var conn = new SqlConnection(_connectionString);
            if (companyId.HasValue)
            {
                await conn.ExecuteAsync("INSERT INTO AttendanceSessions (CompanyId, SessionDate, SessionName) VALUES (@CompanyId, @Date, @Name)", 
                    new { CompanyId = companyId, Date = sessionDate, Name = sessionName });
                return RedirectToAction(nameof(ManageSessions), new { companyId });
            }
            else
            {
                await conn.ExecuteAsync("INSERT INTO AttendanceSessions (WaveId, SessionDate, SessionName) VALUES (@WaveId, @Date, @Name)", 
                    new { WaveId = waveId, Date = sessionDate, Name = sessionName });
                return RedirectToAction(nameof(ManageSessions), new { waveId });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteSession(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM UserAttendance WHERE SessionId = @Sid", new { Sid = sessionId });
            await conn.ExecuteAsync("DELETE FROM AttendanceSessions WHERE Id = @Sid", new { Sid = sessionId });
            
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> ExportAttendance(int? waveId, int? companyId)
        {
            using var conn = new SqlConnection(_connectionString);
            
            string title = "";
            List<dynamic> sessions;
            List<dynamic> trainees;
            List<dynamic> attendanceData;

            if (companyId.HasValue)
            {
                var company = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT Id, Name FROM Companies WHERE Id = @Id", new { Id = companyId.Value });
                if (company == null) return NotFound();
                title = company.Name;

                sessions = (await conn.QueryAsync<dynamic>(@"
                    SELECT Id, SessionName, SessionDate 
                    FROM AttendanceSessions 
                    WHERE CompanyId = @CompanyId 
                    ORDER BY SessionDate", new { CompanyId = companyId.Value })).ToList();

                trainees = (await conn.QueryAsync<dynamic>(@"
                    SELECT CONCAT('CT_', Id) as Id, FullName as UserName, UserCode, BranchName
                    FROM CompanyTrainees
                    WHERE CompanyId = @CompanyId
                    ORDER BY FullName", new { CompanyId = companyId.Value })).ToList();

                attendanceData = (await conn.QueryAsync<dynamic>(@"
                    SELECT CONCAT('CT_', UA.CompanyTraineeId) as UserId, UA.SessionId, UA.IsPresent, UA.CheckInTime, UA.CheckOutTime
                    FROM UserAttendance UA
                    JOIN AttendanceSessions S ON UA.SessionId = S.Id
                    WHERE S.CompanyId = @CompanyId AND UA.CompanyTraineeId IS NOT NULL", new { CompanyId = companyId.Value })).ToList();
            }
            else
            {
                var wave = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT Id, WaveName FROM TrainingWaves WHERE Id = @Id", new { Id = waveId.Value });
                if (wave == null) return NotFound();
                title = wave.WaveName;

                sessions = (await conn.QueryAsync<dynamic>(@"
                    SELECT Id, SessionName, SessionDate 
                    FROM AttendanceSessions 
                    WHERE WaveId = @WaveId 
                    ORDER BY SessionDate", new { WaveId = waveId.Value })).ToList();

                trainees = (await conn.QueryAsync<dynamic>(@"
                    SELECT U.Id, U.UserName, U.UserCode, B.BranchName
                    FROM AspNetUsers U
                    JOIN UserWaves UW ON U.Id = UW.UserId
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    WHERE UW.WaveId = @WaveId AND UW.IsActive = 1
                    ORDER BY U.UserName", new { WaveId = waveId.Value })).ToList();

                attendanceData = (await conn.QueryAsync<dynamic>(@"
                    SELECT UA.UserId, UA.SessionId, UA.IsPresent, UA.CheckInTime, UA.CheckOutTime
                    FROM UserAttendance UA
                    JOIN AttendanceSessions S ON UA.SessionId = S.Id
                    WHERE S.WaveId = @WaveId", new { WaveId = waveId.Value })).ToList();
            }

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Attendance Summary");
                
                worksheet.Cell(1, 1).Value = $"Attendance Matrix: {title}";
                worksheet.Cell(1, 1).Style.Font.Bold = true;
                worksheet.Cell(1, 1).Style.Font.FontSize = 14;

                var headerRow = 3;
                worksheet.Cell(headerRow, 1).Value = "Name / الاسم";
                worksheet.Cell(headerRow, 2).Value = "Code / الكود";
                worksheet.Cell(headerRow, 3).Value = "Branch / الفرع";

                int colIdx = 4;
                foreach (var s in sessions)
                {
                    worksheet.Cell(headerRow, colIdx).Value = $"{s.SessionName}\n({s.SessionDate:dd/MM})";
                    worksheet.Cell(headerRow, colIdx).Style.Alignment.WrapText = true;
                    colIdx++;
                }
                worksheet.Cell(headerRow, colIdx).Value = "Total Present / إجمالي الحضور";

                var headerRange = worksheet.Range(headerRow, 1, headerRow, colIdx);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                int rowIdx = 4;
                foreach (var st in trainees)
                {
                    worksheet.Cell(rowIdx, 1).SetValue(st.UserName?.ToString() ?? "");
                    worksheet.Cell(rowIdx, 2).SetValue(st.UserCode?.ToString() ?? "");
                    worksheet.Cell(rowIdx, 3).SetValue(st.BranchName?.ToString() ?? "");

                    int presentCount = 0;
                    int currentCol = 4;
                    foreach (var s in sessions)
                    {
                        var att = attendanceData.FirstOrDefault(a => a.UserId == st.Id && a.SessionId == s.Id);
                        bool isPresent = att != null && att.IsPresent != null && (att.IsPresent.ToString() == "True" || att.IsPresent.ToString() == "1");
                        
                        var cell = worksheet.Cell(rowIdx, currentCol);
                        
                        if (isPresent) {
                            string timeInfo = "";
                            if (!string.IsNullOrEmpty(att.CheckInTime) || !string.IsNullOrEmpty(att.CheckOutTime))
                            {
                                timeInfo = $"\n(In: {att.CheckInTime ?? "-"}, Out: {att.CheckOutTime ?? "-"})";
                            }
                            cell.SetValue($"Present{timeInfo}");
                            cell.Style.Font.FontColor = XLColor.Green;
                            presentCount++;
                        } else {
                            cell.SetValue("Absent");
                            cell.Style.Font.FontColor = XLColor.Red;
                        }
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        cell.Style.Alignment.WrapText = true;
                        currentCol++;
                    }
                    worksheet.Cell(rowIdx, currentCol).SetValue(presentCount);
                    worksheet.Cell(rowIdx, currentCol).Style.Font.Bold = true;
                    rowIdx++;
                }

                worksheet.Columns().AdjustToContents();
                worksheet.Column(1).Width = 30;

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 
                        $"Attendance_{title}_{DateTime.Now:yyyyMMdd}.xlsx");
                }
            }
        }

        public async Task<IActionResult> TakeAttendance(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var session = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT S.*, 
                       TW.WaveName,
                       C.Name as CompanyName
                FROM AttendanceSessions S
                LEFT JOIN TrainingWaves TW ON S.WaveId = TW.Id
                LEFT JOIN Companies C ON S.CompanyId = C.Id
                WHERE S.Id = @Id", new { Id = sessionId });
            
            if (session == null) return NotFound();

            IEnumerable<dynamic> users;

            if (session.CompanyId != null)
            {
                users = await conn.QueryAsync<dynamic>(@"
                    SELECT CONCAT('CT_', U.Id) as Id, U.FullName as UserName, U.Email, U.UserCode, U.BranchName as BranchName,
                           ISNULL(UA.IsPresent, 0) as IsPresent,
                           UA.CheckInTime, UA.CheckOutTime
                    FROM CompanyTrainees U
                    LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.CompanyTraineeId = U.Id
                    WHERE U.CompanyId = @CompanyId", 
                    new { SessionId = sessionId, CompanyId = (int)session.CompanyId });
            }
            else
            {
                users = await conn.QueryAsync<dynamic>(@"
                    SELECT U.Id, U.UserName, U.Email, U.UserCode, B.BranchName,
                           ISNULL(UA.IsPresent, 0) as IsPresent,
                           UA.CheckInTime, UA.CheckOutTime
                    FROM AspNetUsers U
                    JOIN UserWaves UW ON U.Id = UW.UserId
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.UserId = U.Id
                    WHERE UW.WaveId = @WaveId AND UW.IsActive = 1", 
                    new { SessionId = sessionId, WaveId = (int)session.WaveId });
            }

            ViewBag.Session = session;
            return View(users);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateDetailedAttendance(int sessionId, string userId, bool isPresent, string checkInTime, string checkOutTime)
        {
            try 
            {
                using var conn = new SqlConnection(_connectionString);
                var recordedBy = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                
                // Apply defensive schema adjustments just in case
                await conn.ExecuteAsync(@"
                    IF COL_LENGTH('UserAttendance', 'CompanyTraineeId') IS NULL
                    BEGIN
                        ALTER TABLE UserAttendance ADD CompanyTraineeId INT NULL;
                    END
                    
                    ALTER TABLE UserAttendance ALTER COLUMN UserId NVARCHAR(450) NULL;
                ");

            bool isCompanyTrainee = userId != null && userId.StartsWith("CT_");
            int? companyTraineeId = isCompanyTrainee ? int.Parse(userId.Substring(3)) : (int?)null;
            string actualUserId = isCompanyTrainee ? null : userId;

            int? existing = null;
            if (isCompanyTrainee)
            {
                existing = await conn.QueryFirstOrDefaultAsync<int?>("SELECT Id FROM UserAttendance WHERE SessionId = @Sid AND CompanyTraineeId = @Ctid", new { Sid = sessionId, Ctid = companyTraineeId });
            }
            else
            {
                existing = await conn.QueryFirstOrDefaultAsync<int?>("SELECT Id FROM UserAttendance WHERE SessionId = @Sid AND UserId = @Uid", new { Sid = sessionId, Uid = actualUserId });
            }

            if (existing.HasValue)
            {
                await conn.ExecuteAsync(@"
                    UPDATE UserAttendance 
                    SET IsPresent = @IsPresent, 
                        CheckInTime = @CheckIn, 
                        CheckOutTime = @CheckOut, 
                        RecordedAt = GETDATE(), 
                        RecordedBy = @By 
                    WHERE Id = @Id", 
                    new { IsPresent = isPresent, CheckIn = checkInTime, CheckOut = checkOutTime, By = recordedBy, Id = existing.Value });
            }
            else
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO UserAttendance (SessionId, UserId, CompanyTraineeId, IsPresent, CheckInTime, CheckOutTime, RecordedBy) 
                    VALUES (@Sid, @Uid, @Ctid, @IsPresent, @CheckIn, @CheckOut, @By)", 
                    new { Sid = sessionId, Uid = actualUserId, Ctid = companyTraineeId, IsPresent = isPresent, CheckIn = checkInTime, CheckOut = checkOutTime, By = recordedBy });
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



