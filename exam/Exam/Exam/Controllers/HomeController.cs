using Exam.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Threading.Tasks;
using Exam.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Exam.DTOs;
using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Dapper;

namespace Exam.Controllers
{
    public class HomeController : Controller
    {
        private readonly IExamService _examService;
        private readonly IHomeCmsService _homeCmsService;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        public HomeController(
            IExamService examService, 
            IHomeCmsService homeCmsService,
            IConfiguration configuration,
            IWebHostEnvironment env)
        {
            _examService = examService;
            _homeCmsService = homeCmsService;
            _configuration = configuration;
            _env = env;
        }

        public async Task<IActionResult> Index()
        {
            var cmsData = await _homeCmsService.GetHomeCmsDataAsync(activeOnly: true);

            try
            {
                using var conn = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                var partnerCompanies = await conn.QueryAsync<dynamic>(@"
                    SELECT C.Id, C.Name, C.Description, 
                           ISNULL(C.CompanyType, 'Medical') AS CompanyType, 
                           C.LogoUrl, C.Website, C.Tagline,
                           (SELECT COUNT(*) FROM CompanyProducts CP WHERE CP.CompanyId = C.Id AND CP.IsActive = 1) AS ProductCount
                    FROM Companies C
                    WHERE C.LogoUrl IS NOT NULL AND C.LogoUrl <> ''
                      AND ISNULL(C.IsVisibleOnHome, 1) = 1
                    ORDER BY ProductCount DESC, C.CreatedAt DESC");
                ViewBag.PartnerCompanies = partnerCompanies.ToList();
            }
            catch { }

            return View(cmsData);
        }

        public async Task<IActionResult> CompanyProfile(int id)
        {
            if (id <= 0)
            {
                return RedirectToAction(nameof(Index));
            }

            try
            {
                using var conn = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                
                var company = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT C.Id, C.Name, C.LogoUrl, C.CoverImageUrl, C.Tagline, C.Description, 
                           ISNULL(C.CompanyType, 'Medical') AS CompanyType, 
                           C.Website, C.Phone, C.Email, C.ContactPerson,
                           (SELECT COUNT(1) FROM dbo.CompanyProducts CP WHERE CP.CompanyId = C.Id AND CP.IsActive = 1) AS ProductCount,
                           (SELECT COUNT(1) FROM dbo.CompanySessionTrainees CST WHERE CST.CompanyId = C.Id) AS TraineesCount,
                           (SELECT COUNT(1) FROM dbo.AttendanceSessions S WHERE S.CompanyId = C.Id) AS SessionsCount
                    FROM dbo.Companies C
                    WHERE C.Id = @Id", new { Id = id });

                if (company == null)
                {
                    return NotFound();
                }

                var products = await conn.QueryAsync<CompanyProductDto>(@"
                    SELECT Id, CompanyId, ProductName, GenericName, Category, DosageForm, Price, Description, ImageUrl, IsActive, CreatedAt
                    FROM dbo.CompanyProducts
                    WHERE CompanyId = @Id AND IsActive = 1
                    ORDER BY ProductName ASC", new { Id = id });

                ViewBag.Company = company;
                ViewBag.Products = products.ToList();
                return View();
            }
            catch
            {
                return RedirectToAction(nameof(Index));
            }
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetNotifications()
        {
            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { count = 0 });
            }

            var exams = await _examService.GetStudentExamsByStudentIdAsync(userId);
            int pendingCount = exams?.Count() ?? 0;
            return Json(new { count = pendingCount });
        }

        [Authorize]
        public async Task<IActionResult> StudentExams()
        {
            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Challenge();
            }

            var shift = await _examService.GetUserShiftAsync(userId);
            ViewBag.UserShift = shift;

            // Store shift info in cookies (best-effort) so views/JS can read if needed
            try
            {
                if (shift != null && shift.ShiftId > 0)
                {
                    Response.Cookies.Append("ShiftName", shift.ShiftName ?? "");
                    Response.Cookies.Append("ShiftStart", shift.StartTime.ToString());
                    Response.Cookies.Append("ShiftEnd", shift.EndTime.ToString());
                }
            }
            catch { }

            var exams = await _examService.GetStudentExamsByStudentIdAsync(userId);
            return View(exams);
        }

        [Authorize]
        [HttpGet("/Profile")]
        [HttpGet("/Home/Profile")]
        [HttpGet("/MyProfile")]
        public async Task<IActionResult> Profile()
        {
            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Challenge();
            }

