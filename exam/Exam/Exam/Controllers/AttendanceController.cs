using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
        private readonly Exam.Services.IN8nService _n8nService;
        private readonly Microsoft.AspNetCore.SignalR.IHubContext<Exam.Hubs.NotificationHub> _hubContext;

        public AttendanceController(
            IConfiguration configuration,
            Exam.Services.IN8nService n8nService,
            Microsoft.AspNetCore.SignalR.IHubContext<Exam.Hubs.NotificationHub> hubContext)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")!;
            _n8nService = n8nService;
            _hubContext = hubContext;
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
                    IF EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees WHERE SessionId = @SessionId)
                    BEGIN
                        SELECT CONCAT('CT_', U.Id) as Id, U.FullName as UserName, U.Email, U.UserCode, U.BranchName as BranchName,
                               ISNULL(UA.IsPresent, 0) as IsPresent,
                               UA.CheckInTime, UA.CheckOutTime
                        FROM dbo.CompanySessionTrainees CST
                        JOIN CompanyTrainees U ON CST.CompanyTraineeId = U.Id
                        LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.CompanyTraineeId = U.Id
                        WHERE CST.SessionId = @SessionId
                        ORDER BY U.FullName
                    END
                    ELSE
                    BEGIN
                        SELECT CONCAT('CT_', U.Id) as Id, U.FullName as UserName, U.Email, U.UserCode, U.BranchName as BranchName,
                               ISNULL(UA.IsPresent, 0) as IsPresent,
                               UA.CheckInTime, UA.CheckOutTime
                        FROM CompanyTrainees U
                        LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.CompanyTraineeId = U.Id
                        WHERE U.CompanyId = @CompanyId
                        ORDER BY U.FullName
                    END", 
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
            try
            {
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TrainingWaves') AND name = 'IsActive')
                    BEGIN
                        ALTER TABLE dbo.TrainingWaves ADD IsActive BIT NOT NULL CONSTRAINT DF_TrainingWaves_IsActive DEFAULT 1;
                    END");
            }
            catch { }

            var waves = await conn.QueryAsync<dynamic>("SELECT Id, WaveName, StartDate, EndDate, ISNULL(IsOnline, 0) AS IsOnline, Mode, ISNULL(IsActive, 1) AS IsActive FROM TrainingWaves ORDER BY Id DESC");
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
                    SELECT S.*, 
                           (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1) as PresentCount,
                           CASE 
                               WHEN EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees CST WHERE CST.SessionId = S.Id)
                               THEN (SELECT COUNT(*) FROM dbo.CompanySessionTrainees CST WHERE CST.SessionId = S.Id)
                               ELSE (SELECT COUNT(*) FROM CompanyTrainees CU WHERE CU.CompanyId = S.CompanyId)
                           END as TotalTraineesCount,
                           (SELECT COUNT(*) FROM dbo.CompanySessionTrainees CST WHERE CST.SessionId = S.Id) as SpecificAssignedCount
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
        public async Task<IActionResult> CreateSession(int? waveId, int? companyId, DateTime sessionDate, string sessionName, TimeSpan? startTime, TimeSpan? endTime)
        {
            using var conn = new SqlConnection(_connectionString);
            if (companyId.HasValue)
            {
                await conn.ExecuteAsync("INSERT INTO AttendanceSessions (CompanyId, SessionDate, SessionName, StartTime, EndTime) VALUES (@CompanyId, @Date, @Name, @Start, @End)", 
                    new { CompanyId = companyId, Date = sessionDate, Name = sessionName, Start = startTime, End = endTime });
                return RedirectToAction(nameof(ManageSessions), new { companyId });
            }
            else
            {
                await conn.ExecuteAsync("INSERT INTO AttendanceSessions (WaveId, SessionDate, SessionName, StartTime, EndTime) VALUES (@WaveId, @Date, @Name, @Start, @End)", 
                    new { WaveId = waveId, Date = sessionDate, Name = sessionName, Start = startTime, End = endTime });
                return RedirectToAction(nameof(ManageSessions), new { waveId });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateSession(int sessionId, DateTime sessionDate, string sessionName, TimeSpan? startTime, TimeSpan? endTime)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(@"
                UPDATE AttendanceSessions 
                SET SessionDate = @Date, SessionName = @Name, StartTime = @Start, EndTime = @End 
                WHERE Id = @Id", 
                new { Id = sessionId, Date = sessionDate, Name = sessionName, Start = startTime, End = endTime });
            
            return Json(new { success = true, message = "تم تعديل المحاضرة بنجاح" });
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

            bool isLocked = false;
            string lockReason = "";
            DateTime now = DateTime.Now;

            if (session.StartTime != null && session.EndTime != null)
            {
                TimeSpan sTime = (TimeSpan)session.StartTime;
                TimeSpan eTime = (TimeSpan)session.EndTime;
                DateTime sessionDate = ((DateTime)session.SessionDate).Date;

                DateTime sessionStart = sessionDate.Add(sTime);
                DateTime sessionEnd = sessionDate.Add(eTime);

                if (now < sessionStart)
                {
                    isLocked = true;
                    lockReason = $"المحاضرة لم تبدأ بعد. مسموح بأخذ الغياب فقط بين الساعة {sTime:hh\\:mm} والبيانات {eTime:hh\\:mm} بتاريخ {sessionDate:dd/MM/yyyy}.";
                }
                else if (now > sessionEnd)
                {
                    isLocked = true;
                    lockReason = $"انتهى وقت المحاضرة المحدد. كان مسموحاً بأخذ الغياب فقط بين الساعة {sTime:hh\\:mm} و {eTime:hh\\:mm} بتاريخ {sessionDate:dd/MM/yyyy}.";
                }
            }

            ViewBag.IsLocked = isLocked;
            ViewBag.LockReason = lockReason;

            IEnumerable<dynamic> users;

            if (session.CompanyId != null)
            {
                users = await conn.QueryAsync<dynamic>(@"
                    IF EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees WHERE SessionId = @SessionId)
                    BEGIN
                        SELECT CONCAT('CT_', U.Id) as Id, U.FullName as UserName, U.Email, U.UserCode, U.BranchName as BranchName,
                               ISNULL(UA.IsPresent, 0) as IsPresent,
                               UA.CheckInTime, UA.CheckOutTime
                        FROM dbo.CompanySessionTrainees CST
                        JOIN CompanyTrainees U ON CST.CompanyTraineeId = U.Id
                        LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.CompanyTraineeId = U.Id
                        WHERE CST.SessionId = @SessionId
                        ORDER BY U.FullName
                    END
                    ELSE
                    BEGIN
                        SELECT CONCAT('CT_', U.Id) as Id, U.FullName as UserName, U.Email, U.UserCode, U.BranchName as BranchName,
                               ISNULL(UA.IsPresent, 0) as IsPresent,
                               UA.CheckInTime, UA.CheckOutTime
                        FROM CompanyTrainees U
                        LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.CompanyTraineeId = U.Id
                        WHERE U.CompanyId = @CompanyId
                        ORDER BY U.FullName
                    END", 
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

                var session = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT SessionDate, StartTime, EndTime FROM AttendanceSessions WHERE Id = @Id", new { Id = sessionId });
                if (session != null && session.StartTime != null && session.EndTime != null)
                {
                    TimeSpan sTime = (TimeSpan)session.StartTime;
                    TimeSpan eTime = (TimeSpan)session.EndTime;
                    DateTime sessionDate = ((DateTime)session.SessionDate).Date;

                    DateTime sessionStart = sessionDate.Add(sTime);
                    DateTime sessionEnd = sessionDate.Add(eTime);
                    DateTime now = DateTime.Now;

                    if (now < sessionStart || now > sessionEnd)
                    {
                        return Json(new { success = false, message = $"عفواً، تسجيل الغياب مغلق الآن! الموعد المسموح فقط من {sTime:hh\\:mm} إلى {eTime:hh\\:mm} بتاريخ {sessionDate:dd/MM/yyyy}." });
                    }
                }
                
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
                return Json(new { success = false, message = "حدث خطأ: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetSessionTrainees(int sessionId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                var session = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT S.Id, S.SessionName, S.SessionDate, S.StartTime, S.EndTime, S.CompanyId, C.Name AS CompanyName
                    FROM AttendanceSessions S
                    LEFT JOIN Companies C ON S.CompanyId = C.Id
                    WHERE S.Id = @Id", new { Id = sessionId });

                if (session == null) return NotFound(new { success = false, message = "Session not found." });

                int? companyId = (int?)session.CompanyId;
                if (!companyId.HasValue) return BadRequest(new { success = false, message = "This session does not belong to a company." });

                var assigned = (await conn.QueryAsync<dynamic>(@"
                    SELECT CST.Id AS AssignmentId, CT.Id AS TraineeId, CT.FullName, CT.UserCode, CT.BranchName, CT.Email, CT.Phone, CST.AssignedAt,
                           ISNULL(CST.ApprovalStatus, 'Approved') AS ApprovalStatus,
                           CST.RejectionReason,
                           CST.ReplacedWithTraineeId,
                           CST.ActionedAt
                    FROM dbo.CompanySessionTrainees CST
                    JOIN CompanyTrainees CT ON CST.CompanyTraineeId = CT.Id
                    WHERE CST.SessionId = @SessionId
                    ORDER BY CT.FullName", new { SessionId = sessionId })).ToList();

                return Json(new
                {
                    success = true,
                    session = new
                    {
                        id = (int)session.Id,
                        name = (string)session.SessionName,
                        date = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd"),
                        time = session.StartTime != null ? $"{((TimeSpan)session.StartTime):hh\\:mm} - {((TimeSpan)session.EndTime):hh\\:mm}" : "",
                        companyId = companyId.Value,
                        companyName = (string)(session.CompanyName ?? "")
                    },
                    assignedCount = assigned.Count,
                    assignedTrainees = assigned,
                    availablePool = new List<dynamic>()
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> SearchSessionCandidates(int sessionId, string query)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var session = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT S.Id, S.CompanyId
                    FROM AttendanceSessions S
                    WHERE S.Id = @Id", new { Id = sessionId });

                if (session == null || session.CompanyId == null)
                    return Json(new { success = false, message = "الجلسة غير موجودة أو غير مرتبطة بشركة." });

                int compId = (int)session.CompanyId;
                string raw = (query ?? "").Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return Json(new { success = true, candidates = new List<dynamic>() });
                }

                // Parse multi-code delimiters: comma, pipe, semicolon, newline
                var delimiters = new[] { ',', '|', '،', ';', '\n', '\r' };
                var tokens = raw.Split(delimiters, StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim())
                                .Where(t => !string.IsNullOrEmpty(t))
                                .Distinct()
                                .ToList();

                bool isMultiCode = tokens.Count > 1 || raw.Contains(",") || raw.Contains("|") || raw.Contains("،");

                string whereClause;
                object queryParams;

                if (isMultiCode)
                {
                    whereClause = "WHERE (c.UserCode IN @Codes OR c.FullName IN @Codes)";
                    queryParams = new { CompanyId = compId, SessionId = sessionId, Codes = tokens };
                }
                else
                {
                    whereClause = @"WHERE (c.FullName LIKE @Q OR c.UserCode LIKE @Q OR c.BranchName LIKE @Q OR c.Email LIKE @Q)
                                      AND ( (c.FullName IS NOT NULL AND c.FullName <> '' AND c.FullName <> 'Trainee') OR (c.UserCode IS NOT NULL AND c.UserCode <> '--') )";
                    queryParams = new { CompanyId = compId, SessionId = sessionId, Q = $"%{tokens[0]}%" };
                }

                string sql = $@"
                    ;WITH Combined AS (
                        SELECT 
                            u.Id AS UserId,
                            ct.Id AS CompanyTraineeId,
                            COALESCE(NULLIF(ct.FullName, ''), NULLIF(u.FullName, ''), NULLIF(u.UserName, ''), N'Trainee') AS FullName,
                            COALESCE(NULLIF(ct.UserCode, ''), NULLIF(u.UserCode, ''), N'--') AS UserCode,
                            COALESCE(NULLIF(ct.BranchName, ''), NULLIF(b.BranchName, ''), N'بدون فرع') AS BranchName,
                            COALESCE(NULLIF(ct.Email, ''), NULLIF(u.Email, ''), N'--') AS Email,
                            COALESCE(NULLIF(ct.Phone, ''), NULLIF(u.PhoneNumber, ''), N'--') AS Phone,
                            COALESCE(NULLIF(ct.JobTitle, ''), NULLIF(u.UserRoleCustom, ''), N'Trainee') AS JobTitle,
                            ISNULL(b.BypassApproval, 0) AS IsBranchExempt
                        FROM AspNetUsers u WITH (NOLOCK)
                        LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                        LEFT JOIN CompanyTrainees ct WITH (NOLOCK) ON ct.CompanyId = @CompanyId AND (ct.UserCode = u.UserCode OR (ct.Email = u.Email AND ct.Email <> '--'))
                        WHERE (u.IsActive = 1 OR ct.Id IS NOT NULL)

                        UNION

                        SELECT 
                            CAST(NULL AS NVARCHAR(450)) AS UserId,
                            ct.Id AS CompanyTraineeId,
                            ISNULL(NULLIF(ct.FullName, ''), N'Trainee') AS FullName,
                            ISNULL(NULLIF(ct.UserCode, ''), N'--') AS UserCode,
                            ISNULL(NULLIF(ct.BranchName, ''), N'بدون فرع') AS BranchName,
                            ISNULL(NULLIF(ct.Email, ''), N'--') AS Email,
                            ISNULL(NULLIF(ct.Phone, ''), N'--') AS Phone,
                            ISNULL(NULLIF(ct.JobTitle, ''), N'Trainee') AS JobTitle,
                            ISNULL(b.BypassApproval, 0) AS IsBranchExempt
                        FROM CompanyTrainees ct WITH (NOLOCK)
                        LEFT JOIN Branches b WITH (NOLOCK) ON b.BranchName = ct.BranchName
                        WHERE ct.CompanyId = @CompanyId
                          AND NOT EXISTS (
                              SELECT 1 FROM AspNetUsers u2 WITH (NOLOCK) 
                              WHERE u2.UserCode = ct.UserCode OR (u2.Email = ct.Email AND u2.Email <> '--')
                          )
                    )
                    SELECT TOP 60 
                        c.UserId,
                        c.CompanyTraineeId,
                        c.FullName,
                        c.UserCode,
                        c.BranchName,
                        c.Email,
                        c.Phone,
                        c.JobTitle,
                        c.IsBranchExempt,
                        CASE 
                            WHEN c.CompanyTraineeId IS NOT NULL THEN CONCAT('CT_', c.CompanyTraineeId)
                            ELSE CONCAT('U_', c.UserId)
                        END AS CandidateKey
                    FROM Combined c
                    {whereClause}
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.CompanySessionTrainees cst WITH (NOLOCK)
                          WHERE cst.SessionId = @SessionId
                            AND (
                                (c.CompanyTraineeId IS NOT NULL AND cst.CompanyTraineeId = c.CompanyTraineeId)
                                OR (c.UserId IS NOT NULL AND cst.UserId = c.UserId)
                            )
                      )
                    ORDER BY c.FullName";

                var candidatesRaw = (await conn.QueryAsync<dynamic>(sql, queryParams)).ToList();

                var candidates = candidatesRaw.Select(c => {
                    var d = (IDictionary<string, object>)c;
                    string userId = d.ContainsKey("UserId") && d["UserId"] != null ? d["UserId"].ToString() : null;
                    int? companyTraineeId = d.ContainsKey("CompanyTraineeId") && d["CompanyTraineeId"] != null && int.TryParse(d["CompanyTraineeId"].ToString(), out int cti) ? cti : (int?)null;
                    string fullName = d.ContainsKey("FullName") && d["FullName"] != null && !string.IsNullOrWhiteSpace(d["FullName"].ToString()) ? d["FullName"].ToString() : "Trainee";
                    string userCode = d.ContainsKey("UserCode") && d["UserCode"] != null && !string.IsNullOrWhiteSpace(d["UserCode"].ToString()) ? d["UserCode"].ToString() : "--";
                    string branchName = d.ContainsKey("BranchName") && d["BranchName"] != null && !string.IsNullOrWhiteSpace(d["BranchName"].ToString()) ? d["BranchName"].ToString() : "بدون فرع";
                    string email = d.ContainsKey("Email") && d["Email"] != null ? d["Email"].ToString() : "--";
                    string phone = d.ContainsKey("Phone") && d["Phone"] != null ? d["Phone"].ToString() : "--";
                    string jobTitle = d.ContainsKey("JobTitle") && d["JobTitle"] != null && !string.IsNullOrWhiteSpace(d["JobTitle"].ToString()) ? d["JobTitle"].ToString() : "Trainee";
                    bool isBranchExempt = d.ContainsKey("IsBranchExempt") && d["IsBranchExempt"] != null && (d["IsBranchExempt"].ToString() == "1" || d["IsBranchExempt"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
                    string candidateKey = d.ContainsKey("CandidateKey") && d["CandidateKey"] != null ? d["CandidateKey"].ToString() : (companyTraineeId.HasValue ? $"CT_{companyTraineeId.Value}" : $"U_{userId}");

                    return new {
                        userId = userId,
                        companyTraineeId = companyTraineeId,
                        fullName = fullName,
                        userCode = userCode,
                        branchName = branchName,
                        email = email,
                        phone = phone,
                        jobTitle = jobTitle,
                        isBranchExempt = isBranchExempt,
                        candidateKey = candidateKey
                    };
                }).ToList();

                return Json(new { success = true, isMultiCode = isMultiCode, count = candidates.Count, candidates = candidates });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AssignTraineesToSession([FromBody] SessionTraineesAssignRequest req)
        {
            if (req == null || req.SessionId <= 0)
            {
                return Json(new { success = false, message = "بيانات الطلب غير صالحة." });
            }

            var keys = new HashSet<string>(req.CandidateKeys ?? new List<string>());
            if (req.TraineeIds != null)
            {
                foreach (var tid in req.TraineeIds)
                {
                    if (tid > 0) keys.Add($"CT_{tid}");
                }
            }

            if (!keys.Any())
            {
                return Json(new { success = false, message = "لم يتم تحديد أي متدربين." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var session = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT S.Id, S.SessionName, S.SessionDate, S.StartTime, S.EndTime, S.CompanyId, C.Name AS CompanyName
                    FROM AttendanceSessions S
                    LEFT JOIN Companies C ON S.CompanyId = C.Id
                    WHERE S.Id = @Id", new { Id = req.SessionId });

                if (session == null) return NotFound(new { success = false, message = "الجلسة غير موجودة." });

                var assignedBy = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                int count = 0;
                var assignedTraineePayloads = new List<dynamic>();
                var pendingBranchGroups = new Dictionary<int, (string BranchName, int Count)>();

                foreach (var rawKey in keys)
                {
                    string key = rawKey.Trim();
                    int? companyTraineeId = null;
                    string? targetUserId = null;
                    string fullName = "";
                    string userCode = "";
                    string email = "";
                    string phone = "";
                    string branchName = "بدون فرع";
                    int? branchId = null;
                    bool isBranchExempt = false;

                    if (key.StartsWith("U_"))
                    {
                        targetUserId = key.Substring(2);
                        var u = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                            SELECT u.Id, u.FullName, u.UserName, u.UserCode, u.Email, u.PhoneNumber, u.UserRoleCustom,
                                   u.BranchId, b.BranchName, ISNULL(b.BypassApproval, 0) AS IsBranchExempt
                            FROM AspNetUsers u
                            LEFT JOIN Branches b ON u.BranchId = b.Id
                            WHERE u.Id = @UserId", new { UserId = targetUserId });

                        if (u != null)
                        {
                            string rawFn = (string)(u.FullName ?? "");
                            fullName = !string.IsNullOrWhiteSpace(rawFn) ? rawFn : (string)(u.UserName ?? "Trainee");
                            userCode = (string)(u.UserCode ?? "--");
                            email = (string)(u.Email ?? "");
                            phone = (string)(u.PhoneNumber ?? "");
                            branchName = (string)(u.BranchName ?? "بدون فرع");
                            branchId = (int?)u.BranchId;
                            isBranchExempt = (int)u.IsBranchExempt == 1;

                            // Ensure record exists in CompanyTrainees and pull any missing details
                            var ctInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                                SELECT TOP 1 Id, FullName, BranchName, Phone, JobTitle FROM CompanyTrainees 
                                WHERE CompanyId = @CompanyId 
                                  AND ((UserCode = @Code AND UserCode <> '--') OR (Email = @Email AND Email <> '--'))",
                                new { CompanyId = (int)session.CompanyId, Code = userCode, Email = email });

                            if (ctInfo != null)
                            {
                                companyTraineeId = (int)ctInfo.Id;
                                string ctFn = (string)(ctInfo.FullName ?? "");
                                if ((string.IsNullOrWhiteSpace(fullName) || fullName == "Trainee") && !string.IsNullOrWhiteSpace(ctFn))
                                {
                                    fullName = ctFn;
                                }
                                string ctBr = (string)(ctInfo.BranchName ?? "");
                                if ((string.IsNullOrWhiteSpace(branchName) || branchName == "بدون فرع") && !string.IsNullOrWhiteSpace(ctBr))
                                {
                                    branchName = ctBr;
                                }
                            }
                            else
                            {
                                companyTraineeId = await conn.QueryFirstOrDefaultAsync<int>(@"
                                    INSERT INTO CompanyTrainees (CompanyId, FullName, UserCode, JobTitle, BranchName, Email, Phone, CreatedAt)
                                    VALUES (@CompanyId, @FullName, @UserCode, @JobTitle, @BranchName, @Email, @Phone, GETDATE());
                                    SELECT CAST(SCOPE_IDENTITY() AS INT);",
                                    new {
                                        CompanyId = (int)session.CompanyId,
                                        FullName = fullName,
                                        UserCode = userCode,
                                        JobTitle = (string)(u.UserRoleCustom ?? "Trainee"),
                                        BranchName = branchName,
                                        Email = email,
                                        Phone = phone
                                    });
                            }
                        }
                    }
                    else if (key.StartsWith("CT_"))
                    {
                        if (int.TryParse(key.Substring(3), out int ctId))
                        {
                            companyTraineeId = ctId;
                            var ct = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                                SELECT CT.Id, CT.FullName, CT.UserCode, CT.BranchName, CT.Email, CT.Phone,
                                       b.Id AS BranchId, ISNULL(b.BypassApproval, 0) AS IsBranchExempt
                                FROM CompanyTrainees CT
                                LEFT JOIN Branches b ON b.BranchName = CT.BranchName
                                WHERE CT.Id = @Id", new { Id = ctId });

                            if (ct != null)
                            {
                                fullName = (string)(ct.FullName ?? "");
                                userCode = (string)(ct.UserCode ?? "--");
                                email = (string)(ct.Email ?? "");
                                phone = (string)(ct.Phone ?? "");
                                branchName = (string)(ct.BranchName ?? "بدون فرع");
                                branchId = (int?)ct.BranchId;
                                isBranchExempt = (int)ct.IsBranchExempt == 1;

                                targetUserId = await conn.QueryFirstOrDefaultAsync<string>(@"
                                    SELECT Id FROM AspNetUsers 
                                    WHERE (UserCode = @Code AND UserCode <> '--') OR (Email = @Email AND Email <> '--')",
                                    new { Code = userCode, Email = email });
                            }
                        }
                    }

                    if (!companyTraineeId.HasValue) continue;

                    bool isExempt = req.BypassApproval || isBranchExempt;
                    string status = isExempt ? "Approved" : "Pending";

                    int affected = await conn.ExecuteAsync(@"
                        IF NOT EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees WHERE SessionId = @SessionId AND (CompanyTraineeId = @TraineeId OR (UserId = @UserId AND @UserId IS NOT NULL)))
                        BEGIN
                            INSERT INTO dbo.CompanySessionTrainees (SessionId, CompanyTraineeId, UserId, AssignedAt, AssignedBy, ApprovalStatus)
                            VALUES (@SessionId, @TraineeId, @UserId, GETDATE(), @AssignedBy, @Status)
                        END", new { SessionId = req.SessionId, TraineeId = companyTraineeId.Value, UserId = targetUserId, AssignedBy = assignedBy, Status = status });

                    if (affected > 0)
                    {
                        count++;
                        assignedTraineePayloads.Add(new
                        {
                            id = companyTraineeId.Value,
                            userId = targetUserId,
                            fullName = fullName,
                            userCode = userCode,
                            email = email,
                            phone = phone,
                            branchName = branchName
                        });

                        if (!isExempt && branchId.HasValue)
                        {
                            int bId = branchId.Value;
                            if (pendingBranchGroups.ContainsKey(bId))
                                pendingBranchGroups[bId] = (branchName, pendingBranchGroups[bId].Count + 1);
                            else
                                pendingBranchGroups[bId] = (branchName, 1);
                        }

                        // In-App Notification to trainee
                        if (req.SendNotification && !string.IsNullOrEmpty(targetUserId))
                        {
                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.AppNotifications (UserId, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                                VALUES (@UserId, N'تم تسجيلك في تدريب شركة 🏢', CONCAT(N'تم تسجيلك لحضور جلسة: ', @SessionName, N' الخاصة بشركة: ', @CompanyName, N' بتاريخ: ', @SessionDate), '/Attendance/MyAttendance', 'CompanySessionAssigned', CAST(@SessionId AS NVARCHAR(50)), GETDATE())",
                                new {
                                    UserId = targetUserId,
                                    SessionName = (string)session.SessionName,
                                    CompanyName = (string)(session.CompanyName ?? "Company"),
                                    SessionDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd"),
                                    SessionId = req.SessionId
                                });
                        }
                    }
                }

                // Notify branch managers for non-exempt pending groups
                foreach (var kvp in pendingBranchGroups)
                {
                    int bId = kvp.Key;
                    var info = kvp.Value;
                    string title = $"ترشيح متدربين لسيشن شركة: {session.CompanyName} 🏢";
                    string message = $"تم ترشيح {info.Count} متدرب من فرع ({info.BranchName}) لحضور سيشن ({session.SessionName}) لشركة {session.CompanyName}. يرجى مراجعة المرشحين والموافقة أو الرفض أو الاستبدال.";
                    string linkUrl = $"/Attendance/ManageSessions?companyId={session.CompanyId}";

                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.AppNotifications (BranchId, Title, Message, LinkUrl, Type, ReferenceId, IsRead, CreatedAt)
                        VALUES (@BranchId, @Title, @Message, @LinkUrl, 'CompanySessionNomination', @ReferenceId, 0, GETDATE())",
                        new { BranchId = bId, Title = title, Message = message, LinkUrl = linkUrl, ReferenceId = session.Id.ToString() });
                }

                // 2. n8n Webhook Trigger (for email dispatch and automation workflows)
                if (req.SendEmailViaN8n && assignedTraineePayloads.Any())
                {
                    var webhookPayload = new
                    {
                        @event = "company_session_assignment",
                        sessionId = (int)session.Id,
                        sessionName = (string)session.SessionName,
                        sessionDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd"),
                        startTime = session.StartTime != null ? $"{((TimeSpan)session.StartTime):hh\\:mm}" : "09:00",
                        endTime = session.EndTime != null ? $"{((TimeSpan)session.EndTime):hh\\:mm}" : "12:00",
                        companyId = (int?)session.CompanyId,
                        companyName = (string)(session.CompanyName ?? ""),
                        trainees = assignedTraineePayloads
                    };

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _n8nService.TriggerCompanySessionNotificationAsync(webhookPayload);
                        }
                        catch { }
                    });
                }

                if (count == 0)
                {
                    return Json(new { success = false, message = "لم يتم تعيين أي متدرب (قد يكون المتدربون مسجلين بالفعل في هذه الجلسة أو المفاتيح غير صالحة)." });
                }

                await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تعيين متدربين لجلسة تدريب الشركة.");
                return Json(new { success = true, count = count, message = $"تم تعيين {count} متدرب بنجاح وإرسال الإشعارات وبيانات الـ API." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RemoveTraineeFromSession(int sessionId, int traineeId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    "DELETE FROM dbo.CompanySessionTrainees WHERE SessionId = @SessionId AND CompanyTraineeId = @TraineeId",
                    new { SessionId = sessionId, TraineeId = traineeId });
                return Json(new { success = true, message = "تم حذف المتدرب من هذه السيشن بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ImportSessionTraineesFromExcel(int sessionId, Microsoft.AspNetCore.Http.IFormFile excelFile, bool bypassApproval = false)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "يرجى تحديد ملف إكسيل صالح." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var session = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT S.Id, S.SessionName, S.SessionDate, S.StartTime, S.EndTime, S.CompanyId, C.Name AS CompanyName
                    FROM AttendanceSessions S
                    LEFT JOIN Companies C ON S.CompanyId = C.Id
                    WHERE S.Id = @Id", new { Id = sessionId });

                if (session == null || session.CompanyId == null)
                {
                    return Json(new { success = false, message = "الجلسة المحددة غير صالحة." });
                }

                int companyId = (int)session.CompanyId;
                var assignedBy = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                int importedCount = 0;
                var newTraineeIds = new List<int>();

                using (var stream = new MemoryStream())
                {
                    await excelFile.CopyToAsync(stream);
                    using (var workbook = new XLWorkbook(stream))
                    {
                        var worksheet = workbook.Worksheet(1);
                        var rows = worksheet.RangeUsed().RowsUsed().Skip(1);

                        foreach (var row in rows)
                        {
                            string fullName = row.Cell(1).GetString().Trim();
                            string userCode = row.Cell(2).GetString().Trim();
                            string jobTitle = row.Cell(3).GetString().Trim();
                            string branchName = row.Cell(4).GetString().Trim();
                            string email = row.Cell(5).GetString().Trim();
                            string phone = row.Cell(6).GetString().Trim();

                            if (string.IsNullOrWhiteSpace(fullName) && string.IsNullOrWhiteSpace(userCode)) continue;

                            int traineeId = await conn.QueryFirstOrDefaultAsync<int>(
                                "SELECT Id FROM CompanyTrainees WHERE CompanyId = @CompanyId AND (UserCode = @Code OR (FullName = @Name AND FullName <> ''))",
                                new { CompanyId = companyId, Code = userCode, Name = fullName });

                            if (traineeId == 0)
                            {
                                traineeId = await conn.QuerySingleAsync<int>(@"
                                    INSERT INTO CompanyTrainees (CompanyId, FullName, UserCode, JobTitle, BranchName, Email, Phone, CreatedAt)
                                    VALUES (@CompanyId, @FullName, @UserCode, @JobTitle, @BranchName, @Email, @Phone, GETDATE());
                                    SELECT CAST(SCOPE_IDENTITY() AS INT);",
                                    new {
                                        CompanyId = companyId,
                                        FullName = fullName,
                                        UserCode = string.IsNullOrEmpty(userCode) ? "--" : userCode,
                                        JobTitle = string.IsNullOrEmpty(jobTitle) ? "Trainee" : jobTitle,
                                        BranchName = string.IsNullOrEmpty(branchName) ? "Global" : branchName,
                                        Email = string.IsNullOrEmpty(email) ? "--" : email,
                                        Phone = string.IsNullOrEmpty(phone) ? "--" : phone
                                    });
                            }

                            // Check branch exemption
                            var branchInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
                                "SELECT Id, ISNULL(BypassApproval, 0) AS BypassApproval FROM Branches WHERE BranchName = @BranchName",
                                new { BranchName = branchName });

                            bool isExempt = bypassApproval || (branchInfo != null && (int)branchInfo.BypassApproval == 1);
                            string status = isExempt ? "Approved" : "Pending";

                            int affected = await conn.ExecuteAsync(@"
                                IF NOT EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees WHERE SessionId = @SessionId AND CompanyTraineeId = @TraineeId)
                                BEGIN
                                    INSERT INTO dbo.CompanySessionTrainees (SessionId, CompanyTraineeId, AssignedAt, AssignedBy, ApprovalStatus)
                                    VALUES (@SessionId, @TraineeId, GETDATE(), @AssignedBy, @Status)
                                END", new { SessionId = sessionId, TraineeId = traineeId, AssignedBy = assignedBy, Status = status });

                            if (affected > 0)
                            {
                                importedCount++;
                                newTraineeIds.Add(traineeId);
                            }
                        }
                    }
                }

                if (newTraineeIds.Any())
                {
                    var trainees = (await conn.QueryAsync<dynamic>(
                        "SELECT Id, FullName, UserCode, BranchName, Email, Phone FROM CompanyTrainees WHERE Id IN @Ids",
                        new { Ids = newTraineeIds })).ToList();

                    var webhookPayload = new
                    {
                        @event = "company_session_assignment_excel",
                        sessionId = sessionId,
                        sessionName = (string)session.SessionName,
                        sessionDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd"),
                        companyName = (string)(session.CompanyName ?? ""),
                        trainees = trainees
                    };

                    _ = Task.Run(async () =>
                    {
                        try { await _n8nService.TriggerCompanySessionNotificationAsync(webhookPayload); } catch { }
                    });
                }

                return Json(new { success = true, message = $"تم استيراد وتسكين {importedCount} متدرب في هذه السيشن بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "فشل استيراد الملف: " + ex.Message });
            }
        }
    }

    public class SessionTraineesAssignRequest
    {
        public int SessionId { get; set; }
        public List<int> TraineeIds { get; set; } = new List<int>();
        public List<string> CandidateKeys { get; set; } = new List<string>();
        public bool SendNotification { get; set; } = true;
        public bool SendEmailViaN8n { get; set; } = true;
        public bool BypassApproval { get; set; } = false;
    }
}



