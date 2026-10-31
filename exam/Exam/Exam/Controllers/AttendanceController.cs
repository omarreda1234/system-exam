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
using System.Security.Claims;
using ClosedXML.Excel;
using System.IO;
using Exam.Services.RabbitMQ;

namespace Exam.Controllers
{
    [Authorize(Roles = "Admin,HR,Reception,SoftSkills Specialist")]
    public class AttendanceController : Controller
    {
        private readonly string _connectionString;
        private readonly Exam.Services.IN8nService _n8nService;
        private readonly Microsoft.AspNetCore.SignalR.IHubContext<Exam.Hubs.NotificationHub> _hubContext;
        private readonly Exam.Services.IEmailSender? _emailSender;
        private readonly IRabbitMqEmailProducer? _rabbitMqProducer;

        public AttendanceController(
            IConfiguration configuration,
            Exam.Services.IN8nService n8nService,
            Microsoft.AspNetCore.SignalR.IHubContext<Exam.Hubs.NotificationHub> hubContext,
            Exam.Services.IEmailSender? emailSender = null,
            IRabbitMqEmailProducer? rabbitMqProducer = null)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")!;
            _n8nService = n8nService;
            _hubContext = hubContext;
            _emailSender = emailSender;
            _rabbitMqProducer = rabbitMqProducer;
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
                        WHERE CST.SessionId = @SessionId AND (CST.ApprovalStatus = 'Approved' OR CST.ApprovalStatus IS NULL)
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
                      AND (UW.ApprovalStatus = 'Approved' OR UW.ApprovalStatus IS NULL)
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
                               THEN (SELECT COUNT(*) FROM dbo.CompanySessionTrainees CST WHERE CST.SessionId = S.Id AND (CST.ApprovalStatus = 'Approved' OR CST.ApprovalStatus IS NULL))
                               ELSE (SELECT COUNT(*) FROM CompanyTrainees CU WHERE CU.CompanyId = S.CompanyId)
                           END as TotalTraineesCount,
                           (SELECT COUNT(*) FROM dbo.CompanySessionTrainees CST WHERE CST.SessionId = S.Id AND (CST.ApprovalStatus = 'Approved' OR CST.ApprovalStatus IS NULL)) as SpecificAssignedCount,
                           (SELECT COUNT(*) FROM dbo.CompanySessionTrainees CST WHERE CST.SessionId = S.Id AND CST.ApprovalStatus = 'Pending') as PendingAssignedCount
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
                    SELECT S.*, 
                           (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1) as PresentCount,
                           (SELECT COUNT(*) FROM UserWaves UW WHERE UW.WaveId = S.WaveId AND UW.IsActive = 1 AND (UW.ApprovalStatus = 'Approved' OR UW.ApprovalStatus IS NULL)) as TotalTraineesCount,
                           (SELECT COUNT(*) FROM UserWaves UW WHERE UW.WaveId = S.WaveId AND UW.IsActive = 1 AND UW.ApprovalStatus = 'Pending') as PendingAssignedCount
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
            bool isAdmin = User.IsInRole("Admin");
            DateTime now = DateTime.Now;

            if (session.StartTime != null && session.EndTime != null && !isAdmin)
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

            ViewBag.IsAdmin = isAdmin;
            ViewBag.IsLocked = isLocked;
            ViewBag.LockReason = lockReason;

            int pendingCount = 0;
            IEnumerable<dynamic> users;

            if (session.CompanyId != null)
            {
                pendingCount = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) 
                    FROM dbo.CompanySessionTrainees CST
                    WHERE CST.SessionId = @SessionId AND CST.ApprovalStatus = 'Pending'",
                    new { SessionId = sessionId });