            string connStr = _configuration.GetConnectionString("DefaultConnection") 
                ?? "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True";

            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            var userQuery = @"
                SELECT 
                    u.Id AS UserId, 
                    ISNULL(u.FullName, u.UserName) AS FullName, 
                    u.UserName, 
                    u.Email, 
                    u.PhoneNumber, 
                    u.UserCode, 
                    ISNULL((SELECT TOP 1 r.Name FROM AspNetUserRoles ur JOIN AspNetRoles r ON ur.RoleId = r.Id WHERE ur.UserId = u.Id), '') AS RoleName, 
                    u.CertificateScore, 
                    u.CertificateCode,
                    ISNULL(b.BranchName, 'General / Not Assigned') AS BranchName, 
                    ISNULL(b.BranchCode, '-') AS BranchCode,
                    ISNULL(s.ShiftName, 'Standard Shift') AS ShiftName, 
                    s.StartTime AS ShiftStartTime, 
                    s.EndTime AS ShiftEndTime
                FROM AspNetUsers u
                LEFT JOIN Branches b ON u.BranchId = b.Id
                LEFT JOIN Shifts s ON u.ShiftId = s.Id
                WHERE u.Id = @UserId";

            var profile = await conn.QueryFirstOrDefaultAsync<UserProfileViewModel>(userQuery, new { UserId = userId });
            if (profile == null)
            {
                return NotFound("User profile not found.");
            }

            // Fallback Role Name if empty
            if (string.IsNullOrWhiteSpace(profile.RoleName))
            {
                var roleClaims = User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
                if (roleClaims.Any())
                {
                    profile.RoleName = string.Join(", ", roleClaims);
                }
                else
                {
                    profile.RoleName = "Trainee";
                }
            }

            // Ensure certificate upload/download tracking columns exist
            try
            {
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserWaveCertificates') AND name = 'FileUploaded')
                        ALTER TABLE dbo.UserWaveCertificates ADD FileUploaded BIT NOT NULL CONSTRAINT DF_UWC_FileUploaded DEFAULT 0;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserWaveCertificates') AND name = 'UploadedAt')
                        ALTER TABLE dbo.UserWaveCertificates ADD UploadedAt DATETIME NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserWaveCertificates') AND name = 'FileName')
                        ALTER TABLE dbo.UserWaveCertificates ADD FileName NVARCHAR(260) NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserWaveCertificates') AND name = 'FileData')
                        ALTER TABLE dbo.UserWaveCertificates ADD FileData VARBINARY(MAX) NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserWaveCertificates') AND name = 'IsDownloaded')
                        ALTER TABLE dbo.UserWaveCertificates ADD IsDownloaded BIT NOT NULL CONSTRAINT DF_UWC_IsDownloaded DEFAULT 0;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserWaveCertificates') AND name = 'DownloadedAt')
                        ALTER TABLE dbo.UserWaveCertificates ADD DownloadedAt DATETIME NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserWaveCertificates') AND name = 'DownloadCount')
                        ALTER TABLE dbo.UserWaveCertificates ADD DownloadCount INT NOT NULL CONSTRAINT DF_UWC_DownloadCount DEFAULT 0;
                ");
            }
            catch { }

            // Fetch DB wave certificates
            var certsQuery = @"
                SELECT 
                    uwc.WaveId,
                    ISNULL(w.WaveName, CONCAT('Wave #', uwc.WaveId)) AS WaveName,
                    uwc.CertificateCode,
                    uwc.Score,
                    uwc.CreatedAt,
                    ISNULL(uwc.FileUploaded, 0) AS FileUploaded,
                    uwc.UploadedAt,
                    uwc.FileName,
                    CASE WHEN uwc.FileData IS NOT NULL AND DATALENGTH(uwc.FileData) > 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS HasDbFileData,
                    ISNULL(uwc.IsDownloaded, 0) AS IsDownloaded,
                    uwc.DownloadedAt,
                    ISNULL(uwc.DownloadCount, 0) AS DownloadCount
                FROM dbo.UserWaveCertificates uwc
                LEFT JOIN TrainingWaves w ON uwc.WaveId = w.Id
                WHERE uwc.UserId = @UserId
                ORDER BY uwc.CreatedAt DESC";

            var dbCerts = (await conn.QueryAsync<UserCertificateItemViewModel>(certsQuery, new { UserId = userId })).ToList();

