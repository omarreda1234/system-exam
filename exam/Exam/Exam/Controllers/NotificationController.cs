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
                bool isAdmin = roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase));
                bool isBranchApprover = roles.Any(r => 
                    r.Equals("Branch Manager", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("BranchManager", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("Branch Supervisor", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("BranchSupervisor", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("Supervisor", StringComparison.OrdinalIgnoreCase));

                // Get user's branch
                int? userBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT BranchId FROM AspNetUsers WHERE Id = @UserId", new { UserId = userId });

                // 1. Unread notifications
                // Managerial notifications sent to BranchId are only shown to Branch Managers / Supervisors.
                string notifSql = @"
                    SELECT TOP 25 
                        Id, Title, Message, LinkUrl, Type, ReferenceId, IsRead, CreatedAt, BranchId
                    FROM dbo.AppNotifications
                    WHERE (UserId = @UserId 
                           OR (@IsBranchApprover = 1 AND BranchId IS NOT NULL AND BranchId = @BranchId)
                           OR (RoleTarget IS NOT NULL AND RoleTarget IN @Roles))
                    ORDER BY IsRead ASC, CreatedAt DESC";

                var notifs = (await conn.QueryAsync<dynamic>(notifSql, new
                {
                    UserId = userId,
                    BranchId = userBranchId,
                    IsBranchApprover = isBranchApprover ? 1 : 0,
                    Roles = roles.Any() ? roles : new List<string> { "__NONE__" }
                })).ToList();

                int unreadCount = notifs.Count(n => n.IsRead == false);

                // 2. Pending wave & company nominations count
                // STRICT BUSINESS RULE: Pending approval action prompt is ONLY for Branch Managers and Supervisors!
                // Pharmacists and Admins do NOT see 'مرشحين بانتظار اعتمادك' action banner.
                int pendingWaveCount = 0;
                int pendingCompanyCount = 0;

                if (isBranchApprover && userBranchId.HasValue)
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
                    unreadCount = unreadCount + (isBranchApprover ? totalPending : 0),
                    pendingNominationsCount = isBranchApprover ? totalPending : 0,
                    pendingWaveCount = isBranchApprover ? pendingWaveCount : 0,
                    pendingCompanyCount = isBranchApprover ? pendingCompanyCount : 0,
                    userBranchId = userBranchId,
                    isBranchApprover = isBranchApprover,
                    isAdmin = isAdmin,
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

        [HttpPost]
        public async Task<IActionResult> DeleteNotification(int id)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                var userId = CurrentUserId;
                var roles = CurrentUserRoles;
                bool isAdmin = roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase));

                int? userBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT BranchId FROM AspNetUsers WHERE Id = @UserId", new { UserId = userId });

                await conn.ExecuteAsync(@"
                    DELETE FROM dbo.AppNotifications 
                    WHERE Id = @Id 
                      AND (@IsAdmin = 1 
                           OR UserId = @UserId 
                           OR (BranchId IS NOT NULL AND BranchId = @BranchId)
                           OR (RoleTarget IS NOT NULL AND RoleTarget IN @Roles))",
                    new { 
                        Id = id, 
                        IsAdmin = isAdmin ? 1 : 0, 
                        UserId = userId, 
                        BranchId = userBranchId, 
                        Roles = roles.Any() ? roles : new List<string> { "__NONE__" } 
                    });

                return Json(new { success = true, message = "تم حذف الإشعار بنجاح." });
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
                bool isBranchApprover = roles.Any(r => 
                    r.Equals("Branch Manager", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("BranchManager", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("Branch Supervisor", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("BranchSupervisor", StringComparison.OrdinalIgnoreCase) ||
                    r.Equals("Supervisor", StringComparison.OrdinalIgnoreCase));

                bool isAdminOrHr = roles.Contains("Admin") || roles.Contains("HR") || roles.Contains("Human Resources");

                int? userBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT BranchId FROM AspNetUsers WHERE Id = @UserId", new { UserId = userId });

                if (!isBranchApprover || !userBranchId.HasValue)
                {
                    return Json(new { success = true, waveNominations = new List<object>(), companyNominations = new List<object>() });
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

                var raw = await conn.QueryAsync<dynamic>(sql, new { BranchId = branchId, WaveId = waveId });
                var colleagues = raw.Select(c => {
                    var d = (IDictionary<string, object>)c;
                    string id = d.ContainsKey("Id") && d["Id"] != null ? d["Id"].ToString()! : "";
                    string fn = d.ContainsKey("FullName") && d["FullName"] != null ? d["FullName"].ToString()! : "User";
                    string uc = d.ContainsKey("UserCode") && d["UserCode"] != null ? d["UserCode"].ToString()! : "--";
                    string jt = d.ContainsKey("RoleName") && d["RoleName"] != null ? d["RoleName"].ToString()! : "User";
                    return new
                    {
                        id = id,
                        value = id,
                        userId = id,
                        fullName = fn,
                        FullName = fn,
                        userCode = uc,
                        UserCode = uc,
                        jobTitle = jt,
                        JobTitle = jt
                    };
                }).ToList();

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
                    ;WITH BranchUsers AS (
                        SELECT 
                            u.Id AS UserId,
                            ct.Id AS CompanyTraineeId,
                            COALESCE(NULLIF(u.FullName, ''), NULLIF(u.UserName, ''), NULLIF(ct.FullName, ''), N'User') AS FullName,
                            COALESCE(NULLIF(u.UserCode, ''), NULLIF(ct.UserCode, ''), N'--') AS UserCode,
                            COALESCE(NULLIF(r.Name, ''), NULLIF(ct.JobTitle, ''), N'Pharmacist') AS JobTitle,
                            COALESCE(NULLIF(b.BranchName, ''), NULLIF(ct.BranchName, ''), @BranchName) AS BranchName
                        FROM AspNetUsers u WITH (NOLOCK)
                        LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                        LEFT JOIN AspNetUserRoles ur ON u.Id = ur.UserId
                        LEFT JOIN AspNetRoles r ON ur.RoleId = r.Id
                        LEFT JOIN CompanyTrainees ct WITH (NOLOCK) ON ct.CompanyId = @CompanyId AND (ct.UserCode = u.UserCode OR (ct.Email = u.Email AND ct.Email <> '--'))
                        WHERE (b.BranchName = @BranchName OR u.BranchId = (SELECT TOP 1 Id FROM Branches WHERE BranchName = @BranchName))
                          AND u.IsActive = 1

                        UNION

                        SELECT 
                            CAST(NULL AS NVARCHAR(450)) AS UserId,
                            ct.Id AS CompanyTraineeId,
                            ISNULL(NULLIF(ct.FullName, ''), N'Trainee') AS FullName,
                            ISNULL(NULLIF(ct.UserCode, ''), N'--') AS UserCode,
                            ISNULL(NULLIF(ct.JobTitle, ''), N'Trainee') AS JobTitle,
                            ct.BranchName
                        FROM CompanyTrainees ct WITH (NOLOCK)
                        WHERE ct.BranchName = @BranchName
                          AND NOT EXISTS (
                              SELECT 1 FROM AspNetUsers u2 WITH (NOLOCK)
                              LEFT JOIN Branches b2 WITH (NOLOCK) ON u2.BranchId = b2.Id
                              WHERE (u2.UserCode = ct.UserCode OR (u2.Email = ct.Email AND ct.Email <> '--'))
                                AND (b2.BranchName = @BranchName OR u2.BranchId = (SELECT TOP 1 Id FROM Branches WHERE BranchName = @BranchName))
                          )
                    )
                    SELECT 
                        bu.UserId,
                        bu.CompanyTraineeId,
                        CASE 
                            WHEN bu.UserId IS NOT NULL THEN bu.UserId 
                            ELSE CAST(bu.CompanyTraineeId AS NVARCHAR(100)) 
                        END AS Value,
                        bu.FullName,
                        bu.UserCode,
                        bu.JobTitle,
                        bu.BranchName
                    FROM BranchUsers bu
                    WHERE NOT EXISTS (
                        SELECT 1 FROM dbo.CompanySessionTrainees cst WITH (NOLOCK)
                        WHERE cst.SessionId = @SessionId
                          AND cst.ApprovalStatus IN ('Approved', 'Pending')
                          AND (
                              (bu.CompanyTraineeId IS NOT NULL AND cst.CompanyTraineeId = bu.CompanyTraineeId)
                              OR (bu.UserId IS NOT NULL AND cst.UserId = bu.UserId)
                          )
                    )
                    ORDER BY bu.FullName ASC";

                var raw = await conn.QueryAsync<dynamic>(sql, new { SessionId = sessionId, CompanyId = companyId, BranchName = branchName });
                var colleagues = raw.Select(c => {
                    var d = (IDictionary<string, object>)c;
                    string uId = d.ContainsKey("UserId") && d["UserId"] != null ? d["UserId"].ToString() : null;
                    int? cti = d.ContainsKey("CompanyTraineeId") && d["CompanyTraineeId"] != null && int.TryParse(d["CompanyTraineeId"].ToString(), out int parsed) ? parsed : (int?)null;
                    string val = d.ContainsKey("Value") && d["Value"] != null ? d["Value"].ToString() : (uId ?? cti?.ToString() ?? "");
                    string fn = d.ContainsKey("FullName") && d["FullName"] != null ? d["FullName"].ToString() : "Trainee";
                    string uc = d.ContainsKey("UserCode") && d["UserCode"] != null ? d["UserCode"].ToString() : "--";
                    string jt = d.ContainsKey("JobTitle") && d["JobTitle"] != null ? d["JobTitle"].ToString() : "Pharmacist";
                    string bn = d.ContainsKey("BranchName") && d["BranchName"] != null ? d["BranchName"].ToString() : branchName;

                    return new
                    {
                        id = val,
                        value = val,
                        userId = uId,
                        companyTraineeId = cti,
                        fullName = fn,
                        FullName = fn,
                        userCode = uc,
                        UserCode = uc,
                        jobTitle = jt,
                        JobTitle = jt,
                        branchName = bn
                    };
                }).ToList();

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

                var actorUser = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT ISNULL(FullName, UserName) AS FullName FROM AspNetUsers WHERE Id = @Id", new { Id = currentUserId });
                string actorName = (string)(actorUser?.FullName ?? User.Identity?.Name ?? "مدير الفرع");
                string actorRole = roles.FirstOrDefault(r => r.Contains("Branch") || r.Contains("Supervisor") || r.Contains("Admin")) ?? "Branch Manager";

                // ----------------------------------------------------
                // 1. Handling Company Session Trainee Nominations
                // ----------------------------------------------------
                if (nomType == "company")
                {
                    var cNomination = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT cst.Id, cst.SessionId, cst.CompanyTraineeId, cst.ApprovalStatus, ct.FullName AS TraineeName,
                               ISNULL(ct.UserCode, '--') AS UserCode, ISNULL(ct.JobTitle, 'Trainee') AS JobTitle,
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

                        // Log to NominationAuditLogs
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.NominationAuditLogs (
                                EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                                BranchId, BranchName, Action, ActionReason, ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                            ) VALUES (
                                'CompanySession', @EntityId, @EntityName, @CompanyName, @TraineeId, @TraineeName, @TraineeCode, @TraineeRole,
                                @BranchId, @BranchName, 'Approved', NULL, @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                            )", new {
                                EntityId = (int)cNomination.SessionId,
                                EntityName = (string)cNomination.SessionName,
                                CompanyName = (string)(cNomination.CompanyName ?? ""),
                                TraineeId = cNomination.CompanyTraineeId.ToString(),
                                TraineeName = (string)cNomination.TraineeName,
                                TraineeCode = (string)(cNomination.UserCode ?? "--"),
                                TraineeRole = (string)(cNomination.JobTitle ?? "Trainee"),
                                BranchId = (int?)cNomination.BranchId,
                                BranchName = (string)(cNomination.BranchName ?? "الفرع"),
                                ActionByUserId = currentUserId,
                                ActionByUserName = actorName,
                                ActionByUserRole = actorRole
                            });

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                            VALUES ('Admin', N'اعتماد حضور متدرب شركة ✅', CONCAT(N'قام مدير فرع ', @BranchName, N' باعتماد حضور: ', @TraineeName, N' لسيشن: ', @SessionName), CONCAT('/Attendance/ManageSessions?companyId=', @CompanyId), 'CompanySessionApproved', CAST(@Id AS NVARCHAR(50)), GETDATE())",
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

                        // Log to NominationAuditLogs
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.NominationAuditLogs (
                                EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                                BranchId, BranchName, Action, ActionReason, ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                            ) VALUES (
                                'CompanySession', @EntityId, @EntityName, @CompanyName, @TraineeId, @TraineeName, @TraineeCode, @TraineeRole,
                                @BranchId, @BranchName, 'Rejected', @Reason, @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                            )", new {
                                EntityId = (int)cNomination.SessionId,
                                EntityName = (string)cNomination.SessionName,
                                CompanyName = (string)(cNomination.CompanyName ?? ""),
                                TraineeId = cNomination.CompanyTraineeId.ToString(),
                                TraineeName = (string)cNomination.TraineeName,
                                TraineeCode = (string)(cNomination.UserCode ?? "--"),
                                TraineeRole = (string)(cNomination.JobTitle ?? "Trainee"),
                                BranchId = (int?)cNomination.BranchId,
                                BranchName = (string)(cNomination.BranchName ?? "الفرع"),
                                Reason = req.Reason.Trim(),
                                ActionByUserId = currentUserId,
                                ActionByUserName = actorName,
                                ActionByUserRole = actorRole
                            });

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                            VALUES ('Admin', N'اعتذار/رفض متدرب شركة ❌', CONCAT(N'قام مدير فرع ', @BranchName, N' برفض حضور: ', @TraineeName, N' لسيشن: ', @SessionName, N'. السبب: ', @Reason), CONCAT('/Attendance/ManageSessions?companyId=', @CompanyId), 'CompanySessionRejected', CAST(@Id AS NVARCHAR(50)), GETDATE())",
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
                        var allKeys = (req.ReplacementUserIds ?? new List<string>())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Distinct()
                            .ToList();

                        if (!allKeys.Any() && !string.IsNullOrWhiteSpace(req.ReplacementUserId))
                        {
                            allKeys.Add(req.ReplacementUserId);
                        }
                        if (!allKeys.Any() && req.ReplacementTraineeId.HasValue && req.ReplacementTraineeId.Value > 0)
                        {
                            allKeys.Add(req.ReplacementTraineeId.Value.ToString());
                        }

                        if (!allKeys.Any())
                        {
                            return Json(new { success = false, message = "يرجى اختيار زميل بديل واحد على الأقل من الفرع." });
                        }

                        int compId = (int)(cNomination.CompanyId ?? 0);
                        string branchName = (string)(cNomination.BranchName ?? "الفرع");
                        var addedNames = new List<string>();

                        async Task<(int? ctId, string? uId, string name, string code)> ResolveCandidate(string key)
                        {
                            if (int.TryParse(key, out int ctNum))
                            {
                                var ct = await conn.QueryFirstOrDefaultAsync<dynamic>(
                                    "SELECT Id, FullName, UserCode FROM CompanyTrainees WHERE Id = @Id", new { Id = ctNum });
                                if (ct != null)
                                {
                                    return (ctNum, null, (string)ct.FullName, (string)(ct.UserCode ?? ctNum.ToString()));
                                }
                            }

                            var u = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                                SELECT u.Id, u.FullName, u.UserName, u.UserCode, u.Email, u.PhoneNumber, u.UserRoleCustom,
                                       b.BranchName, b.Id AS BranchId
                                FROM AspNetUsers u WITH (NOLOCK)
                                LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                                WHERE u.Id = @UserId", new { UserId = key });

                            if (u != null)
                            {
                                string uFn = !string.IsNullOrWhiteSpace((string)u.FullName) ? (string)u.FullName : (string)u.UserName;
                                string uCode = (string)(u.UserCode ?? "--");
                                string uEmail = (string)(u.Email ?? "--");
                                string uBranch = (string)(u.BranchName ?? branchName);

                                var existingCt = await conn.QueryFirstOrDefaultAsync<int?>(@"
                                    SELECT TOP 1 Id FROM CompanyTrainees 
                                    WHERE CompanyId = @CompanyId AND ((UserCode = @Code AND UserCode <> '--') OR (Email = @Email AND Email <> '--'))",
                                    new { CompanyId = compId, Code = uCode, Email = uEmail });

                                int ctIdResult;
                                if (existingCt.HasValue)
                                {
                                    ctIdResult = existingCt.Value;
                                }
                                else
                                {
                                    ctIdResult = await conn.QueryFirstOrDefaultAsync<int>(@"
                                        INSERT INTO CompanyTrainees (CompanyId, FullName, UserCode, JobTitle, BranchName, Email, Phone, CreatedAt)
                                        VALUES (@CompanyId, @FullName, @UserCode, @JobTitle, @BranchName, @Email, @Phone, GETDATE());
                                        SELECT CAST(SCOPE_IDENTITY() AS INT);",
                                        new {
                                            CompanyId = compId,
                                            FullName = uFn,
                                            UserCode = uCode,
                                            JobTitle = (string)(u.UserRoleCustom ?? "Pharmacist"),
                                            BranchName = uBranch,
                                            Email = uEmail,
                                            Phone = (string)(u.PhoneNumber ?? "--")
                                        });
                                }

                                return (ctIdResult, (string)u.Id, uFn, uCode);
                            }

                            return (null, null, "Unknown", "--");
                        }

                        // 1. First Colleague (Direct Replacement)
                        var firstColleague = await ResolveCandidate(allKeys[0]);
                        if (string.IsNullOrWhiteSpace(firstColleague.name) || firstColleague.name == "Unknown")
                        {
                            return Json(new { success = false, message = "بيانات الزميل البديل الأول غير صالحة." });
                        }

                        addedNames.Add(firstColleague.name);

                        // Mark old as Replaced
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.CompanySessionTrainees 
                            SET ApprovalStatus = 'Replaced',
                                ReplacedWithTraineeId = @ReplacementId,
                                ActionedByUserId = @ActionedBy,
                                ActionedAt = GETDATE()
                            WHERE Id = @Id",
                            new { Id = req.UserWaveId, ReplacementId = firstColleague.ctId, ActionedBy = currentUserId });

                        // Insert new as Approved
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.CompanySessionTrainees (SessionId, CompanyTraineeId, UserId, AssignedAt, AssignedBy, ApprovalStatus, ActionedByUserId, ActionedAt)
                            VALUES (@SessionId, @TraineeId, @UserId, GETDATE(), @ActionedBy, 'Approved', @ActionedBy, GETDATE())",
                            new {
                                SessionId = (int)cNomination.SessionId,
                                TraineeId = firstColleague.ctId,
                                UserId = firstColleague.uId,
                                ActionedBy = currentUserId
                            });

                        // Log to NominationAuditLogs
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.NominationAuditLogs (
                                EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                                BranchId, BranchName, Action, ActionReason, ReplacementTraineeId, ReplacementTraineeName,
                                ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                            ) VALUES (
                                'CompanySession', @EntityId, @EntityName, @CompanyName, @TraineeId, @TraineeName, @TraineeCode, @TraineeRole,
                                @BranchId, @BranchName, 'Replaced', CONCAT(N'تم استبداله بـ: ', @RepName), @RepId, @RepName,
                                @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                            )", new {
                                EntityId = (int)cNomination.SessionId,
                                EntityName = (string)cNomination.SessionName,
                                CompanyName = (string)(cNomination.CompanyName ?? ""),
                                TraineeId = cNomination.CompanyTraineeId != null ? cNomination.CompanyTraineeId.ToString() : (string)(cNomination.UserId ?? ""),
                                TraineeName = (string)cNomination.TraineeName,
                                TraineeCode = (string)(cNomination.UserCode ?? "--"),
                                TraineeRole = (string)(cNomination.JobTitle ?? "Trainee"),
                                BranchId = (int?)cNomination.BranchId,
                                BranchName = branchName,
                                RepId = firstColleague.code,
                                RepName = firstColleague.name,
                                ActionByUserId = currentUserId,
                                ActionByUserName = actorName,
                                ActionByUserRole = actorRole
                            });

                        // 2. Extra Selected Colleagues
                        for (int i = 1; i < allKeys.Count; i++)
                        {
                            var extraColleague = await ResolveCandidate(allKeys[i]);
                            if (string.IsNullOrWhiteSpace(extraColleague.name) || extraColleague.name == "Unknown") continue;

                            addedNames.Add(extraColleague.name);

                            await conn.ExecuteAsync(@"
                                IF NOT EXISTS (
                                    SELECT 1 FROM dbo.CompanySessionTrainees 
                                    WHERE SessionId = @SessionId AND (CompanyTraineeId = @TraineeId OR (UserId = @UserId AND @UserId IS NOT NULL))
                                )
                                BEGIN
                                    INSERT INTO dbo.CompanySessionTrainees (SessionId, CompanyTraineeId, UserId, AssignedAt, AssignedBy, ApprovalStatus, ActionedByUserId, ActionedAt)
                                    VALUES (@SessionId, @TraineeId, @UserId, GETDATE(), @ActionedBy, 'Approved', @ActionedBy, GETDATE())
                                END",
                                new {
                                    SessionId = (int)cNomination.SessionId,
                                    TraineeId = extraColleague.ctId,
                                    UserId = extraColleague.uId,
                                    ActionedBy = currentUserId
                                });

                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.NominationAuditLogs (
                                    EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                                    BranchId, BranchName, Action, ActionReason,
                                    ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                                ) VALUES (
                                    'CompanySession', @EntityId, @EntityName, @CompanyName, @TraineeId, @TraineeName, @TraineeCode, 'Trainee',
                                    @BranchId, @BranchName, 'Approved', CONCAT(N'مرشح بديل إضافي من الفرع مع استبدال: ', @OldTrainee),
                                    @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                                )", new {
                                    EntityId = (int)cNomination.SessionId,
                                    EntityName = (string)cNomination.SessionName,
                                    CompanyName = (string)(cNomination.CompanyName ?? ""),
                                    TraineeId = extraColleague.ctId != null ? extraColleague.ctId.ToString() : extraColleague.uId,
                                    TraineeName = extraColleague.name,
                                    TraineeCode = extraColleague.code,
                                    BranchId = (int?)cNomination.BranchId,
                                    BranchName = branchName,
                                    OldTrainee = (string)cNomination.TraineeName,
                                    ActionByUserId = currentUserId,
                                    ActionByUserName = actorName,
                                    ActionByUserRole = actorRole
                                });
                        }

                        string namesSummary = string.Join("، ", addedNames);
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                            VALUES ('Admin', N'استبدال متدرب في سيشن شركة 🔄', CONCAT(N'قام مدير فرع ', @BranchName, N' باستبدال: ', @OldTrainee, N' بـ: (', @NamesSummary, N') في سيشن: ', @SessionName), CONCAT('/Attendance/ManageSessions?companyId=', @CompanyId), 'CompanySessionReplaced', CAST(@Id AS NVARCHAR(50)), GETDATE())",
                            new {
                                BranchName = branchName,
                                OldTrainee = (string)cNomination.TraineeName,
                                NamesSummary = namesSummary,
                                SessionName = (string)cNomination.SessionName,
                                CompanyId = (int?)cNomination.CompanyId,
                                Id = req.UserWaveId
                            });

                        await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات تدريب الشركة.");
                        return Json(new { success = true, message = $"تم استبدال {cNomination.TraineeName} واعتماد ({addedNames.Count}) زملاء بدلاء ({namesSummary}) للسيشن بنجاح." });
                    }
                }

                // ----------------------------------------------------
                // 2. Handling Wave Candidate Nominations
                // ----------------------------------------------------
                var nomination = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT uw.Id, uw.UserId, uw.WaveId, uw.ApprovalStatus, u.BranchId, ISNULL(u.FullName, u.UserName) as StudentName,
                           ISNULL(u.UserCode, '--') as UserCode, b.BranchName, tw.WaveName
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

                    // Log to NominationAuditLogs
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.NominationAuditLogs (
                            EntityType, EntityId, EntityName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                            BranchId, BranchName, Action, ActionReason, ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                        ) VALUES (
                            'Wave', @WaveId, @WaveName, @UserId, @StudentName, @UserCode, 'Trainee',
                            @BranchId, @BranchName, 'Approved', NULL, @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                        )", new {
                            WaveId = (int)nomination.WaveId,
                            WaveName = (string)nomination.WaveName,
                            UserId = (string)nomination.UserId,
                            StudentName = (string)nomination.StudentName,
                            UserCode = (string)(nomination.UserCode ?? "--"),
                            BranchId = (int?)nomination.BranchId,
                            BranchName = (string)(nomination.BranchName ?? "الفرع"),
                            ActionByUserId = currentUserId,
                            ActionByUserName = actorName,
                            ActionByUserRole = actorRole
                        });

                    // Notify Admins
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                        VALUES ('Admin', N'اعتماد مرشح للويف ✅', CONCAT(N'قام مدير فرع ', @BranchName, N' باعتماد ترشيح: ', @StudentName, N' في ويف: ', @WaveName), CONCAT('/Admin/WaveDetails/', @WaveId), 'WaveNominationApproved', CAST(@Id AS NVARCHAR(50)), GETDATE())",
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

                    // Log to NominationAuditLogs
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.NominationAuditLogs (
                            EntityType, EntityId, EntityName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                            BranchId, BranchName, Action, ActionReason, ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                        ) VALUES (
                            'Wave', @WaveId, @WaveName, @UserId, @StudentName, @UserCode, 'Trainee',
                            @BranchId, @BranchName, 'Rejected', @Reason, @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                        )", new {
                            WaveId = (int)nomination.WaveId,
                            WaveName = (string)nomination.WaveName,
                            UserId = (string)nomination.UserId,
                            StudentName = (string)nomination.StudentName,
                            UserCode = (string)(nomination.UserCode ?? "--"),
                            BranchId = (int?)nomination.BranchId,
                            BranchName = (string)(nomination.BranchName ?? "الفرع"),
                            Reason = req.Reason.Trim(),
                            ActionByUserId = currentUserId,
                            ActionByUserName = actorName,
                            ActionByUserRole = actorRole
                        });

                    // Notify Admins
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                        VALUES ('Admin', N'اعتذار/رفض مرشح للويف ❌', CONCAT(N'قام مدير فرع ', @BranchName, N' برفض ترشيح: ', @StudentName, N' للويف: ', @WaveName, N'. السبب: ', @Reason), CONCAT('/Admin/WaveDetails/', @WaveId), 'WaveNominationRejected', CAST(@Id AS NVARCHAR(50)), GETDATE())",
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
                    var allWaveKeys = (req.ReplacementUserIds ?? new List<string>())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct()
                        .ToList();

                    if (!allWaveKeys.Any() && !string.IsNullOrWhiteSpace(req.ReplacementUserId))
                    {
                        allWaveKeys.Add(req.ReplacementUserId);
                    }

                    if (!allWaveKeys.Any())
                    {
                        return Json(new { success = false, message = "يرجى اختيار زميل بديل واحد على الأقل من الفرع." });
                    }

                    var addedWaveNames = new List<string>();

                    // 1. First Colleague (Direct Replacement)
                    var firstUserId = allWaveKeys[0];
                    var replacementUser = await conn.QueryFirstOrDefaultAsync<dynamic>(
                        "SELECT Id, ISNULL(FullName, UserName) as FullName, ISNULL(UserCode, '--') as UserCode FROM AspNetUsers WHERE Id = @Id",
                        new { Id = firstUserId });

                    if (replacementUser == null)
                    {
                        return Json(new { success = false, message = "الموظف البديل المحدد غير موجود." });
                    }

                    string firstFn = (string)replacementUser.FullName;
                    addedWaveNames.Add(firstFn);

                    // Mark old as Replaced
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.UserWaves 
                        SET ApprovalStatus = 'Replaced',
                            IsActive = 0,
                            ReplacedWithUserId = @ReplacementId,
                            ActionedByUserId = @ActionedBy,
                            ActionedAt = GETDATE()
                        WHERE Id = @Id",
                        new { Id = req.UserWaveId, ReplacementId = firstUserId, ActionedBy = currentUserId });

                    // Insert new candidate as Approved
                    int newId = await conn.QuerySingleAsync<int>(@"
                        INSERT INTO dbo.UserWaves (UserId, WaveId, JoinDate, IsActive, ApprovalStatus, ActionedByUserId, ActionedAt)
                        VALUES (@UserId, @WaveId, GETDATE(), 1, 'Approved', @ActionedBy, GETDATE());
                        SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        new {
                            UserId = firstUserId,
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
                        new { UserId = firstUserId, WaveId = (int)nomination.WaveId });

                    // Log to NominationAuditLogs
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.NominationAuditLogs (
                            EntityType, EntityId, EntityName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                            BranchId, BranchName, Action, ActionReason, ReplacementTraineeId, ReplacementTraineeName,
                            ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                        ) VALUES (
                            'Wave', @WaveId, @WaveName, @UserId, @StudentName, @UserCode, 'Trainee',
                            @BranchId, @BranchName, 'Replaced', CONCAT(N'تم استبداله بـ: ', @RepName), @RepId, @RepName,
                            @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                        )", new {
                            WaveId = (int)nomination.WaveId,
                            WaveName = (string)nomination.WaveName,
                            UserId = (string)nomination.UserId,
                            StudentName = (string)nomination.StudentName,
                            UserCode = (string)(nomination.UserCode ?? "--"),
                            BranchId = (int?)nomination.BranchId,
                            BranchName = (string)(nomination.BranchName ?? "الفرع"),
                            RepId = (string)(replacementUser.UserCode ?? firstUserId),
                            RepName = firstFn,
                            ActionByUserId = currentUserId,
                            ActionByUserName = actorName,
                            ActionByUserRole = actorRole
                        });

                    // 2. Extra Selected Colleagues for Wave
                    for (int i = 1; i < allWaveKeys.Count; i++)
                    {
                        var extraUid = allWaveKeys[i];
                        var extraU = await conn.QueryFirstOrDefaultAsync<dynamic>(
                            "SELECT Id, ISNULL(FullName, UserName) as FullName, ISNULL(UserCode, '--') as UserCode FROM AspNetUsers WHERE Id = @Id",
                            new { Id = extraUid });
                        if (extraU == null) continue;

                        string extraFn = (string)extraU.FullName;
                        addedWaveNames.Add(extraFn);

                        await conn.ExecuteAsync(@"
                            IF NOT EXISTS (SELECT 1 FROM dbo.UserWaves WHERE UserId = @UserId AND WaveId = @WaveId)
                            BEGIN
                                INSERT INTO dbo.UserWaves (UserId, WaveId, JoinDate, IsActive, ApprovalStatus, ActionedByUserId, ActionedAt)
                                VALUES (@UserId, @WaveId, GETDATE(), 1, 'Approved', @ActionedBy, GETDATE())
                            END",
                            new {
                                UserId = extraUid,
                                WaveId = (int)nomination.WaveId,
                                ActionedBy = currentUserId
                            });

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.UserAttendance (SessionId, UserId, IsPresent, RecordedAt)
                            SELECT s.Id, @UserId, 0, GETDATE()
                            FROM dbo.AttendanceSessions s
                            WHERE s.WaveId = @WaveId
                              AND NOT EXISTS (SELECT 1 FROM dbo.UserAttendance ua WHERE ua.SessionId = s.Id AND ua.UserId = @UserId)",
                            new { UserId = extraUid, WaveId = (int)nomination.WaveId });

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.NominationAuditLogs (
                                EntityType, EntityId, EntityName, TraineeId, TraineeName, TraineeCode, TraineeRole,
                                BranchId, BranchName, Action, ActionReason,
                                ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                            ) VALUES (
                                'Wave', @WaveId, @WaveName, @UserId, @StudentName, @UserCode, 'Trainee',
                                @BranchId, @BranchName, 'Approved', CONCAT(N'مرشح بديل إضافي من الفرع مع استبدال: ', @OldStudent),
                                @ActionByUserId, @ActionByUserName, @ActionByUserRole, GETDATE()
                            )", new {
                                WaveId = (int)nomination.WaveId,
                                WaveName = (string)nomination.WaveName,
                                UserId = extraUid,
                                StudentName = extraFn,
                                UserCode = (string)(extraU.UserCode ?? "--"),
                                BranchId = (int?)nomination.BranchId,
                                BranchName = (string)(nomination.BranchName ?? "الفرع"),
                                OldStudent = (string)nomination.StudentName,
                                ActionByUserId = currentUserId,
                                ActionByUserName = actorName,
                                ActionByUserRole = actorRole
                            });
                    }

                    // Notify Admins
                    string waveNamesSummary = string.Join("، ", addedWaveNames);
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.AppNotifications (RoleTarget, Title, Message, LinkUrl, Type, ReferenceId, CreatedAt)
                        VALUES ('Admin', N'استبدال مرشح في الويف 🔄', CONCAT(N'قام مدير فرع ', @BranchName, N' باستبدال: ', @OldStudent, N' بـ: (', @WaveNamesSummary, N') في ويف: ', @WaveName), CONCAT('/Admin/WaveDetails/', @WaveId), 'WaveNominationReplaced', CAST(@NewId AS NVARCHAR(50)), GETDATE())",
                        new {
                            BranchName = (string)(nomination.BranchName ?? "الفرع"),
                            OldStudent = (string)nomination.StudentName,
                            WaveNamesSummary = waveNamesSummary,
                            WaveName = (string)nomination.WaveName,
                            WaveId = (int)nomination.WaveId,
                            NewId = newId
                        });

                    await _hubContext.Clients.All.SendAsync("ReceiveNotification", "تم تحديث ترشيحات الويف.");
                    return Json(new { success = true, message = $"تم استبدال {nomination.StudentName} واعتماد ({addedWaveNames.Count}) زملاء بدلاء ({waveNamesSummary}) للويف بنجاح." });
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
        public List<string>? ReplacementUserIds { get; set; }
        public List<int>? ReplacementTraineeIds { get; set; }
    }
}