                users = await conn.QueryAsync<dynamic>(@"
                    IF EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees WHERE SessionId = @SessionId)
                    BEGIN
                        SELECT CONCAT('CT_', U.Id) as Id, U.FullName as UserName, U.Email, U.UserCode, U.BranchName as BranchName,
                               ISNULL(UA.IsPresent, 0) as IsPresent,
                               UA.CheckInTime, UA.CheckOutTime
                        FROM dbo.CompanySessionTrainees CST
                        JOIN CompanyTrainees U ON CST.CompanyTraineeId = U.Id
                        LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.CompanyTraineeId = U.Id
                        WHERE CST.SessionId = @SessionId AND (CST.ApprovalStatus = 'Approved' OR CST.ApprovalStatus IS NULL)
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
                pendingCount = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) 
                    FROM UserWaves UW
                    WHERE UW.WaveId = @WaveId AND UW.IsActive = 1 AND UW.ApprovalStatus = 'Pending'",
                    new { WaveId = (int)session.WaveId });

                users = await conn.QueryAsync<dynamic>(@"
                    SELECT U.Id, U.UserName, U.Email, U.UserCode, B.BranchName,
                           ISNULL(UA.IsPresent, 0) as IsPresent,
                           UA.CheckInTime, UA.CheckOutTime
                    FROM AspNetUsers U
                    JOIN UserWaves UW ON U.Id = UW.UserId
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    LEFT JOIN UserAttendance UA ON UA.SessionId = @SessionId AND UA.UserId = U.Id
                    WHERE UW.WaveId = @WaveId AND UW.IsActive = 1
                      AND (UW.ApprovalStatus = 'Approved' OR UW.ApprovalStatus IS NULL)
                    ORDER BY U.UserName", 
                    new { SessionId = sessionId, WaveId = (int)session.WaveId });
            }

            ViewBag.PendingCount = pendingCount;
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

                bool isAdmin = User.IsInRole("Admin");
                var session = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT SessionDate, StartTime, EndTime FROM AttendanceSessions WHERE Id = @Id", new { Id = sessionId });
                if (!isAdmin && session != null && session.StartTime != null && session.EndTime != null)
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

            // Security check: Never allow taking attendance for a pending or rejected candidate!
            if (isCompanyTrainee)
            {
                var cstStatus = await conn.QueryFirstOrDefaultAsync<string>(@"
                    SELECT ApprovalStatus FROM dbo.CompanySessionTrainees 
                    WHERE SessionId = @SessionId AND CompanyTraineeId = @CompanyTraineeId",
                    new { SessionId = sessionId, CompanyTraineeId = companyTraineeId });
                if (cstStatus != null && cstStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(new { success = false, message = "عفواً، لا يمكن تسجيل حضور هذا المتدرب لأنه معلق بانتظار موافقة مدير الفرع!" });
                }
                if (cstStatus != null && (cstStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase) || cstStatus.Equals("Replaced", StringComparison.OrdinalIgnoreCase)))
                {
                    return Json(new { success = false, message = "عفواً، لا يمكن تسجيل حضور هذا المتدرب لأن ترشيحه مرفوض أو تم استبداله!" });
                }
            }
            else if (!string.IsNullOrEmpty(actualUserId))
            {
                var uwStatus = await conn.QueryFirstOrDefaultAsync<string>(@"
                    SELECT UW.ApprovalStatus 
                    FROM UserWaves UW
                    JOIN AttendanceSessions S ON S.WaveId = UW.WaveId
                    WHERE S.Id = @SessionId AND UW.UserId = @UserId",
                    new { SessionId = sessionId, UserId = actualUserId });
                if (uwStatus != null && uwStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(new { success = false, message = "عفواً، لا يمكن تسجيل حضور هذا المتدرب لأنه معلق بانتظار موافقة مدير الفرع!" });
                }
                if (uwStatus != null && (uwStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase) || uwStatus.Equals("Replaced", StringComparison.OrdinalIgnoreCase)))
                {
                    return Json(new { success = false, message = "عفواً، لا يمكن تسجيل حضور هذا المتدرب لأن ترشيحه مرفوض أو تم استبداله!" });
                }
            }

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
                    SELECT S.Id, S.SessionName, S.SessionDate, S.StartTime, S.EndTime, S.CompanyId, S.TotalSeats, C.Name AS CompanyName
                    FROM AttendanceSessions S
                    LEFT JOIN Companies C ON S.CompanyId = C.Id
                    WHERE S.Id = @Id", new { Id = sessionId });

                if (session == null) return NotFound(new { success = false, message = "Session not found." });

                int? companyId = (int?)session.CompanyId;
                if (!companyId.HasValue) return BadRequest(new { success = false, message = "This session does not belong to a company." });

                var assignedRaw = (await conn.QueryAsync<dynamic>(@"
                    SELECT CST.Id AS AssignmentId, CT.Id AS TraineeId, CT.FullName, CT.UserCode, CT.JobTitle, CT.BranchName, CT.Email, CT.Phone, CST.AssignedAt,
                           ISNULL(CST.ApprovalStatus, 'Approved') AS ApprovalStatus,
                           CST.RejectionReason,
                           CST.ReplacedWithTraineeId,
                           CST.ActionedAt,
                           CST.UserId,
                           ISNULL(B.Id, 0) AS BranchId
                    FROM dbo.CompanySessionTrainees CST
                    JOIN dbo.CompanyTrainees CT ON CST.CompanyTraineeId = CT.Id
                    LEFT JOIN dbo.Branches B ON B.BranchName = CT.BranchName OR LTRIM(RTRIM(B.BranchName)) = LTRIM(RTRIM(CT.BranchName))
                    WHERE CST.SessionId = @SessionId
                    ORDER BY CT.FullName", new { SessionId = sessionId })).ToList();

                var assigned = assignedRaw.Select(a => {
                    var d = (IDictionary<string, object>)a;
                    return new {
                        assignmentId = d.ContainsKey("AssignmentId") ? d["AssignmentId"] : null,
                        traineeId = d.ContainsKey("TraineeId") ? d["TraineeId"] : null,
                        fullName = d.ContainsKey("FullName") && d["FullName"] != null ? d["FullName"].ToString() : "Trainee",
                        userCode = d.ContainsKey("UserCode") && d["UserCode"] != null ? d["UserCode"].ToString() : "--",
                        jobTitle = d.ContainsKey("JobTitle") && d["JobTitle"] != null ? d["JobTitle"].ToString() : "Trainee",
                        branchName = d.ContainsKey("BranchName") && d["BranchName"] != null ? d["BranchName"].ToString() : "عام",
                        email = d.ContainsKey("Email") && d["Email"] != null ? d["Email"].ToString() : "--",
                        phone = d.ContainsKey("Phone") && d["Phone"] != null ? d["Phone"].ToString() : "--",
                        assignedAt = d.ContainsKey("AssignedAt") ? d["AssignedAt"] : null,
                        approvalStatus = d.ContainsKey("ApprovalStatus") && d["ApprovalStatus"] != null ? d["ApprovalStatus"].ToString() : "Approved",
                        rejectionReason = d.ContainsKey("RejectionReason") ? d["RejectionReason"] : null,
                        replacedWithTraineeId = d.ContainsKey("ReplacedWithTraineeId") ? d["ReplacedWithTraineeId"] : null,
                        actionedAt = d.ContainsKey("ActionedAt") ? d["ActionedAt"] : null,
                        userId = d.ContainsKey("UserId") && d["UserId"] != null ? d["UserId"].ToString() : null,
                        branchId = d.ContainsKey("BranchId") && d["BranchId"] != null ? Convert.ToInt32(d["BranchId"]) : 0
                    };
                }).ToList();

                int totalSeatsVal = session.TotalSeats != null ? (int)session.TotalSeats : 50;

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
                        companyName = (string)(session.CompanyName ?? ""),
                        totalSeats = totalSeatsVal
                    },
                    totalSeats = totalSeatsVal,
                    assignedCount = assigned.Count(t => t.approvalStatus == "Approved"),
                    totalAssignedCount = assigned.Count,
                    approvedCount = assigned.Count(t => t.approvalStatus == "Approved"),
                    pendingCount = assigned.Count(t => t.approvalStatus == "Pending"),
                    replacedCount = assigned.Count(t => t.approvalStatus == "Replaced"),
                    assignedTrainees = assigned,
                    availablePool = new List<dynamic>()
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateSessionTotalSeats([FromBody] UpdateSessionTotalSeatsRequest req)
        {
            if (req == null || req.SessionId <= 0) return BadRequest(new { success = false, message = "Invalid sessionId" });
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync("UPDATE AttendanceSessions SET TotalSeats = @TotalSeats WHERE Id = @Id", new { Id = req.SessionId, TotalSeats = req.TotalSeats });
                return Json(new { success = true, totalSeats = req.TotalSeats });
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
                            isBranchExempt = u.IsBranchExempt != null && (u.IsBranchExempt is bool bVal ? bVal : u.IsBranchExempt.ToString() == "1" || u.IsBranchExempt.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

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
                                isBranchExempt = ct.IsBranchExempt != null && (ct.IsBranchExempt is bool ctVal ? ctVal : ct.IsBranchExempt.ToString() == "1" || ct.IsBranchExempt.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

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

                        // Log to NominationAuditLogs
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.NominationAuditLogs (
                                EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                                BranchId, BranchName, Action, ActionReason,
                                ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                            ) VALUES (
                                'CompanySession', @EntityId, @EntityName, @CompanyName, @TraineeId, @TraineeName, @TraineeCode, 'Trainee',
                                @BranchId, @BranchName, @Action, NULL,
                                @ActionByUserId, @ActionByUserName, 'Admin', GETDATE()
                            )",
                            new {
                                EntityId = req.SessionId,
                                EntityName = (string)session.SessionName,
                                CompanyName = (string)(session.CompanyName ?? ""),
                                TraineeId = companyTraineeId.Value.ToString(),
                                TraineeName = fullName,
                                TraineeCode = userCode,
                                BranchId = branchId,
                                BranchName = branchName,
                                Action = status,
                                ActionByUserId = assignedBy,
                                ActionByUserName = User.Identity?.Name ?? "الإدارة"
                            });

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

                // 2. RabbitMQ Message Broker Email Dispatch
                if (req.SendRabbitMqEmail && _rabbitMqProducer != null && assignedTraineePayloads.Any())
                {
                    string sName = (string)session.SessionName;
                    string cName = (string)(session.CompanyName ?? "");
                    string sDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd");
                    string sTime = session.StartTime != null ? $"{((TimeSpan)session.StartTime):hh\\:mm}" : "09:00";
                    string eTime = session.EndTime != null ? $"{((TimeSpan)session.EndTime):hh\\:mm}" : "12:00";

                    var messages = new List<EmailNotificationMessage>();
                    foreach (var trainee in assignedTraineePayloads)
                    {
                        string tEmail = (string)(trainee.email ?? "");
                        string tName = (string)(trainee.fullName ?? "");
                        string tCode = (string)(trainee.userCode ?? "--");
                        string tBranch = (string)(trainee.branchName ?? "الفرع");

                        if (!string.IsNullOrWhiteSpace(tEmail) && tEmail.Contains("@"))
                        {
                            messages.Add(new EmailNotificationMessage
                            {
                                To = tEmail.Trim(),
                                Subject = $"تأكيد التسجيل والاعتماد في جلسة تدريب: {sName} - {cName}",
                                BodyHtml = EmailTemplateHelper.BuildCompanySessionEmail(tName, sName, cName, sDate, sTime, eTime, tBranch, req.BypassApproval ? "DirectApproval" : "SessionAssignment"),
                                CandidateName = tName,
                                UserCode = tCode,
                                BranchName = tBranch,
                                SessionOrWaveName = sName,
                                SessionDate = sDate,
                                StartTime = sTime,
                                EndTime = eTime,
                                CompanyName = cName,
                                EventType = req.BypassApproval ? "DirectApproval" : "SessionAssignment"
                            });
                        }
                    }

                    if (messages.Any())
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await _rabbitMqProducer.EnqueueEmailsAsync(messages); } catch { }
                        });
                    }
                }

                // 3. n8n Webhook Trigger (for email dispatch and automation workflows)
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
                        bool n8nSent = false;
                        try
                        {
                            n8nSent = await _n8nService.TriggerCompanySessionNotificationAsync(webhookPayload);
                        }
                        catch { }

                        // Fallback: If n8n is not running and RabbitMQ didn't send, send directly via SMTP
                        if (!n8nSent && !req.SendRabbitMqEmail && _emailSender != null)
                        {
                            foreach (var trainee in assignedTraineePayloads)
                            {
                                string tEmail = (string)(trainee.email ?? "");
                                string tName = (string)(trainee.fullName ?? "");
                                if (!string.IsNullOrWhiteSpace(tEmail) && tEmail.Contains("@"))
                                {
                                    try
                                    {
                                        string sName = (string)session.SessionName;
                                        string cName = (string)(session.CompanyName ?? "");
                                        string sDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd");
                                        string subject = $"دعوة لحضور تدريب: {sName} - {cName}";
                                        string html = $"<div dir='rtl' style='font-family: Arial, sans-serif; padding: 20px; line-height: 1.8; color: #1e293b; background-color: #ffffff; border: 1px solid #e2e8f0; border-radius: 12px;'>" +
                                            $"<h2 style='color: #0f766e;'>مرحباً {tName}،</h2>" +
                                            $"<p>تم تسجيلك لحضور جلسة تدريب خاصة بشركة: <strong>{cName}</strong></p>" +
                                            $"<div style='background-color: #f8fafc; padding: 15px; border-radius: 8px; margin: 15px 0;'>" +
                                            $"<p style='margin: 5px 0;'><strong>اسم الجلسة:</strong> {sName}</p>" +
                                            $"<p style='margin: 5px 0;'><strong>التاريخ:</strong> {sDate}</p>" +
                                            $"</div>" +
                                            $"<p>نتمنى لك دوام التوفيق والنجاح.</p>" +
                                            $"<hr style='border: none; border-top: 1px solid #e2e8f0; margin: 20px 0;'/>" +
                                            $"<p style='font-size: 12px; color: #64748b;'>أكاديمية الطرشوبي - التدريب والتطوير المستمر</p>" +
                                            $"</div>";

                                        await _emailSender.SendEmailAsync(tEmail, subject, html);
                                    }
                                    catch { }
                                }
                            }
                        }
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
        public async Task<IActionResult> ApproveSessionTrainee(int assignmentId, bool sendEmail = true)
        {
            if (assignmentId <= 0)
            {
                return Json(new { success = false, message = "معرف التعيين غير صالح." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                string currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                string currentUserName = User.Identity?.Name ?? "الإدارة";

                var traineeInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT CST.Id, CST.SessionId, CST.CompanyTraineeId, CT.FullName AS TraineeName,
                           CT.UserCode, CT.JobTitle, CT.BranchName, CT.Email, CT.Phone, B.Id AS BranchId, 
                           S.SessionName, S.SessionDate, S.StartTime, S.EndTime, S.CompanyId, C.Name AS CompanyName
                    FROM dbo.CompanySessionTrainees CST
                    JOIN dbo.CompanyTrainees CT ON CST.CompanyTraineeId = CT.Id
                    JOIN dbo.AttendanceSessions S ON CST.SessionId = S.Id
                    LEFT JOIN dbo.Companies C ON S.CompanyId = C.Id
                    LEFT JOIN dbo.Branches B ON B.BranchName = CT.BranchName OR LTRIM(RTRIM(B.BranchName)) = LTRIM(RTRIM(CT.BranchName))
                    WHERE CST.Id = @Id", new { Id = assignmentId });

                if (traineeInfo == null)
                {
                    return Json(new { success = false, message = "سجل التعيين غير موجود." });
                }

                await conn.ExecuteAsync(@"
                    UPDATE dbo.CompanySessionTrainees 
                    SET ApprovalStatus = 'Approved',
                        ActionedByUserId = @ActionedBy,
                        ActionedAt = GETDATE()
                    WHERE Id = @Id",
                    new { Id = assignmentId, ActionedBy = currentUserId });

                try
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.NominationAuditLogs (
                            EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                            BranchId, BranchName, Action, ActionReason, ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                        ) VALUES (
                            'CompanySession', @EntityId, @EntityName, @CompanyName, @TraineeId, @TraineeName, @TraineeCode, @TraineeRole,
                            @BranchId, @BranchName, 'Approved', 'Direct Admin Approval', @ActionByUserId, @ActionByUserName, 'Admin', GETDATE()
                        )", new {
                            EntityId = (int)traineeInfo.SessionId,
                            EntityName = (string)traineeInfo.SessionName,
                            CompanyName = (string)(traineeInfo.CompanyName ?? ""),
                            TraineeId = traineeInfo.CompanyTraineeId.ToString(),
                            TraineeName = (string)traineeInfo.TraineeName,
                            TraineeCode = (string)(traineeInfo.UserCode ?? "--"),
                            TraineeRole = (string)(traineeInfo.JobTitle ?? "Trainee"),
                            BranchId = (int?)traineeInfo.BranchId,
                            BranchName = (string)(traineeInfo.BranchName ?? "الفرع"),
                            ActionByUserId = currentUserId,
                            ActionByUserName = currentUserName
                        });
                }
                catch { }

                // Asynchronous RabbitMQ Email Dispatch upon direct approval
                if (sendEmail && _rabbitMqProducer != null)
                {
                    string toEmail = (string)(traineeInfo.Email ?? "");
                    if (!string.IsNullOrWhiteSpace(toEmail) && toEmail.Contains("@"))
                    {
                        string sName = (string)traineeInfo.SessionName;
                        string cName = (string)(traineeInfo.CompanyName ?? "");
                        string sDate = traineeInfo.SessionDate != null ? ((DateTime)traineeInfo.SessionDate).ToString("yyyy-MM-dd") : "";
                        string sTime = traineeInfo.StartTime != null ? $"{((TimeSpan)traineeInfo.StartTime):hh\\:mm}" : "09:00";
                        string eTime = traineeInfo.EndTime != null ? $"{((TimeSpan)traineeInfo.EndTime):hh\\:mm}" : "12:00";
                        string bName = (string)(traineeInfo.BranchName ?? "الفرع");
                        string trName = (string)traineeInfo.TraineeName;

                        var emailMsg = new EmailNotificationMessage
                        {
                            To = toEmail.Trim(),
                            Subject = $"تأكيد اعتماد حضور تدريب شركة: {sName} - {cName}",
                            BodyHtml = EmailTemplateHelper.BuildCompanySessionEmail(trName, sName, cName, sDate, sTime, eTime, bName, "DirectApproval"),
                            CandidateName = trName,
                            UserCode = (string)(traineeInfo.UserCode ?? "--"),
                            BranchName = bName,
                            SessionOrWaveName = sName,
                            SessionDate = sDate,
                            StartTime = sTime,
                            EndTime = eTime,
                            CompanyName = cName,
                            EventType = "DirectApproval"
                        };

                        _ = Task.Run(async () =>
                        {
                            try { await _rabbitMqProducer.EnqueueEmailAsync(emailMsg); } catch { }
                        });
                    }
                }

                await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم اعتماد وحضور المتدرب مباشرة من قبل الإدارة.");
                return Json(new { success = true, message = $"تم اعتماد المتدرب {traineeInfo.TraineeName} بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ApproveAllPendingSessionTrainees(int sessionId, bool sendEmail = true)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                string currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                var pendingTrainees = (await conn.QueryAsync<dynamic>(@"
                    SELECT CST.Id, CST.CompanyTraineeId, CT.FullName AS TraineeName, CT.UserCode, CT.Email, CT.Phone, CT.BranchName,
                           S.SessionName, S.SessionDate, S.StartTime, S.EndTime, C.Name AS CompanyName
                    FROM dbo.CompanySessionTrainees CST
                    JOIN dbo.CompanyTrainees CT ON CST.CompanyTraineeId = CT.Id
                    JOIN dbo.AttendanceSessions S ON CST.SessionId = S.Id
                    LEFT JOIN dbo.Companies C ON S.CompanyId = C.Id
                    WHERE CST.SessionId = @SessionId AND CST.ApprovalStatus = 'Pending'",
                    new { SessionId = sessionId })).ToList();

                int updatedCount = await conn.ExecuteAsync(@"
                    UPDATE dbo.CompanySessionTrainees 
                    SET ApprovalStatus = 'Approved',
                        ActionedByUserId = @ActionedBy,
                        ActionedAt = GETDATE()
                    WHERE SessionId = @SessionId AND ApprovalStatus = 'Pending'",
                    new { SessionId = sessionId, ActionedBy = currentUserId });

                if (sendEmail && _rabbitMqProducer != null && pendingTrainees.Any())
                {
                    var messages = new List<EmailNotificationMessage>();
                    foreach (var pt in pendingTrainees)
                    {
                        string email = (string)(pt.Email ?? "");
                        if (!string.IsNullOrWhiteSpace(email) && email.Contains("@"))
                        {
                            string sName = (string)pt.SessionName;
                            string cName = (string)(pt.CompanyName ?? "");
                            string sDate = pt.SessionDate != null ? ((DateTime)pt.SessionDate).ToString("yyyy-MM-dd") : "";
                            string sTime = pt.StartTime != null ? $"{((TimeSpan)pt.StartTime):hh\\:mm}" : "09:00";
                            string eTime = pt.EndTime != null ? $"{((TimeSpan)pt.EndTime):hh\\:mm}" : "12:00";
                            string bName = (string)(pt.BranchName ?? "الفرع");
                            string trName = (string)pt.TraineeName;

                            messages.Add(new EmailNotificationMessage
                            {
                                To = email.Trim(),
                                Subject = $"تأكيد اعتماد حضور تدريب شركة: {sName} - {cName}",
                                BodyHtml = EmailTemplateHelper.BuildCompanySessionEmail(trName, sName, cName, sDate, sTime, eTime, bName, "DirectApproval"),
                                CandidateName = trName,
                                UserCode = (string)(pt.UserCode ?? "--"),
                                BranchName = bName,
                                SessionOrWaveName = sName,
                                SessionDate = sDate,
                                StartTime = sTime,
                                EndTime = eTime,
                                CompanyName = cName,
                                EventType = "DirectApproval"
                            });
                        }
                    }

                    if (messages.Any())
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await _rabbitMqProducer.EnqueueEmailsAsync(messages); } catch { }
                        });
                    }
                }

                await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم اعتماد وحضور جميع المتدربين المعلقين في الجلسة.");
                return Json(new { success = true, count = updatedCount, message = $"تم اعتماد وقبول {updatedCount} متدرب معلق بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }


        [HttpPost]
        public async Task<IActionResult> ImportSessionTraineesFromExcel(int sessionId, Microsoft.AspNetCore.Http.IFormFile excelFile, bool bypassApproval = false, bool sendEmail = true)
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

                    // Asynchronous RabbitMQ Email Dispatch for imported trainees
                    if (sendEmail && _rabbitMqProducer != null && bypassApproval)
                    {
                        string sName = (string)session.SessionName;
                        string cName = (string)(session.CompanyName ?? "");
                        string sDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd");
                        string sTime = session.StartTime != null ? $"{((TimeSpan)session.StartTime):hh\\:mm}" : "09:00";
                        string eTime = session.EndTime != null ? $"{((TimeSpan)session.EndTime):hh\\:mm}" : "12:00";

                        var messages = new List<EmailNotificationMessage>();
                        foreach (var tr in trainees)
                        {
                            string trEmail = (string)(tr.Email ?? "");
                            string trName = (string)(tr.FullName ?? "");
                            string trBranch = (string)(tr.BranchName ?? "الفرع");

                            if (!string.IsNullOrWhiteSpace(trEmail) && trEmail.Contains("@"))
                            {
                                messages.Add(new EmailNotificationMessage
                                {
                                    To = trEmail.Trim(),
                                    Subject = $"تأكيد التسجيل والاعتماد في جلسة تدريب: {sName} - {cName}",
                                    BodyHtml = EmailTemplateHelper.BuildCompanySessionEmail(trName, sName, cName, sDate, sTime, eTime, trBranch, "ExcelImport"),
                                    CandidateName = trName,
                                    UserCode = (string)(tr.UserCode ?? "--"),
                                    BranchName = trBranch,
                                    SessionOrWaveName = sName,
                                    SessionDate = sDate,
                                    StartTime = sTime,
                                    EndTime = eTime,
                                    CompanyName = cName,
                                    EventType = "ExcelImport"
                                });
                            }
                        }

                        if (messages.Any())
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await _rabbitMqProducer.EnqueueEmailsAsync(messages); } catch { }
                            });
                        }
                    }

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
                        bool n8nSent = false;
                        try { n8nSent = await _n8nService.TriggerCompanySessionNotificationAsync(webhookPayload); } catch { }

                        if (!n8nSent && !sendEmail && _emailSender != null)
                        {
                            foreach (var tr in trainees)
                            {
                                string trEmail = (string)(tr.Email ?? "");
                                string trName = (string)(tr.FullName ?? "");
                                if (!string.IsNullOrWhiteSpace(trEmail) && trEmail.Contains("@"))
                                {
                                    try
                                    {
                                        string sName = (string)session.SessionName;
                                        string cName = (string)(session.CompanyName ?? "");
                                        string sDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd");
                                        string subject = $"دعوة لحضور تدريب: {sName} - {cName}";
                                        string html = $"<div dir='rtl' style='font-family: Arial, sans-serif; padding: 20px; line-height: 1.8; color: #1e293b; background-color: #ffffff; border: 1px solid #e2e8f0; border-radius: 12px;'>" +
                                            $"<h2 style='color: #0f766e;'>مرحباً {trName}،</h2>" +
                                            $"<p>تم تسجيلك لحضور جلسة تدريب خاصة بشركة: <strong>{cName}</strong></p>" +
                                            $"<div style='background-color: #f8fafc; padding: 15px; border-radius: 8px; margin: 15px 0;'>" +
                                            $"<p style='margin: 5px 0;'><strong>اسم الجلسة:</strong> {sName}</p>" +
                                            $"<p style='margin: 5px 0;'><strong>التاريخ:</strong> {sDate}</p>" +
                                            $"</div>" +
                                            $"<p>نتمنى لك دوام التوفيق والنجاح.</p>" +
                                            $"<hr style='border: none; border-top: 1px solid #e2e8f0; margin: 20px 0;'/>" +
                                            $"<p style='font-size: 12px; color: #64748b;'>أكاديمية الطرشوبي - التدريب والتطوير المستمر</p>" +
                                            $"</div>";

                                        await _emailSender.SendEmailAsync(trEmail, subject, html);
                                    }
                                    catch { }
                                }
                            }
                        }
                    });
                }

                return Json(new { success = true, message = $"تم استيراد وتسكين {importedCount} متدرب في هذه السيشن بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "فشل استيراد الملف: " + ex.Message });
            }
        }

        // ==========================================
        // SESSION INJECTION RULES (قواعد الحقن والتوزيع الدوري حسب الأقسام)
        // ==========================================

        [HttpGet, HttpPost]
        public async Task<IActionResult> GetDepartmentsForInjection([FromBody] BranchStaffFilterCountRequest? filter, string? branchIds = null, string? shiftIds = null, int? sessionId = null)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);

                List<int>? targetBranchIds = filter?.BranchIds;
                List<int>? targetShiftIds = filter?.ShiftIds;
                int targetSessionId = filter?.SessionId ?? sessionId ?? 0;

                if (targetBranchIds == null && !string.IsNullOrWhiteSpace(branchIds))
                {
                    targetBranchIds = branchIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                               .Select(s => int.TryParse(s, out var id) ? id : 0)
                                               .Where(id => id > 0).ToList();
                }

                if (targetShiftIds == null && !string.IsNullOrWhiteSpace(shiftIds))
                {
                    targetShiftIds = shiftIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(s => int.TryParse(s, out var id) ? id : 0)
                                             .Where(id => id > 0).ToList();
                }

                bool hasBranches = targetBranchIds != null && targetBranchIds.Any();
                bool hasShifts = targetShiftIds != null && targetShiftIds.Any();
                string branchFilterSql = hasBranches ? "AND u.BranchId IN @BranchIds" : "";
                string shiftFilterSql = hasShifts ? "AND (u.ShiftId IN @ShiftIds OR u.ShiftId IS NULL)" : "";

                string sql = $@"
                    SELECT d.Id, d.Name, ISNULL(d.Code, '') AS Code,
                           (SELECT COUNT(*) FROM AspNetUsers u 
                            WHERE u.DepartmentId = d.Id 
                              AND u.IsActive = 1
                              {branchFilterSql}
                              {shiftFilterSql}
                              AND (@SessionId = 0 OR NOT EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees cur WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id))
                           ) AS ActiveStaffCount
                    FROM Departments d
                    WHERE d.IsActive = 1 OR d.IsActive IS NULL
                    ORDER BY d.Name";

                var depts = (await conn.QueryAsync<dynamic>(sql, new
                {
                    BranchIds = targetBranchIds,
                    ShiftIds = targetShiftIds,
                    SessionId = targetSessionId
                })).ToList();

                var result = depts.Select(d => {
                    var dict = (IDictionary<string, object>)d;
                    return new {
                        id = dict.ContainsKey("Id") ? dict["Id"] : null,
                        name = dict.ContainsKey("Name") && dict["Name"] != null ? dict["Name"].ToString() : "Department",
                        code = dict.ContainsKey("Code") && dict["Code"] != null ? dict["Code"].ToString() : "",
                        staffCount = dict.ContainsKey("ActiveStaffCount") && dict["ActiveStaffCount"] != null ? Convert.ToInt32(dict["ActiveStaffCount"]) : 0
                    };
                }).ToList();

                return Json(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompanyInjectionRules(int? companyId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                var rules = await conn.QueryAsync<CompanyInjectionRuleDto>(@"
                    SELECT Id, CompanyId, RuleName, DepartmentQuotasJson, CreatedAt, UpdatedAt
                    FROM CompanyInjectionRules
                    WHERE (CompanyId = @CompanyId OR CompanyId IS NULL)
                    ORDER BY UpdatedAt DESC", new { CompanyId = companyId });
                return Json(new { success = true, data = rules });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SaveCompanyInjectionRule([FromBody] SaveCompanyInjectionRuleRequest? body, int? id, int? companyId, string? ruleName, string? quotasJson)
        {
            int? targetId = body?.Id ?? id;
            int? targetCompanyId = body?.CompanyId ?? companyId;
            string? targetRuleName = body?.RuleName ?? ruleName;
            string? targetQuotasJson = body?.DepartmentQuotasJson ?? body?.QuotasJson ?? quotasJson;

            if (string.IsNullOrWhiteSpace(targetRuleName) || string.IsNullOrWhiteSpace(targetQuotasJson))
            {
                return Json(new { success = false, message = "اسم القاعدة وتوزيع الكوتة مطلوبان." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                if (!targetId.HasValue || targetId.Value <= 0)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO CompanyInjectionRules (CompanyId, RuleName, DepartmentQuotasJson, CreatedAt, UpdatedAt)
                        VALUES (@CompanyId, @RuleName, @QuotasJson, GETDATE(), GETDATE())",
                        new { CompanyId = targetCompanyId, RuleName = targetRuleName.Trim(), QuotasJson = targetQuotasJson });
                    return Json(new { success = true, message = "تم حفظ قالب التوزيع بنجاح ويمكنك استخدامه في أي جلسة قادمة." });
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        UPDATE CompanyInjectionRules 
                        SET RuleName = @RuleName, DepartmentQuotasJson = @QuotasJson, UpdatedAt = GETDATE()
                        WHERE Id = @Id",
                        new { Id = targetId.Value, RuleName = targetRuleName.Trim(), QuotasJson = targetQuotasJson });
                    return Json(new { success = true, message = "تم تحديث قالب التوزيع بنجاح." });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteCompanyInjectionRule(int? id, int? ruleId)
        {
            try
            {
                var targetId = id ?? ruleId ?? 0;
                if (targetId <= 0)
                {
                    return Json(new { success = false, message = "معرف القالب غير صالح." });
                }

                using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync("DELETE FROM CompanyInjectionRules WHERE Id = @Id", new { Id = targetId });
                return Json(new { success = true, message = "تم حذف قالب التوزيع بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private static string FormatShiftTimeSpan(TimeSpan? time)
        {
            if (!time.HasValue) return "";
            var ts = time.Value;
            int hour24 = ts.Hours;
            int minute = ts.Minutes;
            string period = hour24 >= 12 ? "م" : "ص";
            int hour12 = hour24 % 12;
            if (hour12 == 0) hour12 = 12;
            return $"{hour12:D2}:{minute:D2} {period}";
        }

        [HttpGet]
        public async Task<IActionResult> GetBranchStaffMetadataForInjection(int? sessionId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);

                var branches = (await conn.QueryAsync<dynamic>(@"
                    SELECT b.Id, b.BranchName,
                           COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') AND u.IsActive = 1 THEN u.Id END) AS PharmacistCount,
                           COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%assistant%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%مساعد%' OR r.Name LIKE '%assistant%' OR r.Name LIKE N'%مساعد%') 
                                                AND NOT (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') 
                                                AND u.IsActive = 1 THEN u.Id END) AS AssistantCount,
                           COUNT(DISTINCT CASE WHEN u.IsActive = 1 THEN u.Id END) AS TotalStaffCount
                    FROM Branches b WITH (NOLOCK)
                    LEFT JOIN AspNetUsers u WITH (NOLOCK) ON u.BranchId = b.Id
                    LEFT JOIN AspNetUserRoles ur WITH (NOLOCK) ON ur.UserId = u.Id
                    LEFT JOIN AspNetRoles r WITH (NOLOCK) ON ur.RoleId = r.Id
                    WHERE b.IsActive = 1 OR b.IsActive IS NULL
                    GROUP BY b.Id, b.BranchName
                    ORDER BY b.BranchName")).ToList();

                var shiftsData = (await conn.QueryAsync<dynamic>(@"
                    SELECT s.Id, s.ShiftName, s.StartTime, s.EndTime,
                           MAX(sg.Id) AS GroupId,
                           MAX(sg.GroupName) AS GroupName,
                           MAX(sg.ColorCode) AS GroupColor,
                           COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') AND u.IsActive = 1 THEN u.Id END) AS PharmacistCount,
                           COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%assistant%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%مساعد%' OR r.Name LIKE '%assistant%' OR r.Name LIKE N'%مساعد%') 
                                                AND NOT (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') 
                                                AND u.IsActive = 1 THEN u.Id END) AS AssistantCount,
                           COUNT(DISTINCT CASE WHEN u.IsActive = 1 THEN u.Id END) AS TotalStaffCount
                    FROM Shifts s WITH (NOLOCK)
                    LEFT JOIN ShiftGroupItems sgi WITH (NOLOCK) ON s.Id = sgi.ShiftId
                    LEFT JOIN ShiftGroups sg WITH (NOLOCK) ON sgi.GroupId = sg.Id
                    LEFT JOIN AspNetUsers u WITH (NOLOCK) ON u.ShiftId = s.Id
                    LEFT JOIN AspNetUserRoles ur WITH (NOLOCK) ON ur.UserId = u.Id
                    LEFT JOIN AspNetRoles r WITH (NOLOCK) ON ur.RoleId = r.Id
                    GROUP BY s.Id, s.ShiftName, s.StartTime, s.EndTime
                    ORDER BY s.ShiftName")).ToList();

                var shiftGroups = (await conn.QueryAsync<dynamic>(@"
                    SELECT sg.Id, sg.GroupName, sg.Description, sg.ColorCode,
                           COUNT(sgi.ShiftId) AS ShiftsCount
                    FROM ShiftGroups sg WITH (NOLOCK)
                    LEFT JOIN ShiftGroupItems sgi WITH (NOLOCK) ON sg.Id = sgi.GroupId
                    GROUP BY sg.Id, sg.GroupName, sg.Description, sg.ColorCode
                    ORDER BY sg.Id")).ToList();

                var shifts = shiftsData.Select(s => {
                    var dict = (IDictionary<string, object>)s;
                    int id = Convert.ToInt32(dict["Id"]);
                    string name = dict["ShiftName"]?.ToString() ?? "Shift";
                    TimeSpan? st = dict["StartTime"] is TimeSpan ts1 ? ts1 : null;
                    TimeSpan? et = dict["EndTime"] is TimeSpan ts2 ? ts2 : null;
                    string timeFormatted = "";
                    if (st.HasValue && et.HasValue)
                    {
                        double hours = (et.Value - st.Value).TotalHours;
                        if (hours <= 0) hours += 24;
                        timeFormatted = $"من {FormatShiftTimeSpan(st)} إلى {FormatShiftTimeSpan(et)} ({hours:0.#} س)";
                    }

                    int pCount = dict.ContainsKey("PharmacistCount") && dict["PharmacistCount"] != null ? Convert.ToInt32(dict["PharmacistCount"]) : 0;
                    int aCount = dict.ContainsKey("AssistantCount") && dict["AssistantCount"] != null ? Convert.ToInt32(dict["AssistantCount"]) : 0;

                    int? gId = dict.ContainsKey("GroupId") && dict["GroupId"] != null ? Convert.ToInt32(dict["GroupId"]) : (int?)null;
                    string gName = dict.ContainsKey("GroupName") && dict["GroupName"] != null ? dict["GroupName"].ToString() : null;
                    string gColor = dict.ContainsKey("GroupColor") && dict["GroupColor"] != null ? dict["GroupColor"].ToString() : "#7C3AED";

                    return new {
                        id = id,
                        shiftName = name,
                        timeFormatted = timeFormatted,
                        startTime = st?.ToString(@"hh\:mm"),
                        endTime = et?.ToString(@"hh\:mm"),
                        pharmacistCount = pCount,
                        assistantCount = aCount,
                        totalStaffCount = pCount + aCount,
                        groupId = gId,
                        groupName = gName,
                        groupColor = gColor
                    };
                }).ToList();

                var totalCounts = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT 
                        COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') AND u.IsActive = 1 THEN u.Id END) AS TotalPharmacists,
                        COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%assistant%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%مساعد%' OR r.Name LIKE '%assistant%' OR r.Name LIKE N'%مساعد%') 
                                            AND NOT (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') 
                                            AND u.IsActive = 1 THEN u.Id END) AS TotalAssistants
                    FROM AspNetUsers u WITH (NOLOCK)
                    LEFT JOIN AspNetUserRoles ur WITH (NOLOCK) ON ur.UserId = u.Id
                    LEFT JOIN AspNetRoles r WITH (NOLOCK) ON ur.RoleId = r.Id
                    WHERE u.IsActive = 1");

                return Json(new
                {
                    success = true,
                    branches = branches,
                    shifts = shifts,
                    shiftGroups = shiftGroups,
                    totalPharmacists = (int)(totalCounts?.TotalPharmacists ?? 0),
                    totalAssistants = (int)(totalCounts?.TotalAssistants ?? 0)
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> GetAvailableBranchStaffCounts([FromBody] BranchStaffFilterCountRequest req)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                bool hasBranches = req?.BranchIds != null && req.BranchIds.Any();
                bool hasShifts = req?.ShiftIds != null && req.ShiftIds.Any();
                int sessionId = req?.SessionId ?? 0;

                string branchFilterSql = hasBranches ? "AND u.BranchId IN @BranchIds" : "";
                string shiftFilterSql = hasShifts ? "AND u.ShiftId IN @ShiftIds" : "";

                string sql = $@"
                    SELECT 
                        COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') THEN u.Id END) AS AvailablePharmacists,
                        COUNT(DISTINCT CASE WHEN (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%assistant%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%مساعد%' OR r.Name LIKE '%assistant%' OR r.Name LIKE N'%مساعد%') 
                                            AND NOT (LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%' OR r.Name LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%') THEN u.Id END) AS AvailableAssistants
                    FROM AspNetUsers u WITH (NOLOCK)
                    LEFT JOIN AspNetUserRoles ur WITH (NOLOCK) ON ur.UserId = u.Id
                    LEFT JOIN AspNetRoles r WITH (NOLOCK) ON ur.RoleId = r.Id
                    WHERE u.IsActive = 1
                      {branchFilterSql}
                      {shiftFilterSql}
                      AND (@SessionId = 0 OR NOT EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees cur WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id))";

                var counts = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new
                {
                    BranchIds = req?.BranchIds,
                    ShiftIds = req?.ShiftIds,
                    SessionId = sessionId
                });

                return Json(new
                {
                    success = true,
                    availablePharmacists = (int)(counts?.AvailablePharmacists ?? 0),
                    availableAssistants = (int)(counts?.AvailableAssistants ?? 0)
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PreviewInjectionRule([FromBody] InjectionRulePreviewRequest req)
        {
            bool hasDeptQuotas = req?.DepartmentQuotas != null && req.DepartmentQuotas.Any(q => q.Quota > 0);
            bool hasBranchStaffQuotas = (req?.PharmacistQuota > 0) || (req?.AssistantQuota > 0);

            if (req == null || req.SessionId <= 0 || (!hasDeptQuotas && !hasBranchStaffQuotas))
            {
                return Json(new { success = false, message = "يرجى تحديد حصة عددية للصيادلة أو المساعدين أو الأقسام." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                var previewList = new List<InjectionCandidatePreviewDto>();

                bool hasBranches = req.BranchIds != null && req.BranchIds.Any();
                bool hasShifts = req.ShiftIds != null && req.ShiftIds.Any();
                string branchFilterSql = hasBranches ? "AND u.BranchId IN @BranchIds" : "";
                string shiftFilterSql = hasShifts ? "AND u.ShiftId IN @ShiftIds" : "";
                string deptShiftFilterSql = hasShifts ? "AND (u.ShiftId IN @ShiftIds OR u.ShiftId IS NULL)" : "";

                // 1. Pharmacists Quota
                if (req.PharmacistQuota > 0)
                {
                    string pharmSql = $@"
                        SELECT 
                            CONCAT('U_', u.Id) AS CandidateKey,
                            u.Id AS UserId,
                            ISNULL(u.FullName, u.UserName) AS FullName,
                            ISNULL(u.UserCode, '--') AS UserCode,
                            N'صيدلي' AS JobTitle,
                            ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
                            ISNULL(s.ShiftName, N'غير محدد') AS ShiftName,
                            0 AS DepartmentId,
                            N'طاقم الفروع (صيادلة)' AS DepartmentName,
                            (SELECT COUNT(1) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) AS PreviousAttendedCount,
                            (SELECT MAX(cst.AssignedAt) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) AS LastAttendedDate,
                            1 AS IsSelected
                        FROM AspNetUsers u WITH (NOLOCK)
                        LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                        LEFT JOIN Shifts s WITH (NOLOCK) ON u.ShiftId = s.Id
                        WHERE u.IsActive = 1
                          AND (
                              LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' 
                              OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%'
                              OR EXISTS (
                                  SELECT 1 FROM AspNetUserRoles ur 
                                  JOIN AspNetRoles r ON ur.RoleId = r.Id 
                                  WHERE ur.UserId = u.Id AND (LOWER(r.Name) LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%')
                              )
                          )
                          {branchFilterSql}
                          {shiftFilterSql}
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.CompanySessionTrainees cur 
                              WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id
                          )
                        ORDER BY PreviousAttendedCount ASC, LastAttendedDate ASC, u.CreatedAt ASC";

                    var pharmCandidates = (await conn.QueryAsync<InjectionCandidatePreviewDto>(pharmSql, new
                    {
                        SessionId = req.SessionId,
                        BranchIds = req.BranchIds,
                        ShiftIds = req.ShiftIds
                    })).Take(req.PharmacistQuota).ToList();

                    previewList.AddRange(pharmCandidates);
                }

                // 2. Assistants Quota
                if (req.AssistantQuota > 0)
                {
                    var alreadySelectedUserIds = previewList.Select(p => p.UserId).ToList();
                    string assistSql = $@"
                        SELECT 
                            CONCAT('U_', u.Id) AS CandidateKey,
                            u.Id AS UserId,
                            ISNULL(u.FullName, u.UserName) AS FullName,
                            ISNULL(u.UserCode, '--') AS UserCode,
                            N'مساعد صيدلي' AS JobTitle,
                            ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
                            ISNULL(s.ShiftName, N'غير محدد') AS ShiftName,
                            0 AS DepartmentId,
                            N'طاقم الفروع (مساعدين)' AS DepartmentName,
                            (SELECT COUNT(1) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) AS PreviousAttendedCount,
                            (SELECT MAX(cst.AssignedAt) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) AS LastAttendedDate,
                            1 AS IsSelected
                        FROM AspNetUsers u WITH (NOLOCK)
                        LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                        LEFT JOIN Shifts s WITH (NOLOCK) ON u.ShiftId = s.Id
                        WHERE u.IsActive = 1
                          AND (
                              LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%assistant%' 
                              OR ISNULL(u.UserRoleCustom, '') LIKE N'%مساعد%'
                              OR EXISTS (
                                  SELECT 1 FROM AspNetUserRoles ur 
                                  JOIN AspNetRoles r ON ur.RoleId = r.Id 
                                  WHERE ur.UserId = u.Id AND (LOWER(r.Name) LIKE '%assistant%' OR r.Name LIKE N'%مساعد%')
                              )
                          )
                          AND NOT (
                              LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' 
                              OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%'
                              OR EXISTS (
                                  SELECT 1 FROM AspNetUserRoles ur 
                                  JOIN AspNetRoles r ON ur.RoleId = r.Id 
                                  WHERE ur.UserId = u.Id AND (LOWER(r.Name) LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%')
                              )
                          )
                          {branchFilterSql}
                          {shiftFilterSql}
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.CompanySessionTrainees cur 
                              WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id
                          )
                        ORDER BY PreviousAttendedCount ASC, LastAttendedDate ASC, u.CreatedAt ASC";

                    var assistCandidates = (await conn.QueryAsync<InjectionCandidatePreviewDto>(assistSql, new
                    {
                        SessionId = req.SessionId,
                        BranchIds = req.BranchIds,
                        ShiftIds = req.ShiftIds
                    })).Where(c => !alreadySelectedUserIds.Contains(c.UserId)).Take(req.AssistantQuota).ToList();

                    previewList.AddRange(assistCandidates);
                }

                // 3. Department Quotas
                if (hasDeptQuotas && req.DepartmentQuotas != null)
                {
                    var alreadySelectedUserIds = previewList.Select(p => p.UserId).ToList();

                    foreach (var dq in req.DepartmentQuotas.Where(q => q.Quota > 0))
                    {
                        var candidates = (await conn.QueryAsync<InjectionCandidatePreviewDto>($@"
                            SELECT 
                                CONCAT('U_', u.Id) AS CandidateKey,
                                u.Id AS UserId,
                                ISNULL(u.FullName, u.UserName) AS FullName,
                                ISNULL(u.UserCode, '--') AS UserCode,
                                ISNULL(u.UserRoleCustom, 'Trainee') AS JobTitle,
                                ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
                                ISNULL(s.ShiftName, N'غير محدد') AS ShiftName,
                                @DeptId AS DepartmentId,
                                @DeptName AS DepartmentName,
                                (SELECT COUNT(1) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) AS PreviousAttendedCount,
                                (SELECT MAX(cst.AssignedAt) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) AS LastAttendedDate,
                                1 AS IsSelected
                            FROM AspNetUsers u WITH (NOLOCK)
                            LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                            LEFT JOIN Shifts s WITH (NOLOCK) ON u.ShiftId = s.Id
                            WHERE u.DepartmentId = @DeptId
                              AND u.IsActive = 1
                              {branchFilterSql}
                              {deptShiftFilterSql}
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.CompanySessionTrainees cur 
                                  WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id
                              )
                            ORDER BY PreviousAttendedCount ASC, LastAttendedDate ASC, u.CreatedAt ASC",
                            new { 
                                DeptId = dq.DepartmentId, 
                                DeptName = dq.DepartmentName, 
                                SessionId = req.SessionId,
                                BranchIds = req.BranchIds,
                                ShiftIds = req.ShiftIds
                            }))
                            .Where(c => !alreadySelectedUserIds.Contains(c.UserId))
                            .Take(dq.Quota)
                            .ToList();

                        previewList.AddRange(candidates);
                    }
                }

                return Json(new
                {
                    success = true,
                    totalCount = previewList.Count,
                    candidates = previewList
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ExecuteInjectionRule([FromBody] InjectionRuleExecuteRequest req)
        {
            if (req == null || req.SessionId <= 0)
            {
                return Json(new { success = false, message = "بيانات الطلب غير صالحة." });
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

                List<string> userIdsToInject = new List<string>();

                if (req.ConfirmedCandidateKeys != null && req.ConfirmedCandidateKeys.Any())
                {
                    foreach (var key in req.ConfirmedCandidateKeys)
                    {
                        if (key.StartsWith("U_")) userIdsToInject.Add(key.Substring(2));
                        else userIdsToInject.Add(key);
                    }
                }
                else if (req.UserIds != null && req.UserIds.Any())
                {
                    foreach (var u in req.UserIds)
                    {
                        if (u.StartsWith("U_")) userIdsToInject.Add(u.Substring(2));
                        else userIdsToInject.Add(u);
                    }
                }
                else
                {
                    // Direct execution based on quotas
                    bool hasBranches = req.BranchIds != null && req.BranchIds.Any();
                    bool hasShifts = req.ShiftIds != null && req.ShiftIds.Any();
                    string branchFilterSql = hasBranches ? "AND u.BranchId IN @BranchIds" : "";
                    string shiftFilterSql = hasShifts ? "AND u.ShiftId IN @ShiftIds" : "";
                    string deptShiftFilterSql = hasShifts ? "AND (u.ShiftId IN @ShiftIds OR u.ShiftId IS NULL)" : "";

                    if (req.PharmacistQuota > 0)
                    {
                        var uids = (await conn.QueryAsync<string>($@"
                            SELECT u.Id
                            FROM AspNetUsers u WITH (NOLOCK)
                            WHERE u.IsActive = 1
                              AND (
                                  LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' 
                                  OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%'
                                  OR EXISTS (
                                      SELECT 1 FROM AspNetUserRoles ur 
                                      JOIN AspNetRoles r ON ur.RoleId = r.Id 
                                      WHERE ur.UserId = u.Id AND (LOWER(r.Name) LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%')
                                  )
                              )
                              {branchFilterSql}
                              {shiftFilterSql}
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.CompanySessionTrainees cur 
                                  WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id
                              )
                            ORDER BY 
                                (SELECT COUNT(1) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) ASC,
                                (SELECT MAX(cst.AssignedAt) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) ASC,
                                u.CreatedAt ASC",
                            new { SessionId = req.SessionId, BranchIds = req.BranchIds, ShiftIds = req.ShiftIds }))
                            .Take(req.PharmacistQuota)
                            .ToList();

                        userIdsToInject.AddRange(uids);
                    }

                    if (req.AssistantQuota > 0)
                    {
                        var uids = (await conn.QueryAsync<string>($@"
                            SELECT u.Id
                            FROM AspNetUsers u WITH (NOLOCK)
                            WHERE u.IsActive = 1
                              AND (
                                  LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%assistant%' 
                                  OR ISNULL(u.UserRoleCustom, '') LIKE N'%مساعد%'
                                  OR EXISTS (
                                      SELECT 1 FROM AspNetUserRoles ur 
                                      JOIN AspNetRoles r ON ur.RoleId = r.Id 
                                      WHERE ur.UserId = u.Id AND (LOWER(r.Name) LIKE '%assistant%' OR r.Name LIKE N'%مساعد%')
                                  )
                              )
                              AND NOT (
                                  LOWER(ISNULL(u.UserRoleCustom, '')) LIKE '%pharmacist%' 
                                  OR ISNULL(u.UserRoleCustom, '') LIKE N'%صيدل%'
                                  OR EXISTS (
                                      SELECT 1 FROM AspNetUserRoles ur 
                                      JOIN AspNetRoles r ON ur.RoleId = r.Id 
                                      WHERE ur.UserId = u.Id AND (LOWER(r.Name) LIKE '%pharmacist%' OR r.Name LIKE N'%صيدل%')
                                  )
                              )
                              {branchFilterSql}
                              {shiftFilterSql}
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.CompanySessionTrainees cur 
                                  WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id
                              )
                            ORDER BY 
                                (SELECT COUNT(1) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) ASC,
                                (SELECT MAX(cst.AssignedAt) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) ASC,
                                u.CreatedAt ASC",
                            new { SessionId = req.SessionId, BranchIds = req.BranchIds, ShiftIds = req.ShiftIds }))
                            .Where(id => !userIdsToInject.Contains(id))
                            .Take(req.AssistantQuota)
                            .ToList();

                        userIdsToInject.AddRange(uids);
                    }

                    if (req.DepartmentQuotas != null && req.DepartmentQuotas.Any(q => q.Quota > 0))
                    {
                        foreach (var dq in req.DepartmentQuotas.Where(q => q.Quota > 0))
                        {
                            var uids = (await conn.QueryAsync<string>($@"
                                SELECT u.Id
                                FROM AspNetUsers u WITH (NOLOCK)
                                WHERE u.DepartmentId = @DeptId
                                  AND u.IsActive = 1
                                  {branchFilterSql}
                                  {deptShiftFilterSql}
                                  AND NOT EXISTS (
                                      SELECT 1 FROM dbo.CompanySessionTrainees cur 
                                      WHERE cur.SessionId = @SessionId AND cur.UserId = u.Id
                                  )
                                ORDER BY 
                                    (SELECT COUNT(1) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) ASC,
                                    (SELECT MAX(cst.AssignedAt) FROM dbo.CompanySessionTrainees cst WHERE cst.UserId = u.Id) ASC,
                                    u.CreatedAt ASC",
                                new { 
                                    DeptId = dq.DepartmentId, 
                                    SessionId = req.SessionId,
                                    BranchIds = req.BranchIds,
                                    ShiftIds = req.ShiftIds
                                }))
                                .Where(id => !userIdsToInject.Contains(id))
                                .Take(dq.Quota)
                                .ToList();

                            userIdsToInject.AddRange(uids);
                        }
                    }
                }

                if (!userIdsToInject.Any())
                {
                    return Json(new { success = false, message = "لم يتم العثور على موظفين مؤهلين للحقن وفق الشروط والكوتة المحددة." });
                }

                var assignedBy = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                int count = 0;
                string status = req.BypassApproval ? "Approved" : "Pending";
                var injectedPayloads = new List<dynamic>();

                foreach (var uId in userIdsToInject.Distinct())
                {
                    var u = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT u.Id, u.FullName, u.UserName, u.UserCode, u.Email, u.PhoneNumber, u.UserRoleCustom,
                               u.BranchId, b.BranchName
                        FROM AspNetUsers u
                        LEFT JOIN Branches b ON u.BranchId = b.Id
                        WHERE u.Id = @UserId", new { UserId = uId });

                    if (u == null) continue;

                    string fullName = !string.IsNullOrWhiteSpace((string)(u.FullName ?? "")) ? (string)u.FullName : (string)(u.UserName ?? "Trainee");
                    string userCode = (string)(u.UserCode ?? "--");
                    string email = (string)(u.Email ?? "");
                    string phone = (string)(u.PhoneNumber ?? "");
                    string branchName = (string)(u.BranchName ?? "بدون فرع");

                    int? companyTraineeId = null;
                    if (session.CompanyId != null)
                    {
                        var ct = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                            SELECT TOP 1 Id FROM CompanyTrainees 
                            WHERE CompanyId = @CompanyId AND ((UserCode = @Code AND UserCode <> '--') OR (Email = @Email AND Email <> '--'))",
                            new { CompanyId = (int)session.CompanyId, Code = userCode, Email = email });

                        if (ct != null)
                        {
                            companyTraineeId = (int)ct.Id;
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

                    int affected = await conn.ExecuteAsync(@"
                        IF NOT EXISTS (SELECT 1 FROM dbo.CompanySessionTrainees WHERE SessionId = @SessionId AND UserId = @UserId)
                        BEGIN
                            INSERT INTO dbo.CompanySessionTrainees (SessionId, CompanyTraineeId, UserId, AssignedAt, AssignedBy, ApprovalStatus)
                            VALUES (@SessionId, @TraineeId, @UserId, GETDATE(), @AssignedBy, @Status)
                        END",
                        new { SessionId = req.SessionId, TraineeId = companyTraineeId, UserId = uId, AssignedBy = assignedBy, Status = status });

                    if (affected > 0)
                    {
                        count++;
                        injectedPayloads.Add(new
                        {
                            id = companyTraineeId ?? 0,
                            userId = uId,
                            fullName = fullName,
                            userCode = userCode,
                            email = email,
                            phone = phone,
                            branchName = branchName
                        });

                        if (req.SendNotification)
                        {
                            await conn.ExecuteAsync(@"
                                INSERT INTO Notifications (UserId, Title, Message, LinkUrl, Type, CreatedAt)
                                VALUES (@UserId, N'ترشيح لحضور سيشن تدريبية 🎯', CONCAT(N'تم ترشيحك لحضور جلسة: ', @SessionName, N' الخاصة بشركة: ', @CompanyName), '/Home/Profile?tab=mycompanies', 'CompanySessionAssigned', GETDATE())",
                                new { UserId = uId, SessionName = (string)session.SessionName, CompanyName = (string)(session.CompanyName ?? "") });
                        }
                    }
                }

                // Asynchronous RabbitMQ Email Dispatch for Fair Rotation injected trainees
                if (req.SendEmail && _rabbitMqProducer != null && injectedPayloads.Any())
                {
                    string sName = (string)session.SessionName;
                    string cName = (string)(session.CompanyName ?? "");
                    string sDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd");
                    string sTime = session.StartTime != null ? $"{((TimeSpan)session.StartTime):hh\\:mm}" : "09:00";
                    string eTime = session.EndTime != null ? $"{((TimeSpan)session.EndTime):hh\\:mm}" : "12:00";

                    var messages = new List<EmailNotificationMessage>();
                    foreach (var tr in injectedPayloads)
                    {
                        string trEmail = (string)(tr.email ?? "");
                        string trName = (string)(tr.fullName ?? "");
                        string trBranch = (string)(tr.branchName ?? "الفرع");

                        if (!string.IsNullOrWhiteSpace(trEmail) && trEmail.Contains("@"))
                        {
                            messages.Add(new EmailNotificationMessage
                            {
                                To = trEmail.Trim(),
                                Subject = $"تأكيد الترشيح والاعتماد في جلسة تدريب: {sName} - {cName}",
                                BodyHtml = EmailTemplateHelper.BuildCompanySessionEmail(trName, sName, cName, sDate, sTime, eTime, trBranch, "FairRotationInjection"),
                                CandidateName = trName,
                                UserCode = (string)(tr.userCode ?? "--"),
                                BranchName = trBranch,
                                SessionOrWaveName = sName,
                                SessionDate = sDate,
                                StartTime = sTime,
                                EndTime = eTime,
                                CompanyName = cName,
                                EventType = "FairRotationInjection"
                            });
                        }
                    }

                    if (messages.Any())
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await _rabbitMqProducer.EnqueueEmailsAsync(messages); } catch { }
                        });
                    }
                }

                if (req.SendN8nWebhook && injectedPayloads.Any())
                {
                    var webhookPayload = new
                    {
                        event_type = "company_session_nomination",
                        sessionId = (int)session.Id,
                        sessionName = (string)session.SessionName,
                        sessionDate = ((DateTime)session.SessionDate).ToString("yyyy-MM-dd"),
                        companyName = (string)(session.CompanyName ?? ""),
                        trainees = injectedPayloads
                    };

                    _ = Task.Run(async () =>
                    {
                        try { await _n8nService.TriggerCompanySessionNotificationAsync(webhookPayload); } catch { }
                    });
                }

                return Json(new
                {
                    success = true,
                    injectedCount = count,
                    message = $"تم تعيين {count} متدرب بنجاح بنظام التناوب الدوري العادل (Fair Rotation)!"
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "خطأ أثناء تنفيذ قاعدة الحقن: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ExportInjectionPreviewExcel([FromBody] ExportInjectionPreviewRequest req)
        {
            if (req == null || req.Candidates == null || !req.Candidates.Any())
            {
                return BadRequest(new { success = false, message = "لا توجد بيانات مرشحين للتصدير." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                var session = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT S.Id, S.SessionName, S.SessionDate, S.StartTime, S.EndTime, S.CompanyId, C.Name AS CompanyName
                    FROM AttendanceSessions S
                    LEFT JOIN Companies C ON S.CompanyId = C.Id
                    WHERE S.Id = @Id", new { Id = req.SessionId });

                string sessionName = (string)(session?.SessionName ?? "جلسة تدريب");
                string companyName = (string)(session?.CompanyName ?? "شركة التدريب");
                DateTime sessionDate = session?.SessionDate != null ? (DateTime)session.SessionDate : DateTime.Today;
                string startTimeStr = session?.StartTime != null ? FormatShiftTimeSpan((TimeSpan)session.StartTime) : "--";
                string endTimeStr = session?.EndTime != null ? FormatShiftTimeSpan((TimeSpan)session.EndTime) : "--";

                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("كشف ترشيحات الحضور");
                worksheet.RightToLeft = true;
                worksheet.ShowGridLines = true;
                worksheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;

                // 1. Header Banner (Rows 1-2)
                var titleRange = worksheet.Range("A1:K2");
                titleRange.Merge();
                titleRange.Value = "أكاديمية الطرشوبي للتدريب والتطوير — كشف ترشيحات الحضور والتحضير الفعلي (Fair Rotation)";
                titleRange.Style.Font.Bold = true;
                titleRange.Style.Font.FontSize = 14;
                titleRange.Style.Font.FontColor = XLColor.White;
                titleRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#58111A"); // Deep Luxury Maroon (نبيتي ملكي داكن)
                titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                // 2. Session Info Block (Rows 3-4)
                worksheet.Cell("A3").Value = "اسم الشركة الراعية:";
                worksheet.Cell("B3").Value = companyName;
                worksheet.Cell("D3").Value = "اسم الجلسة / المحاضرة:";
                worksheet.Cell("E3").Value = sessionName;
                worksheet.Cell("G3").Value = "تاريخ وموعد الجلسة:";
                worksheet.Cell("H3").Value = $"{sessionDate:yyyy-MM-dd} (من {startTimeStr} إلى {endTimeStr})";

                worksheet.Cell("A4").Value = "إجمالي المرشحين:";
                worksheet.Cell("B4").Value = $"{req.Candidates.Count} متدرب مرشح";
                worksheet.Cell("D4").Value = "تاريخ استخراج الكشف:";
                worksheet.Cell("E4").Value = $"{DateTime.Now:yyyy-MM-dd hh:mm tt}";
                worksheet.Cell("G4").Value = "نظام التوزيع:";
                worksheet.Cell("H4").Value = "التوزيع العادل التناوبي (الأقل حضوراً أولاً)";

                var infoRange = worksheet.Range("A3:K4");
                infoRange.Style.Font.FontSize = 10;
                infoRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF1F2"); // Soft Rose Tint
                infoRange.Style.Font.FontName = "Calibri";
                worksheet.Range("A3:A4").Style.Font.Bold = true;
                worksheet.Range("D3:D4").Style.Font.Bold = true;
                worksheet.Range("G3:G4").Style.Font.Bold = true;

                // 3. Table Headers (Row 6)
                int headerRow = 6;
                worksheet.Row(headerRow).Height = 28;
                var headers = new (string text, int col)[]
                {
                    ("م", 1),
                    ("كود الموظف", 2),
                    ("اسم المتدرب / الموظف", 3),
                    ("المسمى الوظيفي / الدور", 4),
                    ("القسم الإداري", 5),
                    ("الفرع", 6),
                    ("الشيفت", 7),
                    ("مرات الحضور السابقة", 8),
                    ("أولوية الاختيار", 9),
                    ("حالة التحضير الفعلي", 10),
                    ("توقيع المتدرب", 11)
                };

                foreach (var h in headers)
                {
                    var cell = worksheet.Cell(headerRow, h.col);
                    cell.Value = h.text;
                }

                var headerRange = worksheet.Range(headerRow, 1, headerRow, 11);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontSize = 11;
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#800020"); // Classic Burgundy / Maroon (لون نبيتي فخم)
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                // 4. Data Rows
                int currentRow = 7;
                int seq = 1;
                foreach (var c in req.Candidates)
                {
                    worksheet.Row(currentRow).Height = 24;
                    bool isEven = seq % 2 == 0;
                    var rowBg = isEven ? XLColor.FromHtml("#FFF5F6") : XLColor.White;

                    worksheet.Cell(currentRow, 1).SetValue(seq);
                    worksheet.Cell(currentRow, 2).SetValue(c.UserCode ?? "--");
                    worksheet.Cell(currentRow, 3).SetValue(c.FullName ?? "--");
                    worksheet.Cell(currentRow, 4).SetValue(c.JobTitle ?? "--");
                    worksheet.Cell(currentRow, 5).SetValue(c.DepartmentName ?? "--");
                    worksheet.Cell(currentRow, 6).SetValue(c.BranchName ?? "بدون فرع");
                    worksheet.Cell(currentRow, 7).SetValue(c.ShiftName ?? "غير محدد");
                    worksheet.Cell(currentRow, 8).SetValue(c.PreviousAttendedCount);

                    var priorityCell = worksheet.Cell(currentRow, 9);
                    if (c.PreviousAttendedCount == 0)
                    {
                        priorityCell.Value = "⭐ أولوية أولى (لم يحضر مسبقاً)";
                        priorityCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCFCE7"); // Soft Emerald
                        priorityCell.Style.Font.FontColor = XLColor.FromHtml("#15803D"); // Dark Green
                        priorityCell.Style.Font.Bold = true;
                    }
                    else
                    {
                        priorityCell.Value = $"حضر {c.PreviousAttendedCount} دورة سابقة";
                        priorityCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9"); // Slate light
                        priorityCell.Style.Font.FontColor = XLColor.FromHtml("#475569");
                    }

                    // Attendance check box for printing/marking on site
                    var statusCell = worksheet.Cell(currentRow, 10);
                    statusCell.Value = "[   ] حاضر        [   ] غائب";
                    statusCell.Style.Font.FontColor = XLColor.FromHtml("#64748B");

                    // Signature space
                    worksheet.Cell(currentRow, 11).Value = "";

                    // Row style
                    for (int col = 1; col <= 11; col++)
                    {
                        var cell = worksheet.Cell(currentRow, col);
                        if (col != 9) cell.Style.Fill.BackgroundColor = rowBg;
                        cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.RightBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.TopBorderColor = XLColor.FromHtml("#E2E8F0");
                        cell.Style.Border.BottomBorderColor = XLColor.FromHtml("#E2E8F0");
                        cell.Style.Border.LeftBorderColor = XLColor.FromHtml("#E2E8F0");
                        cell.Style.Border.RightBorderColor = XLColor.FromHtml("#E2E8F0");
                        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    }

                    worksheet.Cell(currentRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(currentRow, 3).Style.Font.Bold = true;
                    worksheet.Cell(currentRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(currentRow, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    currentRow++;
                    seq++;
                }

                // Summary Total Row (Clean cell-by-cell borders to prevent Excel OpenXML corruption)
                worksheet.Row(currentRow).Height = 26;
                worksheet.Cell(currentRow, 1).SetValue("المجموع");
                worksheet.Cell(currentRow, 3).SetValue($"إجمالي المرشحين بالكشف: {req.Candidates.Count} متدرب");
                for (int col = 1; col <= 11; col++)
                {
                    var cell = worksheet.Cell(currentRow, col);
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE4E6"); // Soft Maroon/Rose tint
                    cell.Style.Border.TopBorder = XLBorderStyleValues.Medium;
                    cell.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
                    cell.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
                    cell.Style.Border.RightBorder = XLBorderStyleValues.Medium;
                    cell.Style.Border.TopBorderColor = XLColor.FromHtml("#800020"); // Maroon Border
                    cell.Style.Border.BottomBorderColor = XLColor.FromHtml("#800020");
                    cell.Style.Border.LeftBorderColor = XLColor.FromHtml("#800020");
                    cell.Style.Border.RightBorderColor = XLColor.FromHtml("#800020");
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                }
                worksheet.Cell(currentRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                worksheet.Cell(currentRow, 1).Style.Font.FontColor = XLColor.FromHtml("#800020");
                worksheet.Cell(currentRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                worksheet.Cell(currentRow, 3).Style.Font.FontColor = XLColor.FromHtml("#800020");

                // Auto-fit columns with safety minimums
                worksheet.Columns().AdjustToContents();
                worksheet.Column(1).Width = Math.Max(worksheet.Column(1).Width, 6);
                worksheet.Column(2).Width = Math.Max(worksheet.Column(2).Width, 14);
                worksheet.Column(3).Width = Math.Max(worksheet.Column(3).Width, 30);
                worksheet.Column(4).Width = Math.Max(worksheet.Column(4).Width, 20);
                worksheet.Column(5).Width = Math.Max(worksheet.Column(5).Width, 20);
                worksheet.Column(6).Width = Math.Max(worksheet.Column(6).Width, 18);
                worksheet.Column(7).Width = Math.Max(worksheet.Column(7).Width, 22);
                worksheet.Column(8).Width = Math.Max(worksheet.Column(8).Width, 18);
                worksheet.Column(9).Width = Math.Max(worksheet.Column(9).Width, 26);
                worksheet.Column(10).Width = Math.Max(worksheet.Column(10).Width, 24);
                worksheet.Column(11).Width = Math.Max(worksheet.Column(11).Width, 20);

                using var memoryStream = new MemoryStream();
                workbook.SaveAs(memoryStream);
                memoryStream.Position = 0;

                string cleanSessionName = string.Join("_", sessionName.Split(Path.GetInvalidFileNameChars()));
                string fileName = $"Trainees_Roster_{cleanSessionName}_{sessionDate:yyyyMMdd}.xlsx";

                return File(memoryStream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, message = "خطأ أثناء إنشاء ملف الإكسيل: " + ex.Message });
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
        public bool SendRabbitMqEmail { get; set; } = true;
    }

    public class SaveCompanyInjectionRuleRequest
    {
        public int? Id { get; set; }
        public int? CompanyId { get; set; }
        public string? RuleName { get; set; }
        public string? QuotasJson { get; set; }
        public string? DepartmentQuotasJson { get; set; }
    }

    public class UpdateSessionTotalSeatsRequest
    {
        public int SessionId { get; set; }
        public int? TotalSeats { get; set; }
    }
}