            // Physical Certificate Files Scanning on Server
            string webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            string certsBaseDir = Path.Combine(webRoot, "uploads", "certificates");
            string certsLegacyDir = Path.Combine(webRoot, "uploads", "certs");

            var matchedFiles = new List<(string FullPath, string RelPath, string FileName, int? WaveId, long Size)>();

            string userCode = (profile.UserCode ?? "").Trim();
            bool hasNumericUserCode = long.TryParse(userCode, out long numericUserCode);

            bool IsMatchingUserCodeFile(string fileNameWithoutExt)
            {
                if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(fileNameWithoutExt)) return false;
                if (fileNameWithoutExt.Equals(userCode, StringComparison.OrdinalIgnoreCase)) return true;
                if (hasNumericUserCode && long.TryParse(fileNameWithoutExt, out long fileNum) && fileNum == numericUserCode) return true;
                if (fileNameWithoutExt.Contains(userCode, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            if (!string.IsNullOrEmpty(userCode))
            {
                // Scan certsBaseDir and subfolders
                if (Directory.Exists(certsBaseDir))
                {
                    var files = Directory.GetFiles(certsBaseDir, "*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        string fn = Path.GetFileNameWithoutExtension(f).Trim();
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".pdf" && ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                        if (IsMatchingUserCodeFile(fn))
                        {
                            var fi = new FileInfo(f);
                            if (fi.Length == 0) continue;
                            int? waveId = null;
                            var parentName = Directory.GetParent(f)?.Name;
                            if (int.TryParse(parentName, out int parsedWId))
                            {
                                waveId = parsedWId;
                            }

                            string rel = Path.GetRelativePath(webRoot, f).Replace('\\', '/');
                            matchedFiles.Add((f, rel, fi.Name, waveId, fi.Length));
                        }
                    }
                }

                // Also scan legacy certs folder
                if (Directory.Exists(certsLegacyDir))
                {
                    var legacyFiles = Directory.GetFiles(certsLegacyDir, "*", SearchOption.TopDirectoryOnly);
                    foreach (var f in legacyFiles)
                    {
                        string fn = Path.GetFileNameWithoutExtension(f).Trim();
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".pdf" && ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                        if (IsMatchingUserCodeFile(fn))
                        {
                            var fi = new FileInfo(f);
                            if (fi.Length == 0) continue;
                            string rel = Path.GetRelativePath(webRoot, f).Replace('\\', '/');
                            matchedFiles.Add((f, rel, fi.Name, null, fi.Length));
                        }
                    }
                }
            }

            var finalCerts = new List<UserCertificateItemViewModel>();
            var usedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Match DB wave certificates with files (or DB stored PDF data)
            foreach (var cert in dbCerts)
            {
                var match = matchedFiles.FirstOrDefault(m => 
                    !usedFilePaths.Contains(m.FullPath) && 
                    (
                        (cert.WaveId.HasValue && m.WaveId == cert.WaveId.Value) ||
                        (!string.IsNullOrEmpty(cert.CertificateCode) && m.FileName.Contains(cert.CertificateCode, StringComparison.OrdinalIgnoreCase))
                    ));

                if (match.FullPath == null && !cert.WaveId.HasValue)
                {
                    match = matchedFiles.FirstOrDefault(m => !usedFilePaths.Contains(m.FullPath));
                }

                if (match.FullPath != null)
                {
                    usedFilePaths.Add(match.FullPath);
                    cert.HasFileOnDisk = true;
                    cert.FileName = match.FileName;
                    cert.FileSizeBytes = match.Size;
                    cert.FileExtension = Path.GetExtension(match.FileName).TrimStart('.').ToUpperInvariant();
                    cert.DownloadUrl = Url.Action("DownloadCertificate", "Home", new { fileName = match.FileName, waveId = cert.WaveId });
                }
                else if (cert.HasDbFileData)
                {
                    string fallbackFileName = !string.IsNullOrWhiteSpace(cert.FileName) ? cert.FileName : $"{(string.IsNullOrEmpty(userCode) ? "Certificate" : userCode)}.pdf";
                    cert.HasFileOnDisk = true;
                    cert.FileName = fallbackFileName;
                    cert.FileExtension = Path.GetExtension(fallbackFileName).TrimStart('.').ToUpperInvariant();
                    cert.DownloadUrl = Url.Action("DownloadCertificate", "Home", new { fileName = fallbackFileName, waveId = cert.WaveId });
                }
                else
                {
                    cert.HasFileOnDisk = false;
                }

                finalCerts.Add(cert);
            }

            // 2. Add any remaining disk files matching user code (and sync them to DB so WaveName & Serial are accurate)
            var allWaveNames = (await conn.QueryAsync<(int Id, string WaveName)>("SELECT Id, WaveName FROM dbo.TrainingWaves")).ToDictionary(x => x.Id, x => x.WaveName);
            foreach (var mf in matchedFiles.Where(m => !usedFilePaths.Contains(m.FullPath)))
            {
                string resolvedWaveName = "Official Academy Certificate / شهادة معتمدة";
                if (mf.WaveId.HasValue && allWaveNames.TryGetValue(mf.WaveId.Value, out string? dbWaveName) && !string.IsNullOrWhiteSpace(dbWaveName))
                {
                    resolvedWaveName = dbWaveName;
                }
                else if (mf.WaveId.HasValue)
                {
                    resolvedWaveName = $"Wave #{mf.WaveId.Value}";
                }

                string resolvedCertCode = profile.CertificateCode ?? $"WTTA-{DateTime.Now.Year}-{(mf.WaveId ?? 1).ToString().PadLeft(3, '0')}-PB-Off-{userCode}";

                finalCerts.Add(new UserCertificateItemViewModel
                {
                    WaveId = mf.WaveId,
                    WaveName = resolvedWaveName,
                    CertificateCode = resolvedCertCode,
                    Score = profile.CertificateScore,
                    CreatedAt = System.IO.File.GetLastWriteTime(mf.FullPath),
                    HasFileOnDisk = true,
                    FileUploaded = true,
                    FileName = mf.FileName,
                    FileSizeBytes = mf.Size,
                    FileExtension = Path.GetExtension(mf.FileName).TrimStart('.').ToUpperInvariant(),
                    DownloadUrl = Url.Action("DownloadCertificate", "Home", new { fileName = mf.FileName, waveId = mf.WaveId })
                });
            }

            profile.Certificates = finalCerts;

            // 3. Fetch Student Assigned Exams
            try
            {
                var exams = await _examService.GetStudentExamsByStudentIdAsync(userId);
                profile.Exams = exams ?? new List<ExamDto>();
                profile.UserShift = await _examService.GetUserShiftAsync(userId);
            }
            catch { }

            // 4. Fetch Student Active Wave & Continuous Assignments
            // 4. Fetch Student Waves, Continuous Assignments & Companies
            try
            {
                var myWaves = (await conn.QueryAsync<UserWaveItemViewModel>(@"
                    SELECT 
                        uw.WaveId, 
                        w.WaveName, 
                        w.StartDate, 
                        w.EndDate, 
                        w.Mode, 
                        w.IsOnline, 
                        ISNULL(uw.ApprovalStatus, 'Approved') AS ApprovalStatus, 
                        uw.JoinDate
                    FROM dbo.UserWaves uw WITH (NOLOCK)
                    INNER JOIN dbo.TrainingWaves w WITH (NOLOCK) ON uw.WaveId = w.Id
                    WHERE uw.UserId = @UserId
                    ORDER BY w.StartDate DESC, uw.Id DESC", 
                    new { UserId = userId })).ToList();

                profile.MyWaves = myWaves;

                var activeWave = myWaves.FirstOrDefault(w => w.ApprovalStatus == "Approved") ?? myWaves.FirstOrDefault();
                if (activeWave != null)
                {
                    profile.ActiveWaveName = activeWave.WaveName;
                    int waveId = activeWave.WaveId;
                    var assignments = await conn.QueryAsync<dynamic>(@"
                        SELECT a.*, 
                               (SELECT COUNT(*) FROM dbo.AssignmentSubmissions s WITH (NOLOCK) WHERE s.AssignmentId = a.Id AND s.UserId = @UserId) as SubmissionsCount
                        FROM dbo.ContinuousAssignments a WITH (NOLOCK)
                        WHERE a.WaveId = @WaveId AND a.IsActive = 1
                        ORDER BY a.ScheduledEndTime DESC",
                        new { WaveId = waveId, UserId = userId });
                    profile.Assignments = assignments;
                }

                // Fetch Student Company Sessions / Companies
                var myCompanies = (await conn.QueryAsync<UserCompanySessionItemViewModel>(@"
                    SELECT DISTINCT
                        s.Id AS SessionId,
                        s.SessionName,
                        s.CompanyId,
                        ISNULL(c.Name, N'شركة غير محددة') AS CompanyName,
                        s.SessionDate,
                        s.StartTime,
                        s.EndTime,
                        ISNULL(cst.ApprovalStatus, 'Approved') AS ApprovalStatus
                    FROM dbo.CompanySessionTrainees cst WITH (NOLOCK)
                    LEFT JOIN dbo.CompanyTrainees ct WITH (NOLOCK) ON cst.CompanyTraineeId = ct.Id
                    INNER JOIN dbo.AttendanceSessions s WITH (NOLOCK) ON cst.SessionId = s.Id
                    LEFT JOIN dbo.Companies c WITH (NOLOCK) ON s.CompanyId = c.Id
                    WHERE cst.UserId = @UserId 
                       OR (@UserCode <> '' AND ct.UserCode = @UserCode)
                       OR (@Email <> '' AND ct.Email = @Email)
                    ORDER BY s.SessionDate DESC, s.Id DESC",
                    new { UserId = userId, UserCode = userCode ?? "", Email = profile.Email ?? "" })).ToList();

                profile.MyCompanies = myCompanies;
            }
            catch { }

            // 5. Program Dash Access Check
            var roleClaimsList = User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
            profile.HasProgramDashAccess = User.IsInRole("Admin") || 
                roleClaimsList.Any(r => r.ToLower().Contains("pharmacist") || r.ToLower().Contains("صيدل") || r.ToLower().Contains("assistant") || r.ToLower().Contains("مساعد") || r.ToLower().Contains("doctor"));

            return View(profile);
        }

        [Authorize]
        [HttpGet("/Home/DownloadCertificate")]
        public async Task<IActionResult> DownloadCertificate(string fileName, int? waveId = null, bool adminPreview = false)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return BadRequest("Invalid certificate file name.");
            }

