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
            return View(cmsData);
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
                    ISNULL(u.UserRoleCustom, '') AS RoleName, 
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

            // Fetch DB wave certificates
            var certsQuery = @"
                SELECT 
                    uwc.WaveId,
                    ISNULL(w.WaveName, CONCAT('Wave #', uwc.WaveId)) AS WaveName,
                    uwc.CertificateCode,
                    uwc.Score,
                    uwc.CreatedAt
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

                        if (fn.Equals(userCode, StringComparison.OrdinalIgnoreCase) || 
                            fn.Contains(userCode, StringComparison.OrdinalIgnoreCase))
                        {
                            var fi = new FileInfo(f);
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

                        if (fn.Equals(userCode, StringComparison.OrdinalIgnoreCase) || 
                            fn.Contains(userCode, StringComparison.OrdinalIgnoreCase))
                        {
                            var fi = new FileInfo(f);
                            string rel = Path.GetRelativePath(webRoot, f).Replace('\\', '/');
                            matchedFiles.Add((f, rel, fi.Name, null, fi.Length));
                        }
                    }
                }
            }

            var finalCerts = new List<UserCertificateItemViewModel>();
            var usedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Match DB wave certificates with files
            foreach (var cert in dbCerts)
            {
                var match = matchedFiles.FirstOrDefault(m => 
                    !usedFilePaths.Contains(m.FullPath) && 
                    (
                        (cert.WaveId.HasValue && m.WaveId == cert.WaveId.Value) ||
                        (!string.IsNullOrEmpty(cert.CertificateCode) && m.FileName.Contains(cert.CertificateCode, StringComparison.OrdinalIgnoreCase))
                    ));

                if (match.FullPath == null)
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
                else
                {
                    cert.HasFileOnDisk = false;
                }

                finalCerts.Add(cert);
            }

            // 2. Add any remaining disk files matching user code
            foreach (var mf in matchedFiles.Where(m => !usedFilePaths.Contains(m.FullPath)))
            {
                finalCerts.Add(new UserCertificateItemViewModel
                {
                    WaveId = mf.WaveId,
                    WaveName = mf.WaveId.HasValue ? $"Wave #{mf.WaveId.Value}" : "Official Academy Certificate / شهادة معتمدة",
                    CertificateCode = profile.CertificateCode ?? $"CERT-{userCode}",
                    Score = profile.CertificateScore,
                    CreatedAt = System.IO.File.GetCreationTime(mf.FullPath),
                    HasFileOnDisk = true,
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
            try
            {
                var activeWave = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT uw.WaveId, w.WaveName 
                    FROM dbo.UserWaves uw WITH (NOLOCK)
                    INNER JOIN dbo.TrainingWaves w WITH (NOLOCK) ON uw.WaveId = w.Id
                    WHERE uw.UserId = @UserId AND uw.IsActive = 1", 
                    new { UserId = userId });

                if (activeWave != null)
                {
                    profile.ActiveWaveName = activeWave.WaveName;
                    int waveId = Convert.ToInt32(activeWave.WaveId);
                    var assignments = await conn.QueryAsync<dynamic>(@"
                        SELECT a.*, 
                               (SELECT COUNT(*) FROM dbo.AssignmentSubmissions s WITH (NOLOCK) WHERE s.AssignmentId = a.Id AND s.UserId = @UserId) as SubmissionsCount
                        FROM dbo.ContinuousAssignments a WITH (NOLOCK)
                        WHERE a.WaveId = @WaveId AND a.IsActive = 1
                        ORDER BY a.ScheduledEndTime DESC",
                        new { WaveId = waveId, UserId = userId });
                    profile.Assignments = assignments;
                }
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
        public async Task<IActionResult> DownloadCertificate(string fileName, int? waveId = null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return BadRequest("Invalid certificate file name.");
            }

            fileName = Path.GetFileName(fileName); // Prevent directory traversal

            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Challenge();
            }

            // Security: Current user must own this UserCode OR be Admin/HR
            bool isAdminOrHr = User.IsInRole("Admin") || User.IsInRole("HR") || User.IsInRole("Human Resources");

            if (!isAdminOrHr)
            {
                string connStr = _configuration.GetConnectionString("DefaultConnection") 
                    ?? "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True";
                using var conn = new SqlConnection(connStr);
                string userCode = await conn.QueryFirstOrDefaultAsync<string>(
                    "SELECT UserCode FROM AspNetUsers WHERE Id = @UserId", new { UserId = userId });

                if (string.IsNullOrEmpty(userCode) || !fileName.Contains(userCode, StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }
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

            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                return NotFound("الملف غير متوفر على السيرفر حالياً.");
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string contentType = ext switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            return PhysicalFile(filePath, contentType, fileName);
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
