using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Dapper;
using Exam.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Exam.Controllers
{
    [Authorize]
    public class NotificationController : Controller
    {
        private readonly string _connectionString;
        private readonly IHubContext<NotificationHub> _hubContext;

        public NotificationController(IConfiguration configuration, IHubContext<NotificationHub> hubContext)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")!;
            _hubContext = hubContext;
        }

        private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        private List<string> CurrentUserRoles => User.Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
            .Select(c => c.Value)
            .ToList();

        [HttpGet]
        public async Task<IActionResult> GetHeaderNotifications()
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var userId = CurrentUserId;
                var roles = CurrentUserRoles;
                bool isAdminOrHr = roles.Contains("Admin") || roles.Contains("HR") || roles.Contains("Human Resources");

                // Get user's branch
                int? userBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT BranchId FROM AspNetUsers WHERE Id = @UserId", new { UserId = userId });

                // 1. Unread notifications
                string notifSql = @"
                    SELECT TOP 15 
                        Id, Title, Message, LinkUrl, Type, ReferenceId, IsRead, CreatedAt, BranchId
                    FROM dbo.AppNotifications
                    WHERE (UserId = @UserId 
                           OR (BranchId IS NOT NULL AND BranchId = @BranchId)
                           OR (RoleTarget IS NOT NULL AND RoleTarget IN @Roles))
                    ORDER BY IsRead ASC, CreatedAt DESC";

                var notifs = (await conn.QueryAsync<dynamic>(notifSql, new
                {
                    UserId = userId,
                    BranchId = userBranchId,
                    Roles = roles.Any() ? roles : new List<string> { "__NONE__" }
                })).ToList();

                int unreadCount = notifs.Count(n => n.IsRead == false);

                // 2. Pending wave & company nominations count
                int pendingWaveCount = 0;
                int pendingCompanyCount = 0;
                if (isAdminOrHr)
                {
                    pendingWaveCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM dbo.UserWaves WHERE ApprovalStatus = 'Pending'");
                    pendingCompanyCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM dbo.CompanySessionTrainees WHERE ApprovalStatus = 'Pending'");
                }
                else if (userBranchId.HasValue)
                {
                    pendingWaveCount = await conn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(1) 
                        FROM dbo.UserWaves uw
                        JOIN AspNetUsers u ON uw.UserId = u.Id
                        WHERE uw.ApprovalStatus = 'Pending' AND u.BranchId = @BranchId",
                        new { BranchId = userBranchId.Value });

                    pendingCompanyCount = await conn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(1)
                        FROM dbo.CompanySessionTrainees cst
                        JOIN dbo.CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id
                        JOIN dbo.Branches b ON b.BranchName = ct.BranchName
                        WHERE cst.ApprovalStatus = 'Pending' AND b.Id = @BranchId",
                        new { BranchId = userBranchId.Value });
                }

                int totalPending = pendingWaveCount + pendingCompanyCount;

                return Json(new
                {
                    success = true,
                    unreadCount = unreadCount + totalPending,
                    pendingNominationsCount = totalPending,
                    pendingWaveCount = pendingWaveCount,
                    pendingCompanyCount = pendingCompanyCount,
                    userBranchId = userBranchId,
                    notifications = notifs.Select(n => new
                    {
                        id = (int)n.Id,
                        title = (string)n.Title,
                        message = (string)n.Message,
                        linkUrl = (string)n.LinkUrl,
                        type = (string)n.Type,
                        isRead = (bool)n.IsRead,
                        createdAt = ((DateTime)n.CreatedAt).ToString("yyyy-MM-dd HH:mm")
                    })
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> MarkAsRead(int? id)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                var userId = CurrentUserId;

                if (id.HasValue && id.Value > 0)
                {
                    await conn.ExecuteAsync("UPDATE dbo.AppNotifications SET IsRead = 1 WHERE Id = @Id", new { Id = id.Value });
                }
                else
                {
                    await conn.ExecuteAsync("UPDATE dbo.AppNotifications SET IsRead = 1 WHERE UserId = @UserId", new { UserId = userId });
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetPendingBranchNominations(int? waveId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var userId = CurrentUserId;
                var roles = CurrentUserRoles;
                bool isAdminOrHr = roles.Contains("Admin") || roles.Contains("HR") || roles.Contains("Human Resources");

                int? userBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT BranchId FROM AspNetUsers WHERE Id = @UserId", new { UserId = userId });

                if (!isAdminOrHr && !userBranchId.HasValue)
                {
                    return Json(new { success = true, nominations = new List<object>() });
                }

                string sql = @"
                    SELECT 
                        uw.Id AS UserWaveId,
                        uw.UserId,
                        ISNULL(u.FullName, u.UserName) AS StudentName,
                        u.UserName,
                        ISNULL(u.UserCode, '--') AS UserCode,
                        ISNULL(u.PhoneNumber, '--') AS PhoneNumber,
                        ISNULL(u.Email, '--') AS Email,
                        u.BranchId,
                        ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
                        uw.WaveId,
                        tw.WaveName,
                        tw.StartDate,
                        tw.EndDate,
                        tw.Mode,
                        uw.ApprovalStatus,
                        uw.JoinDate
                    FROM dbo.UserWaves uw WITH (NOLOCK)
                    JOIN AspNetUsers u WITH (NOLOCK) ON uw.UserId = u.Id
                    LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                    JOIN dbo.TrainingWaves tw WITH (NOLOCK) ON uw.WaveId = tw.Id
                    WHERE uw.ApprovalStatus = 'Pending'
                      AND (@WaveId IS NULL OR uw.WaveId = @WaveId)
                      AND (@IsAdmin = 1 OR u.BranchId = @BranchId)
                    ORDER BY tw.StartDate ASC, b.BranchName ASC, u.FullName ASC";

                var waveNominations = (await conn.QueryAsync<dynamic>(sql, new
                {
                    WaveId = waveId,
                    IsAdmin = isAdminOrHr ? 1 : 0,
                    BranchId = userBranchId
                })).Select(n => new
                {
                    userWaveId = (int)n.UserWaveId,
                    userId = (string)n.UserId,
                    studentName = (string)n.StudentName,
                    userName = (string)n.UserName,
                    userCode = (string)n.UserCode,
                    phoneNumber = (string)n.PhoneNumber,
                    email = (string)n.Email,
                    branchId = (int?)n.BranchId,
                    branchName = (string)n.BranchName,
                    waveId = (int)n.WaveId,
                    waveName = (string)n.WaveName,
                    mode = (string)(n.Mode ?? "Offline"),
                    startDate = n.StartDate != null ? ((DateTime)n.StartDate).ToString("yyyy-MM-dd") : "--",
                    endDate = n.EndDate != null ? ((DateTime)n.EndDate).ToString("yyyy-MM-dd") : "--",
                    approvalStatus = (string)n.ApprovalStatus,
                    joinDate = ((DateTime)n.JoinDate).ToString("yyyy-MM-dd"),
                    type = "wave"
                }).ToList();

                // 2. Pending company sessions nominations
                string companySql = @"
                    SELECT 
                        cst.Id AS CompanySessionTraineeId,
                        cst.SessionId,
                        cst.CompanyTraineeId,
                        ct.FullName AS TraineeName,
                        ISNULL(ct.UserCode, '--') AS UserCode,
                        ISNULL(ct.JobTitle, 'Trainee') AS JobTitle,
                        ISNULL(ct.BranchName, N'بدون فرع') AS BranchName,
                        ISNULL(ct.Email, '--') AS Email,
                        ISNULL(ct.Phone, '--') AS PhoneNumber,
                        b.Id AS BranchId,
                        s.SessionName,
                        s.SessionDate,
                        s.StartTime,
                        s.EndTime,
                        c.Id AS CompanyId,
                        ISNULL(c.Name, 'Company') AS CompanyName,
                        cst.ApprovalStatus,
                        cst.AssignedAt
                    FROM dbo.CompanySessionTrainees cst
                    JOIN dbo.CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id
                    LEFT JOIN dbo.Branches b ON b.BranchName = ct.BranchName
                    JOIN dbo.AttendanceSessions s ON cst.SessionId = s.Id
                    LEFT JOIN dbo.Companies c ON s.CompanyId = c.Id
                    WHERE cst.ApprovalStatus = 'Pending'
                      AND (@IsAdmin = 1 OR b.Id = @BranchId)
                    ORDER BY s.SessionDate ASC, ct.FullName ASC";

                var companyNominations = (await conn.QueryAsync<dynamic>(companySql, new
                {
                    IsAdmin = isAdminOrHr ? 1 : 0,
                    BranchId = userBranchId
                })).Select(c => new
                {
                    companySessionTraineeId = (int)c.CompanySessionTraineeId,
                    userWaveId = (int)c.CompanySessionTraineeId, // alias for common ID
                    sessionId = (int)c.SessionId,
                    companyTraineeId = (int)c.CompanyTraineeId,
                    traineeName = (string)c.TraineeName,
                    studentName = (string)c.TraineeName,
                    userCode = (string)c.UserCode,
                    jobTitle = (string)c.JobTitle,
                    branchName = (string)c.BranchName,
                    branchId = (int?)c.BranchId,
                    email = (string)c.Email,
                    phoneNumber = (string)c.PhoneNumber,
                    sessionName = (string)c.SessionName,
                    companyId = (int?)c.CompanyId,
                    companyName = (string)c.CompanyName,
                    sessionDate = c.SessionDate != null ? ((DateTime)c.SessionDate).ToString("yyyy-MM-dd") : "--",
                    sessionTime = c.StartTime != null ? $"{((TimeSpan)c.StartTime):hh\\:mm} - {((TimeSpan)c.EndTime):hh\\:mm}" : "",
                    approvalStatus = (string)c.ApprovalStatus,
                    assignedAt = ((DateTime)c.AssignedAt).ToString("yyyy-MM-dd"),
                    type = "company"
                }).ToList();

                return Json(new
                {
                    success = true,
                    nominations = waveNominations,
                    waveNominations = waveNominations,
                    companyNominations = companyNominations,
                    totalPending = waveNominations.Count + companyNominations.Count
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAvailableBranchColleagues(int branchId, int waveId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                string sql = @"
                    SELECT 
                        u.Id,
                        ISNULL(u.FullName, u.UserName) AS FullName,
                        u.UserName,
                        ISNULL(u.UserCode, '--') AS UserCode,
                        ISNULL(r.Name, 'User') AS RoleName
                    FROM AspNetUsers u WITH (NOLOCK)
                    LEFT JOIN AspNetUserRoles ur ON u.Id = ur.UserId
                    LEFT JOIN AspNetRoles r ON ur.RoleId = r.Id
                    WHERE u.BranchId = @BranchId
                      AND u.IsActive = 1
                      AND NOT EXISTS (SELECT 1 FROM dbo.UserWaves uw WHERE uw.UserId = u.Id AND uw.WaveId = @WaveId AND uw.ApprovalStatus IN ('Approved', 'Pending'))
                    ORDER BY u.FullName ASC";

                var colleagues = (await conn.QueryAsync<dynamic>(sql, new { BranchId = branchId, WaveId = waveId })).ToList();
                return Json(new { success = true, colleagues });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAvailableCompanyColleagues(int sessionId, int companyId, string branchName)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                string sql = @"
                    SELECT ct.Id, ct.FullName, ct.UserCode, ct.JobTitle, ct.BranchName
                    FROM CompanyTrainees ct
                    WHERE ct.CompanyId = @CompanyId
                      AND ct.BranchName = @BranchName
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.CompanySessionTrainees cst
                          WHERE cst.SessionId = @SessionId 
                            AND cst.CompanyTraineeId = ct.Id 
                            AND cst.ApprovalStatus IN ('Approved', 'Pending')
                      )
                    ORDER BY ct.FullName ASC";

                var colleagues = (await conn.QueryAsync<dynamic>(sql, new { SessionId = sessionId, CompanyId = companyId, BranchName = branchName })).ToList();
                return Json(new { success = true, colleagues });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetBranchesApprovalSettings()
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                var branches = await conn.QueryAsync<dynamic>(
                    "SELECT Id, BranchName, ISNULL(BypassApproval, 0) as BypassApproval FROM Branches ORDER BY BranchName");
                return Json(new { success = true, branches });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ToggleBranchBypassApproval(int branchId, bool bypass)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    "UPDATE Branches SET BypassApproval = @Bypass WHERE Id = @Id",
                    new { Bypass = bypass ? 1 : 0, Id = branchId });
                return Json(new { success = true, message = "تم تحديث إعدادات الاعتماد المباشر للفرع بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ActionNomination([FromBody] NominationActionRequest req)
        {
            if (req == null || req.UserWaveId <= 0 || string.IsNullOrEmpty(req.Action))
            {
                return Json(new { success = false, message = "بيانات الطلب غير صالحة." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var currentUserId = CurrentUserId;
                var roles = CurrentUserRoles;
                bool isAdminOrHr = roles.Contains("Admin") || roles.Contains("HR") || roles.Contains("Human Resources");
                string action = req.Action.Trim().ToLowerInvariant();
                string nomType = (req.Type ?? "wave").ToLowerInvariant().Trim();

                // ----------------------------------------------------
                // 1. Handling Company Session Trainee Nominations
                // ----------------------------------------------------
                if (nomType == "company")
                {
                    var cNomination = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT cst.Id, cst.SessionId, cst.CompanyTraineeId, cst.ApprovalStatus, ct.FullName AS TraineeName,
                               ct.BranchName, b.Id AS BranchId, s.SessionName, c.Name AS CompanyName, s.CompanyId
                        FROM dbo.CompanySessionTrainees cst
                        JOIN dbo.CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id
                        LEFT JOIN dbo.Branches b ON b.BranchName = ct.BranchName
                        JOIN dbo.AttendanceSessions s ON cst.SessionId = s.Id
                        LEFT JOIN dbo.Companies c ON s.CompanyId = c.Id
                        WHERE cst.Id = @Id", new { Id = req.UserWaveId });

                    if (cNomination == null)
                    {
                        return Json(new { success = false, message = "لم يتم العثور على سجل متدرب الشركة." });
                    }

                    if (!isAdminOrHr)
                    {
                        int? managerBranch = await conn.QueryFirstOrDefaultAsync<int?>(
                            "SELECT BranchId FROM AspNetUsers WHERE Id = @UserId", new { UserId = currentUserId });
                        if (managerBranch != (int?)cNomination.BranchId)
                        {
                            return Json(new { success = false, message = "ليس لديك صلاحية اعتماد متدربي هذا الفرع." });
                        }
                    }

                    if (action == "accept" || action == "approve")
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.CompanySessionTrainees 
                            SET ApprovalStatus = 'Approved',
                                ActionedByUserId = @ActionedBy,
                                ActionedAt = GETDATE()
                            WHERE Id = @Id",
                            new { Id = req.UserWaveId, ActionedBy = currentUserId });

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                            VALUES ('Admin', N'اعتماد حضور متدرب شركة ✅', CONCAT(N'تم اعتماد حضور: ', @TraineeName, N' من فرع: ', @BranchName, N' لسيشن: ', @SessionName), CONCAT('/Attendance/ManageSessions?companyId=', @CompanyId), 'CompanySessionApproved', CAST(@Id AS NVARCHAR(50)), GETDATE())",
                            new {
                                TraineeName = (string)cNomination.TraineeName,
                                BranchName = (string)(cNomination.BranchName ?? "الفرع"),
                                SessionName = (string)cNomination.SessionName,
                                CompanyId = (int?)cNomination.CompanyId,
                                Id = req.UserWaveId
                            });

                        await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات تدريب الشركة.");
                        return Json(new { success = true, message = $"تم اعتماد المتدرب {cNomination.TraineeName} لحضور السيشن بنجاح." });
                    }
                    else if (action == "reject")
                    {
                        if (string.IsNullOrWhiteSpace(req.Reason))
                        {
                            return Json(new { success = false, message = "يرجى كتابة سبب الرفض لتوضيحه للإدارة." });
                        }

                        await conn.ExecuteAsync(@"
                            UPDATE dbo.CompanySessionTrainees 
                            SET ApprovalStatus = 'Rejected',
                                RejectionReason = @Reason,
                                ActionedByUserId = @ActionedBy,
                                ActionedAt = GETDATE()
                            WHERE Id = @Id",
                            new { Id = req.UserWaveId, Reason = req.Reason.Trim(), ActionedBy = currentUserId });

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                            VALUES ('Admin', N'اعتذار/رفض متدرب شركة ❌', CONCAT(N'قام مدير الفرع برفض حضور: ', @TraineeName, N' (', @BranchName, N') لسيشن: ', @SessionName, N'. السبب: ', @Reason), CONCAT('/Attendance/ManageSessions?companyId=', @CompanyId), 'CompanySessionRejected', CAST(@Id AS NVARCHAR(50)), GETDATE())",
                            new {
                                TraineeName = (string)cNomination.TraineeName,
                                BranchName = (string)(cNomination.BranchName ?? "الفرع"),
                                SessionName = (string)cNomination.SessionName,
                                Reason = req.Reason.Trim(),
                                CompanyId = (int?)cNomination.CompanyId,
                                Id = req.UserWaveId
                            });

                        await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات تدريب الشركة.");
                        return Json(new { success = true, message = $"تم تسجيل اعتذار حضور المتدرب {cNomination.TraineeName} مع توضيح السبب." });
                    }
                    else if (action == "replace")
                    {
                        if (!req.ReplacementTraineeId.HasValue || req.ReplacementTraineeId.Value <= 0)
                        {
                            return Json(new { success = false, message = "يرجى اختيار المتدرب البديل من الفرع." });
                        }

                        var repTrainee = await conn.QueryFirstOrDefaultAsync<dynamic>(
                            "SELECT Id, FullName FROM CompanyTrainees WHERE Id = @Id",
                            new { Id = req.ReplacementTraineeId.Value });

                        if (repTrainee == null)
                        {
                            return Json(new { success = false, message = "المتدرب البديل المحدد غير موجود." });
                        }

                        // Mark old as Replaced
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.CompanySessionTrainees 
                            SET ApprovalStatus = 'Replaced',
                                ReplacedWithTraineeId = @ReplacementId,
                                ActionedByUserId = @ActionedBy,
                                ActionedAt = GETDATE()
                            WHERE Id = @Id",
                            new { Id = req.UserWaveId, ReplacementId = req.ReplacementTraineeId.Value, ActionedBy = currentUserId });

                        // Insert new as Approved
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.CompanySessionTrainees (SessionId, CompanyTraineeId, AssignedAt, AssignedBy, ApprovalStatus, ActionedByUserId, ActionedAt)
                            VALUES (@SessionId, @TraineeId, GETDATE(), @ActionedBy, 'Approved', @ActionedBy, GETDATE())",
                            new {
                                SessionId = (int)cNomination.SessionId,
                                TraineeId = req.ReplacementTraineeId.Value,
                                ActionedBy = currentUserId
                            });

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                            VALUES ('Admin', N'استبدال متدرب في سيشن شركة 🔄', CONCAT(N'تم استبدال: ', @OldTrainee, N' بـ: ', @NewTrainee, N' في سيشن: ', @SessionName), CONCAT('/Attendance/ManageSessions?companyId=', @CompanyId), 'CompanySessionReplaced', CAST(@Id AS NVARCHAR(50)), GETDATE())",
                            new {
                                OldTrainee = (string)cNomination.TraineeName,
                                NewTrainee = (string)repTrainee.FullName,
                                SessionName = (string)cNomination.SessionName,
                                CompanyId = (int?)cNomination.CompanyId,
                                Id = req.UserWaveId
                            });

                        await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات تدريب الشركة.");
                        return Json(new { success = true, message = $"تم استبدال {cNomination.TraineeName} بـ {repTrainee.FullName} واعتماده للسيشن بنجاح." });
                    }
                }

                // ----------------------------------------------------
                // 2. Handling Wave Candidate Nominations
                // ----------------------------------------------------
                var nomination = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT uw.Id, uw.UserId, uw.WaveId, uw.ApprovalStatus, u.BranchId, ISNULL(u.FullName, u.UserName) as StudentName,
                           b.BranchName, tw.WaveName
                    FROM dbo.UserWaves uw
                    JOIN AspNetUsers u ON uw.UserId = u.Id
                    LEFT JOIN Branches b ON u.BranchId = b.Id
                    JOIN dbo.TrainingWaves tw ON uw.WaveId = tw.Id
                    WHERE uw.Id = @Id", new { Id = req.UserWaveId });

                if (nomination == null)
                {
                    return Json(new { success = false, message = "لم يتم العثور على سجل الترشيح." });
                }

                // Verify branch access if not Admin
                if (!isAdminOrHr)
                {
                    int? managerBranch = await conn.QueryFirstOrDefaultAsync<int?>(
                        "SELECT BranchId FROM AspNetUsers WHERE Id = @UserId", new { UserId = currentUserId });
                    if (managerBranch != (int?)nomination.BranchId)
                    {
                        return Json(new { success = false, message = "ليس لديك صلاحية اعتماد مرشحي هذا الفرع." });
                    }
                }

                if (action == "accept" || action == "approve")
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.UserWaves 
                        SET ApprovalStatus = 'Approved',
                            IsActive = 1,
                            ActionedByUserId = @ActionedBy,
                            ActionedAt = GETDATE()
                        WHERE Id = @Id",
                        new { Id = req.UserWaveId, ActionedBy = currentUserId });

                    // Generate user attendance records if wave has sessions
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.UserAttendance (SessionId, UserId, IsPresent, RecordedAt)
                        SELECT s.Id, @UserId, 0, GETDATE()
                        FROM dbo.AttendanceSessions s
                        WHERE s.WaveId = @WaveId
                          AND NOT EXISTS (SELECT 1 FROM dbo.UserAttendance ua WHERE ua.SessionId = s.Id AND ua.UserId = @UserId)",
                        new { UserId = (string)nomination.UserId, WaveId = (int)nomination.WaveId });

                    // Notify Admins
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                        VALUES ('Admin', N'اعتماد مرشح للويف ✅', CONCAT(N'تم اعتماد المرشح: ', @StudentName, N' من فرع: ', @BranchName, N' في الويف: ', @WaveName), CONCAT('/Admin/WaveDetails/', @WaveId), 'WaveNominationApproved', CAST(@Id AS NVARCHAR(50)), GETDATE())",
                        new {
                            StudentName = (string)nomination.StudentName,
                            BranchName = (string)(nomination.BranchName ?? "الفرع"),
                            WaveName = (string)nomination.WaveName,
                            WaveId = (int)nomination.WaveId,
                            Id = req.UserWaveId
                        });

                    await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات الويف.");
                    return Json(new { success = true, message = $"تم اعتماد وترشيح الموظف {nomination.StudentName} بنجاح." });
                }
                else if (action == "reject")
                {
                    if (string.IsNullOrWhiteSpace(req.Reason))
                    {
                        return Json(new { success = false, message = "يرجى كتابة سبب الرفض لتوضيحه للإدارة." });
                    }

                    await conn.ExecuteAsync(@"
                        UPDATE dbo.UserWaves 
                        SET ApprovalStatus = 'Rejected',
                            IsActive = 0,
                            RejectionReason = @Reason,
                            ActionedByUserId = @ActionedBy,
                            ActionedAt = GETDATE()
                        WHERE Id = @Id",
                        new { Id = req.UserWaveId, Reason = req.Reason.Trim(), ActionedBy = currentUserId });

                    // Notify Admins
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                        VALUES ('Admin', N'اعتذار/رفض مرشح للويف ❌', CONCAT(N'قام مدير الفرع برفض ترشيح: ', @StudentName, N' (', @BranchName, N') للويف: ', @WaveName, N'. السبب: ', @Reason), CONCAT('/Admin/WaveDetails/', @WaveId), 'WaveNominationRejected', CAST(@Id AS NVARCHAR(50)), GETDATE())",
                        new {
                            StudentName = (string)nomination.StudentName,
                            BranchName = (string)(nomination.BranchName ?? "الفرع"),
                            WaveName = (string)nomination.WaveName,
                            Reason = req.Reason.Trim(),
                            WaveId = (int)nomination.WaveId,
                            Id = req.UserWaveId
                        });

                    await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات الويف.");
                    return Json(new { success = true, message = $"تم تسجيل رفض ترشيح {nomination.StudentName} مع توضيح السبب." });
                }
                else if (action == "replace")
                {
                    if (string.IsNullOrWhiteSpace(req.ReplacementUserId))
                    {
                        return Json(new { success = false, message = "يرجى اختيار الموظف البديل من الفرع." });
                    }

                    var replacementUser = await conn.QueryFirstOrDefaultAsync<dynamic>(
                        "SELECT Id, ISNULL(FullName, UserName) as FullName FROM AspNetUsers WHERE Id = @Id",
                        new { Id = req.ReplacementUserId });

                    if (replacementUser == null)
                    {
                        return Json(new { success = false, message = "الموظف البديل المحدد غير موجود." });
                    }

                    // Mark old as Replaced
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.UserWaves 
                        SET ApprovalStatus = 'Replaced',
                            IsActive = 0,
                            ReplacedWithUserId = @ReplacementId,
                            ActionedByUserId = @ActionedBy,
                            ActionedAt = GETDATE()
                        WHERE Id = @Id",
                        new { Id = req.UserWaveId, ReplacementId = req.ReplacementUserId, ActionedBy = currentUserId });

                    // Insert new candidate as Approved
                    int newId = await conn.QuerySingleAsync<int>(@"
                        INSERT INTO dbo.UserWaves (UserId, WaveId, JoinDate, IsActive, ApprovalStatus, ActionedByUserId, ActionedAt)
                        VALUES (@UserId, @WaveId, GETDATE(), 1, 'Approved', @ActionedBy, GETDATE());
                        SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        new {
                            UserId = req.ReplacementUserId,
                            WaveId = (int)nomination.WaveId,
                            ActionedBy = currentUserId
                        });

                    // Generate attendance records for substitute
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.UserAttendance (SessionId, UserId, IsPresent, RecordedAt)
                        SELECT s.Id, @UserId, 0, GETDATE()
                        FROM dbo.AttendanceSessions s
                        WHERE s.WaveId = @WaveId
                          AND NOT EXISTS (SELECT 1 FROM dbo.UserAttendance ua WHERE ua.SessionId = s.Id AND ua.UserId = @UserId)",
                        new { UserId = req.ReplacementUserId, WaveId = (int)nomination.WaveId });

                    // Notify Admins
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                        VALUES ('Admin', N'استبدال مرشح في الويف 🔄', CONCAT(N'تم استبدال: ', @OldStudent, N' بـ: ', @NewStudent, N' في ويف: ', @WaveName), CONCAT('/Admin/WaveDetails/', @WaveId), 'WaveNominationReplaced', CAST(@NewId AS NVARCHAR(50)), GETDATE())",
                        new {
                            OldStudent = (string)nomination.StudentName,
                            NewStudent = (string)replacementUser.FullName,
                            WaveName = (string)nomination.WaveName,
                            WaveId = (int)nomination.WaveId,
                            NewId = newId
                        });

                    await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات الويف.");
                    return Json(new { success = true, message = $"تم استبدال {nomination.StudentName} بـ {replacementUser.FullName} واعتماده للويف بنجاح." });
                }

                return Json(new { success = false, message = "إجراء غير معروف." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ: " + ex.Message });
            }
        }
    }

    public class NominationActionRequest
    {
        public string Type { get; set; } = "wave"; // "wave" or "company"
        public int UserWaveId { get; set; } // for wave (UserWaves.Id) OR for company (CompanySessionTrainees.Id)
        public string Action { get; set; } = "";
        public string? Reason { get; set; }
        public string? ReplacementUserId { get; set; }
        public int? ReplacementTraineeId { get; set; }
    }
}