            fileName = Path.GetFileName(fileName); // Prevent directory traversal
            string fileNameNoExt = Path.GetFileNameWithoutExtension(fileName).Trim();

            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Challenge();
            }

            string connStr = _configuration.GetConnectionString("DefaultConnection") 
                ?? "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True";
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            bool isPrivileged = User.IsInRole("Admin") || User.IsInRole("HR") || User.IsInRole("Human Resources") || User.IsInRole("Branch Manager") || User.IsInRole("Branch Supervisor");

            string currentUserCode = (await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT UserCode FROM AspNetUsers WHERE Id = @UserId", new { UserId = userId }) ?? "").Trim();

            bool isOwner = false;
            if (!string.IsNullOrEmpty(currentUserCode))
            {
                if (fileNameNoExt.Equals(currentUserCode, StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains(currentUserCode, StringComparison.OrdinalIgnoreCase) ||
                    (long.TryParse(fileNameNoExt, out long fnNum) && long.TryParse(currentUserCode, out long ucNum) && fnNum == ucNum))
                {
                    isOwner = true;
                }
            }

            if (!isOwner && waveId.HasValue && waveId.Value > 0)
            {
                var hasCertRow = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT 1 FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                    new { UserId = userId, WaveId = waveId.Value });
                if (hasCertRow.HasValue) isOwner = true;
            }

            if (!isOwner && !isPrivileged)
            {
                return Forbid();
            }

            string webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            string filePath = "";

            if (waveId.HasValue && waveId.Value > 0)
            {
                string candidate = Path.Combine(webRoot, "uploads", "certificates", waveId.Value.ToString(), fileName);
                if (System.IO.File.Exists(candidate)) filePath = candidate;
            }

            if (string.IsNullOrEmpty(filePath))
            {
                string candidate = Path.Combine(webRoot, "uploads", "certificates", fileName);
                if (System.IO.File.Exists(candidate)) filePath = candidate;
            }

            if (string.IsNullOrEmpty(filePath))
            {
                string candidate = Path.Combine(webRoot, "uploads", "certs", fileName);
                if (System.IO.File.Exists(candidate)) filePath = candidate;
            }

            if (string.IsNullOrEmpty(filePath) && Directory.Exists(Path.Combine(webRoot, "uploads", "certificates")))
            {
                var allFound = Directory.GetFiles(Path.Combine(webRoot, "uploads", "certificates"), fileName, SearchOption.AllDirectories);
                if (allFound.Any()) filePath = allFound.First();
            }

            byte[]? dbBytes = null;
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                // Fallback to DB FileData if stored in UserWaveCertificates
                try
                {
                    if (isOwner)
                    {
                        dbBytes = await conn.QueryFirstOrDefaultAsync<byte[]>(@"
                            SELECT TOP 1 FileData 
                            FROM dbo.UserWaveCertificates 
                            WHERE UserId = @UserId 
                              AND (@WaveId IS NULL OR WaveId = @WaveId) 
                              AND FileData IS NOT NULL
                            ORDER BY CreatedAt DESC",
                            new { UserId = userId, WaveId = waveId });
                    }
                    else if (!string.IsNullOrEmpty(fileNameNoExt))
                    {
                        dbBytes = await conn.QueryFirstOrDefaultAsync<byte[]>(@"
                            SELECT TOP 1 UWC.FileData 
                            FROM dbo.UserWaveCertificates UWC
                            INNER JOIN AspNetUsers U ON UWC.UserId = U.Id
                            WHERE (LTRIM(RTRIM(U.UserCode)) = @Code OR UWC.FileName = @FileName)
                              AND (@WaveId IS NULL OR UWC.WaveId = @WaveId)
                              AND UWC.FileData IS NOT NULL
                            ORDER BY UWC.CreatedAt DESC",
                            new { Code = fileNameNoExt, FileName = fileName, WaveId = waveId });
                    }

                    if (dbBytes != null && dbBytes.Length > 0)
                    {
                        string targetDir = waveId.HasValue && waveId.Value > 0
                            ? Path.Combine(webRoot, "uploads", "certificates", waveId.Value.ToString())
                            : Path.Combine(webRoot, "uploads", "certificates");
                        Directory.CreateDirectory(targetDir);
                        filePath = Path.Combine(targetDir, fileName);
                        await System.IO.File.WriteAllBytesAsync(filePath, dbBytes);
                    }
                }
                catch { }
            }

