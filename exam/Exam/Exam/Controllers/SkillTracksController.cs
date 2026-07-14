using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.AspNetCore.Http;
using ClosedXML.Excel;
using System;
using Microsoft.AspNetCore.Identity;

namespace Exam.Controllers
{
    [Authorize(Roles = "Admin,SoftSkills Specialist,Reception")]
    public class SkillTracksController : Controller
    {
        private readonly string _connectionString;

        public SkillTracksController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        private async Task<dynamic> GetDefaultTrack(SqlConnection conn)
        {
            var track = await conn.QueryFirstOrDefaultAsync("SELECT * FROM SkillTracks WHERE TrackName = 'SoftSkills Track'");
            if (track == null)
            {
                await conn.ExecuteAsync("INSERT INTO SkillTracks (TrackName) VALUES ('SoftSkills Track')");
                track = await conn.QueryFirstOrDefaultAsync("SELECT * FROM SkillTracks WHERE TrackName = 'SoftSkills Track'");
            }
            return track;
        }

        public async Task<IActionResult> Index()
        {
            if (User.IsInRole("Reception") && !User.IsInRole("Admin") && !User.IsInRole("SoftSkills Specialist"))
            {
                return RedirectToAction("Index", "Attendance");
            }

            using var conn = new SqlConnection(_connectionString);
            var track = await GetDefaultTrack(conn);

            var sessions = await conn.QueryAsync(@"
                SELECT S.*, 
                    (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id) as EnrolledCount,
                    (SELECT COUNT(*) FROM UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1) as PresentCount
                FROM AttendanceSessions S
                WHERE S.SkillTrackId = @Id
                ORDER BY S.SessionDate DESC", new { Id = track.Id });

            ViewBag.Track = track;
            
            // Get all other sessions for cloning
            ViewBag.AllSessions = sessions.ToList();
            
            return View(sessions);
        }

        [HttpPost]
        public async Task<IActionResult> CreateSession(DateTime sessionDate, string sessionName)
        {
            using var conn = new SqlConnection(_connectionString);
            var track = await GetDefaultTrack(conn);
            await conn.ExecuteAsync("INSERT INTO AttendanceSessions (SkillTrackId, SessionDate, SessionName) VALUES (@TrackId, @Date, @Name)", 
                new { TrackId = track.Id, Date = sessionDate, Name = sessionName });
            return Json(new { success = true });
        }
        
        [HttpPost]
        public async Task<IActionResult> DeleteSession(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM UserAttendance WHERE SessionId = @Sid", new { Sid = sessionId });
            await conn.ExecuteAsync("DELETE FROM AttendanceSessions WHERE Id = @Sid", new { Sid = sessionId });
            return Json(new { success = true });
        }

        // ========================== PER SESSION TRAINEES ==========================
        
        [HttpGet]
        public async Task<IActionResult> GetSessionTrainees(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var users = await conn.QueryAsync(@"
                SELECT UA.Id as LinkId, U.Id as UserId, U.FullName as UserName, U.UserCode, U.Email, B.BranchName
                FROM UserAttendance UA
                JOIN AspNetUsers U ON UA.UserId = U.Id
                LEFT JOIN Branches B ON U.BranchId = B.Id
                WHERE UA.SessionId = @Id
                ORDER BY U.FullName", new { Id = sessionId });
            return Json(users);
        }

        [HttpPost]
        public async Task<IActionResult> RemoveSessionTrainee(int linkId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM UserAttendance WHERE Id = @Id", new { Id = linkId });
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> LoadManagersAndHR(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var users = await conn.QueryAsync<string>(@"
                SELECT U.Id 
                FROM AspNetUsers U
                JOIN AspNetUserRoles UR ON U.Id = UR.UserId
                JOIN AspNetRoles R ON UR.RoleId = R.Id
                WHERE R.Name IN ('Branch Manager', 'Branch Supervisor', 'HR', 'Human Resources')
            ");

            int added = 0;
            foreach (var uid in users.Distinct())
            {
                var exists = await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(1) FROM UserAttendance WHERE SessionId = @Sid AND UserId = @Uid", new { Sid = sessionId, Uid = uid });
                if (exists == 0)
                {
                    await conn.ExecuteAsync("INSERT INTO UserAttendance (SessionId, UserId, IsPresent, RecordedBy) VALUES (@Sid, @Uid, 0, 'System')", new { Sid = sessionId, Uid = uid });
                    added++;
                }
            }
            return Json(new { success = true, count = added });
        }

        [HttpPost]
        public async Task<IActionResult> CloneTrainees(int targetSessionId, int sourceSessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var sourceUsers = await conn.QueryAsync<string>("SELECT UserId FROM UserAttendance WHERE SessionId = @SourceId", new { SourceId = sourceSessionId });
            int added = 0;
            foreach (var uid in sourceUsers)
            {
                var exists = await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(1) FROM UserAttendance WHERE SessionId = @TargetId AND UserId = @Uid", new { TargetId = targetSessionId, Uid = uid });
                if (exists == 0)
                {
                    await conn.ExecuteAsync("INSERT INTO UserAttendance (SessionId, UserId, IsPresent, RecordedBy) VALUES (@Sid, @Uid, 0, 'System')", new { Sid = targetSessionId, Uid = uid });
                    added++;
                }
            }
            return Json(new { success = true, count = added });
        }

        [HttpGet]
        public async Task<IActionResult> GetTraineeDetailsByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return Json(new { exists = false });
            using var conn = new SqlConnection(_connectionString);
            var user = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT u.Id, u.FullName, u.UserName, u.Email, u.PhoneNumber, r.Name AS RoleName, b.BranchName
                FROM AspNetUsers u
                LEFT JOIN AspNetUserRoles ur ON u.Id = ur.UserId
                LEFT JOIN AspNetRoles r ON ur.RoleId = r.Id
                LEFT JOIN Branches b ON u.BranchId = b.Id
                WHERE u.UserCode = @Code", new { Code = code });
            if (user != null) return Json(new { exists = true, data = user });
            return Json(new { exists = false });
        }

        [HttpPost]
        public async Task<IActionResult> AddTraineeToSession(int sessionId, string fullName, string userCode, string jobTitle, string branchName, string email, string phone)
        {
            using var conn = new SqlConnection(_connectionString);
            string userId = await conn.QueryFirstOrDefaultAsync<string>("SELECT Id FROM AspNetUsers WHERE UserCode = @Code", new { Code = userCode });
            
            if (userId == null)
            {
                // Create user logic
                userId = Guid.NewGuid().ToString();
                int? branchId = null;
                if (!string.IsNullOrEmpty(branchName))
                {
                    branchId = await conn.QueryFirstOrDefaultAsync<int?>("SELECT Id FROM Branches WHERE BranchName = @BranchName", new { BranchName = branchName });
                }

                var hasher = new PasswordHasher<IdentityUser>();
                var hash = hasher.HashPassword(null, "Trshoob@12345");

                await conn.ExecuteAsync(@"
                    INSERT INTO AspNetUsers (Id, FullName, UserCode, Email, PhoneNumber, BranchId, UserName, NormalizedUserName, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp)
                    VALUES (@Id, @FullName, @UserCode, @Email, @Phone, @BranchId, @UserCode, UPPER(@UserCode), UPPER(@Email), 1, @Hash, @Stamp)
                ", new { Id = userId, FullName = fullName, UserCode = userCode, Email = email, Phone = phone, BranchId = branchId, Hash = hash, Stamp = Guid.NewGuid().ToString() });
            }

            var exists = await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(1) FROM UserAttendance WHERE SessionId = @Sid AND UserId = @Uid", new { Sid = sessionId, Uid = userId });
            if (exists == 0)
            {
                await conn.ExecuteAsync("INSERT INTO UserAttendance (SessionId, UserId, IsPresent, RecordedBy) VALUES (@Sid, @Uid, 0, 'System')", new { Sid = sessionId, Uid = userId });
                return Json(new { success = true, message = "Trainee added successfully." });
            }
            return Json(new { success = false, message = "Trainee is already in this session." });
        }

        [HttpPost]
        public async Task<IActionResult> ImportUsersToSession(int sessionId, IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0) {
                return Json(new { success = false, message = "No file selected." });
            }

            using var conn = new SqlConnection(_connectionString);
            using var stream = excelFile.OpenReadStream();
            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheets.First();
            var rows = worksheet.RowsUsed().Skip(1); 
            int count = 0;

            foreach (var row in rows)
            {
                var userCode = row.Cell(1).Value.ToString().Trim();
                if (!string.IsNullOrEmpty(userCode))
                {
                    var userId = await conn.QueryFirstOrDefaultAsync<string>("SELECT Id FROM AspNetUsers WHERE UserCode = @Code", new { Code = userCode });
                    if (userId != null)
                    {
                        var exists = await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(1) FROM UserAttendance WHERE SessionId = @Sid AND UserId = @UId", new { Sid = sessionId, Uid = userId });
                        if (exists == 0)
                        {
                            await conn.ExecuteAsync("INSERT INTO UserAttendance (SessionId, UserId, IsPresent, RecordedBy) VALUES (@Sid, @UId, 0, 'System')", new { Sid = sessionId, Uid = userId });
                            count++;
                        }
                    }
                }
            }

            return Json(new { success = true, count = count });
        }

        public async Task<IActionResult> TakeAttendance(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var session = await conn.QueryFirstOrDefaultAsync("SELECT S.*, ST.TrackName FROM AttendanceSessions S JOIN SkillTracks ST ON S.SkillTrackId = ST.Id WHERE S.Id = @Id", new { Id = sessionId });
            if (session == null) return NotFound();

            var users = await conn.QueryAsync(@"
                SELECT U.Id, U.FullName as UserName, U.Email, U.UserCode, B.BranchName,
                       ISNULL(UA.IsPresent, 0) as IsPresent,
                       UA.CheckInTime, UA.CheckOutTime
                FROM UserAttendance UA
                JOIN AspNetUsers U ON UA.UserId = U.Id
                LEFT JOIN Branches B ON U.BranchId = B.Id
                WHERE UA.SessionId = @SessionId
                ORDER BY U.FullName", new { SessionId = sessionId });

            ViewBag.Session = session;
            return View(users);
        }

        [HttpGet]
        public async Task<IActionResult> ExportSessionAttendance(int sessionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var session = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT S.*, ST.TrackName FROM AttendanceSessions S JOIN SkillTracks ST ON S.SkillTrackId = ST.Id WHERE S.Id = @Id", new { Id = sessionId });
            
            if (session == null) return NotFound();

            var users = await conn.QueryAsync<dynamic>(@"
                SELECT U.Id, ISNULL(NULLIF(U.FullName, ''), U.UserName) as UserName, U.Email, U.UserCode, B.BranchName,
                       ISNULL(UA.IsPresent, 0) as IsPresent,
                       UA.CheckInTime, UA.CheckOutTime
                FROM UserAttendance UA
                JOIN AspNetUsers U ON UA.UserId = U.Id
                LEFT JOIN Branches B ON U.BranchId = B.Id
                WHERE UA.SessionId = @SessionId
                ORDER BY U.FullName", new { SessionId = sessionId });

            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Session Attendance");
                
                worksheet.Cell(1, 1).Value = $"Session Attendance: {session.TrackName} - {session.SessionName}";
                worksheet.Cell(1, 1).Style.Font.Bold = true;
                worksheet.Cell(1, 1).Style.Font.FontSize = 14;

                worksheet.Cell(2, 1).Value = $"Date: {session.SessionDate:MMMM dd, yyyy}";
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
                headerRange.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#1E293B");
                headerRange.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
                headerRange.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                int rowIdx = 5;
                foreach (var u in users)
                {
                    worksheet.Cell(rowIdx, 1).Value = u.UserName;
                    worksheet.Cell(rowIdx, 2).Value = u.UserCode;
                    worksheet.Cell(rowIdx, 3).Value = u.BranchName ?? "-";
                    
                    bool isPresent = u.IsPresent?.ToString() == "1" || u.IsPresent?.ToString() == "True";
                    worksheet.Cell(rowIdx, 4).Value = isPresent ? "Present" : "Absent";
                    worksheet.Cell(rowIdx, 4).Style.Font.FontColor = isPresent ? ClosedXML.Excel.XLColor.Green : ClosedXML.Excel.XLColor.Red;
                    worksheet.Cell(rowIdx, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                    worksheet.Cell(rowIdx, 5).SetValue(u.CheckInTime?.ToString() ?? "-");
                    worksheet.Cell(rowIdx, 5).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                    worksheet.Cell(rowIdx, 6).SetValue(u.CheckOutTime?.ToString() ?? "-");
                    worksheet.Cell(rowIdx, 6).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                    rowIdx++;
                }

                worksheet.Columns().AdjustToContents();
                worksheet.Column(1).Width = 30;

                using (var stream = new System.IO.MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 
                        $"Attendance_{((string)session.TrackName).Replace(" ", "_")}_{((string)session.SessionName).Replace(" ", "_")}_{session.SessionDate:yyyyMMdd}.xlsx");
                }
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateDetailedAttendance(int sessionId, string userId, bool isPresent, string checkInTime, string checkOutTime)
        {
            using var conn = new SqlConnection(_connectionString);
            string recordedBy = User.Identity.Name ?? "System";
            
            // User MUST exist in UserAttendance to update their check in/out
            await conn.ExecuteAsync(@"
                UPDATE UserAttendance 
                SET IsPresent = @IsPresent, CheckInTime = @CheckIn, CheckOutTime = @CheckOut, RecordedBy = @By
                WHERE SessionId = @Sid AND UserId = @Uid", new { Sid = sessionId, Uid = userId, IsPresent = isPresent, CheckIn = checkInTime, CheckOut = checkOutTime, By = recordedBy });

            return Json(new { success = true });
        }

        public async Task<IActionResult> Analytics()
        {
            using var conn = new SqlConnection(_connectionString);
            var track = await GetDefaultTrack(conn);
            
            var stats = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    COUNT(DISTINCT S.Id) as TotalSessions,
                    COUNT(UA.Id) as TotalChecks,
                    SUM(CASE WHEN UA.IsPresent = 1 THEN 1 ELSE 0 END) as PresentCount,
                    SUM(CASE WHEN UA.IsPresent = 0 THEN 1 ELSE 0 END) as AbsentCount
                FROM AttendanceSessions S
                LEFT JOIN UserAttendance UA ON S.Id = UA.SessionId
                WHERE S.SkillTrackId = @Id", new { Id = track.Id });

            var logs = await conn.QueryAsync<dynamic>(@"
                SELECT TOP 1000 
                    U.FullName, U.UserCode, 
                    'SkillTrack' as SessionType,
                    ST.TrackName as GroupName,
                    S.SessionName, S.SessionDate,
                    ISNULL(UA.IsPresent, 0) as IsPresent,
                    UA.CheckInTime, UA.CheckOutTime
                FROM UserAttendance UA
                JOIN AttendanceSessions S ON UA.SessionId = S.Id
                JOIN SkillTracks ST ON S.SkillTrackId = ST.Id
                JOIN AspNetUsers U ON UA.UserId = U.Id
                WHERE S.SkillTrackId = @Id
                ORDER BY S.SessionDate DESC", new { Id = track.Id });

            ViewBag.Stats = stats ?? new { TotalSessions = 0, TotalChecks = 0, PresentCount = 0, AbsentCount = 0 };
            return View(logs);
        }
    }
}