            if ((string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) && (dbBytes == null || dbBytes.Length == 0))
            {
                return NotFound("الملف غير متوفر على السيرفر حالياً.");
            }

            // Track student download in dbo.UserWaveCertificates when downloaded by the trainee (not admin preview)
            if (isOwner && !adminPreview)
            {
                try
                {
                    int updatedRows = await conn.ExecuteAsync(@"
                        UPDATE dbo.UserWaveCertificates
                        SET IsDownloaded = 1,
                            DownloadedAt = GETDATE(),
                            DownloadCount = ISNULL(DownloadCount, 0) + 1,
                            FileUploaded = 1
                        WHERE UserId = @UserId AND (@WaveId IS NULL OR WaveId = @WaveId)",
                        new { UserId = userId, WaveId = waveId });

                    if (updatedRows == 0 && waveId.HasValue && waveId.Value > 0)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, EmailSent, Score, CreatedAt, FileUploaded, UploadedAt, FileName, IsDownloaded, DownloadedAt, DownloadCount)
                            VALUES (@UserId, @WaveId, @CertCode, 0, NULL, GETDATE(), 1, GETDATE(), @FileName, 1, GETDATE(), 1)",
                            new { UserId = userId, WaveId = waveId.Value, CertCode = $"WTTA-{DateTime.Now.Year}-{waveId.Value.ToString().PadLeft(3, '0')}-PB-Off-{currentUserCode}", FileName = fileName });
                    }
                }
                catch { }
            }

            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            string contentType = ext switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
            {
                return PhysicalFile(filePath, contentType, fileName);
            }

            return File(dbBytes!, contentType, fileName);
        }

        [HttpGet]
        public async Task<IActionResult> GetInstructions()
        {
            var instructions = await _examService.GetInstructionsAsync();
            return Json(new { instructions });
        }

        [HttpGet("/sentry-test")]
        public async Task<IActionResult> SentryTest()
        {
            var eventId = SentrySdk.CaptureMessage("🔥 Sentry Live Diagnostic Test Message from ASP.NET Core!");
            
            try
            {
                throw new InvalidOperationException("🔥 Sentry Test Exception Triggered Manually!");
            }
            catch (System.Exception ex)
            {
                SentrySdk.CaptureException(ex);
            }

            await SentrySdk.FlushAsync(System.TimeSpan.FromSeconds(3));

            return Content($"Sentry Test Event Sent Successfully! EventId: {eventId}. Please check Sentry Dashboard (Issues & Performance / Traces).");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error(string message = null, string trace = null)
        {
            return View(new ErrorViewModel 
            { 
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
                ErrorMessage = message,
                StackTrace = trace
            });
        }
    }
}
