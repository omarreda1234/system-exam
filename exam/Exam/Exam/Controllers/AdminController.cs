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
using Microsoft.DotNet.Scaffolding.Shared.Messaging;

namespace Exam.Controllers
{
    [Authorize(Roles = "Admin,HR,Human Resources,Branch Manager,SoftSkills Specialist")]
    public class AdminController : Controller
    {
        private readonly IExamService _examService;
        private readonly IEmailSender _emailSender;
        private readonly Microsoft.AspNetCore.SignalR.IHubContext<Exam.Hubs.NotificationHub> _hubContext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly string _connectionString;
        private readonly IHubContext<ImportHub> _hub2Context;
        private readonly IAuthService _authService;

        public AdminController(
            IExamService examService,
            IEmailSender emailSender,
            Microsoft.AspNetCore.SignalR.IHubContext<Exam.Hubs.NotificationHub> hubContext,
            Microsoft.AspNetCore.SignalR.IHubContext<Exam.Hubs.ImportHub> hub2Context,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            IAuthService authService,
            IConfiguration configuration)
        {
            _examService = examService;
            _emailSender = emailSender;
            _hubContext = hubContext;
            _hub2Context = hub2Context;
            _userManager = userManager;
            _roleManager = roleManager;
            _authService = authService;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
        {
            if (User.IsInRole("HR") || User.IsInRole("Human Resources"))
            {
                var allowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Index",
                    "AllUsers", 
                    "PendingRequests", 
                    "ApproveRequest", 
                    "RejectRequest", 
                    "CheckExistence",
                    "DeactivateUser", 
                    "ActivateUser", 
                    "SendCustomEmail", 
                    "ResetUserPassword", 
                    "GetUsersWithoutCertificate", 
                    "AddUser", 
                    "UpdateUserShift",
                    "GetWaves",
                    "CreateWave",
                    "UpdateUserRole",
                    "UpdateUserProfile",
                    "DeactivatedUsers",
                    "DeactivateUserByCode",
                    "ImportDeactivationsFromExcel",
                    "DeleteUserPermanently",
                    "ImportUsersToWaveFromExcel",
                    "Companies",
                    "AddCompany",
                    "EditCompany",
                    "DeleteCompany",
                    "ClearCompanyTrainees",
                    "ImportCompanyTraineesFromExcel",
                    "DownloadTraineesTemplate",
                    "DownloadPersonnelTemplate",
                    "GetCompanyTrainees",
                    "DeleteCompanyTrainee",
                    "AddCompanyTraineeManually",
                    "GetTraineeDetailsByCode",
                    "Waves",
                    "WaveDetails",
                    "GetWaveUserIds",
                    "GetUsersByWaveId",
                    "AssignUsersToWave",
                    "RemoveStudentFromExam",
                    "RemoveAllStudentsFromExam",
                    "WipeStudentData",
                    "ReassignExamToStudents",
                    "AssignExamToStudents",
                    "GetEligibleUsersForExam",
                    "ResendAssignmentEmail"
                };
                
                var actionName = context.RouteData.Values["action"]?.ToString();
                if (!allowedActions.Contains(actionName))
                {
                    context.Result = Forbid();
                    return;
                }
            }
            if (User.IsInRole("Branch Manager"))
            {
                var allowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Index",
                    "Students",
                    "WeeklyResults",
                    "GetFilteredExams",
                    "ExportStudentsToExcel",
                    "SendCertificates",
                    "SendFailEmails",
                    "ReassignExamToStudents",
                    "GetStudentExamReview",
                    "GetWeeklyResultsPaged"
                };
                
                var actionName = context.RouteData.Values["action"]?.ToString();
                if (!allowedActions.Contains(actionName))
                {
                    context.Result = Forbid();
                    return;
                }
            }
            base.OnActionExecuting(context);
        }

        [HttpGet("Admin/UpdateTopicSchema")]
        public async Task<IActionResult> UpdateTopicSchema()
        {
            using var conn = new SqlConnection(_connectionString);
            try {
                await conn.ExecuteAsync("ALTER TABLE Topics ADD CreatedAt DATETIME DEFAULT GETDATE() WITH VALUES;");
                return Ok("Schema updated successfully.");
            } catch (Exception ex) {
                return Ok("Already updated or error: " + ex.Message);
            }
        }

        public async Task<IActionResult> Index()
        {
            if (User.IsInRole("HR") || User.IsInRole("Human Resources"))
            {
                return RedirectToAction("AllUsers");
            }
            if (User.IsInRole("Branch Manager"))
            {
                return RedirectToAction("WeeklyResults");
            }
            if (User.IsInRole("SoftSkills Specialist") && !User.IsInRole("Admin"))
            {
                return RedirectToAction("Index", "SkillTracks");
            }
            var data = await _examService.GetDashboardDataAsync();
            return View(data);
        }

        [HttpGet]
        public async Task<IActionResult> PendingRequests()
        {
            using var conn = new SqlConnection(_connectionString);
            var requests = await conn.QueryAsync<dynamic>(@"
                SELECT R.*, B.BranchName 
                FROM RegistrationRequests R
                LEFT JOIN Branches B ON R.BranchId = B.Id
                WHERE R.Status = 'Pending'
                ORDER BY R.RequestDate DESC");
            return View(requests);
        }

        [HttpPost]
        public async Task<IActionResult> ApproveRequest(int requestId)
        {
            using var conn = new SqlConnection(_connectionString);
            var req = await conn.QueryFirstOrDefaultAsync<RegistrationRequest>(
                "SELECT Id, FullName, Email, Gmail, UserCode, PasswordHash AS Password, JobTitle, Shift, PhoneNumber, BranchId, Notes, Status FROM RegistrationRequests WHERE Id = @Id", 
                new { Id = requestId });
            
            if (req == null || req.Status != "Pending") return Json(new { success = false, message = "Request not found or already processed." });

            // Check if user code already exists in AspNetUsers
            var existingUserByCode = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT Email, UserCode FROM AspNetUsers WHERE UserCode = @Code", 
                new { Code = req.UserCode });

            if (existingUserByCode != null)
            {
                return Json(new { success = false, message = "هذا الكود مسجل مسبقاً لمستخدم آخر." });
            }

            // 1. Prepare RegisterDTO
            int? shiftId = null;
            if (int.TryParse(req.Shift, out int parsedShiftId) && parsedShiftId > 0)
            {
                shiftId = parsedShiftId;
            }

            int? branchId = req.BranchId;
            if (branchId <= 0) branchId = null;
            
            string targetRole = "User";
            if (!string.IsNullOrEmpty(req.JobTitle))
            {
                var jobLower = req.JobTitle.ToLower();
                if (jobLower.Contains("صيدل") || jobLower.Contains("\u0635\u064a\u062f\u0644") || jobLower.Contains("pharmacist") || jobLower.Contains("ØµÙŠØ¯Ù„ÙŠ")) 
                    targetRole = "Pharmacist";
                else if (jobLower.Contains("مساعد") || jobLower.Contains("\u0645\u0633\u0627\u0639\u062f") || jobLower.Contains("assistant") || jobLower.Contains("Ù…Ø³Ø§Ø¹Ø¯")) 
                    targetRole = "Assistant";
                else if (jobLower.Contains("مدير") || jobLower.Contains("\u0645\u062f\u064a\u0631") || jobLower.Contains("manager") || jobLower.Contains("Ù…Ø¯ÙŠØ±")) 
                    targetRole = "Branch Manager";
            }

            var registerDto = new RegisterDTO
            {
                UserName = req.FullName,
                FullName = req.FullName,
                Email = req.Email,
                Password = req.Password,
                Phone = req.PhoneNumber,
                BranchId = branchId,
                ShiftId = shiftId,
                RoleName = targetRole,
                UserCode = req.UserCode,
                CertificateCode = null
            };

            // 2. Use AuthService to Register
            var result = await _authService.RegisterAsync(registerDto);
            
            if (result.Succeeded)
            {
                // 3. Update Request Status
                await conn.ExecuteAsync("UPDATE RegistrationRequests SET Status = 'Approved', ProcessedDate = GETDATE(), ProcessedBy = @By WHERE Id = @Id", 
                    new { Id = requestId, By = User.Identity.Name });

                return Json(new { success = true, message = "ت م ت   ا ل م و ا ف ق ة   ع ل ى   ا ل ط ل ب   ب ن ج ا ح !" });
            }

            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return Json(new { success = false, message = "Error creating user: " + errors });
        }

        [HttpPost]
        public async Task<IActionResult> RejectRequest(int requestId)
        {
            using var conn = new SqlConnection(_connectionString);
            var req = await conn.QueryFirstOrDefaultAsync<RegistrationRequest>("SELECT Id, Status, FullName, Email FROM RegistrationRequests WHERE Id = @Id", new { Id = requestId });
            
            if (req == null || req.Status != "Pending") return Json(new { success = false, message = "Request not found." });

            // 1. Update Status to Rejected
            await conn.ExecuteAsync("UPDATE RegistrationRequests SET Status = 'Rejected', ProcessedDate = GETDATE(), ProcessedBy = @By WHERE Id = @Id", 
                new { Id = requestId, By = User.Identity.Name });

            return Json(new { success = true, message = "Request rejected successfully." });
        }

        [HttpGet]
        public async Task<IActionResult> CheckExistence(string email, string code)
        {
            using var conn = new SqlConnection(_connectionString);
            var existingUser = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT Email, UserCode FROM AspNetUsers WHERE UserCode = @Code", 
                new { Code = code });

            if (existingUser != null)
            {
                return Json(new { exists = true, message = "هذا الكود مسجل مسبقاً لمستخدم آخر." });
            }

            return Json(new { exists = false, message = "هذا الكود جديد بالكامل، لم يتم العثور على كود مكرر." });
        }

        public async Task<IActionResult> Exams()
        {
            ViewBag.ExamTypes = await _examService.GetAllExamTypesAsync();
            var exams = await _examService.GetAllExamsWithDetailsAsync();
            return View(exams);
        }

        public async Task<IActionResult> WeeklyExams()
        {
            ViewBag.ExamTypes = (await _examService.GetAllExamTypesAsync()).Where(t => !(t.TypeName ?? "").ToLower().Contains("wave"));
            var exams = await _examService.GetAllExamsWithDetailsAsync();
            var weeklyExams = exams.Where(e => !(e.ExamType ?? "").ToLower().Contains("wave")).ToList();
            return View(weeklyExams);
        }

        public async Task<IActionResult> WaveExams()
        {
            ViewBag.ExamTypes = (await _examService.GetAllExamTypesAsync()).Where(t => (t.TypeName ?? "").ToLower().Contains("wave"));
            ViewBag.Waves = await _examService.GetAllWavesAsync();
            var exams = await _examService.GetAllExamsWithDetailsAsync();
            var waveExams = exams.Where(e => (e.ExamType ?? "").ToLower().Contains("wave")).ToList();
            return View(waveExams);
        }

        // Admin filter: waves dropdown
        [HttpGet]
        public async Task<IActionResult> GetWaves()
        {
            var waves = await _examService.GetAllWavesAsync();
            return Json(waves);
        }

        // Admin filter: exams dropdown results
        // examTypeId: 0 => all exam types
        // waveId: 0 => ignored (caller should ensure waveId is provided when needed)
        [HttpGet]
        public async Task<IActionResult> GetExamsForFilter(int examTypeId, int waveId = 0)
        {
            IEnumerable<Exam.DTOs.adminExamDto> exams;

            if (waveId > 0)
            {
                exams = await _examService.GetExamsByWaveIdAsync(waveId);
            }
            else if (examTypeId == 0)
            {
                exams = await _examService.GetAllExamsWithDetailsAsync();
            }
            else
            {
                exams = await _examService.GetExamsByTypeAsync(examTypeId);
            }

            if (examTypeId != 0 || waveId != 0)
            {
                exams = exams.Where(e =>
                {
                    bool isWaveyRecord = (e.ExamType ?? "").ToLower().Contains("wave");
                    if (isWaveyRecord)
                    {
                        return !string.IsNullOrEmpty(e.WaveName);
                    }
                    return true;
                });
            }

            return Json(exams);
        }

        [HttpGet]
        public async Task<IActionResult> GetWeeklyExamsForFilter(int examTypeId = 0)
        {
            var exams = await _examService.GetAllExamsWithDetailsAsync();
            var weeklyExams = exams.Where(e => !(e.ExamType ?? "").ToLower().Contains("wave"));
            
            if (examTypeId > 0) {
                var allTypes = await _examService.GetAllExamTypesAsync();
                var targetTypeName = allTypes.FirstOrDefault(t => t.Id == examTypeId)?.TypeName;
                weeklyExams = weeklyExams.Where(e => e.ExamType == targetTypeName);
            }
            
            return Json(weeklyExams);
        }

        [HttpGet]
        public async Task<IActionResult> GetWaveExamsForFilter(int waveId = 0, int examTypeId = 0)
        {
            IEnumerable<Exam.DTOs.adminExamDto> exams;

            if (waveId > 0)
            {
                exams = await _examService.GetExamsByWaveIdAsync(waveId);
            }
            else
            {
                exams = await _examService.GetAllExamsWithDetailsAsync();
            }

            var waveExams = exams.Where(e => (e.ExamType ?? "").ToLower().Contains("wave"));

            if (examTypeId > 0) {
                var allTypes = await _examService.GetAllExamTypesAsync();
                var targetTypeName = allTypes.FirstOrDefault(t => t.Id == examTypeId)?.TypeName;
                waveExams = waveExams.Where(e => e.ExamType == targetTypeName);
            }

            return Json(waveExams);
        }

        [HttpGet]
        public async Task<IActionResult> GetStudents()
        {
            var students = await _examService.GetAllStudentsAsync();
            return Json(students);
        }

        [HttpGet]
        public async Task<IActionResult> GetGenerationRules(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            var rules = await conn.QueryAsync<ExamGenerationRuleDto>(
                @"SELECT r.*, c.CategoryName 
                  FROM ExamGenerationRules r 
                  LEFT JOIN Categories c ON r.CategoryId = c.Id 
                  WHERE r.ExamId = @ExamId",
                new { ExamId = examId }
            );
            return Json(rules);
        }
        [HttpGet]
        public async Task<IActionResult> ExportAllStudentsToExcel()
        {
            try
            {
                var results = await _examService.GetExamResultsAsync();
                using (var workbook = new XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("All Exam Results");
                    var currentRow = 1;

                    string[] headers = { "Exam Name", "Student Name", "Email", "Wave", "Branch", "Status", "Score %", "Time (Min)", "Passed" };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = worksheet.Cell(currentRow, i + 1);
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.LightGray;
                    }

                    foreach (var item in results)
                    {
                        currentRow++;
                        worksheet.Cell(currentRow, 1).Value = item.ExamName;
                        worksheet.Cell(currentRow, 2).Value = item.StudentName;
                        worksheet.Cell(currentRow, 3).Value = item.StudentEmail;
                        worksheet.Cell(currentRow, 4).Value = item.WaveName;
                        worksheet.Cell(currentRow, 5).Value = item.BranchName;
                        worksheet.Cell(currentRow, 6).Value = item.Status;
                        worksheet.Cell(currentRow, 7).Value = item.Score;
                        worksheet.Cell(currentRow, 8).Value = item.DurationInMinutes;
                        worksheet.Cell(currentRow, 9).Value = item.IsPassed == true ? "Yes" : (item.IsPassed == false ? "No" : "N/A");
                    }

                    worksheet.Columns().AdjustToContents();

                    using (var stream = new MemoryStream())
                    {
                        workbook.SaveAs(stream);
                        var content = stream.ToArray();
                        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"All_Exams_Report_{DateTime.Now:yyyyMMdd}.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                return Content($"Error: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportStudentsToExcel(int examId)
        {
            try
            {
                if (User.IsInRole("Branch Manager"))
                {
                    using var conn = new SqlConnection(_connectionString);
                    var examEndTime = await conn.QueryFirstOrDefaultAsync<DateTime?>(
                        "SELECT EndTime FROM Exams WHERE Id = @Id", 
                        new { Id = examId });

                    if (examEndTime.HasValue && DateTime.Now < examEndTime.Value)
                    {
                        return Content("Results are coming soon after the exam concludes.");
                    }
                }

                var results = await _examService.GetExamResultsByExamIdAsync(examId);
                if (User.IsInRole("Branch Manager"))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    if (currentUser != null && currentUser.BranchId.HasValue)
                    {
                        using var conn = new SqlConnection(_connectionString);
                        var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                            "SELECT BranchName FROM Branches WHERE Id = @Id", 
                            new { Id = currentUser.BranchId.Value });
                            
                        if (!string.IsNullOrEmpty(branchName))
                        {
                            results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                        }
                    }
                    else
                    {
                        results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                    }
                }
                var examInfo = await _examService.GetExamByIdAsync(examId);
                string examTitle = examInfo?.Title ?? "Exam";

                using (var workbook = new XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Exam Participation Report");
                    var currentRow = 1;

                    // Header Info
                    worksheet.Cell(currentRow, 1).Value = "Exam:";
                    worksheet.Cell(currentRow, 2).Value = examTitle;
                    currentRow++;
                    worksheet.Cell(currentRow, 1).Value = "Generated:";
                    worksheet.Cell(currentRow, 2).Value = DateTime.Now.ToString("g");
                    currentRow += 2;

                    string[] headers = { "Student Name", "Email", "Batch/Wave", "Branch", "Status", "Start Time", "End Time", "Score (%)", "Points", "Available", "Time (Min)", "Passed", "User Code" };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = worksheet.Cell(currentRow, i + 1);
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4F46E5");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    foreach (var item in results)
                    {
                        currentRow++;
                        worksheet.Cell(currentRow, 1).Value = item.StudentName;
                        worksheet.Cell(currentRow, 2).Value = item.StudentEmail;
                        worksheet.Cell(currentRow, 3).Value = item.WaveName ?? "Global";
                        worksheet.Cell(currentRow, 4).Value = item.BranchName ?? "Global";
                        worksheet.Cell(currentRow, 5).Value = item.Status;
                        worksheet.Cell(currentRow, 6).Value = item.ActualStartTime.HasValue ? item.ActualStartTime.Value.ToString("hh:mm tt") : "--";
                        worksheet.Cell(currentRow, 7).Value = item.ActualEndTime.HasValue ? item.ActualEndTime.Value.ToString("hh:mm tt") : "--";
                        worksheet.Cell(currentRow, 8).Value = item.Score;
                        worksheet.Cell(currentRow, 9).Value = item.FinalScore;
                      //  worksheet.Cell(currentRow, 10).Value = item.TotalScoreAvailable;
                        worksheet.Cell(currentRow, 11).Value = item.DurationInMinutes;
                        worksheet.Cell(currentRow, 12).Value = item.IsPassed == true ? "Yes" : (item.IsPassed == false ? "No" : "N/A");
                        worksheet.Cell(currentRow, 13).Value = item.UserCode;
                    }

                    worksheet.Columns().AdjustToContents();

                    using (var stream = new MemoryStream())
                    {
                        workbook.SaveAs(stream);
                        var content = stream.ToArray();
                        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{examTitle.Replace(" ", "_")}_Report.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                return Content($"Error: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportWaveResultsToExcel(int waveId)
        {
            try
            {
                var waves = await _examService.GetAllWavesAsync();
                var waveInfo = waves.FirstOrDefault(w => w.Id == waveId);
                string waveName = waveInfo?.WaveName ?? "Wave";

                var results = (await _examService.GetWaveAggregateResultsAsync(waveId)).ToList();

                // Branch manager restriction
                if (User.IsInRole("Branch Manager"))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    if (currentUser != null && currentUser.BranchId.HasValue)
                    {
                        using var conn = new SqlConnection(_connectionString);
                        var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                            "SELECT BranchName FROM Branches WHERE Id = @Id",
                            new { Id = currentUser.BranchId.Value });
                        if (!string.IsNullOrEmpty(branchName))
                        {
                            results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase)).ToList();
                        }
                        else
                        {
                            results = new List<Exam.DTOs.WaveStudentResultDto>();
                        }
                    }
                    else
                    {
                        results = new List<Exam.DTOs.WaveStudentResultDto>();
                    }
                }

                using (var workbook = new XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Wave Performance Report");
                    var currentRow = 1;

                    // Header Info
                    worksheet.Cell(currentRow, 1).Value = "Wave:";
                    worksheet.Cell(currentRow, 2).Value = waveName;
                    currentRow++;
                    worksheet.Cell(currentRow, 1).Value = "Generated:";
                    worksheet.Cell(currentRow, 2).Value = DateTime.Now.ToString("g");
                    currentRow += 2;

                    string[] headers = { "Student Name", "Email", "User Code", "Role", "Batch/Wave", "Branch", "Exams Completed", "Exams Assigned", "Total Score", "Total Available", "Aggregate (%)", "Wave Status" };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = worksheet.Cell(currentRow, i + 1);
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0D9488");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    foreach (var item in results)
                    {
                        currentRow++;
                        worksheet.Cell(currentRow, 1).Value = item.StudentName;
                        worksheet.Cell(currentRow, 2).Value = item.StudentEmail;
                        worksheet.Cell(currentRow, 3).Value = item.UserCode;
                        worksheet.Cell(currentRow, 4).Value = item.RoleName;
                        worksheet.Cell(currentRow, 5).Value = item.WaveName ?? "Global";
                        worksheet.Cell(currentRow, 6).Value = item.BranchName ?? "Global";
                        worksheet.Cell(currentRow, 7).Value = item.ExamsCompleted;
                        worksheet.Cell(currentRow, 8).Value = item.ExamsAssigned;
                        worksheet.Cell(currentRow, 9).Value = item.TotalScore;
                        worksheet.Cell(currentRow, 10).Value = item.TotalAvailable;
                        worksheet.Cell(currentRow, 11).Value = item.AggregatePercentage;
                        worksheet.Cell(currentRow, 12).Value = item.WaveStatus;
                    }

                    worksheet.Columns().AdjustToContents();

                    using (var stream = new MemoryStream())
                    {
                        workbook.SaveAs(stream);
                        var content = stream.ToArray();
                        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{waveName.Replace(" ", "_")}_Wave_Report.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                return Content($"Error: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> Students(int? examId, int? typeId = null, int? month = null, int? year = null, string forceMode = null)
        {
            var examTypes = await _examService.GetAllExamTypesAsync();
            ViewBag.ExamTypes = examTypes;
            ViewBag.SelectedTypeId = typeId;
            ViewBag.SelectedMonth = month;
            ViewBag.SelectedYear = year;

            var exams = await _examService.GetActiveExamsForDropdownAsync(typeId, month, year);

            if (forceMode == "wavey")
            {
                exams = exams.Where(e => (e.TypeName ?? "").ToLower().Contains("wave") || (e.Title ?? "").ToLower().Contains("wave")).ToList();
            }
            else if (forceMode == "weekly")
            {
                if (!User.IsInRole("Branch Manager") && (!typeId.HasValue || typeId.Value == 0))
                {
                    exams = exams.Where(e => !((e.TypeName ?? "").ToLower().Contains("wave") || (e.Title ?? "").ToLower().Contains("wave"))).ToList();
                }
            }

            ViewBag.Exams = exams;

            int selectedId = 0;
            if (examId.HasValue && exams.Any(e => e.Id == examId.Value))
            {
                selectedId = examId.Value;
            }
            else
            {
                selectedId = exams.OrderByDescending(e => e.StartTime).FirstOrDefault()?.Id ?? 0;
            }

            ViewBag.SelectedExamId = selectedId;

            if (selectedId > 0)
            {
                var exam = await _examService.GetExamByIdAsync(selectedId);
                if (exam != null)
                {
                    bool isWavey = (exam.ExamType ?? "").ToLower().Contains("wave") || 
                                   (exam.Title ?? "").ToLower().Contains("wave");
                    
                    ViewBag.ExamTitle = exam.Title;

                    if (isWavey)
                    {
                        var results = await _examService.GetExamResultsByExamIdAsync(selectedId);
                        if (User.IsInRole("Branch Manager"))
                        {
                            var currentUser = await _userManager.GetUserAsync(User);
                            if (currentUser != null && currentUser.BranchId.HasValue)
                            {
                                using var conn = new SqlConnection(_connectionString);
                                var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                                    "SELECT BranchName FROM Branches WHERE Id = @Id", 
                                    new { Id = currentUser.BranchId.Value });
                                    
                                if (!string.IsNullOrEmpty(branchName))
                                {
                                    results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase));
                                }
                                else
                                {
                                    results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                                }
                            }
                            else
                            {
                                results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                            }
                        }
                        if (forceMode == "weekly")
                        {
                            return View("WeeklyResults", Enumerable.Empty<Exam.DTOs.ExamResultRowDto>());
                        }
                        return View("Students", results);
                    }
                    else
                    {
                        return View("WeeklyResults", Enumerable.Empty<Exam.DTOs.ExamResultRowDto>());
                    }
                }
            }

            return View("WeeklyResults", Enumerable.Empty<Exam.DTOs.ExamResultRowDto>());
        }

        [HttpGet]
        public async Task<IActionResult> WaveyResults(int? waveId, int? month = null, int? year = null)
        {
            var waves = (await _examService.GetAllWavesAsync()).ToList();

            // Apply year/month filters on Wave StartDate if provided
            if (year.HasValue && year.Value > 0)
                waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Year == year.Value).ToList();
            if (month.HasValue && month.Value > 0)
                waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Month == month.Value).ToList();

            ViewBag.Waves = waves;
            ViewBag.SelectedMonth = month;
            ViewBag.SelectedYear = year;

            int selectedWaveId = waveId ?? 0;
            if (selectedWaveId <= 0)
                selectedWaveId = waves.FirstOrDefault()?.Id ?? 0;
            ViewBag.SelectedWaveId = selectedWaveId;

            if (selectedWaveId > 0)
            {
                var results = (await _examService.GetWaveAggregateResultsAsync(selectedWaveId)).ToList();

                // Branch manager restriction
                if (User.IsInRole("Branch Manager"))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    if (currentUser != null && currentUser.BranchId.HasValue)
                    {
                        using var conn = new SqlConnection(_connectionString);
                        var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                            "SELECT BranchName FROM Branches WHERE Id = @Id",
                            new { Id = currentUser.BranchId.Value });
                        if (!string.IsNullOrEmpty(branchName))
                            results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase)).ToList();
                        else
                            results = new List<Exam.DTOs.WaveStudentResultDto>();
                    }
                    else
                        results = new List<Exam.DTOs.WaveStudentResultDto>();
                }

                return View(results);
            }

            return View(new List<Exam.DTOs.WaveStudentResultDto>());
        }

        [HttpGet]
        public async Task<IActionResult> GetStudentWaveDetails(string studentId, int waveId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var student = await _userManager.FindByIdAsync(studentId);
            if (student == null) return NotFound("Student not found.");

            // 1. Get all exams in this wave
            var exams = (await conn.QueryAsync<dynamic>(@"
                SELECT 
                    e.Id AS ExamId,
                    e.Title AS ExamTitle,
                    e.TotalPoints,
                    e.TotalQuestionsToShow,
                    et.TypeName AS ExamType,
                    e.IsFinalExam
                FROM Exams e
                LEFT JOIN ExamTypes et ON e.ExamTypeId = et.Id
                WHERE e.WaveId = @WaveId
                ORDER BY e.Title", new { WaveId = waveId })).ToList();

            // 2. Get the student's latest attempts on these exams
            var attempts = (await conn.QueryAsync<dynamic>(@"
                WITH LatestAttempts AS (
                    SELECT 
                        uea.ExamId,
                        uea.Status,
                        ISNULL(uea.FinalScore, 0) AS FinalScore,
                        uea.Score AS Percentage,
                        uea.AttemptNumber,
                        uea.IsPassed,
                        uea.Id AS AttemptId,
                        ROW_NUMBER() OVER (PARTITION BY uea.ExamId ORDER BY uea.AttemptNumber DESC) AS rn
                    FROM UserExamAttempts uea
                    INNER JOIN Exams e ON e.Id = uea.ExamId
                    WHERE e.WaveId = @WaveId AND uea.UserId = @UserId
                )
                SELECT * FROM LatestAttempts WHERE rn = 1", 
                new { WaveId = waveId, UserId = studentId })).ToList();

            var roles = await _userManager.GetRolesAsync(student);
            string userRole = roles.FirstOrDefault() ?? "All";

            var examIds = exams.Select(e => (int)e.ExamId).ToList();
            var rules = new List<dynamic>();
            if (examIds.Any())
            {
                rules = (await conn.QueryAsync<dynamic>(@"
                    SELECT ExamId, EasyCount, MediumCount, HardCount, CategoryId, TargetRole 
                    FROM ExamGenerationRules 
                    WHERE ExamId IN @ExamIds", 
                    new { ExamIds = examIds })).ToList();
            }

            // Fetch seen question points for all attempts for this user in this wave
            var seenPointsList = new List<dynamic>();
            if (examIds.Any())
            {
                seenPointsList = (await conn.QueryAsync<dynamic>(@"
                    SELECT usq.AttemptId, SUM(q.Points) AS SeenPoints
                    FROM UserSeenQuestions usq
                    JOIN Questions q ON usq.QuestionId = q.Id
                    JOIN UserExamAttempts uea ON usq.AttemptId = uea.Id
                    WHERE uea.UserId = @UserId AND uea.ExamId IN @ExamIds
                    GROUP BY usq.AttemptId",
                    new { UserId = studentId, ExamIds = examIds })).ToList();
            }

            var attemptSeenPoints = seenPointsList.ToDictionary(
                x => (int)x.AttemptId,
                x => (decimal)x.SeenPoints
            );

            // Calculate MaxPoints per exam for this student
            var examMaxPoints = new Dictionary<int, decimal>();
            foreach (var exam in exams)
            {
                int examId = (int)exam.ExamId;
                bool isFinal = exam.IsFinalExam != null && (bool)exam.IsFinalExam;
                string typeName = exam.ExamType ?? "";
                
                var attempt = attempts.FirstOrDefault(a => (int)a.ExamId == examId);
                decimal? seenPoints = null;
                if (attempt != null)
                {
                    if (attemptSeenPoints.TryGetValue((int)attempt.AttemptId, out decimal sp))
                    {
                        seenPoints = sp;
                    }
                }

                decimal maxPoints = 0;
                if (seenPoints.HasValue && seenPoints.Value > 0)
                {
                    maxPoints = seenPoints.Value;
                }
                else
                {
                    // Calculate expected points from rules
                    var examRules = rules.Where(r => (int)r.ExamId == examId).ToList();
                    var matchingRules = examRules.Where(r => {
                        string target = (string)r.TargetRole;
                        if (target == "All") return true;
                        return string.Equals(target, userRole, StringComparison.OrdinalIgnoreCase) ||
                               userRole.ToLower().Contains(target.ToLower());
                    }).ToList();

                    if (matchingRules.Any())
                    {
                        decimal rulePoints = 0;
                        foreach (var rule in matchingRules)
                        {
                            int count = (int)rule.EasyCount + (int)rule.MediumCount + (int)rule.HardCount;
                            decimal catPoints = 1; // Default fallback
                            rulePoints += count * catPoints;
                        }
                        maxPoints = rulePoints;
                    }
                    else
                    {
                        int questionsToShow = exam.TotalQuestionsToShow != null ? (int)exam.TotalQuestionsToShow : 0;
                        if (questionsToShow > 0)
                        {
                            maxPoints = questionsToShow * 1.0m;
                        }
                        else
                        {
                            maxPoints = (decimal)(exam.TotalPoints ?? 0);
                        }
                    }

                    if (maxPoints <= 0)
                    {
                        maxPoints = 5.0m;
                    }
                }

                examMaxPoints[examId] = maxPoints;
            }

            // 3. Map them together
            var details = new List<dynamic>();
            foreach (var exam in exams)
            {
                int examId = (int)exam.ExamId;
                var attempt = attempts.FirstOrDefault(a => (int)a.ExamId == examId);

                details.Add(new {
                    ExamId = examId,
                    ExamTitle = (string)exam.ExamTitle,
                    TotalPoints = examMaxPoints[examId],
                    Status = attempt != null ? (string)attempt.Status : "Not Started",
                    FinalScore = attempt != null ? (decimal)attempt.FinalScore : 0,
                    Percentage = attempt != null ? (decimal)attempt.Percentage : 0,
                    AttemptNumber = attempt != null ? (int)attempt.AttemptNumber : 0,
                    IsPassed = attempt != null ? (bool?)attempt.IsPassed : null,
                    AttemptId = attempt != null ? (int?)attempt.AttemptId : null
                });
            }

            ViewBag.StudentName = student.FullName ?? student.UserName;
            ViewBag.StudentId = studentId;
            return PartialView("GetStudentWaveDetails", details);
        }

        [HttpGet]
        public async Task<IActionResult> LiveMonitor(
            int? branchId = null, int? shiftId = null, string roleName = null,
            string status = null, int? waveId = null, string date = null, int? examId = null)
        {
            // Load filter options
            var branches  = await _examService.GetAllBranchesAsync();
            var shifts    = await _examService.GetAllShiftsAsync();
            var waves     = await _examService.GetAllWavesAsync();
            var examTypes = await _examService.GetAllExamTypesAsync();

            var allExams = await _examService.GetAllExamsWithDetailsAsync();
            var weeklyExams = allExams.Where(e => !(e.ExamType ?? "").ToLower().Contains("wave")).ToList();

            using var conn = new SqlConnection(_connectionString);
            var roles = (await conn.QueryAsync<string>(
                "SELECT DISTINCT r.Name FROM AspNetRoles r " +
                "INNER JOIN AspNetUserRoles ur ON ur.RoleId = r.Id " +
                "WHERE r.Name NOT IN ('Admin','HR','Human Resources','Branch Manager','Reception','SoftSkills Specialist') " +
                "ORDER BY r.Name")).ToList();

            ViewBag.Branches  = branches;
            ViewBag.Shifts    = shifts;
            ViewBag.Waves     = waves;
            ViewBag.WeeklyExams = weeklyExams;
            ViewBag.ExamTypes = examTypes;
            ViewBag.Roles     = roles;
            ViewBag.SelectedBranchId = branchId;
            ViewBag.SelectedShiftId  = shiftId;
            ViewBag.SelectedRoleName = roleName;
            ViewBag.SelectedStatus   = status;
            ViewBag.SelectedWaveId   = waveId;
            ViewBag.SelectedDate     = date;
            ViewBag.SelectedExamId   = examId;

            // Summary stats
            ViewBag.TotalCount      = 0;
            ViewBag.InProgressCount = 0;
            ViewBag.CompletedCount  = 0;
            ViewBag.NotStartedCount = 0;

            return View(Enumerable.Empty<Exam.DTOs.LiveMonitorRowDto>());
        }

        [HttpGet]
        public async Task<IActionResult> ExportLiveMonitorToExcel(
            int? branchId = null, int? shiftId = null, string roleName = null,
            string status = null, int? waveId = null, string date = null, int? examId = null)
        {
            try
            {
                DateTime? parsedDate = null;
                if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var d))
                    parsedDate = d;

                var results = await _examService.GetLiveMonitorDataAsync(
                    branchId, shiftId, roleName, status, waveId, parsedDate, examId);

                using (var workbook = new XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Live Monitor");
                    var currentRow = 1;

                    string[] headers = { "Personnel", "User Code", "Branch", "Shift", "Role", "Exam", "Wave", "Status", "Start Time", "End Time", "Duration (Min)", "Score", "Total Points", "Percentage (%)", "Result" };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = worksheet.Cell(currentRow, i + 1);
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2A766F");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    foreach (var item in results)
                    {
                        currentRow++;
                        worksheet.Cell(currentRow, 1).Value = item.StudentName;
                        worksheet.Cell(currentRow, 2).Value = item.UserCode;
                        worksheet.Cell(currentRow, 3).Value = item.BranchName ?? "--";
                        worksheet.Cell(currentRow, 4).Value = item.ShiftName ?? "--";
                        worksheet.Cell(currentRow, 5).Value = item.RoleName ?? "--";
                        worksheet.Cell(currentRow, 6).Value = item.ExamTitle;
                        worksheet.Cell(currentRow, 7).Value = item.WaveName ?? "--";
                        worksheet.Cell(currentRow, 8).Value = item.Status;
                        worksheet.Cell(currentRow, 9).Value = item.StartTime.HasValue ? item.StartTime.Value.ToString("dd/MM/yyyy hh:mm tt") : "--";
                        worksheet.Cell(currentRow, 10).Value = item.EndTime.HasValue ? item.EndTime.Value.ToString("dd/MM/yyyy hh:mm tt") : "--";
                        worksheet.Cell(currentRow, 11).Value = item.DurationInMinutes;
                        worksheet.Cell(currentRow, 12).Value = item.FinalScore;
                        worksheet.Cell(currentRow, 13).Value = item.TotalPoints;
                        worksheet.Cell(currentRow, 14).Value = item.Percentage;

                        string resultStr = "--";
                        if (item.Status == "Completed")
                        {
                            resultStr = item.IsPassed == true ? "PASS" : "FAILED";
                        }
                        worksheet.Cell(currentRow, 15).Value = resultStr;
                    }

                    worksheet.Columns().AdjustToContents();

                    using (var stream = new MemoryStream())
                    {
                        workbook.SaveAs(stream);
                        var content = stream.ToArray();
                        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Live_Monitor_Report_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                return Content($"Error: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetExamsByWaveId(int waveId)
        {
            using var conn = new SqlConnection(_connectionString);
            var exams = await conn.QueryAsync<dynamic>(
                "SELECT Id, Title FROM Exams WHERE WaveId = @WaveId AND IsActive = 1 ORDER BY Title",
                new { WaveId = waveId }
            );
            return Json(exams.Select(e => new { id = e.Id, title = e.Title }));
        }

        [HttpGet]
        public async Task<IActionResult> GetLiveMonitorData(
            int? branchId, int? shiftId, string roleName, string status,
            int? waveId, string date, int? examId)
        {
            DateTime? parsedDate = null;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var d))
                parsedDate = d;

            var results = await _examService.GetLiveMonitorDataAsync(
                branchId, shiftId, roleName, status, waveId, parsedDate, examId);

            return Json(results.Select(r => new {
                r.UserId, r.StudentName, r.StudentEmail, r.UserCode,
                r.RoleName, r.BranchName, r.ShiftName,
                r.ExamTitle, r.ExamType, r.WaveName,
                r.Status, r.FinalScore, r.TotalPoints, r.Percentage,
                startTime = r.StartTime?.ToString("dd/MM/yyyy HH:mm"),
                endTime   = r.EndTime?.ToString("dd/MM/yyyy HH:mm"),
                r.DurationInMinutes, r.AttemptNumber, r.IsPassed, r.CertificateCode
            }));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GetLiveMonitorPaged()
        {
            var draw = Request.Form["draw"].FirstOrDefault();
            var startStr = Request.Form["start"].FirstOrDefault();
            var lengthStr = Request.Form["length"].FirstOrDefault();
            var searchValue = Request.Form["search[value]"].FirstOrDefault();
            var orderColumnIndex = Request.Form["order[0][column]"].FirstOrDefault();
            var orderDir = Request.Form["order[0][dir]"].FirstOrDefault();

            // Filters passed from form serialization
            var branchIdStr = Request.Form["branchId"].FirstOrDefault();
            var shiftIdStr = Request.Form["shiftId"].FirstOrDefault();
            var roleName = Request.Form["roleName"].FirstOrDefault();
            var status = Request.Form["status"].FirstOrDefault();
            var waveIdStr = Request.Form["waveId"].FirstOrDefault();
            var dateStr = Request.Form["date"].FirstOrDefault();
            var examIdStr = Request.Form["examId"].FirstOrDefault();

            int start = string.IsNullOrEmpty(startStr) ? 0 : int.Parse(startStr);
            int length = string.IsNullOrEmpty(lengthStr) ? 10 : int.Parse(lengthStr);

            int? branchId = string.IsNullOrEmpty(branchIdStr) ? null : int.Parse(branchIdStr);
            int? shiftId = string.IsNullOrEmpty(shiftIdStr) ? null : int.Parse(shiftIdStr);
            int? waveId = string.IsNullOrEmpty(waveIdStr) ? null : int.Parse(waveIdStr);
            int? examId = string.IsNullOrEmpty(examIdStr) ? null : int.Parse(examIdStr);

            DateTime? parsedDate = null;
            if (!string.IsNullOrWhiteSpace(dateStr) && DateTime.TryParse(dateStr, out var d))
                parsedDate = d;

            var results = await _examService.GetLiveMonitorDataAsync(
                branchId, shiftId, roleName, status, waveId, parsedDate, examId);

            int totalRecords = results.Count();

            // Stats counts for cards (computed from the matches BEFORE search filter)
            int totalCount = totalRecords;
            int inProgressCount = results.Count(r => r.Status == "InProgress");
            int completedCount = results.Count(r => r.Status == "Completed");
            int notStartedCount = results.Count(r => r.Status == "Not Started");

            // Apply search
            if (!string.IsNullOrEmpty(searchValue))
            {
                var searchLower = searchValue.ToLower();
                results = results.Where(r =>
                    (r.StudentName ?? "").ToLower().Contains(searchLower) ||
                    (r.StudentEmail ?? "").ToLower().Contains(searchLower) ||
                    (r.UserCode ?? "").ToLower().Contains(searchLower) ||
                    (r.BranchName ?? "").ToLower().Contains(searchLower) ||
                    (r.ShiftName ?? "").ToLower().Contains(searchLower) ||
                    (r.ExamTitle ?? "").ToLower().Contains(searchLower) ||
                    (r.WaveName ?? "").ToLower().Contains(searchLower) ||
                    (r.Status ?? "").ToLower().Contains(searchLower)
                );
            }

            int filteredRecords = results.Count();

            // Apply sorting
            if (!string.IsNullOrEmpty(orderColumnIndex))
            {
                bool isAsc = orderDir == "asc";
                switch (orderColumnIndex)
                {
                    case "0":
                        results = isAsc ? results.OrderBy(r => r.StudentName) : results.OrderByDescending(r => r.StudentName);
                        break;
                    case "1":
                        results = isAsc ? results.OrderBy(r => r.BranchName) : results.OrderByDescending(r => r.BranchName);
                        break;
                    case "2":
                        results = isAsc ? results.OrderBy(r => r.ShiftName) : results.OrderByDescending(r => r.ShiftName);
                        break;
                    case "3":
                        results = isAsc ? results.OrderBy(r => r.RoleName) : results.OrderByDescending(r => r.RoleName);
                        break;
                    case "4":
                        results = isAsc ? results.OrderBy(r => r.ExamTitle) : results.OrderByDescending(r => r.ExamTitle);
                        break;
                    case "5":
                        results = isAsc ? results.OrderBy(r => r.WaveName) : results.OrderByDescending(r => r.WaveName);
                        break;
                    case "6":
                        results = isAsc ? results.OrderBy(r => r.Status) : results.OrderByDescending(r => r.Status);
                        break;
                    case "7":
                        results = isAsc ? results.OrderBy(r => r.StartTime) : results.OrderByDescending(r => r.StartTime);
                        break;
                    case "8":
                        results = isAsc ? results.OrderBy(r => r.EndTime) : results.OrderByDescending(r => r.EndTime);
                        break;
                    case "9":
                        results = isAsc ? results.OrderBy(r => r.DurationInMinutes) : results.OrderByDescending(r => r.DurationInMinutes);
                        break;
                    case "10":
                        results = isAsc ? results.OrderBy(r => r.Percentage) : results.OrderByDescending(r => r.Percentage);
                        break;
                    case "11":
                        results = isAsc ? results.OrderBy(r => r.IsPassed) : results.OrderByDescending(r => r.IsPassed);
                        break;
                }
            }

            var pagedResults = results.Skip(start).Take(length).ToList();

            var data = pagedResults.Select(r => new
            {
                userId = r.UserId,
                studentName = r.StudentName,
                studentEmail = r.StudentEmail,
                userCode = r.UserCode ?? "--",
                branchName = r.BranchName ?? "--",
                shiftName = r.ShiftName ?? "--",
                roleName = r.RoleName ?? "--",
                examTitle = r.ExamTitle,
                examType = r.ExamType,
                waveName = r.WaveName ?? "--",
                status = r.Status,
                finalScore = r.FinalScore,
                totalPoints = r.TotalPoints,
                percentage = r.Percentage,
                startTimeDate = r.StartTime?.ToString("dd/MM/yyyy") ?? "--",
                startTimeTime = r.StartTime?.ToString("hh:mm tt") ?? "",
                endTimeDate = r.EndTime?.ToString("dd/MM/yyyy") ?? "--",
                endTimeTime = r.EndTime?.ToString("hh:mm tt") ?? "",
                duration = r.DurationInMinutes,
                isPassed = r.IsPassed,
                certificateCode = r.CertificateCode,
                attemptId = r.AttemptId
            });

            return Json(new
            {
                draw = draw,
                recordsTotal = totalRecords,
                recordsFiltered = filteredRecords,
                totalCount = totalCount,
                inProgressCount = inProgressCount,
                completedCount = completedCount,
                notStartedCount = notStartedCount,
                data = data
            });
        }

        [HttpGet]
        public async Task<IActionResult> Certificates(int? waveId, int? examId = null, int? typeId = null, int? month = null, int? year = null)

        {
            var examTypes = (await _examService.GetAllExamTypesAsync())
                .Where(t => t.TypeName != null && t.TypeName.ToLower().Contains("wave"))
                .ToList();
            ViewBag.ExamTypes = examTypes;
            ViewBag.SelectedTypeId = typeId;
            ViewBag.SelectedMonth = month;
            ViewBag.SelectedYear = year;

            var waves = (await _examService.GetAllWavesAsync()).ToList();

            // Apply year/month filters on Wave StartDate if provided
            if (year.HasValue && year.Value > 0)
            {
                waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Year == year.Value).ToList();
            }
            if (month.HasValue && month.Value > 0)
            {
                waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Month == month.Value).ToList();
            }

            ViewBag.Waves = waves;

            using var conn = new SqlConnection(_connectionString);
            int selectedWaveId = waveId ?? 0;
            if (selectedWaveId <= 0 && examId.HasValue && examId.Value > 0)
            {
                // Try to find the WaveId from the examId
                selectedWaveId = await conn.QueryFirstOrDefaultAsync<int>(
                    "SELECT WaveId FROM Exams WHERE Id = @ExamId",
                    new { ExamId = examId.Value });
            }

            if (selectedWaveId <= 0)
            {
                selectedWaveId = waves.FirstOrDefault()?.Id ?? 0;
            }

            ViewBag.SelectedWaveId = selectedWaveId;

            int finalExamId = 0;
            if (selectedWaveId > 0)
            {
                finalExamId = await conn.QueryFirstOrDefaultAsync<int>(
                    "SELECT TOP 1 Id FROM Exams WHERE WaveId = @WaveId AND IsFinalExam = 1 ORDER BY StartTime DESC",
                    new { WaveId = selectedWaveId });
            }
            ViewBag.SelectedExamId = finalExamId;

            if (finalExamId > 0)
            {
                var exam = await _examService.GetExamByIdAsync(finalExamId);
                if (exam != null)
                {
                    var results = await _examService.GetExamResultsByExamIdAsync(finalExamId);
                    
                    if (User.IsInRole("Branch Manager"))
                    {
                        var currentUser = await _userManager.GetUserAsync(User);
                        if (currentUser != null && currentUser.BranchId.HasValue)
                        {
                            var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                                "SELECT BranchName FROM Branches WHERE Id = @Id", 
                                new { Id = currentUser.BranchId.Value });
                                
                            if (!string.IsNullOrEmpty(branchName))
                                results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase));
                            else
                                results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                        }
                        else
                            results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                    }
                    
                    // Filter who is eligible for certificate based on the new rules:
                    // Pharmacists: aggregate score >= 150
                    // Assistants/Others: aggregate score >= 75
                    var passedResults = results.Where(r => {
                        if (r.Status != "Completed") return false;
                        
                        if (!string.IsNullOrEmpty(r.CertificateCode) || r.Score > 0) return true;
                        
                        bool isPharmacist = r.RoleName != null && (r.RoleName.ToLower().Contains("pharmacist") || r.RoleName.Contains("صيدل"));
                        if (isPharmacist)
                        {
                            return r.FinalScore >= 150;
                        }
                        else
                        {
                            return r.FinalScore >= 75;
                        }
                    }).ToList();
                    
                    ViewBag.ExamTitle = exam.Title;
                    return View(passedResults);
                }
            }

            return View(Enumerable.Empty<Exam.DTOs.ExamResultRowDto>());
        }

        [HttpGet]
        public async Task<IActionResult> WeeklyResults(int? examId, int? typeId = null, int? month = null, int? year = null)
        {
            return await Students(examId, typeId, month, year, "weekly");
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Items()
        {
            using var conn = new SqlConnection(_connectionString);
            var totalCount = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(1) FROM dbo.Items WITH (NOLOCK)
            ", commandTimeout: 60);
            ViewBag.TotalCount = totalCount;
            return View(new List<LocalItemDto>());
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ResetAdminPasswordTemp()
        {
            using var conn = new SqlConnection(_connectionString);
            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<object>();
            var newHash = hasher.HashPassword(new object(), "123456");
            await conn.ExecuteAsync("UPDATE AspNetUsers SET PasswordHash = @Hash WHERE Email = 'anasaladeep@gmail.com'", new { Hash = newHash });
            return Content("Admin password updated to 123456. Hash: " + newHash);
        }


        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetItemsPaged()
        {
            var draw = Request.Form["draw"].FirstOrDefault();
            var startStr = Request.Form["start"].FirstOrDefault();
            var lengthStr = Request.Form["length"].FirstOrDefault();
            var searchValue = Request.Form["search[value]"].FirstOrDefault();
            var orderColumnIndex = Request.Form["order[0][column]"].FirstOrDefault();
            var orderDir = Request.Form["order[0][dir]"].FirstOrDefault();

            int start = string.IsNullOrEmpty(startStr) ? 0 : int.Parse(startStr);
            int length = string.IsNullOrEmpty(lengthStr) ? 10 : int.Parse(lengthStr);

            using var conn = new SqlConnection(_connectionString);

            // Base SQL query
            string sqlBase = @"
                FROM dbo.Items WITH(NOLOCK)
                WHERE 1 = 1";

            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(searchValue))
            {
                sqlBase += " AND (No_ LIKE @Search OR Description LIKE @Search OR [Description 2] LIKE @Search OR [Item Definition] LIKE @Search OR Color LIKE @Search)";
                parameters.Add("Search", $"%{searchValue}%");
            }

            // Get total count (without filters)
            int totalRecords = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Items WITH(NOLOCK)", commandTimeout: 60);

            // Get filtered count
            int filteredRecords = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(1) {sqlBase}", parameters, commandTimeout: 60);

            // Determine sort column
            string sortColumn = "[Last Date Modified]";
            if (orderColumnIndex == "0") sortColumn = "No_";
            else if (orderColumnIndex == "1") sortColumn = "Description";
            else if (orderColumnIndex == "2") sortColumn = "[Description 2]";
            else if (orderColumnIndex == "3") sortColumn = "[Item Definition]";
            else if (orderColumnIndex == "4") sortColumn = "[Storage Instructions]";
            else if (orderColumnIndex == "5") sortColumn = "[Incentive value]";
            else if (orderColumnIndex == "6") sortColumn = "Color";
            else if (orderColumnIndex == "7") sortColumn = "[Date Created]";
            else if (orderColumnIndex == "8") sortColumn = "[Last Date Modified]";
            else if (orderColumnIndex == "9") sortColumn = "LastSyncedAt";

            string sortDirection = (orderDir == "asc") ? "ASC" : "DESC";

            // Paginated query
            string sqlQuery = $@"
                SELECT
                    No_ AS No_,
                    Description AS Description,
                    [Description 2] AS Description2,
                    [Storage Instructions] AS StorageInstructions,
                    [Incentive value] AS IncentiveValue,
                    Color AS Color,
                    [Item Definition] AS ItemDefinition,
                    [Date Created] AS DateCreated,
                    [Last Date Modified] AS LastDateModified,
                    LastSyncedAt AS LastSyncedAt
                {sqlBase}
                ORDER BY {sortColumn} {sortDirection}
                OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY";

            parameters.Add("Start", start);
            parameters.Add("Length", length);

            var items = await conn.QueryAsync<LocalItemDto>(sqlQuery, parameters, commandTimeout: 60);

            var dataList = new List<object>();
            foreach (var item in items)
            {
                dataList.Add(new {
                    no_ = item.No_ ?? "",
                    description = item.Description ?? "",
                    description2 = item.Description2 ?? "",
                    itemDefinition = item.ItemDefinition ?? "",
                    storageInstructions = item.StorageInstructions ?? "",
                    incentiveValue = item.IncentiveValue ?? "",
                    color = item.Color ?? "",
                    dateCreated = item.DateCreated?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                    lastDateModified = item.LastDateModified?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                    lastSyncedAt = item.LastSyncedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-"
                });
            }

            return Json(new {
                draw = draw,
                recordsTotal = totalRecords,
                recordsFiltered = filteredRecords,
                data = dataList
            });
        }


        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SyncItems([FromBody] SyncRequest request)
        {
            if (request == null || request.Password != "123456")
            {
                return Json(new { success = false, message = "Incorrect password." });
            }

            try
            {
                var liveConnectionString = "Server=172.16.0.50;Database=Eltarshouby-Live;User Id=supercommerce;Password=sc@123456;TrustServerCertificate=True;Connection Timeout=60";
                
                using var liveConn = new SqlConnection(liveConnectionString);
                using var localConn = new SqlConnection(_connectionString);

                // 1. Get last sync date from local Items database
                var lastSyncDate = await localConn.QueryFirstOrDefaultAsync<DateTime?>(
                    "SELECT MAX([Last Date Modified]) FROM dbo.Items"
                );

                // 2. Query data from live database using SqlDataReader (stream directly)
                string selectQuery = @"
                    SELECT 
                        No_ AS No_, 
                        Description AS Description, 
                        [Description 2] AS [Description 2], 
                        [Storage Instructions] AS [Storage Instructions], 
                        [Incentive value] AS [Incentive value], 
                        Color AS Color, 
                        [Item Definition] AS [Item Definition], 
                        [Date Created] AS [Date Created], 
                        [Last Date Modified] AS [Last Date Modified]
                    FROM [Tarshobi-Live$Item] WITH (NOLOCK)
                ";

                if (lastSyncDate.HasValue)
                {
                    selectQuery += " WHERE [Last Date Modified] > @LastSyncDate OR [Date Created] > @LastSyncDate";
                }

                await liveConn.OpenAsync();
                using var liveCmd = new SqlCommand(selectQuery, liveConn);
                liveCmd.CommandTimeout = 300;
                if (lastSyncDate.HasValue)
                {
                    liveCmd.Parameters.AddWithValue("@LastSyncDate", lastSyncDate.Value);
                }

                using var reader = await liveCmd.ExecuteReaderAsync();

                // 3. Write to staging temp table
                await localConn.OpenAsync();
                
                using (var createTempCmd = new SqlCommand(@"
                    CREATE TABLE #Staging_Items (
                        No_ NVARCHAR(100) NOT NULL PRIMARY KEY,
                        Description NVARCHAR(1000) NULL,
                        [Description 2] NVARCHAR(2000) NULL,
                        [Storage Instructions] NVARCHAR(MAX) NULL,
                        [Incentive value] NVARCHAR(2000) NULL,
                        Color NVARCHAR(1000) NULL,
                        [Item Definition] NVARCHAR(MAX) NULL,
                        [Date Created] DATETIME NULL,
                        [Last Date Modified] DATETIME NULL
                    )
                ", localConn))
                {
                    await createTempCmd.ExecuteNonQueryAsync();
                }

                int totalSynced = 0;
                using (var bulkCopy = new SqlBulkCopy(localConn))
                {
                    bulkCopy.DestinationTableName = "#Staging_Items";
                    bulkCopy.BulkCopyTimeout = 300;
                    bulkCopy.BatchSize = 10000;
                    
                    bulkCopy.ColumnMappings.Add("No_", "No_");
                    bulkCopy.ColumnMappings.Add("Description", "Description");
                    bulkCopy.ColumnMappings.Add("Description 2", "Description 2");
                    bulkCopy.ColumnMappings.Add("Storage Instructions", "Storage Instructions");
                    bulkCopy.ColumnMappings.Add("Incentive value", "Incentive value");
                    bulkCopy.ColumnMappings.Add("Color", "Color");
                    bulkCopy.ColumnMappings.Add("Item Definition", "Item Definition");
                    bulkCopy.ColumnMappings.Add("Date Created", "Date Created");
                    bulkCopy.ColumnMappings.Add("Last Date Modified", "Last Date Modified");

                    await bulkCopy.WriteToServerAsync(reader);
                }

                using (var countCmd = new SqlCommand("SELECT COUNT(*) FROM #Staging_Items", localConn))
                {
                    totalSynced = (int)await countCmd.ExecuteScalarAsync();
                }

                if (totalSynced == 0)
                {
                    return Json(new { success = true, message = "Already up to date. No new changes found.", count = 0 });
                }

                // 4. Merge staging table into destination table
                int inserted = 0;
                int updated = 0;

                string mergeSql = @"
                    SELECT 
                        SUM(CASE WHEN target.No_ IS NULL THEN 1 ELSE 0 END) as InsertedCount,
                        SUM(CASE WHEN target.No_ IS NOT NULL THEN 1 ELSE 0 END) as UpdatedCount
                    FROM #Staging_Items source
                    LEFT JOIN dbo.Items target ON source.No_ = target.No_;

                    MERGE dbo.Items AS target
                    USING #Staging_Items AS source
                    ON (target.No_ = source.No_)
                    WHEN MATCHED THEN
                        UPDATE SET 
                            target.Description = source.Description,
                            target.[Description 2] = source.[Description 2],
                            target.[Storage Instructions] = source.[Storage Instructions],
                            target.[Incentive value] = source.[Incentive value],
                            target.Color = source.Color,
                            target.[Item Definition] = source.[Item Definition],
                            target.[Date Created] = source.[Date Created],
                            target.[Last Date Modified] = source.[Last Date Modified],
                            target.LastSyncedAt = GETDATE()
                    WHEN NOT MATCHED THEN
                        INSERT (No_, Description, [Description 2], [Storage Instructions], [Incentive value], Color, [Item Definition], [Date Created], [Last Date Modified], LastSyncedAt)
                        VALUES (source.No_, source.Description, source.[Description 2], source.[Storage Instructions], source.[Incentive value], source.Color, source.[Item Definition], source.[Date Created], source.[Last Date Modified], GETDATE());
                ";

                using (var mergeCmd = new SqlCommand(mergeSql, localConn))
                {
                    mergeCmd.CommandTimeout = 300;
                    using (var statsReader = await mergeCmd.ExecuteReaderAsync())
                    {
                        if (statsReader.Read())
                        {
                            inserted = statsReader.IsDBNull(0) ? 0 : statsReader.GetInt32(0);
                            updated = statsReader.IsDBNull(1) ? 0 : statsReader.GetInt32(1);
                        }
                    }
                }

                return Json(new { 
                    success = true, 
                    message = $"Sync completed successfully! {inserted} items inserted, {updated} items updated.",
                    inserted,
                    updated
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error during sync: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetFilteredExams(int? typeId, int? month, int? year, string mode = "weekly")
        {
            if (mode == "cert")
            {
                var waves = (await _examService.GetAllWavesAsync()).ToList();

                if (year.HasValue && year.Value > 0)
                {
                    waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Year == year.Value).ToList();
                }
                if (month.HasValue && month.Value > 0)
                {
                    waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Month == month.Value).ToList();
                }

                var results = waves.Select(w => new {
                    id = w.Id,
                    title = w.StartDate.HasValue 
                        ? $"{w.WaveName.ToUpper()} ({w.StartDate.Value.ToString("MMM yyyy")})"
                        : w.WaveName.ToUpper(),
                    typeName = "TRAINING BATCHES"
                });
                return Json(results);
            }

            var exams = await _examService.GetActiveExamsForDropdownAsync(typeId, month, year);
            if (mode == "wavey")
            {
                exams = exams.Where(e => (e.TypeName ?? "").ToLower().Contains("wave") || (e.Title ?? "").ToLower().Contains("wave")).ToList();
            }
            else if (mode == "weekly")
            {
                if (!User.IsInRole("Branch Manager") && (!typeId.HasValue || typeId.Value == 0))
                {
                    exams = exams.Where(e => !((e.TypeName ?? "").ToLower().Contains("wave") || (e.Title ?? "").ToLower().Contains("wave"))).ToList();
                }
            }
            var examResults = exams.Select(e => new {
                id = e.Id,
                title = !string.IsNullOrEmpty(e.WaveName)
                    ? $"{e.Title.ToUpper()} - {e.WaveName.ToUpper()} ({e.StartTime:MMM yyyy})"
                    : $"{e.Title.ToUpper()} ({e.StartTime:MMM yyyy})",
                typeName = e.TypeName
            });
            return Json(examResults);
        }

        [HttpPost]
        public async Task<IActionResult> SendFailEmails(int examId)
        {
            var results = await _examService.GetExamResultsByExamIdAsync(examId);
            if (User.IsInRole("Branch Manager"))
            {
                var currentUser = await _userManager.GetUserAsync(User);
                if (currentUser != null && currentUser.BranchId.HasValue)
                {
                    using var conn = new SqlConnection(_connectionString);
                    var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                        "SELECT BranchName FROM Branches WHERE Id = @Id", 
                        new { Id = currentUser.BranchId.Value });
                        
                    if (!string.IsNullOrEmpty(branchName))
                    {
                        results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                    }
                }
                else
                {
                    results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                }
            }
            var examInfo = await _examService.GetExamByIdAsync(examId);
            if (examInfo == null) return Json(new { success = false, message = "Exam not found" });

            var failedStudents = results.Where(r => r.Status.StartsWith("Completed") && r.IsPassed == false && !r.EmailSent).ToList();
            if(!failedStudents.Any()) return Json(new { success = false, message = "Ù„Ù… ÙŠØªÙ… Ø§Ù„Ø¹Ø«ÙˆØ± Ø¹Ù„Ù‰ Ø·Ù„Ø§Ø¨ Ø¬Ø¯Ø¯ ÙŠØ³ØªØ­Ù‚ÙˆÙ† Ø¥Ø´Ø¹Ø§Ø± Ø§Ù„Ø±Ø³ÙˆØ¨." });

            // Run in background to prevent UI timeout
            _ = Task.Run(async () => {
                foreach (var student in failedStudents)
                {
                    try {
                        string subject = $"Ø¥Ø´Ø¹Ø§Ø± Ø¨Ù†ØªÙŠØ¬Ø© Ø§Ù„Ø§Ø®ØªØ¨Ø§Ø±: {examInfo.Title}";
                        string htmlBody = $@"
                            <div dir='rtl' style='font-family: Arial, sans-serif; padding: 20px; color: #333;'>
                                <p>Ø¹Ø²ÙŠØ²ÙŠ <strong>{student.StudentName}</strong>ØŒ</p>
                                <p>Ù†Ø£Ø³Ù Ù„Ø¥Ø®Ø¨Ø§Ø±Ùƒ Ø¨Ø£Ù†Ùƒ Ù„Ù… ØªØ¬ØªØ² Ø§Ø®ØªØ¨Ø§Ø± <strong>{examInfo.Title}</strong>.</p>
                                <p>Ù†ØªÙŠØ¬ØªÙƒ ÙƒØ§Ù†Øª: <strong>{student.Score:0.00}%</strong></p>
                                <p>Ù†ØªÙ…Ù†Ù‰ Ù„Ùƒ Ø§Ù„ØªÙˆÙÙŠÙ‚ ÙÙŠ Ø§Ù„Ù…Ø­Ø§ÙˆÙ„Ø§Øª Ø§Ù„Ù‚Ø§Ø¯Ù…Ø©ØŒ ÙˆÙ„Ø§ ØªØªØ±Ø¯Ø¯ ÙÙŠ Ù…Ø±Ø§Ø¬Ø¹Ø© Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ø¹Ù„Ù…ÙŠØ© ÙˆØ¥Ø¹Ø§Ø¯Ø© Ø§Ù„Ù…Ø­Ø§ÙˆÙ„Ø© Ø¹Ù†Ø¯Ù…Ø§ ØªÙƒÙˆÙ† Ø¬Ø§Ù‡Ø²Ø§Ù‹.</p>
                                <br>
                                <p>Ù…Ø¹ Ø®Ø§Ù„Øµ Ø§Ù„ØªØ­ÙŠØ§ØªØŒ</p>
                                <p>Walid Tarshoubi Training Academy</p>
                            </div>";

                        await _emailSender.SendEmailAsync(student.StudentEmail, subject, htmlBody);
                        
                        using var conn = new SqlConnection(_connectionString);
                        await conn.ExecuteAsync("UPDATE UserExamAttempts SET EmailSent = 1 WHERE Id = @Id", new { Id = student.AttemptId });
                    } catch { /* Silent log or handle if needed */ }
                }
            });

            return Json(new { success = true, message = $"ØªÙ… Ø¨Ø¯Ø¡ Ø¥Ø±Ø³Ø§Ù„ {failedStudents.Count} Ø¥Ø´Ø¹Ø§Ø± Ø±Ø³ÙˆØ¨ ÙÙŠ Ø§Ù„Ø®Ù„ÙÙŠØ© Ø¨Ù†Ø¬Ø§Ø­." });
        }

        [HttpPost]
        public async Task<IActionResult> SendCertificates(int examId, [FromBody] List<string> selectedIds = null)
        {
            try
            {
                var results = await _examService.GetExamResultsByExamIdAsync(examId);
                if (User.IsInRole("Branch Manager"))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    if (currentUser != null && currentUser.BranchId.HasValue)
                    {
                        using var conn = new SqlConnection(_connectionString);
                        var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                            "SELECT BranchName FROM Branches WHERE Id = @Id", 
                            new { Id = currentUser.BranchId.Value });
                            
                        if (!string.IsNullOrEmpty(branchName))
                        {
                            results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                        }
                    }
                    else
                    {
                        results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                    }
                }
                var examInfo = await _examService.GetExamByIdAsync(examId);
                if (examInfo == null) return Json(new { success = false, message = "Exam not found" });

                var candidates = results.Where(r => {
                    if (r.Status != "Completed") return false;
                    
                    bool isPharmacist = r.RoleName != null && (r.RoleName.ToLower().Contains("pharmacist") || r.RoleName.Contains("صيدل"));
                    if (isPharmacist)
                    {
                        return r.FinalScore >= 150;
                    }
                    else
                    {
                        return r.FinalScore >= 75;
                    }
                });
                if (selectedIds != null && selectedIds.Any())
                {
                    candidates = candidates.Where(c => selectedIds.Contains(c.Id));
                }

                var passedStudents = candidates.ToList();
                if (!passedStudents.Any()) return Json(new { success = false, message = "لم يتم اختيار طلاب أكملوا الامتحان لإرسال الشهادات." });

                int count = 0;

                string templatePath = "";
                if (!string.IsNullOrEmpty(examInfo.CertificateTemplatePath))
                {
                    templatePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", examInfo.CertificateTemplatePath.TrimStart('/'));
                }

                if (string.IsNullOrEmpty(templatePath) || !System.IO.File.Exists(templatePath))
                {
                    templatePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "certificate_template.png");
                }

                if (!System.IO.File.Exists(templatePath))
                    return Json(new { success = false, message = "Ù„Ù… ÙŠØªÙ… Ø§Ù„Ø¹Ø«ÙˆØ± Ø¹Ù„Ù‰ ØªØµÙ…ÙŠÙ… Ø§Ù„Ø´Ù‡Ø§Ø¯Ø© Ø§Ù„Ø§ÙØªØ±Ø§Ø¶ÙŠ Ø£Ùˆ Ø§Ù„Ù…Ø®ØµØµ." });

                string courseType = "PB";
                if (!string.IsNullOrEmpty(examInfo.ExamType))
                {
                    var words = examInfo.ExamType.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 0) courseType = string.Join("", words.Select(w => w[0])).ToUpper();
                }

                foreach (var student in passedStudents)
                {
                    string code = student.CertificateCode;
                    if (string.IsNullOrEmpty(code))
                    {
                        string waveNum = "001";
                        var digits = new string((student.WaveName ?? "").Where(char.IsDigit).ToArray());
                        if (!string.IsNullOrEmpty(digits)) waveNum = digits.PadLeft(3, '0');
                        
                        string yearStr = (student.ActualStartTime ?? examInfo.StartTime).Year.ToString();
                        string userCodeStr = student.UserCode ?? "0000";
                        
                        string roleAbbr = "PH";
                        if (student.RoleName != null && (student.RoleName.ToLower().Contains("assistant") || student.RoleName.Contains("مساعد")))
                        {
                            roleAbbr = "AS";
                        }
                        
                        code = $"WTTA-{yearStr}-{waveNum}-{courseType}-{roleAbbr}-{userCodeStr}";

                        using var conn = new SqlConnection(_connectionString);
                        
                        if (student.AttemptId.HasValue)
                        {
                            var saveSql = "UPDATE UserExamAttempts SET CertificateCode = @Code, EmailSent = 1 WHERE Id = @Id";
                            await conn.ExecuteAsync(saveSql, new { Id = student.AttemptId.Value, Code = code });
                        }
                        
                        if (!string.IsNullOrEmpty(student.Id))
                        {
                            var saveUserSql = "UPDATE AspNetUsers SET CertificateCode = @Code WHERE Id = @Id";
                            await conn.ExecuteAsync(saveUserSql, new { Id = student.Id, Code = code });

                            if (examInfo.WaveId.HasValue && examInfo.WaveId.Value > 0)
                            {
                                var certExists = await conn.QueryFirstOrDefaultAsync<int?>(
                                    "SELECT Id FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                                    new { UserId = student.Id, WaveId = examInfo.WaveId.Value });

                                if (certExists != null)
                                {
                                    await conn.ExecuteAsync(@"
                                        UPDATE dbo.UserWaveCertificates 
                                        SET CertificateCode = @CertCode
                                        WHERE UserId = @UserId AND WaveId = @WaveId",
                                        new { CertCode = code, UserId = student.Id, WaveId = examInfo.WaveId.Value });
                                }
                                else
                                {
                                    await conn.ExecuteAsync(@"
                                        INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, Score, CreatedAt)
                                        VALUES (@UserId, @WaveId, @CertCode, NULL, @CreatedAt)",
                                        new { UserId = student.Id, WaveId = examInfo.WaveId.Value, CertCode = code, CreatedAt = DateTime.Now });
                                }
                            }
                        }
                    }

                    using var ms = new MemoryStream();
                    using (var document = new PdfSharpCore.Pdf.PdfDocument())
                    {
                        var page = document.AddPage();
                        page.Orientation = PdfSharpCore.PageOrientation.Landscape;
                        using (var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page))
                        {
                            using (var image = PdfSharpCore.Drawing.XImage.FromFile(templatePath))
                            {
                                page.Width = image.PointWidth;
                                page.Height = image.PointHeight;
                                gfx.DrawImage(image, 0, 0, page.Width, page.Height);
                            }

                            // Use the elegant "Great Vibes" font for the student name
                            var font = new PdfSharpCore.Drawing.XFont("Great Vibes", 72, PdfSharpCore.Drawing.XFontStyle.Regular);
                            gfx.DrawString(student.StudentName, font, PdfSharpCore.Drawing.XBrushes.Navy,
                                new PdfSharpCore.Drawing.XRect(0, page.Height * 0.40, page.Width, 120),
                                PdfSharpCore.Drawing.XStringFormats.Center);

                            // Prominent display of the Certificate ID for verification
                            var codeFont = new PdfSharpCore.Drawing.XFont("Arial", 12, PdfSharpCore.Drawing.XFontStyle.Bold);
                            gfx.DrawString($"Verification ID: {code}", codeFont, PdfSharpCore.Drawing.XBrushes.DimGray, 
                                new PdfSharpCore.Drawing.XRect(60, page.Height - 100, page.Width - 120, 40), 
                                PdfSharpCore.Drawing.XStringFormats.BottomLeft);
                        }
                        document.Save(ms, false);
                    }

                    byte[] pdfBytes = ms.ToArray();
                    string subject = $"ØªÙ‡Ø§Ù†ÙŠÙ†Ø§! Ø´Ù‡Ø§Ø¯Ø© Ø¥ØªÙ…Ø§Ù… Ø§Ø®ØªØ¨Ø§Ø± {examInfo.Title}";
                    string htmlBody = $@"
                    <div dir='rtl' style='font-family: Arial, sans-serif; padding: 20px; color: #333;'>
                        <p>Ø¹Ø²ÙŠØ²ÙŠ <strong>{student.StudentName}</strong>ØŒ</p>
                        <p>ØªÙ‡Ø§Ù†ÙŠÙ†Ø§ Ø§Ù„Ø­Ø§Ø±Ø©! Ù„Ù‚Ø¯ Ø§Ø¬ØªØ²Øª Ø§Ø®ØªØ¨Ø§Ø± <strong>{examInfo.Title}</strong> Ø¨Ù†Ø¬Ø§Ø­ ÙˆØªÙÙˆÙ‚.</p>
                        <p>Ù…Ø±ÙÙ‚ Ù…Ø¹ Ù‡Ø°Ù‡ Ø§Ù„Ø±Ø³Ø§Ù„Ø© Ø´Ù‡Ø§Ø¯Ø© Ø§Ù„ØªØ®Ø±Ø¬ Ø§Ù„Ø®Ø§ØµØ© Ø¨Ùƒ ÙƒÙ…Ù„Ù PDF.</p>
                        <p>ÙƒÙˆØ¯ Ø§Ù„Ø´Ù‡Ø§Ø¯Ø© Ø§Ù„Ù…Ø±Ø¬Ø¹ÙŠ Ø§Ù„Ø®Ø§Øµ Ø¨Ùƒ Ù‡Ùˆ: <strong>{code}</strong></p>
                        <br>
                        <p>Ù…Ø¹ Ø®Ø§Ù„Øµ ØªÙ…Ù†ÙŠØ§ØªÙ†Ø§ Ø¨Ø¯ÙˆØ§Ù… Ø§Ù„Ù†Ø¬Ø§Ø­ ÙˆØ§Ù„ØªÙˆÙÙŠÙ‚ØŒ</p>
                        <p>Walid Tarshoubi Training Academy</p>
                    </div>
                ";

                    await _emailSender.SendEmailWithAttachmentAsync(student.StudentEmail, subject, htmlBody, pdfBytes, $"Certificate_{student.StudentName.Replace(" ", "_")}.pdf");
                    
                    if (student.AttemptId.HasValue)
                    {
                        using var conn = new SqlConnection(_connectionString);
                        await conn.ExecuteAsync("UPDATE UserExamAttempts SET EmailSent = 1 WHERE Id = @Id", new { Id = student.AttemptId.Value });
                    }

                    count++;
                }
                return Json(new { success = true, message = $"ØªÙ… Ø¥Ø±Ø³Ø§Ù„ {count} Ø´Ù‡Ø§Ø¯Ø© Ø¨Ù†Ø¬Ø§Ø­!" });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = "Ø®Ø·Ø£ ÙÙŠ Ø§Ù„Ø³ÙŠØ±ÙØ±: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadCertificateTemplate(int examId, IFormFile templateFile)
        {
            if (templateFile == null || templateFile.Length == 0) return BadRequest("No file uploaded");

            var fileName = $"cert_template_{examId}_{Guid.NewGuid().ToString("N").Substring(0, 6)}{Path.GetExtension(templateFile.FileName)}";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "certs", fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await templateFile.CopyToAsync(stream);
            }

            var relativePath = $"/uploads/certs/{fileName}";
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync("UPDATE Exams SET CertificateTemplatePath = @Path WHERE Id = @Id", new { Path = relativePath, Id = examId });
            }

            return Json(new { success = true, path = relativePath });
        }

        [HttpGet]
        public async Task<IActionResult> GetResultReport(int attemptId)
        {
            var details = await _examService.GetResultReportAsync(attemptId);
            return PartialView("_ResultReport", details);
        }

        [HttpGet]
        public async Task<IActionResult> GetStudentExamReview(int examId, string studentId)
        {
            if (examId <= 0 || string.IsNullOrWhiteSpace(studentId))
                return BadRequest();

            var rows = await _examService.GetStudentExamReviewAsync(examId, studentId);
            //return View("_StudentExamReview");
            return PartialView("GetStudentExamReview", rows);
        }

        [HttpGet]
        public async Task<IActionResult> GetEligibleUsersForExam(int examId)
        {
            try
            {
                var exam = await _examService.GetExamByIdAsync(examId);
                if (exam == null) return NotFound("Exam not found");

                using var conn = new SqlConnection(_connectionString);
                
                // Optimized query: Uses standard Joins instead of heavy Outer Applies for better performance
                string sql = @"
                    SELECT 
                        U.Id, U.UserName, ISNULL(U.FullName, U.UserName) as FullName, U.Email, U.PhoneNumber as Phone, 
                        U.UserCode,
                        ISNULL(R.Name, 'User') as RoleName,
                        ISNULL(W.WaveName, 'GLOBAL') as WaveName,
                        ISNULL(B.BranchName, 'N/A') as BranchName,
                        ISNULL(A.Status, '') as LastExamStatus,
                        CASE WHEN EA.Id IS NOT NULL THEN 1 ELSE 0 END AS IsAlreadyAssigned,
                        ISNULL(ABS_CTE.AbsCount, 0) as AbsenceCount
                    FROM AspNetUsers U WITH(NOLOCK)
                    LEFT JOIN AspNetUserRoles UR ON U.Id = UR.UserId
                    LEFT JOIN AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
                    LEFT JOIN (
                        SELECT UW.UserId, TW.WaveName, TW.Id as WaveId,
                               ROW_NUMBER() OVER(PARTITION BY UW.UserId ORDER BY UW.Id DESC) as rn
                        FROM UserWaves UW
                        JOIN TrainingWaves TW ON UW.WaveId = TW.Id
                        WHERE UW.IsActive = 1
                    ) W ON U.Id = W.UserId AND W.rn = 1
                    LEFT JOIN Branches B WITH(NOLOCK) ON U.BranchId = B.Id
                    LEFT JOIN ExamAssignments EA WITH(NOLOCK) ON EA.StudentId = U.Id AND EA.ExamId = @ExamId
                    LEFT JOIN (
                        SELECT UserId, ExamId, Id, Status,
                               ROW_NUMBER() OVER(PARTITION BY UserId, ExamId ORDER BY StartTime DESC) as rn
                        FROM UserExamAttempts WITH(NOLOCK)
                    ) A ON A.UserId = U.Id AND A.ExamId = @ExamId AND A.rn = 1
                    LEFT JOIN (
                        SELECT UA.UserId, S.WaveId, COUNT(UA.Id) as AbsCount
                        FROM UserAttendance UA
                        JOIN AttendanceSessions S ON UA.SessionId = S.Id
                        WHERE UA.IsPresent = 0 AND S.WaveId IS NOT NULL
                        GROUP BY UA.UserId, S.WaveId
                    ) ABS_CTE ON ABS_CTE.UserId = U.Id AND ABS_CTE.WaveId = W.WaveId
                    WHERE U.IsActive = 1
                    AND (R.Name IS NULL OR LOWER(R.Name) NOT IN ('admin', 'superadmin'))
                    ";

                // If exam is wave-specific, restrict to that wave
                if (exam.WaveId > 0)
                {
                    sql += " AND EXISTS (SELECT 1 FROM dbo.UserWaves UW2 WHERE UW2.UserId = U.Id AND UW2.WaveId = @WaveId AND UW2.IsActive = 1)";
                }

                var users = await conn.QueryAsync<UserDto>(sql, new { ExamId = examId, WaveId = exam.WaveId });

                return Json(users);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RemoveStudentFromExam(int examId, string studentId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM ExamAssignments WHERE ExamId = @ExamId AND StudentId = @StudentId", new { ExamId = examId, StudentId = studentId });
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveAllStudentsFromExam(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                // Delete all student exam attempt details for this exam
                var attemptIds = await conn.QueryAsync<int>(
                    "SELECT Id FROM UserExamAttempts WHERE ExamId = @ExamId",
                    new { ExamId = examId }, transaction);

                if (attemptIds.Any())
                {
                    await conn.ExecuteAsync(
                        "DELETE FROM StudentQuestionDetails WHERE UserExamAttemptId IN @Ids",
                        new { Ids = attemptIds }, transaction);

                    await conn.ExecuteAsync(
                        "DELETE FROM UserSeenQuestions WHERE AttemptId IN @Ids",
                        new { Ids = attemptIds }, transaction);

                    await conn.ExecuteAsync(
                        "DELETE FROM UserExamAttempts WHERE Id IN @Ids",
                        new { Ids = attemptIds }, transaction);
                }

                // Delete all assignments for this exam
                await conn.ExecuteAsync(
                    "DELETE FROM ExamAssignments WHERE ExamId = @ExamId",
                    new { ExamId = examId }, transaction);

                transaction.Commit();
                return Json(new { success = true, message = "All student assignments and attempts wiped successfully." });
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                return StatusCode(500, new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> WipeStudentData(int examId, string studentId)
        {
            try
            {
                await _examService.WipeStudentExamDataAsync(examId, studentId);
                return Json(new { success = true, message = "Student data wiped successfully. You can now re-assign the exam." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AssignExamToStudents(int examId, [FromBody] List<string> studentIds)
        {
            if (studentIds == null || !studentIds.Any())
            {
                return BadRequest("No students selected.");
            }

            try
            {
                var siteUrl = "http://41.33.149.186:5208";
                var assignedCount = await _examService.AssignExamToStudentsAsync(examId, studentIds, siteUrl);
                return Ok(new { AssignedCount = assignedCount });
            }
            catch (System.Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ReassignExamToStudents(int examId, [FromBody] ReassignRequest request)
        {
            if (request.StudentIds == null || !request.StudentIds.Any()) return BadRequest("No students selected.");
            try
            {
                if (User.IsInRole("Branch Manager"))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    if (currentUser != null && currentUser.BranchId.HasValue)
                    {
                        using var conn = new SqlConnection(_connectionString);
                        var validStudentIds = (await conn.QueryAsync<string>(
                            "SELECT Id FROM AspNetUsers WHERE Id IN @Ids AND BranchId = @BranchId",
                            new { Ids = request.StudentIds, BranchId = currentUser.BranchId.Value })
                        ).ToList();
                        
                        request.StudentIds = validStudentIds;
                        
                        if (!request.StudentIds.Any())
                        {
                            return Json(new { success = false, message = "No valid students in your branch selected." });
                        }
                    }
                    else
                    {
                        return Forbid();
                    }
                }
                var exam = await _examService.GetExamByIdAsync(examId);
                var siteLink = "http://41.33.149.186:5208";

                var distinctStudentIds = request.StudentIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                if (!distinctStudentIds.Any())
                {
                    return Ok(new { success = true, count = 0 });
                }

                using (var conn = new SqlConnection(_connectionString))
                {
                    await conn.OpenAsync();

                    using (var transaction = conn.BeginTransaction())
                    {
                        try
                        {
                            foreach (var sid in distinctStudentIds)
                            {
                                var existingId = await conn.ExecuteScalarAsync<int?>(@"
                                    SELECT TOP 1 EA.Id FROM ExamAssignments EA
                                    WHERE EA.ExamId = @ExamId AND EA.StudentId = @StudentId
                                    AND NOT EXISTS (
                                        SELECT 1 FROM UserExamAttempts UA 
                                        WHERE UA.UserId = EA.StudentId AND UA.ExamId = EA.ExamId
                                        AND UA.AttemptDate >= ISNULL(EA.ScheduledStartTime, '2000-01-01')
                                        AND UA.[Status] IN ('Completed', 'Fail_Cheating', 'Fail_ProhibitedActions', 'Fail_Timeout', 'Fail_Abandoned')
                                    )
                                    ORDER BY EA.Id DESC", new { ExamId = examId, StudentId = sid }, transaction);

                                if (existingId.HasValue)
                                {
                                    await conn.ExecuteAsync(@"
                                        UPDATE ExamAssignments 
                                        SET ScheduledStartTime = @StartTime, ScheduledEndTime = @EndTime, IsEmailSent = 0
                                        WHERE Id = @Id", new { Id = existingId.Value, StartTime = request.StartTime, EndTime = request.EndTime }, transaction);
                                }
                                else
                                {
                                    var sql = @"
                                        INSERT INTO ExamAssignments (ExamId, StudentId, ScheduledStartTime, ScheduledEndTime, IsEmailSent)
                                        VALUES (@ExamId, @StudentId, @StartTime, @EndTime, 0)";
                                    await conn.ExecuteAsync(sql, new { ExamId = examId, StudentId = sid, StartTime = request.StartTime, EndTime = request.EndTime }, transaction);
                                }
                            }
                            transaction.Commit();
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }
                    }

                    // Send Email Notification in background using materialized user info
                    var users = (await conn.QueryAsync<UserDto>(
                        "SELECT Id, UserName, Email FROM AspNetUsers WHERE Id IN @Ids", new { Ids = distinctStudentIds }))
                        .ToList();

                    bool isWeeklyExam = false;
                    if (exam != null)
                    {
                        string examType = exam.ExamType ?? "";
                        isWeeklyExam = examType.ToLower().Contains("weekly") || !examType.ToLower().Contains("wave") || !exam.WaveId.HasValue;
                    }

                    if (!isWeeklyExam)
                    {
                        _ = Task.Run(async () =>
                        {
                            foreach (var user in users)
                            {
                                if (string.IsNullOrEmpty(user.Email)) continue;

                                var subject = $"🚨 Re-Assignment: {exam.Title}";
                                var body = $@"
                                    <div style='font-family: sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #eee; border-radius: 10px;'>
                                        <h2 style='color: #4f46e5;'>Exam Re-Assignment</h2>
                                        <p>Hello <b>{user.UserName}</b>,</p>
                                        <p>You have been reassigned the exam: <b style='color: #ef4444;'>{exam.Title}</b>.</p>
                                        <div style='background: #f8fafc; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                                            <p style='margin: 0;'><b>New Window:</b></p>
                                            <p style='margin: 5px 0;'>Starts: {request.StartTime:yyyy-MM-dd HH:mm}</p>
                                            <p style='margin: 5px 0;'>Ends: {request.EndTime:yyyy-MM-dd HH:mm}</p>
                                        </div>
                                        <p>Please login to the portal to complete your assessment.</p>
                                        <a href='{siteLink}' style='display: inline-block; background: #4f46e5; color: white; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold;'>Login to Portal</a>
                                        <p style='margin-top: 30px; font-size: 12px; color: #64748b;'>Eltarshoubi Academy - Online Examination System</p>
                                    </div>";
                                
                                await _emailSender.SendEmailAsync(user.Email, subject, body);
                            }
                        });
                    }
                }
                return Ok(new { success = true, count = distinctStudentIds.Count });
            }
            catch (System.Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        public class ReassignRequest
        {
            public List<string> StudentIds { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
        }



        [HttpPost]
        public async Task<IActionResult> GetWeeklyResultsPaged()
        {
            var draw = Request.Form["draw"].FirstOrDefault();
            var startStr = Request.Form["start"].FirstOrDefault();
            var lengthStr = Request.Form["length"].FirstOrDefault();
            var searchValue = Request.Form["search[value]"].FirstOrDefault();
            var orderColumnIndex = Request.Form["order[0][column]"].FirstOrDefault();
            var orderDir = Request.Form["order[0][dir]"].FirstOrDefault();
            var examIdStr = Request.Form["examId"].FirstOrDefault();

            int start = string.IsNullOrEmpty(startStr) ? 0 : int.Parse(startStr);
            int length = string.IsNullOrEmpty(lengthStr) ? 10 : int.Parse(lengthStr);
            int examId = string.IsNullOrEmpty(examIdStr) ? 0 : int.Parse(examIdStr);

            if (examId <= 0)
            {
                return Json(new { draw = draw, recordsTotal = 0, recordsFiltered = 0, data = new List<object>() });
            }

            if (User.IsInRole("Branch Manager"))
            {
                using var conn = new SqlConnection(_connectionString);
                var examEndTime = await conn.QueryFirstOrDefaultAsync<DateTime?>(
                    "SELECT EndTime FROM Exams WHERE Id = @Id", 
                    new { Id = examId });

                if (examEndTime.HasValue && DateTime.Now < examEndTime.Value)
                {
                    return Json(new { 
                        draw = draw, 
                        recordsTotal = 0, 
                        recordsFiltered = 0, 
                        data = new List<object>(),
                        isComingSoon = true
                    });
                }
            }

            var results = await _examService.GetExamResultsByExamIdAsync(examId);

            // Apply Branch Manager filters if needed
            if (User.IsInRole("Branch Manager"))
            {
                var currentUser = await _userManager.GetUserAsync(User);
                if (currentUser != null && currentUser.BranchId.HasValue)
                {
                    using var conn = new SqlConnection(_connectionString);
                    var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                        "SELECT BranchName FROM Branches WHERE Id = @Id", 
                        new { Id = currentUser.BranchId.Value });
                        
                    if (!string.IsNullOrEmpty(branchName))
                    {
                        results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                    }
                }
                else
                {
                    results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                }
            }

            int totalRecords = results.Count();

            // Apply Search filter
            if (!string.IsNullOrEmpty(searchValue))
            {
                var searchLower = searchValue.ToLower();
                results = results.Where(r => 
                    (r.StudentName ?? "").ToLower().Contains(searchLower) ||
                    (r.StudentEmail ?? "").ToLower().Contains(searchLower) ||
                    (r.UserCode ?? "").ToLower().Contains(searchLower) ||
                    (r.BranchName ?? "").ToLower().Contains(searchLower) ||
                    (r.Status ?? "").ToLower().Contains(searchLower)
                );
            }

            int filteredRecords = results.Count();

            // Apply Sorting
            if (!string.IsNullOrEmpty(orderColumnIndex))
            {
                bool isAsc = orderDir == "asc";
                switch (orderColumnIndex)
                {
                    case "0": // Personnel Node (StudentName)
                        results = isAsc ? results.OrderBy(r => r.StudentName) : results.OrderByDescending(r => r.StudentName);
                        break;
                    case "1": // Email Address
                        results = isAsc ? results.OrderBy(r => r.StudentEmail) : results.OrderByDescending(r => r.StudentEmail);
                        break;
                    case "2": // Branch
                        results = isAsc ? results.OrderBy(r => r.BranchName) : results.OrderByDescending(r => r.BranchName);
                        break;
                    case "3": // Status
                        results = isAsc ? results.OrderBy(r => r.Status) : results.OrderByDescending(r => r.Status);
                        break;
                    case "4": // Start Time
                        results = isAsc ? results.OrderBy(r => r.ActualStartTime) : results.OrderByDescending(r => r.ActualStartTime);
                        break;
                    case "5": // Finish Time
                        results = isAsc ? results.OrderBy(r => r.ActualEndTime) : results.OrderByDescending(r => r.ActualEndTime);
                        break;
                    case "6": // Time (M) (Duration)
                        results = isAsc ? results.OrderBy(r => r.DurationInMinutes) : results.OrderByDescending(r => r.DurationInMinutes);
                        break;
                    case "7": // Score (Percentage)
                        results = isAsc ? results.OrderBy(r => r.Score) : results.OrderByDescending(r => r.Score);
                        break;
                    case "8": // User Code
                        results = isAsc ? results.OrderBy(r => r.UserCode) : results.OrderByDescending(r => r.UserCode);
                        break;
                }
            }

            // Pagination
            var pagedResults = results.Skip(start).Take(length).ToList();

            // Map results to DataTable-friendly DTO/Anonymous objects
            var data = pagedResults.Select(r => new
            {
                id = r.Id,
                studentName = r.StudentName,
                studentEmail = r.StudentEmail,
                branchName = r.BranchName ?? "--",
                status = r.Status,
                examType = r.ExamType,
                actualStartTimeDate = r.ActualStartTime?.ToString("dd/MM/yyyy") ?? "",
                actualStartTimeTime = r.ActualStartTime?.ToString("hh:mm tt") ?? "--",
                actualEndTimeDate = r.ActualEndTime?.ToString("dd/MM/yyyy") ?? "",
                actualEndTimeTime = r.ActualEndTime?.ToString("hh:mm tt") ?? "--",
                duration = r.DurationInMinutes > 0 ? $"{r.DurationInMinutes} min" : "--",
                scoreDisplay = $"{r.FinalScore.ToString("0")} / {r.TotalScoreAvailable.ToString("0")}",
                scorePercent = $"{r.Score.ToString("0.0")}% Achievement",
                userCode = r.UserCode ?? "--",
                isAdmin = User.IsInRole("Admin")
            });

            return Json(new
            {
                draw = draw,
                recordsTotal = totalRecords,
                recordsFiltered = filteredRecords,
                data = data
            });
        }

        public async Task<IActionResult> AllUsers()
        {
            var allRoles = await _examService.GetAllRolesAsync();
            // English-only Role Filter
            ViewBag.Roles = allRoles.Where(r => !System.Text.RegularExpressions.Regex.IsMatch(r.RoleName, @"\p{IsArabic}") && !r.RoleName.Contains("ØµÙŠØ¯Ù„ÙŠ") && !r.RoleName.Contains("Ù…Ø³Ø§Ø¹Ø¯")).ToList();
            ViewBag.Shifts = await _examService.GetAllShiftsAsync();
            ViewBag.Branches = await _examService.GetAllBranchesAsync();
            return View(Enumerable.Empty<Exam.DTOs.UserWithRoleDto>());
        }

        [HttpPost]
        public async Task<IActionResult> GetUsersPaged()
        {
            // Read DataTable parameters
            var draw = Request.Form["draw"].FirstOrDefault();
            var startStr = Request.Form["start"].FirstOrDefault();
            var lengthStr = Request.Form["length"].FirstOrDefault();
            var searchValue = Request.Form["search[value]"].FirstOrDefault();
            var orderColumnIndex = Request.Form["order[0][column]"].FirstOrDefault();
            var orderDir = Request.Form["order[0][dir]"].FirstOrDefault();

            // Custom filters
            var classification = Request.Form["classification"].FirstOrDefault();
            var branchName = Request.Form["branchName"].FirstOrDefault();

            int start = string.IsNullOrEmpty(startStr) ? 0 : int.Parse(startStr);
            int length = string.IsNullOrEmpty(lengthStr) ? 10 : int.Parse(lengthStr);

            using var conn = new SqlConnection(_connectionString);

            // Base SQL query
            string sqlBase = @"
FROM dbo.AspNetUsers U WITH(NOLOCK)
LEFT JOIN dbo.AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN dbo.AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
LEFT JOIN dbo.Shifts S WITH(NOLOCK) ON S.Id = U.ShiftId
LEFT JOIN dbo.Branches B WITH(NOLOCK) ON U.BranchId = B.Id
WHERE 1 = 1";

            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(searchValue))
            {
                sqlBase += " AND (U.UserName LIKE @Search OR U.Email LIKE @Search OR U.UserCode LIKE @Search)";
                parameters.Add("Search", $"%{searchValue}%");
            }

            if (!string.IsNullOrEmpty(classification))
            {
                sqlBase += " AND UPPER(R.Name) = @Classification";
                parameters.Add("Classification", classification.ToUpper());
            }

            if (!string.IsNullOrEmpty(branchName))
            {
                if (branchName.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase))
                {
                    sqlBase += " AND (B.BranchName IS NULL OR B.BranchName = '')";
                }
                else
                {
                    sqlBase += " AND UPPER(B.BranchName) = @BranchName";
                    parameters.Add("BranchName", branchName.ToUpper());
                }
            }

            // Get total count (without filters)
            int totalRecords = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.AspNetUsers U WITH(NOLOCK)");

            // Get filtered count
            int filteredRecords = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(1) {sqlBase}", parameters);

            // Determine sort column
            string sortColumn = "U.UserName";
            if (orderColumnIndex == "0") sortColumn = "U.UserName";
            else if (orderColumnIndex == "1") sortColumn = "U.Email";
            else if (orderColumnIndex == "2") sortColumn = "B.BranchName";
            else if (orderColumnIndex == "3") sortColumn = "U.UserCode";
            else if (orderColumnIndex == "4") sortColumn = "R.Name";

            string sortDirection = (orderDir == "desc") ? "DESC" : "ASC";

            // Paginated query
            string sqlQuery = $@"
SELECT
    U.Id,
    U.UserName,
    U.FullName,
    U.Email,
    U.PhoneNumber AS Phone,
    U.UserCode AS Code,
    B.Id AS BranchId,
    B.BranchName,
    U.ShiftId AS ShiftId,
    S.StartTime,
    S.EndTime,
    S.ShiftName,
    U.CertificateCode,
    U.IsActive,
    R.Name AS RoleName
{sqlBase}
ORDER BY {sortColumn} {sortDirection}
OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY";

            parameters.Add("Start", start);
            parameters.Add("Length", length);

            var users = await conn.QueryAsync<dynamic>(sqlQuery, parameters);

            var dataList = new List<object>();

            var allRoles = (await _examService.GetAllRolesAsync()).ToList();
            var allShifts = (await _examService.GetAllShiftsAsync()).ToList();

            foreach (var u in users)
            {
                string userId = u.Id;
                string email = u.Email ?? "";
                string userName = u.UserName ?? "";
                string userCode = u.Code ?? "";
                string branchNameVal = u.BranchName ?? "GLOBAL";
                string roleName = u.RoleName ?? "User";
                string shiftName = u.ShiftName ?? "";
                bool isActive = u.IsActive ?? true;
                string certCode = u.CertificateCode ?? "";
                string branchId = u.BranchId?.ToString() ?? "";
                string shiftId = u.ShiftId?.ToString() ?? "0";

                // Generate HTML for Personnel Column
                string avatarChar = string.IsNullOrEmpty(userName) ? "?" : userName.Trim().Substring(0, 1).ToUpper();
                string displayUserName = userName.Replace("_", " ");
                string subCode = string.IsNullOrWhiteSpace(userCode) 
                    ? ("SYS-" + userId.Substring(0, Math.Min(8, userId.Length)).ToUpper()) 
                    : userCode;

                string personnelHtml = $@"
                    <div class='flex items-center gap-3'>
                        <div class='js-user-avatar w-10 h-10 bg-slate-100 dark:bg-slate-800 rounded-xl flex items-center justify-center font-bold text-slate-400'>{avatarChar}</div>
                        <div class='flex flex-col'>
                            <span class='js-user-display font-bold text-slate-900 dark:text-white'>{displayUserName}</span>
                            <span class='js-user-sub text-[9px] font-bold text-slate-400 uppercase'>{subCode}</span>
                        </div>
                    </div>";

                // Email Column
                string emailHtml = $"<span class='js-user-email text-xs font-medium text-slate-500'>{email}</span>";

                // Branch Column
                string activeBadge = !isActive ? "<span class='ml-2 text-[10px] font-bold text-rose-500 bg-rose-50 dark:bg-rose-500/10 px-2 py-1 rounded border border-rose-100 dark:border-rose-800 uppercase'>Inactive</span>" : "";
                string branchHtml = $@"
                    <span class='js-user-branch-name text-[10px] font-bold text-brand-600 dark:text-brand-400 uppercase tracking-widest bg-brand-50 dark:bg-brand-500/10 px-2 py-1 rounded'>{branchNameVal}</span>
                    {activeBadge}";

                // Security Code Column
                string codeHtml = $"<span class='js-user-code font-mono font-bold text-slate-400 bg-slate-50 dark:bg-slate-800 px-2 py-1 rounded'>{userCode}</span>";

                // Classification Column
                string roleHtml = $"<span class='text-[9px] font-bold text-white bg-slate-900 dark:bg-brand-500/20 dark:text-brand-400 px-2.5 py-1 rounded-full uppercase tracking-widest'>{roleName.ToUpper()}</span>";

                // Shift Column
                string shiftHtml = "";
                if (!string.IsNullOrWhiteSpace(shiftName))
                {
                    string startTimeStr = u.StartTime is TimeSpan st ? st.ToString(@"hh\:mm") : "00:00";
                    string endTimeStr = u.EndTime is TimeSpan et ? et.ToString(@"hh\:mm") : "00:00";
                    shiftHtml = $@"
                        <div class='flex flex-col'>
                            <span class='text-[10px] font-bold italic uppercase'>{shiftName}</span>
                            <span class='text-[8px] font-bold text-slate-400'>{startTimeStr} — {endTimeStr}</span>
                        </div>";
                }
                else
                {
                    shiftHtml = "<span class='text-[9px] font-bold text-rose-500/50 uppercase'>Unassigned</span>";
                }

                // Actions Column
                // Role Options
                string roleSelectOptions = "";
                foreach (var r in allRoles)
                {
                    string sel = string.Equals(r.RoleName, roleName, StringComparison.OrdinalIgnoreCase) ? "selected" : "";
                    roleSelectOptions += $"<option value='{r.RoleId}' {sel}>{r.RoleName.ToUpper()}</option>";
                }

                // Shift Options
                string shiftSelectOptions = "<option value='0'>-- SYNC SHIFT --</option>";
                foreach (var s in allShifts)
                {
                    string sel = string.Equals(shiftName, s.ShiftName, StringComparison.OrdinalIgnoreCase) ? "selected" : "";
                    string startTimeStr = s.StartTime.ToString(@"hh\:mm");
                    string endTimeStr = s.EndTime.ToString(@"hh\:mm");
                    shiftSelectOptions += $"<option value='{s.Id}' data-name='{s.ShiftName.ToUpper()}' data-time='{startTimeStr} — {endTimeStr}' {sel}>{s.ShiftName.ToUpper()} ({startTimeStr})</option>";
                }

                string statusButton = "";
                if (isActive)
                {
                    statusButton = $@"
                        <button type='button' onclick=""deactivateUser('{userId}')"" class='w-full bg-white dark:bg-slate-900 text-rose-500 hover:bg-rose-500 hover:text-white p-1.5 rounded-xl transition-all border border-rose-100 dark:border-rose-900/50 flex items-center justify-center gap-2 text-[9px] font-bold uppercase shadow-sm'>
                            <i class='fas fa-ban'></i> Deactivate
                        </button>";
                }
                else
                {
                    statusButton = $@"
                        <button type='button' onclick=""activateUser('{userId}')"" class='w-full bg-white dark:bg-slate-900 text-emerald-500 hover:bg-emerald-500 hover:text-white p-1.5 rounded-xl transition-all border border-emerald-100 dark:border-emerald-900/50 flex items-center justify-center gap-2 text-[9px] font-bold uppercase shadow-sm'>
                            <i class='fas fa-check-circle'></i> Activate
                        </button>";
                }

                var userNameEscaped = userName.Replace("'", "\\'");
                string actionsHtml = $@"
                    <div class='flex flex-col gap-2 min-w-[220px]'>
                        <div class='flex gap-1'>
                            <button type='button' class='js-edit-personnel flex-1 bg-slate-100 dark:bg-slate-800 text-slate-700 dark:text-slate-200 p-1.5 rounded-xl border border-slate-200 dark:border-slate-700 text-[9px] font-bold uppercase hover:bg-brand-50 dark:hover:bg-brand-900/20'>
                                <i class='fas fa-pen'></i> Edit
                            </button>
                            <button type='button' onclick=""deleteUser('{userId}')"" class='flex-1 bg-white dark:bg-slate-900 text-slate-500 hover:text-rose-600 p-1.5 rounded-xl border border-slate-200 dark:border-slate-700 text-[9px] font-bold uppercase'>
                                <i class='fas fa-trash-alt'></i> Delete
                            </button>
                        </div>
                        <button type='button' onclick=""openSendMailModal('{userId}', '{userNameEscaped}')"" class='w-full bg-brand-50 dark:bg-brand-900/20 text-brand-600 dark:text-brand-400 p-1.5 rounded-xl border border-brand-100 dark:border-brand-800 text-[9px] font-bold uppercase hover:bg-brand-600 hover:text-white transition-all flex items-center justify-center gap-2'>
                            <i class='fas fa-paper-plane'></i> Send Custom Message
                        </button>
                        <form action='/Admin/UpdateUserRole' method='post' class='flex gap-2 ajax-update-form' data-type='role'>
                            <input type='hidden' name='userId' value='{userId}' />
                            <select name='roleId' onchange=""$(this).closest('form').submit();"" class='bg-slate-50 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded-xl px-2.5 py-1.5 text-[9px] font-bold text-slate-500 focus:border-brand-500 outline-none flex-1 transition-all'>
                                {roleSelectOptions}
                            </select>
                        </form>
                        <form action='/Admin/UpdateUserShift' method='post' class='flex gap-2 ajax-update-form' data-type='shift'>
                            <input type='hidden' name='userId' value='{userId}' />
                            <select name='newShiftId' onchange=""$(this).closest('form').submit();"" class='bg-slate-50 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded-xl px-2.5 py-1.5 text-[9px] font-bold text-slate-500 focus:border-amber-500 outline-none flex-1 transition-all'>
                                {shiftSelectOptions}
                            </select>
                        </form>
                        {statusButton}
                        <button type='button' onclick=""resetPassword('{userId}')"" class='w-full bg-amber-50 dark:bg-amber-900/20 text-amber-600 dark:text-amber-400 hover:bg-amber-600 hover:text-white p-1.5 rounded-xl transition-all border border-amber-100 dark:border-amber-800 flex items-center justify-center gap-2 text-[9px] font-bold uppercase shadow-sm'>
                            <i class='fas fa-key'></i> Reset Password
                        </button>
                    </div>";

                dataList.Add(new {
                    personnel = personnelHtml,
                    email = emailHtml,
                    branch = branchHtml,
                    code = codeHtml,
                    classification = roleHtml,
                    shift = shiftHtml,
                    actions = actionsHtml,
                    DT_RowId = userId,
                    DT_RowAttr = new { 
                        data_user_id = userId,
                        data_email = email,
                        data_username = userName,
                        data_usercode = userCode,
                        data_branch_id = branchId,
                        data_shift_id = shiftId
                    }
                });
            }

            return Json(new {
                draw = draw,
                recordsTotal = totalRecords,
                recordsFiltered = filteredRecords,
                data = dataList
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddUser(RegisterDTO dto)
        {
            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "Invalid data. Please check all fields.";
                return RedirectToAction("AllUsers");
            }

            var user = new ApplicationUser
            {
                UserName = dto.UserName,
                Email = dto.Email,
                PhoneNumber = dto.Phone,
                BranchId = dto.BranchId,
                UserCode = dto.UserCode,
                ShiftId = dto.ShiftId,
                CertificateCode = dto.CertificateCode,
                IsActive = true
            };

            var result = await _userManager.CreateAsync(user, dto.Password);

            if (result.Succeeded)
            {
                var role = string.IsNullOrEmpty(dto.RoleName) ? "User" : dto.RoleName;

                // Enforce English Only
                if (System.Text.RegularExpressions.Regex.IsMatch(role, @"\p{IsArabic}") || role.Contains("ØµÙŠØ¯Ù„ÙŠ") || role.Contains("Ù…Ø³Ø§Ø¹Ø¯"))
                {
                    return Json(new { success = false, message = "Arabic roles are no longer supported. Please use English (e.g., Pharmacist, Assistant)." });
                }

                if (!await _roleManager.RoleExistsAsync(role))
                {
                    await _roleManager.CreateAsync(new IdentityRole(role));
                }
                await _userManager.AddToRoleAsync(user, role);

                var newUser = await _examService.GetUserWithRoleByIdAsync(user.Id);
                return Json(new { success = true, user = newUser, message = $"User {dto.UserName} added successfully." });
            }
            else
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Json(new { success = false, message = "Failed to add user: " + errors });
            }
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteWave(int waveid)
        {
            await _examService.DeleteWaveAsync(waveid);
            return Json(new { success = true, Message = "Delete wave success" }); 
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportUsersFromExcel(IFormFile excelFile, string connectionId, bool sendWelcomeEmails = false)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "Please upload an Excel file." });
            }

            var successCount = 0;
            var errorLines = new List<string>();
            const string fallbackRoleName = "User";

            try
            {
                using var memoryStream = new MemoryStream();
                await excelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using var workbook = new XLWorkbook(memoryStream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return Json(new { success = false, message = "Excel file has no worksheets." });

                var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var headerRow = 1;

                // Try to find the header row in the first 3 rows
                for (int r = 1; r <= 3; r++)
                {
                    headers.Clear();
                    for (int col = 1; col <= 20; col++)
                    {
                        var val = worksheet.Cell(r, col).Value.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(val) && !headers.ContainsKey(val)) headers[val] = col;
                    }
                    if (headers.ContainsKey("Email")) { headerRow = r; break; }
                }

                int? GetCol(params string[] possibleNames)
                {
                    foreach (var name in possibleNames)
                        if (headers.TryGetValue(name.Trim(), out var colIndex)) return colIndex;
                    return null;
                }


                var colUserName = GetCol("UserName", "Username", "Name");
                var colEmail = GetCol("Email", "E-mail", "Mail");
                var colPassword = GetCol("Password", "Pass");
                var colPhone = GetCol("Phone", "PhoneNumber", "Mobile");
                var colRoleName = GetCol("RoleName", "Role");
                var colUserCode = GetCol("UserCode", "Code");
                // Excel headers: name or code; values matched against sp_Admin_GetAllBranches (or inline Branches query if SP is outdated).
                var colBranchName = GetCol("BranchName", "Branch", "BranchCode", "Location", "Ø§Ù„ÙØ±Ø¹");
                var colCertificateCode = GetCol("CertificateCode", "Certificate");
                var colshiftid = GetCol("ShiftId", "ShieftId", "ShieftId", "Shieft-Id", "Shift Window");

                var missingColumns = new List<string>();
                if (colUserName == null) missingColumns.Add("UserName (أو Name)");
                if (colEmail == null) missingColumns.Add("Email");
                if (colPassword == null) missingColumns.Add("Password (أو Pass)");
                if (colPhone == null) missingColumns.Add("Phone (أو PhoneNumber أو Mobile)");
                if (colRoleName == null) missingColumns.Add("RoleName (أو Role)");
                if (colUserCode == null) missingColumns.Add("UserCode (أو Code)");
                if (colBranchName == null) missingColumns.Add("BranchName (أو Branch أو Location أو الفرع)");
                if (colshiftid == null) missingColumns.Add("ShiftId (أو Shift Window)");

                if (missingColumns.Any())
                {
                    var missingStr = string.Join(", ", missingColumns);
                    return Json(new { 
                        success = false, 
                        message = $"الملف المرفوع تنقصه الأعمدة التالية أو لم يتم التعرف عليها: <br/><strong class='text-rose-600'>{missingStr}</strong>.<br/><br/>" +
                                  "الأعمدة المطلوبة والمسميات المقبولة هي:<br/>" +
                                  "1. <b>UserName</b> (أو Name)<br/>" +
                                  "2. <b>Email</b> (أو Mail)<br/>" +
                                  "3. <b>Password</b> (أو Pass)<br/>" +
                                  "4. <b>Phone</b> (أو Mobile)<br/>" +
                                  "5. <b>RoleName</b> (أو Role)<br/>" +
                                  "6. <b>UserCode</b> (أو Code)<br/>" +
                                  "7. <b>BranchName</b> (أو Branch أو الفرع)<br/>" +
                                  "8. <b>ShiftId</b> (أو Shift Window)<br/>" +
                                  "ملاحظة: عمود <b>CertificateCode</b> اختياري بالكامل وليس ضرورياً."
                    });
                }

                var branchList = (await _examService.GetAllBranchesAsync())
                    .Select(b => (
                        Id: int.TryParse(b.Id, out var bid) ? bid : 0,
                        Name: (b.BranchName ?? "").Trim(),
                        Code: (b.BranchCode ?? "").Trim()))
                    .Where(b => b.Id > 0 && (!string.IsNullOrEmpty(b.Name) || !string.IsNullOrEmpty(b.Code)))
                    .ToList();

                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                var branchIdByExcelValue = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;


                //  



                // --- Loop Rows ---
                for (int row = headerRow + 1; row <= lastRow; row++)
                {
                    var email = worksheet.Cell(row, colEmail.Value).Value.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(email)) continue;

                    try
                    {
                        var rawUserName = colUserName != null ? worksheet.Cell(row, colUserName.Value).Value.ToString()?.Trim() : null;
                        var userName = !string.IsNullOrWhiteSpace(rawUserName) ? rawUserName.Replace(" ", "_") : email;
                        var password = colPassword != null ? worksheet.Cell(row, colPassword.Value).Value.ToString()?.Trim() : "TempPass@123";
                        if (string.IsNullOrWhiteSpace(password)) password = "TempPass@123";
                        
                        var phone = colPhone != null ? worksheet.Cell(row, colPhone.Value).Value.ToString()?.Trim() : null;
                        var roleName = colRoleName != null ? worksheet.Cell(row, colRoleName.Value).Value.ToString()?.Trim() : fallbackRoleName;
                        if (string.IsNullOrWhiteSpace(roleName)) roleName = fallbackRoleName;

                        // --- Role Validation (English Only) ---
                        bool hasArabic = System.Text.RegularExpressions.Regex.IsMatch(roleName, @"\p{IsArabic}");
                        if (hasArabic || roleName.Contains("ØµÙŠØ¯Ù„ÙŠ") || roleName.Contains("Ù…Ø³Ø§Ø¹Ø¯"))
                        {
                            errorLines.Add($"Row {row}: Role '{roleName}' must be in English (e.g., Pharmacist, Assistant). Arabic roles are no longer supported.");
                            continue;
                        }

                        var userCode = colUserCode != null ? worksheet.Cell(row, colUserCode.Value).Value.ToString()?.Trim() : null;
                        var branchName = colBranchName != null ? worksheet.Cell(row, colBranchName.Value).Value.ToString()?.Trim() : null;
                        var certCode = colCertificateCode != null ? worksheet.Cell(row, colCertificateCode.Value).Value.ToString()?.Trim() : null;
                        var shiftIdStr = colshiftid != null ? worksheet.Cell(row, colshiftid.Value).Value.ToString()?.Trim() : null;
                        int? sId = null;
                        if (!string.IsNullOrWhiteSpace(shiftIdStr))
                        {
                            if (double.TryParse(shiftIdStr, out var dValue)) sId = (int)dValue;
                        }

                        int? bId = null;
                        if (!string.IsNullOrWhiteSpace(branchName))
                        {
                            if (!branchIdByExcelValue.TryGetValue(branchName, out bId))
                            {
                                bId = BranchNameResolver.ResolveBranchId(branchName, branchList);
                                branchIdByExcelValue[branchName] = bId;
                            }
                            if (!bId.HasValue)
                                errorLines.Add($"Row {row}: Branch '{branchName}' did not match any branch from sp_Admin_GetAllBranches (after normalize/contains).");
                        }

                        var user = await _userManager.FindByEmailAsync(email);
                        
                        // Check if user exists by UserCode if not found by email
                        if (user == null && !string.IsNullOrWhiteSpace(userCode)) 
                        {
                            user = _userManager.Users.FirstOrDefault(x => x.UserCode == userCode);
                        }

                        // Check if user exists by UserName if still not found
                        if (user == null && userName != email)
                        {
                            var uNameMatch = await _userManager.FindByNameAsync(userName);
                            if (uNameMatch != null) user = uNameMatch;
                        }

                        if (user == null)
                        {
                            user = new ApplicationUser
                            {
                                UserName = userName ?? email,
                                FullName = !string.IsNullOrWhiteSpace(rawUserName) ? rawUserName : null,
                                Email = email,
                                PhoneNumber = phone,
                                UserCode = userCode,
                                CertificateCode = certCode,
                                BranchId = bId,
                                ShiftId = sId,
                                IsActive = true
                            };
                            var res = await _userManager.CreateAsync(user, password);
                            if (!res.Succeeded) { errorLines.Add($"Row {row}: {res.Errors.First().Description}"); continue; }
                        }
                        else
                        {
                            user.FullName = !string.IsNullOrWhiteSpace(rawUserName) && (string.IsNullOrWhiteSpace(user.FullName) || user.FullName == user.UserName || user.FullName.StartsWith("External Trainee") || user.FullName != rawUserName) ? rawUserName : user.FullName;
                            user.PhoneNumber = phone ?? user.PhoneNumber;
                            user.UserCode = userCode ?? user.UserCode;
                            user.CertificateCode = certCode ?? user.CertificateCode;
                            user.BranchId = bId ?? user.BranchId;
                            user.ShiftId = sId ?? user.ShiftId;
                            await _userManager.UpdateAsync(user);
                        }

                        if (sId.HasValue)
                        {
                            await conn.ExecuteAsync("UPDATE AspNetUsers SET ShiftId = @sid WHERE Email = @em", new { sid = sId, em = email });
                        }

                        // Role should already exist. We won't auto-create Arabic roles anymore.
                        if (!await _roleManager.RoleExistsAsync(roleName)) 
                        {
                            await _roleManager.CreateAsync(new IdentityRole(roleName));
                        }
                        if (!await _userManager.IsInRoleAsync(user, roleName)) await _userManager.AddToRoleAsync(user, roleName);

                        if (sendWelcomeEmails)
                        {
                            var userDisplayName = userName ?? email;
                            var userEmail = email;
                            var userPassword = password;

                            // Ø¨Ù†Ø±Ù…ÙŠ Ø§Ù„Ù…Ù‡Ù…Ø© Ù„Ù„Ø®Ù„ÙÙŠØ© Ø¨Ø¯ÙˆÙ† await
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var loginUrl = "http://41.33.149.186:5208/Auth/Login";
                                    var subject = "Welcome to El-Tarshoubi Training Academy Exam System";
                                    var body = $@"
            <div style='font-family: Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: auto; border: 1px solid #eee; padding: 25px; border-radius: 12px; border-top: 4px solid #4f46e5;'>
                <h2 style='color: #4f46e5; text-align: center;'>Welcome to the Academy</h2>
                <p>Dear <strong>{userDisplayName}</strong>,</p>
                <p>Welcome to the **El-Tarshoubi Training Academy Exam System**.</p>
                <p>Your account has been successfully activated...</p>
                
                <div style='background: #f8fafc; padding: 20px; border-radius: 8px; border: 1px solid #e2e8f0; margin: 25px 0;'>
                    <p style='margin: 8px 0;'><strong>Login Email:</strong> {userEmail}</p>
                    <p style='margin: 8px 0;'><strong>Initial Password:</strong> <code style='background: #fff; padding: 2px 6px; border: 1px solid #cbd5e1; border-radius: 4px; color: #e11d48;'>{userPassword}</code></p>
                </div>

                <p style='text-align: center; margin: 35px 0;'>
                    <a href='{loginUrl}' style='background: #4f46e5; color: white; padding: 14px 32px; text-decoration: none; border-radius: 8px; font-weight: bold; display: inline-block;'>Click here to access the Exam System</a>
                </p>
                <p style='font-size: 11px; color: #94a3b8; text-align: center;'>Â© {DateTime.Now.Year} El-Tarshoubi Group. All rights reserved.</p>
            </div>";

                                    await _emailSender.SendEmailAsync(userEmail, subject, body);
                                }
                                catch (Exception exEmail)
                                {
                                    // Ø¨Ù†Ø³Ø¬Ù„ Ø§Ù„Ø®Ø·Ø£ ÙÙŠ Ø§Ù„Ø¯ÙŠØ¨Ø§Ø¬ Ø¨Ø³ Ù„Ø£Ù† Ø§Ù„ÙŠÙˆØ²Ø± Ø®Ù„Ø§Øµ Ù‡ÙŠÙƒÙˆÙ† Ø¬Ø§Ù„Ù‡ Ø§Ù„Ø±Ø¯ Success
                                    System.Diagnostics.Debug.WriteLine($"Background Email Fail for {userEmail}: {exEmail.Message}");
                                }
                            });
                        }

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errorLines.Add($"Row {row}: Error - {ex.Message}");
                    }
                }
                var debugInfo = new List<string> {
                    $"SuccessCount: {successCount}",
                    $"HeaderRow: {headerRow}",
                    $"LastRow: {lastRow}",
                    $"EmailCol: {colEmail}"
                };
                System.IO.File.WriteAllLines("import_debug.txt", debugInfo.Concat(errorLines));
                
                return Json(new { success = true, message = $"Successfully processed {successCount} users.", errors = errorLines });
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("import_debug.txt", "General Error: " + ex.ToString());
                return Json(new { success = false, message = "General Error: " + ex.Message });
            }
        }

        //        [HttpPost]
        //        [ValidateAntiForgeryToken]
        //        public async Task<IActionResult> ImportUsersFromExcel(IFormFile excelFile)
        //        {
        //            if (excelFile == null || excelFile.Length == 0)
        //            {
        //                TempData["ErrorMessage"] = "Please upload an Excel file.";
        //                return RedirectToAction(nameof(AllUsers));
        //            }

        //            var successCount = 0;
        //            var errorLines = new List<string>();

        //            // Default role if the excel doesn't include RoleName.
        //            const string fallbackRoleName = "User";

        //            using var memoryStream = new MemoryStream();
        //            await excelFile.CopyToAsync(memoryStream);
        //            memoryStream.Position = 0;

        //            using var workbook = new XLWorkbook(memoryStream);
        //            var worksheet = workbook.Worksheets.FirstOrDefault();
        //            if (worksheet == null)
        //            {
        //                TempData["ErrorMessage"] = "Excel file has no worksheets.";
        //                return RedirectToAction(nameof(AllUsers));
        //            }

        //            var headerRowNumber = 1;
        //            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        //            var lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 1;

        //            // Build header->columnIndex map (case-insensitive).
        //            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        //            for (int col = 1; col <= lastColumn; col++)
        //            {
        //                var header = worksheet.Cell(headerRowNumber, col).GetString()?.Trim();
        //                if (!string.IsNullOrWhiteSpace(header) && !headers.ContainsKey(header))
        //                {
        //                    headers[header] = col;
        //                }
        //            }

        //            int? GetCol(params string[] possibleNames)
        //            {
        //                foreach (var name in possibleNames)
        //                {
        //                    if (headers.TryGetValue(name, out var colIndex))
        //                        return colIndex;
        //                }
        //                return null;
        //            }

        //            // Expected headers (you can change column names in your Excel and this code will still work as long as they match these keys).
        //            var colUserName = GetCol("UserName", "Username", "Name");
        //            var colEmail = GetCol("Email", "E-mail", "Mail");
        //            var colPassword = GetCol("Password", "Pass");
        //            var colPhone = GetCol("Phone", "PhoneNumber", "Mobile");
        //            var colRoleName = GetCol("RoleName", "Role");
        //            var colUserCode = GetCol("UserCode", "Code");
        //            var colBranchName = GetCol("BranchName", "Branch", "Location");
        //            var colCertificateCode = GetCol("CertificateCode", "Certificate");

        //            // If Email is missing from Excel headers, we can't proceed.
        //            if (colEmail == null)
        //            {
        //                TempData["ErrorMessage"] = "Excel must have an 'Email' column in the first row (header row).";
        //                return RedirectToAction(nameof(AllUsers));
        //            }

        //            using var conn = new SqlConnection(_connectionString);
        //            await conn.OpenAsync();

        //            // Cache branch ids by name to reduce repeated queries.
        //            var branchNameToIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        //            int? TryGetBranchId(string branchName)
        //            {
        //                if (string.IsNullOrWhiteSpace(branchName))
        //                    return null;

        //                if (branchNameToIdCache.TryGetValue(branchName.Trim(), out var cached))
        //                    return cached;

        //                // Branches table is expected to exist in dbo.Branches with columns Id and BranchName.
        //                var id = conn.ExecuteScalar<int?>(
        //                    "SELECT TOP 1 Id FROM dbo.Branches WHERE BranchName = @BranchName;",
        //                    new { BranchName = branchName.Trim() });

        //                if (id.HasValue)
        //                    branchNameToIdCache[branchName.Trim()] = id.Value;

        //                return id;
        //            }

        //            // Update optional user columns only if those columns exist in your DB.
        //            async Task UpdateIfColumnExistsAsync(string columnName, object value, string userId)
        //            {
        //                // No-OP if the column does not exist.
        //                var sql = $@"
        //IF COL_LENGTH('dbo.AspNetUsers', @ColumnName) IS NOT NULL
        //BEGIN
        //    UPDATE dbo.AspNetUsers
        //    SET [{columnName}] = @Value
        //    WHERE Id = @UserId;
        //END";
        //                await conn.ExecuteAsync(sql, new { ColumnName = columnName, Value = value, UserId = userId });
        //            }

        //            for (int row = headerRowNumber + 1; row <= lastRow; row++)
        //            {
        //                var email = worksheet.Cell(row, colEmail.Value).GetString()?.Trim();
        //                if (string.IsNullOrWhiteSpace(email))
        //                    continue;

        //                var userName = colUserName != null ? worksheet.Cell(row, colUserName.Value).GetString()?.Trim() : null;
        //                var password = colPassword != null ? worksheet.Cell(row, colPassword.Value).GetString() : null;
        //                var phone = colPhone != null ? worksheet.Cell(row, colPhone.Value).GetString()?.Trim() : null;
        //                var roleName = colRoleName != null ? worksheet.Cell(row, colRoleName.Value).GetString()?.Trim() : null;
        //                var userCode = colUserCode != null ? worksheet.Cell(row, colUserCode.Value).GetString()?.Trim() : null;
        //                var branchName = colBranchName != null ? worksheet.Cell(row, colBranchName.Value).GetString()?.Trim() : null;
        //                var certificateCode = colCertificateCode != null ? worksheet.Cell(row, colCertificateCode.Value).GetString()?.Trim() : null;

        //                // If role is missing in the row, fall back to a safe default.
        //                roleName = string.IsNullOrWhiteSpace(roleName) ? fallbackRoleName : roleName;

        //                try
        //                {
        //                    var user = await _userManager.FindByEmailAsync(email);
        //                    if (user == null)
        //                    {
        //                        if (string.IsNullOrWhiteSpace(userName))
        //                        {
        //                            errorLines.Add($"Row {row}: UserName is required when creating a new user (Email={email}).");
        //                            continue;
        //                        }

        //                        if (string.IsNullOrWhiteSpace(password))
        //                        {
        //                            errorLines.Add($"Row {row}: Password is required for new users (Email={email}).");
        //                            continue;
        //                        }

        //                        user = new IdentityUser
        //                        {
        //                            UserName = userName,
        //                            Email = email,
        //                            PhoneNumber = phone
        //                        };

        //                        var createResult = await _userManager.CreateAsync(user, password);
        //                        if (!createResult.Succeeded)
        //                        {
        //                            var msg = string.Join("; ", createResult.Errors.Select(e => e.Description));
        //                            errorLines.Add($"Row {row}: Failed to create user (Email={email}). {msg}");
        //                            continue;
        //                        }

        //                        // Optional custom columns (UserCode/CertificateCode/BranchId) if they exist in your DB.
        //                        if (!string.IsNullOrWhiteSpace(userCode))
        //                            await UpdateIfColumnExistsAsync("UserCode", userCode, user.Id);
        //                        if (!string.IsNullOrWhiteSpace(certificateCode))
        //                            await UpdateIfColumnExistsAsync("CertificateCode", certificateCode, user.Id);

        //                        var branchId = TryGetBranchId(branchName);
        //                        if (branchId.HasValue)
        //                            await UpdateIfColumnExistsAsync("BranchId", branchId.Value, user.Id);
        //                    }
        //                    else
        //                    {
        //                        // Keep existing password if already exists; just update basic fields and re-assign role.
        //                        var updated = false;
        //                        if (!string.IsNullOrWhiteSpace(userName) && !string.Equals(user.UserName, userName, StringComparison.OrdinalIgnoreCase))
        //                        {
        //                            user.UserName = userName;
        //                            updated = true;
        //                        }

        //                        if (!string.IsNullOrWhiteSpace(phone) && !string.Equals(user.PhoneNumber, phone, StringComparison.OrdinalIgnoreCase))
        //                        {
        //                            user.PhoneNumber = phone;
        //                            updated = true;
        //                        }

        //                        if (updated)
        //                        {
        //                            var updateResult = await _userManager.UpdateAsync(user);
        //                            if (!updateResult.Succeeded)
        //                            {
        //                                var msg = string.Join("; ", updateResult.Errors.Select(e => e.Description));
        //                                errorLines.Add($"Row {row}: Failed to update user basic fields (Email={email}). {msg}");
        //                                continue;
        //                            }
        //                        }

        //                        if (!string.IsNullOrWhiteSpace(userCode))
        //                            await UpdateIfColumnExistsAsync("UserCode", userCode, user.Id);
        //                        if (!string.IsNullOrWhiteSpace(certificateCode))
        //                            await UpdateIfColumnExistsAsync("CertificateCode", certificateCode, user.Id);

        //                        var branchId = TryGetBranchId(branchName);
        //                        if (branchId.HasValue)
        //                            await UpdateIfColumnExistsAsync("BranchId", branchId.Value, user.Id);
        //                    }

        //                    // Ensure role is assigned (writes to AspNetUserRoles).
        //                    if (!string.IsNullOrWhiteSpace(roleName))
        //                    {
        //                        if (!await _roleManager.RoleExistsAsync(roleName))
        //                        {
        //                            await _roleManager.CreateAsync(new IdentityRole(roleName));
        //                        }

        //                        var currentRoles = await _userManager.GetRolesAsync(user);
        //                        foreach (var r in currentRoles)
        //                        {
        //                            if (!string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase))
        //                            {
        //                                await _userManager.RemoveFromRoleAsync(user, r);
        //                            }
        //                        }

        //                        if (!currentRoles.Any(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase)))
        //                        {
        //                            await _userManager.AddToRoleAsync(user, roleName);
        //                        }
        //                    }

        //                    successCount++;
        //                }
        //                catch (System.Exception ex)
        //                {
        //                    errorLines.Add($"Row {row}: Unexpected error (Email={email}). {ex.Message}");
        //                }
        //            }

        //            if (errorLines.Count == 0)
        //            {
        //                TempData["SuccessMessage"] = $"Imported {successCount} users successfully.";
        //            }
        //            else
        //            {
        //                TempData["SuccessMessage"] = $"Imported {successCount} users. Some rows failed.";
        //                TempData["ErrorMessage"] = string.Join("\n", errorLines);
        //            }

        //            return RedirectToAction(nameof(AllUsers));
        //        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateUserRole(string userId, string roleId)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(roleId))
            {
                return Json(new { success = false, message = "User and role are required." });
            }

            try
            {
                await _examService.UpdateUserRoleByIdAsync(userId, roleId);
                return Json(new { success = true, message = "User role updated." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateUserShift(string userId, int newShiftId)
        {
            if (string.IsNullOrWhiteSpace(userId) || newShiftId <= 0)
            {
                return Json(new { success = false, message = "User and shift are required." });
            }

            try
            {
                await _examService.UpdateUserShiftAsync(userId, newShiftId);
                return Json(new { success = true, message = "User shift updated." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeactivateUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Json(new { success = false, message = "User ID is required." });
            }

            try
            {
                await _examService.DeactivateUserAsync(userId);
                return Json(new { success = true, message = "User deactivated successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Json(new { success = false, message = "User ID is required." });
            }

            try
            {
                await _examService.ActivateUserAsync(userId);
                return Json(new { success = true, message = "User activated successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CloneExam([FromBody] CloneExamRequest request)
        {
            if (request.OldExamId <= 0 || request.NewWaveId <= 0)
            {
                return Json(new { success = false, message = "Invalid exam or wave." });
            }

            if (request.NewEndTime <= request.NewStartTime)
            {
                return Json(new { success = false, message = "End time must be after start time." });
            }

            try
            {
                var newId = await _examService.CloneExamAsync(request.OldExamId, request.NewWaveId, request.NewStartTime, request.NewEndTime, request.NewTitle);
                return Json(new { success = true, newExamId = newId });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        public class CloneExamRequest
        {
            public int OldExamId { get; set; }
            public int NewWaveId { get; set; }
            public DateTime NewStartTime { get; set; }
            public DateTime NewEndTime { get; set; }
            public string NewTitle { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> GetUsersWithoutCertificate()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                const string sql = @"
                    SELECT U.Id, U.UserName, U.Email, U.UserCode, B.BranchName, R.Name AS RoleName, U.CertificateCode
                    FROM AspNetUsers U WITH(NOLOCK)
                    LEFT JOIN Branches B WITH(NOLOCK) ON U.BranchId = B.Id
                    INNER JOIN AspNetUserRoles UR WITH(NOLOCK) ON U.Id = UR.UserId
                    INNER JOIN AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
                    WHERE U.IsActive = 1
                    AND R.Name IN ('pharmacist', 'assistant')";

                var users = await connection.QueryAsync<UserDto>(sql);
                return Json(users);
            }
        }

        [HttpPost]
        public async Task<IActionResult> AssignUsersToWave(int waveId, [FromBody] List<string> userIds)
        {
            if (userIds == null || !userIds.Any())
            {
                return BadRequest("No users selected.");
            }

            try
            {
                var siteLink = "http://41.33.149.186:5208";
                var assignedCount = await _examService.AssignUsersToWaveAsync(waveId, userIds, siteLink);
                return Ok(new { AssignedCount = assignedCount });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> BulkAssignWaveExams(int waveId, [FromBody] List<string> studentIds)
        {
            if (studentIds == null || !studentIds.Any())
            {
                return BadRequest("No students selected.");
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                // Get all active exams belonging to this wave
                var exams = await conn.QueryAsync<dynamic>(
                    "SELECT Id, StartTime, EndTime FROM Exams WHERE WaveId = @WaveId AND IsActive = 1", 
                    new { WaveId = waveId });

                int totalAssigned = 0;
                var siteUrl = "http://41.33.149.186:5208";
                
                foreach (var exam in exams)
                {
                    foreach (var studentId in studentIds)
                    {
                        try
                        {
                            await _examService.AssignExamToStudentAsync((int)exam.Id, studentId, (DateTime?)exam.StartTime, (DateTime?)exam.EndTime);
                            totalAssigned++;
                        }
                        catch (Exception)
                        {
                            // Suppress individual errors to allow processing the rest
                        }
                    }
                }

                return Ok(new { success = true, AssignedCount = totalAssigned, ExamsCount = exams.Count() });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportUsersToWaveFromExcel(int waveId, IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "Please upload an Excel file." });
            }

            try
            {
                using var memoryStream = new MemoryStream();
                await excelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using var workbook = new XLWorkbook(memoryStream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return Json(new { success = false, message = "Excel file has no worksheets." });

                var rows = worksheet.RowsUsed().Skip(1); // Assume row 1 is header
                var excelCodes = new List<string>();

                foreach (var row in rows)
                {
                    var val1 = row.Cell(1).Value.ToString().Trim();
                    var val2 = row.Cell(2).Value.ToString().Trim();

                    var code = "";
                    if (!string.IsNullOrWhiteSpace(val2) && double.TryParse(val2, out _))
                    {
                        code = val2;
                    }
                    else if (!string.IsNullOrWhiteSpace(val1) && double.TryParse(val1, out _))
                    {
                        code = val1;
                    }
                    else
                    {
                        code = !string.IsNullOrWhiteSpace(val2) ? val2 : val1;
                    }

                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        excelCodes.Add(code);
                    }
                }

                if (!excelCodes.Any())
                {
                    return Json(new { success = false, message = "No valid user codes found in the Excel sheet." });
                }

                using var conn = new SqlConnection(_connectionString);
                var matchedUsers = (await conn.QueryAsync<(string Id, string UserCode, string UserName)>(
                    "SELECT Id, UserCode, UserName FROM AspNetUsers WHERE UserCode IN @Codes",
                    new { Codes = excelCodes })).ToList();

                if (!matchedUsers.Any())
                {
                    return Json(new { success = false, message = "No matching users found in the system for the uploaded codes." });
                }

                var userIds = matchedUsers.Select(u => u.Id).ToList();
                var siteLink = "http://41.33.149.186:5208";
                var assignedCount = await _examService.AssignUsersToWaveAsync(waveId, userIds, siteLink);

                var userNamesList = string.Join(", ", matchedUsers.Select(u => u.UserName));
                return Json(new { 
                    success = true, 
                    message = $"Successfully assigned {assignedCount} users to the batch. Enrolled: {userNamesList}" 
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }
        [HttpGet]
        public async Task<IActionResult> getallcategories()
        {
            ViewBag.ExamTypes = await _examService.GetAllExamTypesAsync();
            var categories = await _examService.GetAllCategoriesAsync();
            return View(categories);
        }

        [HttpGet]
        public async Task<IActionResult> LectureTopics()
        {
            ViewBag.Categories = await _examService.GetAllCategoriesAsync();
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetCategoriesByExamType(int examTypeId)
        {
            var categories = await _examService.GetAllCategoriesAsync(examTypeId);
            return Json(categories);
        }

        [HttpGet]
        public async Task<IActionResult> GetTopicsByCategoryId(int categoryId)
        {
            var topics = await _examService.GetTopicsByCategoryAsync(categoryId);
            return Json(topics);
        }

        [HttpPost]
        public async Task<IActionResult> CreateTopic(string topicName, int categoryId)
        {
            if (string.IsNullOrEmpty(topicName)) return Json(new { success = false, message = "Topic name is required" });
            await _examService.CreateTopicAsync(topicName, categoryId);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateTopic(int topicId, string topicName)
        {
            if (string.IsNullOrEmpty(topicName)) return Json(new { success = false, message = "Topic name is required" });
            await _examService.UpdateTopicAsync(topicId, topicName);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteTopic(int topicId)
        {
            await _examService.DeleteTopicAsync(topicId);
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> getallexamtypes()
        {
            var types = await _examService.getallexamtypes();
            return View(types);
        }

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
        public async Task<IActionResult> createcategory(string catname, int? examTypeId = null)
        {
            if (string.IsNullOrEmpty(catname))
            {
                return Json(new { success = false, message = "Please enter category name." });
            }

            await _examService.Createnewcategory(catname, examTypeId);
            return Json(new { success = true, message = "Category added successfully." });
        }

        [HttpGet]
        public async Task<IActionResult> Structure()
        {
            ViewBag.Categories = await _examService.GetAllCategoriesAsync();
            ViewBag.ExamTypes = await _examService.getallexamtypes();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> updatecategory(int id, string name, int? examTypeId = null)
        {
            if (string.IsNullOrEmpty(name)) return Json(new { success = false, message = "Name cannot be empty." });
            await _examService.UpdateCategoryAsync(id, name, examTypeId);
            return Json(new { success = true, message = "Category updated successfully." });
        }

        [HttpPost]
        public async Task<IActionResult> deletecategory(int id)
        {
            await _examService.DeleteCategoryAsync(id);
            return Json(new { success = true, message = "Category deleted successfully." });
        }

        [HttpPost]
        public async Task<IActionResult> createexamtype(string typename)
        {
            if (string.IsNullOrEmpty(typename))
            {
                return Json(new { success = false, message = "Please enter exam type name." });
            }

            await _examService.createexamtype(typename);
            return Json(new { success = true, message = "Exam Type added successfully." });
        }

        [HttpPost]
        public async Task<IActionResult> updateexamtype(int id, string name)
        {
            if (string.IsNullOrEmpty(name)) return Json(new { success = false, message = "Name cannot be empty." });
            await _examService.UpdateExamTypeAsync(id, name);
            return Json(new { success = true, message = "Exam Type updated successfully." });
        }

        [HttpPost]
        public async Task<IActionResult> deleteexamtype(int id)
        {
            try
            {
                await _examService.DeleteExamTypeAsync(id);
                return Json(new { success = true, message = "Exam Type deleted successfully." });
            }
            catch (Exception)
            {
                return Json(new { success = false, message = "Cannot delete Exam Type because it is in use by existing exams." });
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
                    new { wave.WaveName, wave.StartDate },
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

        // --- ROLES MANAGEMENT ---
        [HttpGet]
        public IActionResult Roles()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetRolesList()
        {
            var roles = await _roleManager.Roles.Select(r => new { r.Id, r.Name }).ToListAsync();
            return Json(roles);
        }

        [HttpPost]
        public async Task<IActionResult> AddRole(string roleName)
        {
            if (string.IsNullOrWhiteSpace(roleName)) return Json(new { success = false, message = "Role name cannot be empty." });
            if (await _roleManager.RoleExistsAsync(roleName)) return Json(new { success = false, message = "Role already exists." });
            var result = await _roleManager.CreateAsync(new IdentityRole(roleName));
            return Json(new { success = result.Succeeded, message = result.Succeeded ? "Role added." : "Error adding role." });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateRole(string roleId, string newRoleName)
        {
            if (string.IsNullOrWhiteSpace(newRoleName)) return Json(new { success = false, message = "Role name cannot be empty." });
            var role = await _roleManager.FindByIdAsync(roleId);
            if (role == null) return Json(new { success = false, message = "Role not found." });
            if (await _roleManager.RoleExistsAsync(newRoleName) && role.Name != newRoleName) return Json(new { success = false, message = "Role name already in use." });
            
            role.Name = newRoleName;
            var result = await _roleManager.UpdateAsync(role);
            return Json(new { success = result.Succeeded, message = result.Succeeded ? "Role updated." : "Error updating role." });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteRole(string roleId)
        {
            var role = await _roleManager.FindByIdAsync(roleId);
            if (role == null) return Json(new { success = false, message = "Role not found." });
            
            var usersInRole = await _userManager.GetUsersInRoleAsync(role.Name);
            if (usersInRole.Any()) return Json(new { success = false, message = "Cannot delete role because it is assigned to users." });

            var result = await _roleManager.DeleteAsync(role);
            return Json(new { success = result.Succeeded, message = result.Succeeded ? "Role deleted." : "Error deleting role." });
        }

        [HttpGet]
        public async Task<IActionResult> ManageInstructions()
        {
            var instructions = await _examService.GetInstructionsAsync();
            return View("ManageInstructions", instructions);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateInstructions(string instructions)
        {
            await _examService.UpdateInstructionsAsync(instructions);
            TempData["SuccessMessage"] = "Instructions updated successfully.";
            return RedirectToAction("ManageInstructions");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateUserProfile(string userId, string email, string userName, string userCode)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(email))
                return Json(new { success = false, message = "User and email are required." });

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
                return Json(new { success = false, message = "User not found." });

            var normalizedName = (userName ?? "").Trim().Replace(" ", "_");
            if (string.IsNullOrWhiteSpace(normalizedName))
                return Json(new { success = false, message = "User name is required." });

            var existingEmail = await _userManager.FindByEmailAsync(email.Trim());
            if (existingEmail != null && existingEmail.Id != userId)
                return Json(new { success = false, message = "This email is already assigned to another account." });

            var existingByName = await _userManager.FindByNameAsync(normalizedName);
            if (existingByName != null && existingByName.Id != userId)
                return Json(new { success = false, message = "This user name is already in use." });

            user.UserCode = string.IsNullOrWhiteSpace(userCode) ? null : userCode.Trim();

            // Only touch BranchId when the client sends branchId (changed branch or explicit clear).
            // Omitting the field preserves the current branch (fixes empty <select> sending "" and wiping BranchId).
            if (Request.HasFormContentType && Request.Form.ContainsKey("branchId"))
            {
                var rawBr = Request.Form["branchId"].ToString();
                if (string.IsNullOrWhiteSpace(rawBr))
                    user.BranchId = null;
                else if (int.TryParse(rawBr, out var bid))
                    user.BranchId = bid;
            }

            var setName = await _userManager.SetUserNameAsync(user, normalizedName);
            if (!setName.Succeeded)
                return Json(new { success = false, message = string.Join("; ", setName.Errors.Select(e => e.Description)) });

            var setEmail = await _userManager.SetEmailAsync(user, email.Trim());
            if (!setEmail.Succeeded)
                return Json(new { success = false, message = string.Join("; ", setEmail.Errors.Select(e => e.Description)) });

            var update = await _userManager.UpdateAsync(user);
            if (!update.Succeeded)
                return Json(new { success = false, message = string.Join("; ", update.Errors.Select(e => e.Description)) });

            var branchLabel = "GLOBAL";
            if (user.BranchId.HasValue)
            {
                var branches = await _examService.GetAllBranchesAsync();
                var match = branches.FirstOrDefault(b => int.TryParse(b.Id, out var bid) && bid == user.BranchId.Value);
                if (match != null && !string.IsNullOrWhiteSpace(match.BranchName))
                    branchLabel = match.BranchName;
            }
            return Json(new
            {
                success = true,
                message = "Profile updated.",
                email = user.Email,
                userName = user.UserName,
                userCode = user.UserCode,
                branchId = user.BranchId,
                branchName = branchLabel
            });
        }

        [HttpPost]
        public async Task<IActionResult> SendCustomEmail(string userId, string subject, string message)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(message))
                return Json(new { success = false, message = "User and message are required." });

            try
            {
                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return Json(new { success = false, message = "User not found." });

                var body = $@"
                    <div style='font-family: sans-serif; max-width: 600px; margin: 0 auto; padding: 25px; border: 1px solid #e2e8f0; border-radius: 16px; color: #1e293b;'>
                        <div style='text-align: center; margin-bottom: 25px;'>
                            <h2 style='color: #4f46e5; margin: 0;'>Eltarshoubi Academy</h2>
                            <div style='width: 50px; height: 3px; background: #4f46e5; margin: 10px auto;'></div>
                        </div>
                        <p style='font-size: 16px; line-height: 1.6;'>Hello <b>{user.UserName}</b>,</p>
                        <div style='background: #f8fafc; padding: 20px; border-radius: 12px; margin: 20px 0; border-left: 4px solid #4f46e5;'>
                            <p style='margin: 0; white-space: pre-wrap;'>{message}</p>
                        </div>
                        <p style='font-size: 14px; color: #64748b;'>If you have any questions, please contact the administration.</p>
                        <hr style='border: none; border-top: 1px solid #f1f5f9; margin: 25px 0;' />
                        <p style='font-size: 11px; color: #94a3b8; text-align: center;'>Eltarshoubi Academy - Online Examination System</p>
                    </div>";

                try 
                {
                    await _emailSender.SendEmailAsync(user.Email, subject ?? "Important Message from Academy", body);
                    return Json(new { success = true, message = "Email sent successfully to " + user.Email });
                }
                catch (Exception ex) when (ex.Message.Contains("LIMIT_REACHED"))
                {
                    return Json(new { success = false, message = ex.Message.Replace("LIMIT_REACHED: ", "") });
                }
                catch (Exception ex)
                {
                    return Json(new { success = false, message = "Email failed to send: " + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Failed to send email: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ResetUserPassword(string userId, string newPassword)
        {
            if (string.IsNullOrEmpty(userId)) return Json(new { success = false, message = "User ID required." });
            if (string.IsNullOrEmpty(newPassword)) newPassword = "TempPass@123";

            try
            {
                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return Json(new { success = false, message = "User not found." });

                var hasPassword = await _userManager.HasPasswordAsync(user);
                if (hasPassword)
                {
                    var removeResult = await _userManager.RemovePasswordAsync(user);
                    if (!removeResult.Succeeded)
                    {
                        return Json(new { success = false, message = "Failed to remove old password: " + string.Join(", ", removeResult.Errors.Select(e => e.Description)) });
                    }
                }

                var result = await _userManager.AddPasswordAsync(user, newPassword);
                if (result.Succeeded)
                {
                    return Json(new { success = true, message = $"Password has been successfully updated to '{newPassword}'." });
                }

                return Json(new { success = false, message = string.Join(", ", result.Errors.Select(e => e.Description)) });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ResendAssignmentEmail(int examId, string userId)
        {
            if (string.IsNullOrEmpty(userId) || examId <= 0)
                return Json(new { success = false, message = "Invalid parameters." });

            try
            {
                var siteUrl = "http://41.33.149.186:5208";
                // Offload to background task so UI returns immediately
                _ = Task.Run(async () => {
                    try { await _examService.SendExamAssignmentEmailAsync(userId, examId, siteUrl); }
                    catch { /* Log failure if needed */ }
                });

                return Json(new { success = true, message = "Email sending initiated." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Failed: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return Json(new { success = false, message = "User ID is required." });

            var currentId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.Equals(userId, currentId, StringComparison.Ordinal))
                return Json(new { success = false, message = "You cannot delete your own account." });

            try
            {
                await _examService.DeleteUserCascadeAsync(userId);
                return Json(new { success = true, message = "User and related records were removed." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ImportQuestionsFromExcel(int examId, IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
                return Json(new { success = false, message = "Ù„Ù… ÙŠØªÙ… Ø§Ø®ØªÙŠØ§Ø± Ù…Ù„Ù." });

            if (examId <= 0)
                return Json(new { success = false, message = "ÙƒÙˆØ¯ Ø§Ù„Ø§Ù…ØªØ­Ø§Ù† ØºÙŠØ± ØµØ­ÙŠØ­." });

            try
            {
                using var workbook = new XLWorkbook(excelFile.OpenReadStream());
                var worksheet = workbook.Worksheets.First();
                var rows = worksheet.RowsUsed().Skip(1); // Skip header

                int successCount = 0;
                int totalRows = rows.Count();
                var errors = new List<string>();

                // Dynamic Column Mapping
                var headerRow = worksheet.Row(1);
                var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 1; c <= worksheet.ColumnsUsed().Count(); c++)
                {
                    string h = headerRow.Cell(c).GetString()?.Trim().ToLower().Replace(" ", "").Replace("_", "");
                    if (!string.IsNullOrEmpty(h)) colMap[h] = c;
                }

                // Helper to get col index by name
                int GetCol(params string[] names) {
                    foreach(var n in names) {
                        string key = n.ToLower().Replace(" ", "").Replace("_", "");
                        if (colMap.ContainsKey(key)) return colMap[key];
                    }
                    return -1;
                }

                int qCol = GetCol("QuestionText", "Question", "Text");
                int catCol = GetCol("CategoryId", "Category", "CatId");
                int ptsCol = GetCol("Points", "Point", "Score");
                int diffCol = GetCol("Difficulty", "Diffculty");
                int topicCol = GetCol("TopicId", "Topic", "LectureTopic");

                if (qCol == -1) return Json(new { success = false, message = "Could not find 'Question Text' column in Excel." });

                foreach (var row in rows)
                {
                    try
                    {
                        string qText = row.Cell(qCol).GetString()?.Trim();
                        if (string.IsNullOrEmpty(qText)) continue;

                        // CategoryId
                        int categoryId = 0;
                        if (catCol != -1) {
                            string catVal = row.Cell(catCol).GetString()?.Trim();
                            int.TryParse(catVal, out categoryId);
                        }

                        if (categoryId <= 0)
                        {
                            errors.Add($"Row {row.RowNumber()}: Invalid or missing Category ID.");
                            continue;
                        }

                        // Points
                        int points = 0;
                        if (ptsCol != -1) int.TryParse(row.Cell(ptsCol).GetString(), out points);

                        // Validate Category matches Exam's ExamType
                        var exam = await _examService.GetExamByIdAsync(examId);
                        if (exam != null)
                        {
                            var category = (await _examService.GetAllCategoriesAsync()).FirstOrDefault(c => c.Id == categoryId);
                            if (category != null)
                            {
                                if (category.ExamTypeId != exam.ExamTypeId)
                                {
                                    errors.Add($"Row {row.RowNumber()}: Category '{category.CategoryName}' (ID {categoryId}) does not belong to this exam type.");
                                    continue; 
                                }
                            }
                            else
                            {
                                errors.Add($"Row {row.RowNumber()}: Category ID {categoryId} not found in database.");
                                continue;
                            }
                        }

                        // Difficulty
                        int difficulty = 1;
                        if (diffCol != -1) {
                            string diffStr = row.Cell(diffCol).GetString()?.Trim().ToLower();
                            if (diffStr == "mid" || diffStr == "medium" || diffStr == "2") difficulty = 2;
                            else if (diffStr == "hard" || diffStr == "3") difficulty = 3;
                        }

                        // Topic Id
                        int? topicId = null;
                        if (topicCol != -1) {
                            string tVal = row.Cell(topicCol).GetString()?.Trim();
                            if (!string.IsNullOrEmpty(tVal) && int.TryParse(tVal, out int tid) && tid > 0) topicId = tid;
                        }

                        // Add Question
                        int qId = await _examService.AddQuestionForExistingExamAsync(examId, qText, points, 1, difficulty, topicId);
                        await _examService.UpdateQuestionAsync(examId, qId, qText, points, difficulty, categoryId, topicId);

                        // Choices mapping
                        for (int i = 1; i <= 4; i++)
                        {
                            int cIdx = GetCol($"Choice{i}", $"Option{i}");
                            int isCorrIdx = GetCol($"IsCorrect{i}", $"Correct{i}");

                            if (cIdx != -1) {
                                string choiceText = row.Cell(cIdx).GetString()?.Trim();
                                if (string.IsNullOrEmpty(choiceText)) continue;

                                bool isCorrect = false;
                                if (isCorrIdx != -1) {
                                    string icStr = row.Cell(isCorrIdx).GetString()?.Trim().ToLower();
                                    isCorrect = (icStr == "true" || icStr == "1" || icStr == "yes" || icStr == "ØµØ­" || icStr == "ØµØ­ ");
                                }
                                await _examService.AddChoiceForExistingQuestionAsync(qId, choiceText, isCorrect);
                            }
                        }

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Row {row.RowNumber()}: Error - {ex.Message}");
                    }
                }

                string message = $"Successfully imported {successCount} out of {totalRows} questions.";
                if (errors.Any())
                {
                    message += "\nErrors found:\n" + string.Join("\n", errors.Take(10));
                    if (errors.Count > 10) message += "\n...and more.";
                    return Json(new { success = successCount > 0, message, errors });
                }

                return Json(new { success = true, message = $"Successfully imported {successCount} questions." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Global error reading Excel: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> DeactivatedUsers()
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = @"
                SELECT 
                    U.Id,
                    U.UserName,
                    U.Email,
                    U.PhoneNumber AS Phone,
                    U.UserCode AS Code,
                    B.BranchName,
                    S.ShiftName,
                    U.IsActive
                FROM dbo.AspNetUsers U WITH(NOLOCK)
                LEFT JOIN dbo.Shifts S WITH(NOLOCK) ON S.Id = U.ShiftId
                LEFT JOIN dbo.Branches B WITH(NOLOCK) ON U.BranchId = B.Id
                WHERE U.IsActive = 0
                ORDER BY U.UserName;";
            var users = await conn.QueryAsync<dynamic>(sql);
            return View(users);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeactivateUserByCode(string userCode)
        {
            if (string.IsNullOrWhiteSpace(userCode))
            {
                return Json(new { success = false, message = "User code is required." });
            }

            using var conn = new SqlConnection(_connectionString);
            var user = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT Id, UserName FROM AspNetUsers WHERE UserCode = @UserCode", new { UserCode = userCode.Trim() });

            if (user == null)
            {
                return Json(new { success = false, message = $"User with code {userCode} not found." });
            }

            await _examService.DeactivateUserAsync((string)user.Id);
            return Json(new { success = true, message = $"User {user.UserName} has been deactivated successfully." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportDeactivationsFromExcel(IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "Please upload an Excel file." });
            }

            var successCount = 0;
            var notFoundCodes = new List<string>();

            try
            {
                using var memoryStream = new MemoryStream();
                await excelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using var workbook = new XLWorkbook(memoryStream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return Json(new { success = false, message = "Excel file has no worksheets." });

                var rows = worksheet.RowsUsed().Skip(1); // Assume row 1 is header
                using var conn = new SqlConnection(_connectionString);

                foreach (var row in rows)
                {
                    // Read first column (Code)
                    var codeVal = row.Cell(1).Value.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(codeVal)) continue;

                    // Find user
                    var user = await conn.QueryFirstOrDefaultAsync<dynamic>(
                        "SELECT Id FROM AspNetUsers WHERE UserCode = @UserCode", new { UserCode = codeVal });

                    if (user != null)
                    {
                        await _examService.DeactivateUserAsync((string)user.Id);
                        successCount++;
                    }
                    else
                    {
                        notFoundCodes.Add(codeVal);
                    }
                }

                var msg = $"Deactivated {successCount} users successfully.";
                if (notFoundCodes.Any())
                {
                    msg += $" Codes not found: {string.Join(", ", notFoundCodes)}";
                }

                return Json(new { success = true, message = msg });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUserPermanently(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Json(new { success = false, message = "User ID is required." });
            }

            try
            {
                await _examService.DeleteUserCascadeAsync(userId);
                return Json(new { success = true, message = "User and all associated data permanently deleted." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Companies()
        {
            using var conn = new SqlConnection(_connectionString);
            var list = await conn.QueryAsync<dynamic>(@"
                SELECT C.*, 
                       (SELECT COUNT(*) FROM CompanyTrainees CT WHERE CT.CompanyId = C.Id) as TraineeCount
                FROM Companies C
                ORDER BY C.CreatedAt DESC");
            return View(list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddCompany(string name, string description)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Json(new { success = false, message = "Company Name is required." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync(
                    "INSERT INTO Companies (Name, Description, CreatedAt) VALUES (@Name, @Description, @CreatedAt)",
                    new { Name = name.Trim(), Description = description?.Trim(), CreatedAt = DateTime.Now });

                return Json(new { success = true, message = "Company training portfolio registered successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditCompany(int id, string name, string description)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Json(new { success = false, message = "Company Name is required." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                var rows = await conn.ExecuteAsync(
                    "UPDATE Companies SET Name = @Name, Description = @Description WHERE Id = @Id",
                    new { Id = id, Name = name.Trim(), Description = description?.Trim() });

                return Json(new { success = rows > 0, message = rows > 0 ? "Company updated successfully." : "Company not found." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> FixSwapped()
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE CompanyTrainees SET FullName = UserCode, UserCode = FullName");
            return Json(new { success = true, message = "Data fixed!" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearCompanyTrainees(int companyId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                // We should also delete UserAttendance for these trainees so we don't violate FK if it exists
                await conn.ExecuteAsync("DELETE FROM UserAttendance WHERE CompanyTraineeId IN (SELECT Id FROM CompanyTrainees WHERE CompanyId = @Id)", new { Id = companyId });
                await conn.ExecuteAsync("DELETE FROM CompanyTrainees WHERE CompanyId = @Id", new { Id = companyId });
                return Json(new { success = true, message = "All trainees for this company have been deleted successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCompany(int id)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                // 1. Delete all user attendance records tied to sessions belonging to this company
                await conn.ExecuteAsync("DELETE FROM UserAttendance WHERE SessionId IN (SELECT Id FROM AttendanceSessions WHERE CompanyId = @Id)", new { Id = id });
                
                // 2. Delete all sessions belonging to this company
                await conn.ExecuteAsync("DELETE FROM AttendanceSessions WHERE CompanyId = @Id", new { Id = id });
                
                // 3. Delete company trainees
                await conn.ExecuteAsync("DELETE FROM CompanyTrainees WHERE CompanyId = @Id", new { Id = id });
                
                // 4. Delete legacy company users mapping (just in case)
                await conn.ExecuteAsync("DELETE FROM CompanyUsers WHERE CompanyId = @Id", new { Id = id });
                
                // 4. Finally delete the company itself
                await conn.ExecuteAsync("DELETE FROM Companies WHERE Id = @Id", new { Id = id });

                return Json(new { success = true, message = "Company and all associated data wiped successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompanyTrainees(int companyId)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                var initSql = @"
                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CompanyTrainees')
                    BEGIN
                        CREATE TABLE CompanyTrainees (
                            Id INT IDENTITY(1,1) PRIMARY KEY,
                            CompanyId INT NOT NULL,
                            FullName NVARCHAR(255) NOT NULL,
                            UserCode NVARCHAR(50),
                            JobTitle NVARCHAR(100),
                            BranchName NVARCHAR(100),
                            Email NVARCHAR(255),
                            Phone NVARCHAR(50),
                            CreatedAt DATETIME DEFAULT GETDATE()
                        );
                    END
                ";
                await conn.ExecuteAsync(initSql);

                var trainees = await conn.QueryAsync<dynamic>(@"
                    SELECT Id, FullName as UserName, UserCode, Email, Phone, JobTitle as RoleName, BranchName
                    FROM CompanyTrainees
                    WHERE CompanyId = @CompanyId
                    ORDER BY FullName", new { CompanyId = companyId });

                return Json(new { success = true, data = trainees });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCompanyTrainee(int id)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync("DELETE FROM UserAttendance WHERE CompanyTraineeId = @Id", new { Id = id });
                var rows = await conn.ExecuteAsync("DELETE FROM CompanyTrainees WHERE Id = @Id", new { Id = id });
                return Json(new { success = rows > 0, message = rows > 0 ? "Trainee removed successfully." : "Trainee not found." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportCompanyTraineesFromExcel(int companyId, IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "Please upload an Excel file." });
            }

            try
            {
                using var memoryStream = new MemoryStream();
                await excelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using var workbook = new XLWorkbook(memoryStream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return Json(new { success = false, message = "Excel file has no worksheets." });

                var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var headerRow = 1;

                // Try to find the header row in the first 3 rows
                for (int r = 1; r <= 3; r++)
                {
                    headers.Clear();
                    for (int col = 1; col <= 20; col++)
                    {
                        var val = worksheet.Cell(r, col).Value.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(val) && !headers.ContainsKey(val)) headers[val] = col;
                    }
                    if (headers.ContainsKey("Ø§Ù„ÙƒÙˆØ¯") || headers.ContainsKey("Code") || headers.ContainsKey("Ø§Ù„Ø§Ø³Ù…") || headers.ContainsKey("Name") ||
                        headers.ContainsKey("Full Name") || headers.ContainsKey("User Code") || headers.ContainsKey("FullName") || headers.ContainsKey("UserCode"))
                    {
                        headerRow = r;
                        break;
                    }
                }

                int? GetCol(params string[] possibleNames)
                {
                    foreach (var name in possibleNames)
                    {
                        if (headers.TryGetValue(name.Trim(), out var colIndex)) return colIndex;
                        foreach (var kvp in headers)
                        {
                            if (kvp.Key.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase) || name.Trim().Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                return kvp.Value;
                            }
                        }
                    }
                    return null;
                }

                var colUserCode = GetCol("Ø§Ù„ÙƒÙˆØ¯", "Code", "UserCode", "User Code", "ÙƒÙˆØ¯");
                var colFullName = GetCol("Ø§Ù„Ø§Ø³Ù…", "Name", "FullName", "Full Name", "UserName", "User Name", "Ø§Ø³Ù…");
                var colBranchName = GetCol("Ø§Ù„ÙØ±Ø¹", "ÙØ±Ø¹", "Ø§Ù„Ù…Ù†Ø·Ù‚Ø©", "Ù…Ù†Ø·Ù‚Ø©", "Branch", "Location", "BranchName", "Branch Name");
                var colJobTitle = GetCol("Ø§Ù„ÙˆØ¸ÙŠÙØ©", "ÙˆØ¸ÙŠÙØ©", "JobTitle", "Job Title", "Role", "RoleName", "Role Name");
                var colEmail = GetCol("Ø§Ù„Ø¨Ø±ÙŠØ¯", "Ø§ÙŠÙ…ÙŠÙ„", "Ø¥ÙŠÙ…ÙŠÙ„", "Email", "E-mail", "Mail");
                var colPhone = GetCol("Ø§Ù„ØªÙ„ÙŠÙÙˆÙ†", "ØªÙ„ÙŠÙÙˆÙ†", "Ù…ÙˆØ¨Ø§ÙŠÙ„", "Ø§Ù„Ù‡Ø§ØªÙ", "Ø±Ù‚Ù…", "Phone", "PhoneNumber", "Phone Number", "Mobile");

                var missingColumns = new List<string>();
                if (colFullName == null) missingColumns.Add("FullName (الاسم)");
                if (colUserCode == null) missingColumns.Add("UserCode (الكود)");
                if (colBranchName == null) missingColumns.Add("BranchName (الفرع/المنطقة)");
                if (colJobTitle == null) missingColumns.Add("JobTitle (الوظيفة/Role)");
                if (colEmail == null) missingColumns.Add("Email (البريد)");
                if (colPhone == null) missingColumns.Add("Phone (التليفون/الموبايل)");

                if (missingColumns.Any())
                {
                    var missingStr = string.Join(", ", missingColumns);
                    return Json(new { 
                        success = false, 
                        message = $"الملف المرفوع تنقصه الأعمدة التالية أو لم يتم التعرف عليها: <br/><strong class='text-rose-600'>{missingStr}</strong>.<br/><br/>" +
                                  "الأعمدة المطلوبة والمسميات المقبولة في ملف المتدربين هي:<br/>" +
                                  "1. <b>الاسم</b> (Name أو Full Name أو الاسم)<br/>" +
                                  "2. <b>الكود</b> (Code أو UserCode أو الكود)<br/>" +
                                  "3. <b>الفرع/المنطقة</b> (Branch أو Location أو الفرع أو المنطقة)<br/>" +
                                  "4. <b>الوظيفة</b> (JobTitle أو Role أو الوظيفة)<br/>" +
                                  "5. <b>البريد</b> (Email أو البريد)<br/>" +
                                  "6. <b>التليفون</b> (Phone أو Mobile أو التليفون أو رقم)"
                    });
                }

                int nameColIndex = colFullName.Value;
                int codeColIndex = colUserCode.Value;
                int jobColIndex = colJobTitle.Value;
                int branchColIndex = colBranchName.Value;
                int emailColIndex = colEmail.Value;
                int phoneColIndex = colPhone.Value;

                var initSql = @"
                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CompanyTrainees')
                    BEGIN
                        CREATE TABLE CompanyTrainees (
                            Id INT IDENTITY(1,1) PRIMARY KEY,
                            CompanyId INT NOT NULL,
                            FullName NVARCHAR(255) NOT NULL,
                            UserCode NVARCHAR(50),
                            JobTitle NVARCHAR(100),
                            BranchName NVARCHAR(100),
                            Email NVARCHAR(255),
                            Phone NVARCHAR(50),
                            CreatedAt DATETIME DEFAULT GETDATE()
                        );
                    END
                    IF NOT EXISTS(SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UserAttendance') AND name = 'CompanyTraineeId')
                    BEGIN
                        ALTER TABLE UserAttendance ADD CompanyTraineeId INT NULL;
                    END
                ";
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await conn.ExecuteAsync(initSql);

                var rows = worksheet.RowsUsed().Skip(headerRow); // Skip headers

                int added = 0;
                int updated = 0;

                foreach (var row in rows)
                {
                    var userCode = row.Cell(codeColIndex).Value.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(userCode)) continue;

                    var fullName = row.Cell(nameColIndex).Value.ToString().Trim();
                    var branchName = row.Cell(branchColIndex).Value.ToString().Trim();
                    var jobTitle = row.Cell(jobColIndex).Value.ToString().Trim();
                    var email = row.Cell(emailColIndex).Value.ToString().Trim();
                    var phone = row.Cell(phoneColIndex).Value.ToString().Trim();

                    var existingId = await conn.QueryFirstOrDefaultAsync<int?>(
                        "SELECT Id FROM CompanyTrainees WHERE CompanyId = @CompanyId AND UserCode = @UserCode",
                        new { CompanyId = companyId, UserCode = userCode }
                    );

                    if (existingId.HasValue)
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE CompanyTrainees 
                            SET FullName = @FullName, JobTitle = @JobTitle, BranchName = @BranchName, Email = @Email, Phone = @Phone 
                            WHERE Id = @Id",
                            new { Id = existingId.Value, FullName = fullName, JobTitle = jobTitle, BranchName = branchName, Email = email, Phone = phone });
                        updated++;
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO CompanyTrainees (CompanyId, FullName, UserCode, JobTitle, BranchName, Email, Phone) 
                            VALUES (@CompanyId, @FullName, @UserCode, @JobTitle, @BranchName, @Email, @Phone)",
                            new { CompanyId = companyId, FullName = string.IsNullOrWhiteSpace(fullName) ? userCode : fullName, UserCode = userCode, JobTitle = jobTitle, BranchName = branchName, Email = email, Phone = phone });
                        added++;
                    }
                }

                return Json(new { success = true, message = $"Import complete! Added {added} new trainees and updated {updated} existing." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpGet]
        public IActionResult DownloadTraineesTemplate()
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Trainees Template");
                
                // Add header row
                worksheet.Cell(1, 1).Value = "Full Name";
                worksheet.Cell(1, 2).Value = "User Code";
                worksheet.Cell(1, 3).Value = "Job Title";
                worksheet.Cell(1, 4).Value = "Branch";
                worksheet.Cell(1, 5).Value = "Email";
                worksheet.Cell(1, 6).Value = "Phone";

                // Format header row to look professional
                var headerRange = worksheet.Range("A1:F1");
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b"); // Slate 800
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                
                // Set borders for first 15 rows for clean entry appearance
                for (int row = 2; row <= 15; row++)
                {
                    for (int col = 1; col <= 6; col++)
                    {
                        worksheet.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        worksheet.Cell(row, col).Style.Border.OutsideBorderColor = XLColor.FromHtml("#cbd5e1"); // Slate 300
                    }
                }
                
                // Pre-adjust column widths
                worksheet.Column(1).Width = 30; // Full Name
                worksheet.Column(2).Width = 15; // User Code
                worksheet.Column(3).Width = 25; // Job Title
                worksheet.Column(4).Width = 20; // Branch
                worksheet.Column(5).Width = 30; // Email
                worksheet.Column(6).Width = 20; // Phone

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Trainees_Import_Template.xlsx");
                }
            }
        }

        [HttpGet]
        public IActionResult DownloadPersonnelTemplate()
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Personnel Template");
                
                // Add header row
                worksheet.Cell(1, 1).Value = "UserName";
                worksheet.Cell(1, 2).Value = "Email";
                worksheet.Cell(1, 3).Value = "Password";
                worksheet.Cell(1, 4).Value = "Phone";
                worksheet.Cell(1, 5).Value = "RoleName";
                worksheet.Cell(1, 6).Value = "UserCode";
                worksheet.Cell(1, 7).Value = "BranchName";
                worksheet.Cell(1, 8).Value = "ShiftId";

                // Format header row to look professional
                var headerRange = worksheet.Range("A1:H1");
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#0f172a"); // Slate 900
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                
                // Set borders for first 15 rows for clean entry appearance
                for (int row = 2; row <= 15; row++)
                {
                    for (int col = 1; col <= 8; col++)
                    {
                        worksheet.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        worksheet.Cell(row, col).Style.Border.OutsideBorderColor = XLColor.FromHtml("#cbd5e1"); // Slate 300
                    }
                }
                
                // Pre-adjust column widths
                worksheet.Column(1).Width = 25; // UserName
                worksheet.Column(2).Width = 30; // Email
                worksheet.Column(3).Width = 18; // Password
                worksheet.Column(4).Width = 18; // Phone
                worksheet.Column(5).Width = 18; // RoleName
                worksheet.Column(6).Width = 15; // UserCode
                worksheet.Column(7).Width = 20; // BranchName
                worksheet.Column(8).Width = 15; // ShiftId

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Personnel_Import_Template.xlsx");
                }
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetTraineeDetailsByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return Json(new { exists = false });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                var user = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT u.Id, u.FullName, u.UserName, u.Email, u.PhoneNumber, r.Name AS RoleName, b.BranchName
                    FROM AspNetUsers u
                    LEFT JOIN AspNetUserRoles ur ON u.Id = ur.UserId
                    LEFT JOIN AspNetRoles r ON ur.RoleId = r.Id
                    LEFT JOIN Branches b ON u.BranchId = b.Id
                    WHERE u.UserCode = @Code", new { Code = code.Trim() });

                if (user != null)
                {
                    string nameVal = user.FullName;
                    if (string.IsNullOrWhiteSpace(nameVal))
                    {
                        nameVal = user.UserName;
                    }

                    return Json(new { 
                        exists = true, 
                        fullName = nameVal ?? "", 
                        email = user.Email ?? "", 
                        phone = user.PhoneNumber ?? "", 
                        jobTitle = user.RoleName ?? "Trainee",
                        branchName = user.BranchName ?? ""
                    });
                }
            }
            catch { /* fallback */ }

            return Json(new { exists = false });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddCompanyTraineeManually(int companyId, string fullName, string userCode, string jobTitle, string branchName, string email, string phone)
        {
            if (companyId <= 0)
            {
                return Json(new { success = false, message = "Invalid Company selected." });
            }
            if (string.IsNullOrWhiteSpace(userCode))
            {
                return Json(new { success = false, message = "User Code is required." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);

                // 1. Ensure Table Exists
                var initSql = @"
                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CompanyTrainees')
                    BEGIN
                        CREATE TABLE CompanyTrainees (
                            Id INT IDENTITY(1,1) PRIMARY KEY,
                            CompanyId INT NOT NULL,
                            FullName NVARCHAR(255) NOT NULL,
                            UserCode NVARCHAR(50),
                            JobTitle NVARCHAR(100),
                            BranchName NVARCHAR(100),
                            Email NVARCHAR(255),
                            Phone NVARCHAR(50),
                            CreatedAt DATETIME DEFAULT GETDATE()
                        );
                    END
                ";
                await conn.ExecuteAsync(initSql);

                // 2. Check if user already exists
                var existingId = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT Id FROM CompanyTrainees WHERE CompanyId = @CompanyId AND UserCode = @UserCode",
                    new { CompanyId = companyId, UserCode = userCode.Trim() }
                );

                string finalMessage = "";

                if (existingId.HasValue)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE CompanyTrainees 
                        SET FullName = @FullName, JobTitle = @JobTitle, BranchName = @BranchName, Email = @Email, Phone = @Phone 
                        WHERE Id = @Id",
                        new { Id = existingId.Value, FullName = fullName.Trim(), JobTitle = jobTitle?.Trim(), BranchName = branchName?.Trim(), Email = email?.Trim(), Phone = phone?.Trim() });
                    finalMessage = $"Existing trainee '{fullName}' updated successfully.";
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO CompanyTrainees (CompanyId, FullName, UserCode, JobTitle, BranchName, Email, Phone) 
                        VALUES (@CompanyId, @FullName, @UserCode, @JobTitle, @BranchName, @Email, @Phone)",
                        new { CompanyId = companyId, FullName = string.IsNullOrWhiteSpace(fullName) ? userCode.Trim() : fullName.Trim(), UserCode = userCode.Trim(), JobTitle = jobTitle?.Trim(), BranchName = branchName?.Trim(), Email = email?.Trim(), Phone = phone?.Trim() });
                    finalMessage = $"Added new trainee '{fullName}' successfully.";
                }

                return Json(new { success = true, message = finalMessage });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Branches()
        {
            var branches = await _examService.GetAllBranchesAsync();
            return View(branches);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddBranch(string name, string code)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Json(new { success = false, message = "Branch name is required." });

            try
            {
                using var conn = new SqlConnection(_connectionString);
                // Check if branch name already exists
                var exists = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Branches WHERE BranchName = @Name", new { Name = name.Trim() });
                if (exists > 0)
                    return Json(new { success = false, message = "Branch with this name already exists." });

                await conn.ExecuteAsync("INSERT INTO Branches (BranchName, BranchCode, IsActive) VALUES (@Name, @Code, 1)", 
                    new { Name = name.Trim(), Code = code?.Trim() });
                return Json(new { success = true, message = "Branch created successfully." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding branch: {ex.Message}");
                return Json(new { success = false, message = "An error occurred while adding the branch." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditBranch(int id, string name, string code)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Json(new { success = false, message = "Branch name is required." });

            try
            {
                using var conn = new SqlConnection(_connectionString);
                var exists = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Branches WHERE BranchName = @Name AND Id != @Id", new { Name = name.Trim(), Id = id });
                if (exists > 0)
                    return Json(new { success = false, message = "Another branch with this name already exists." });

                await conn.ExecuteAsync("UPDATE Branches SET BranchName = @Name, BranchCode = @Code WHERE Id = @Id", 
                    new { Id = id, Name = name.Trim(), Code = code?.Trim() });
                return Json(new { success = true, message = "Branch updated successfully." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error editing branch: {ex.Message}");
                return Json(new { success = false, message = "An error occurred while updating the branch." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBranch(int id)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                
                // Check if users exist in this branch
                var usersCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE BranchId = @Id", new { Id = id });
                if (usersCount > 0)
                    return Json(new { success = false, message = $"Cannot delete: {usersCount} users are assigned to this branch." });

                // Delete branch
                await conn.ExecuteAsync("DELETE FROM Branches WHERE Id = @Id", new { Id = id });
                return Json(new { success = true, message = "Branch deleted successfully." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting branch: {ex.Message}");
                return Json(new { success = false, message = "An error occurred while deleting the branch." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MergeBranch(int sourceId, int targetId)
        {
            if (sourceId == targetId)
                return Json(new { success = false, message = "Cannot merge a branch into itself." });

            try
            {
                using var conn = new SqlConnection(_connectionString);
                
                // Move users
                await conn.ExecuteAsync("UPDATE AspNetUsers SET BranchId = @TargetId WHERE BranchId = @SourceId", new { TargetId = targetId, SourceId = sourceId });
                await conn.ExecuteAsync("UPDATE CompanyTrainees SET BranchName = (SELECT BranchName FROM Branches WHERE Id = @TargetId) WHERE BranchName = (SELECT BranchName FROM Branches WHERE Id = @SourceId)", new { TargetId = targetId, SourceId = sourceId });
                
                // Delete source branch
                await conn.ExecuteAsync("DELETE FROM Branches WHERE Id = @SourceId", new { SourceId = sourceId });
                
                return Json(new { success = true, message = "Branch merged successfully." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error merging branch: {ex.Message}");
                return Json(new { success = false, message = "An error occurred while merging." });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadCertificatesOnlyExcel(IFormFile excelFile, int? examId = null, int? waveId = null)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "Please upload an Excel file." });
            }

            try
            {
                using var memoryStream = new MemoryStream();
                await excelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using var workbook = new XLWorkbook(memoryStream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return Json(new { success = false, message = "Excel file has no worksheets." });

                var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var headerRow = 1;

                // Try to find the header row in the first 3 rows
                for (int r = 1; r <= 3; r++)
                {
                    headers.Clear();
                    for (int col = 1; col <= 20; col++)
                    {
                        var val = worksheet.Cell(r, col).Value.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(val) && !headers.ContainsKey(val)) headers[val] = col;
                    }
                    if (headers.ContainsKey("Code") || headers.ContainsKey("UserCode") || headers.ContainsKey("User Code") || 
                        headers.ContainsKey("الرمز") || headers.ContainsKey("الكود") || headers.ContainsKey("كود") || 
                        headers.ContainsKey("Certificate") || headers.ContainsKey("CertificateCode") || headers.ContainsKey("الشهادة") || 
                        headers.ContainsKey("كود الشهادة"))
                    {
                        headerRow = r;
                        break;
                    }
                }

                int? GetCol(params string[] possibleNames)
                {
                    foreach (var name in possibleNames)
                    {
                        if (headers.TryGetValue(name.Trim(), out var colIndex)) return colIndex;
                        foreach (var kvp in headers)
                        {
                            if (kvp.Key.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase) || name.Trim().Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                return kvp.Value;
                            }
                        }
                    }
                    return null;
                }

                var colUserCode = GetCol("Code", "UserCode", "User Code", "الكود", "كود", "كود الطالب");
                var colCertificateCode = GetCol("Certificate", "CertificateCode", "Certificate Code", "الشهادة", "كود الشهادة", "رقم الشهادة");
                var colScore = GetCol("Score", "الدرجة", "النسبة", "النسبه", "النسبة المئوية", "الدرجة المئوية", "درجة", "نسبة", "نسبه", "Score %", "Percentage");
                var colWaveName = GetCol("Wave", "الويف", "الدورة", "المجموعة", "WaveName", "Wave Name");

                if (colUserCode == null || colCertificateCode == null)
                {
                    return Json(new { 
                        success = false, 
                        message = "الملف المرفوع يجب أن يحتوي على عمودين على الأقل: كود المستخدم (UserCode) وكود الشهادة (CertificateCode)." 
                    });
                }

                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;
                int updatedCount = 0;
                var skippedCodes = new List<string>();

                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                for (int row = headerRow + 1; row <= lastRow; row++)
                {
                    var rawUserCode = worksheet.Cell(row, colUserCode.Value).Value.ToString()?.Trim();
                    var rawCertCode = worksheet.Cell(row, colCertificateCode.Value).Value.ToString()?.Trim();
                    var rawWaveName = colWaveName != null ? worksheet.Cell(row, colWaveName.Value).Value.ToString()?.Trim() : null;
                    var rawScore = colScore != null ? worksheet.Cell(row, colScore.Value).Value.ToString()?.Trim() : null;

                    if (string.IsNullOrWhiteSpace(rawUserCode)) continue;

                    // Normalizing double/scientific notation for codes (like 10243.0 or 1.23E+4)
                    if (double.TryParse(rawUserCode, out var codeDouble))
                    {
                        rawUserCode = ((long)codeDouble).ToString();
                    }

                    // 1. Find user by UserCode in AspNetUsers
                    var user = _userManager.Users.FirstOrDefault(x => x.UserCode == rawUserCode);
                    if (user != null)
                    {
                        decimal? parsedScore = null;
                        if (!string.IsNullOrWhiteSpace(rawScore))
                        {
                            var cleanScoreStr = rawScore.Replace("%", "").Trim();
                            if (decimal.TryParse(cleanScoreStr, out var sVal))
                            {
                                parsedScore = sVal;
                            }
                        }

                        // Update AspNetUsers CertificateCode as a general field (optional/fallback)
                        user.CertificateCode = rawCertCode;
                        user.CertificateScore = parsedScore;
                        await _userManager.UpdateAsync(user);

                        // 2. Resolve target WaveId
                        int resolvedWaveId = 0;
                        if (!string.IsNullOrWhiteSpace(rawWaveName))
                        {
                            // Check if the Wave exists
                            var dbWaveId = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT Id FROM dbo.TrainingWaves WHERE WaveName = @WaveName",
                                new { WaveName = rawWaveName });

                            if (dbWaveId == null)
                            {
                                // Create it!
                                dbWaveId = await conn.QueryFirstOrDefaultAsync<int>(@"
                                    INSERT INTO dbo.TrainingWaves (WaveName, StartDate) 
                                    VALUES (@WaveName, @StartDate);
                                    SELECT CAST(SCOPE_IDENTITY() as int);",
                                    new { WaveName = rawWaveName, StartDate = DateTime.Now });
                            }
                            resolvedWaveId = dbWaveId ?? 0;
                        }

                        if (resolvedWaveId <= 0)
                        {
                            resolvedWaveId = waveId ?? 0;
                        }

                        if (resolvedWaveId <= 0 && examId.HasValue && examId.Value > 0)
                        {
                            resolvedWaveId = await conn.QueryFirstOrDefaultAsync<int>(
                                "SELECT WaveId FROM Exams WHERE Id = @ExamId",
                                new { ExamId = examId.Value });
                        }

                        if (resolvedWaveId <= 0)
                        {
                            resolvedWaveId = await conn.QueryFirstOrDefaultAsync<int>(
                                "SELECT TOP 1 Id FROM TrainingWaves ORDER BY StartDate DESC");
                        }

                        if (resolvedWaveId > 0)
                        {
                            // A. Check/insert UserWaves entry (assign user to wave)
                            var userWaveExists = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT WaveId FROM dbo.UserWaves WHERE UserId = @UserId AND WaveId = @WaveId",
                                new { UserId = user.Id, WaveId = resolvedWaveId });

                            if (userWaveExists == null)
                            {
                                await conn.ExecuteAsync(@"
                                    INSERT INTO dbo.UserWaves (UserId, WaveId, JoinDate, IsActive)
                                    VALUES (@UserId, @WaveId, @JoinDate, 1)",
                                    new { UserId = user.Id, WaveId = resolvedWaveId, JoinDate = DateTime.Now });
                            }
                            else
                            {
                                await conn.ExecuteAsync(@"
                                    UPDATE dbo.UserWaves 
                                    SET IsActive = 1 
                                    WHERE UserId = @UserId AND WaveId = @WaveId",
                                    new { UserId = user.Id, WaveId = resolvedWaveId });
                            }

                            // B. Insert or update UserWaveCertificates table
                            var certExists = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT Id FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                                new { UserId = user.Id, WaveId = resolvedWaveId });

                            if (certExists != null)
                            {
                                await conn.ExecuteAsync(@"
                                    UPDATE dbo.UserWaveCertificates 
                                    SET CertificateCode = @CertCode, Score = @Score
                                    WHERE UserId = @UserId AND WaveId = @WaveId",
                                    new { CertCode = rawCertCode, Score = parsedScore, UserId = user.Id, WaveId = resolvedWaveId });
                            }
                            else
                            {
                                await conn.ExecuteAsync(@"
                                    INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, Score, CreatedAt)
                                    VALUES (@UserId, @WaveId, @CertCode, @Score, @CreatedAt)",
                                    new { UserId = user.Id, WaveId = resolvedWaveId, CertCode = rawCertCode, Score = parsedScore, CreatedAt = DateTime.Now });
                            }
                        }

                        updatedCount++;
                    }
                    else
                    {
                        skippedCodes.Add(rawUserCode);
                    }
                }

                var msg = $"تم تحديث كود الشهادة بنجاح لعدد {updatedCount} مستخدم.";
                if (skippedCodes.Any())
                {
                    msg += $" تم تخطي الكودات التالية لعدم وجودها بالنظام: {string.Join(", ", skippedCodes.Take(10))}";
                    if (skippedCodes.Count > 10) msg += $" (وآخرين...)";
                }

                return Json(new { success = true, message = msg });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء قراءة الملف: " + ex.Message });
            }
        }

        [HttpGet("Admin/Assignments")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Assignments()
        {
            ViewBag.Title = "Wave Assignments";
            using var conn = new SqlConnection(_connectionString);
            var waves = await conn.QueryAsync<dynamic>("SELECT Id, WaveName FROM dbo.TrainingWaves ORDER BY Id DESC");
            ViewBag.Waves = waves;
            return View();
        }

        [HttpGet("Admin/GetAssignmentAttempts/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAssignmentAttempts(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            var attempts = await conn.QueryAsync<dynamic>(@"
                SELECT att.Id AS AttemptId, att.Score, att.Status, att.StartTime, att.EndTime, 
                       u.FullName, u.Email, u.UserName, u.UserCode,
                       r.Name AS RoleName
                FROM dbo.StudentAssignmentAttempts att
                INNER JOIN dbo.AspNetUsers u ON att.UserId = u.Id
                LEFT JOIN dbo.AspNetUserRoles ur ON u.Id = ur.UserId
                LEFT JOIN dbo.AspNetRoles r ON ur.RoleId = r.Id
                WHERE att.AssignmentId = @AssignmentId
                ORDER BY att.StartTime DESC",
                new { AssignmentId = id });

            var result = attempts.Select(x => new {
                attemptId = x.AttemptId,
                fullName = x.FullName,
                email = x.Email,
                userName = x.UserName,
                roleName = x.RoleName,
                userCode = x.UserCode,
                score = x.Score,
                status = x.Status,
                startTime = x.StartTime != null ? ((DateTime)x.StartTime).ToString("yyyy-MM-dd HH:mm:ss") : null,
                endTime = x.EndTime != null ? ((DateTime)x.EndTime).ToString("yyyy-MM-dd HH:mm:ss") : null
            });

            return Json(result);
        }

        [HttpGet("Admin/ExportAssignmentAttemptsToExcel/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ExportAssignmentAttemptsToExcel(int id)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                
                var assignment = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT Title FROM dbo.Assignments WHERE Id = @Id", new { Id = id });
                string assignmentTitle = assignment?.Title ?? "Assignment";

                var attempts = await conn.QueryAsync<dynamic>(@"
                    SELECT att.Id AS AttemptId, att.Score, att.Status, att.StartTime, att.EndTime, 
                           u.FullName, u.Email, u.UserName, u.UserCode,
                           r.Name AS RoleName
                    FROM dbo.StudentAssignmentAttempts att
                    INNER JOIN dbo.AspNetUsers u ON att.UserId = u.Id
                    LEFT JOIN dbo.AspNetUserRoles ur ON u.Id = ur.UserId
                    LEFT JOIN dbo.AspNetRoles r ON ur.RoleId = r.Id
                    WHERE att.AssignmentId = @AssignmentId
                    ORDER BY att.StartTime DESC",
                    new { AssignmentId = id });

                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Assignment Attempts");
                    var currentRow = 1;

                    // Header Info
                    worksheet.Cell(currentRow, 1).Value = "Assignment:";
                    worksheet.Cell(currentRow, 2).Value = assignmentTitle;
                    currentRow++;
                    worksheet.Cell(currentRow, 1).Value = "Generated:";
                    worksheet.Cell(currentRow, 2).Value = DateTime.Now.ToString("g");
                    currentRow += 2;

                    string[] headers = { "Student Name", "Email", "User Code", "Role", "Status", "Start Time", "Submit Time", "Score" };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = worksheet.Cell(currentRow, i + 1);
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#4F46E5");
                        cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
                    }

                    foreach (var att in attempts)
                    {
                        currentRow++;
                        worksheet.Cell(currentRow, 1).Value = (string)(att.FullName ?? att.UserName);
                        worksheet.Cell(currentRow, 2).Value = (string)att.Email;
                        worksheet.Cell(currentRow, 3).Value = (string)(att.UserCode ?? "");
                        worksheet.Cell(currentRow, 4).Value = (string)(att.RoleName ?? "Pharmacist");
                        worksheet.Cell(currentRow, 5).Value = (string)att.Status;
                        worksheet.Cell(currentRow, 6).Value = att.StartTime != null ? ((DateTime)att.StartTime).ToString("yyyy-MM-dd HH:mm:ss") : "";
                        worksheet.Cell(currentRow, 7).Value = att.EndTime != null ? ((DateTime)att.EndTime).ToString("yyyy-MM-dd HH:mm:ss") : "";
                        worksheet.Cell(currentRow, 8).Value = att.Status == "Completed" ? $"{att.Score} pts" : "--";
                    }

                    worksheet.Columns().AdjustToContents();

                    using (var stream = new System.IO.MemoryStream())
                    {
                        workbook.SaveAs(stream);
                        var content = stream.ToArray();
                        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Assignment_Attempts_{id}.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                return BadRequest("Error exporting to Excel: " + ex.Message);
            }
        }

        [HttpPost("Admin/GetAssignmentsPaged")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAssignmentsPaged()
        {
            var draw = Request.Form["draw"].FirstOrDefault();
            var start = Request.Form["start"].FirstOrDefault();
            var length = Request.Form["length"].FirstOrDefault();
            var searchValue = Request.Form["search[value]"].FirstOrDefault();

            int pageSize = string.IsNullOrEmpty(length) ? 10 : Convert.ToInt32(length);
            int skip = string.IsNullOrEmpty(start) ? 0 : Convert.ToInt32(start);

            using var conn = new SqlConnection(_connectionString);

            string searchFilter = "";
            var parameters = new DynamicParameters();
            if (!string.IsNullOrEmpty(searchValue))
            {
                searchFilter = " WHERE a.Title LIKE @Search";
                parameters.Add("Search", "%" + searchValue + "%");
            }

            string countQuery = $@"
                SELECT COUNT(1) 
                FROM dbo.Assignments a 
                INNER JOIN dbo.TrainingWaves w ON a.WaveId = w.Id
                {searchFilter}";

            int recordsTotal = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Assignments");
            int recordsFiltered = await conn.ExecuteScalarAsync<int>(countQuery, parameters);

            string dataQuery = $@"
                SELECT a.Id, a.Title, w.WaveName, a.PharmacistMaxScore, a.AssistantMaxScore, 
                       a.ScheduledStartTime, a.ScheduledEndTime, a.CreatedAt
                FROM dbo.Assignments a
                INNER JOIN dbo.TrainingWaves w ON a.WaveId = w.Id
                {searchFilter}
                ORDER BY a.Id DESC
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

            parameters.Add("Skip", skip);
            parameters.Add("Take", pageSize);

            var list = await conn.QueryAsync<dynamic>(dataQuery, parameters);

            var result = list.Select(x => new {
                id = x.Id,
                title = x.Title,
                waveName = x.WaveName,
                pharmacistMaxScore = x.PharmacistMaxScore,
                assistantMaxScore = x.AssistantMaxScore,
                scheduledStartTime = x.ScheduledStartTime != null ? ((DateTime)x.ScheduledStartTime).ToString("yyyy-MM-dd HH:mm:ss") : null,
                scheduledEndTime = x.ScheduledEndTime != null ? ((DateTime)x.ScheduledEndTime).ToString("yyyy-MM-dd HH:mm:ss") : null,
                createdAt = x.CreatedAt != null ? ((DateTime)x.CreatedAt).ToString("yyyy-MM-dd HH:mm:ss") : null
            });

            return Json(new
            {
                draw = draw,
                recordsTotal = recordsTotal,
                recordsFiltered = recordsFiltered,
                data = result
            });
        }

        [HttpGet("Admin/GetAppCategories")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAppCategories()
        {
            using var conn = new SqlConnection(_connectionString);
            var categories = await conn.QueryAsync<dynamic>("SELECT DISTINCT Categories AS categories, Groups AS groups, Subcategories AS subcategories FROM dbo.[App Categories ] ORDER BY categories, groups, subcategories");
            return Json(categories);
        }

        [HttpGet("Admin/SearchItems")]
        [Authorize]
        public async Task<IActionResult> SearchItems(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return Json(new List<object>());
            using var conn = new SqlConnection(_connectionString);
            var items = await conn.QueryAsync<dynamic>("dbo.sp_GetItemsForSearch", new { SearchQuery = q }, commandType: CommandType.StoredProcedure);
            
            var result = items.Select(x => new {
                itemCode = x.ItemCode,
                description = x.Description,
                descriptionAr = x.DescriptionAr,
                category = x.Category,
                group = x.Group,
                subcategory = x.Subcategory,
                itemDefinition = x.ItemDefinition
            });
            
            return Json(result);
        }

        [HttpPost("Admin/CreateAssignment")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateAssignment([FromBody] CreateAssignmentDto model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Title) || model.WaveId <= 0)
            {
                return Json(new { success = false, message = "Invalid data. Please fill required fields." });
            }

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                // Insert Assignment
                string insertAssignSql = @"
                    INSERT INTO dbo.Assignments (Title, WaveId, PharmacistMaxScore, AssistantMaxScore, ScheduledStartTime, ScheduledEndTime, CreatedAt)
                    OUTPUT INSERTED.Id
                    VALUES (@Title, @WaveId, @PharmacistMaxScore, @AssistantMaxScore, @ScheduledStartTime, @ScheduledEndTime, GETDATE());";

                int assignmentId = await conn.QuerySingleAsync<int>(insertAssignSql, new
                {
                    model.Title,
                    model.WaveId,
                    model.PharmacistMaxScore,
                    model.AssistantMaxScore,
                    model.ScheduledStartTime,
                    model.ScheduledEndTime
                }, transaction: transaction);

                // Insert Questions
                if (model.Questions != null && model.Questions.Any())
                {
                    string insertQuestionSql = @"
                        INSERT INTO dbo.AssignmentQuestions (AssignmentId, QuestionType, TargetRole, Points, CategoryName, GroupName, SubcategoryName, RequiredItemsCount, ItemDefinition, CorrectItemNo)
                        VALUES (@AssignmentId, @QuestionType, @TargetRole, @Points, @CategoryName, @GroupName, @SubcategoryName, @RequiredItemsCount, @ItemDefinition, @CorrectItemNo);";

                    foreach (var q in model.Questions)
                    {
                        await conn.ExecuteAsync(insertQuestionSql, new
                        {
                            AssignmentId = assignmentId,
                            q.QuestionType,
                            q.TargetRole,
                            q.Points,
                            q.CategoryName,
                            q.GroupName,
                            q.SubcategoryName,
                            q.RequiredItemsCount,
                            q.ItemDefinition,
                            q.CorrectItemNo
                        }, transaction: transaction);
                    }
                }

                transaction.Commit();
                return Json(new { success = true, message = "Assignment created successfully!" });
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost("Admin/DeleteAssignment/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteAssignment(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Assignments WHERE Id = @Id", new { Id = id });
                return Json(new { success = true, message = "Assignment deleted successfully." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }
    }

    internal static class BranchNameResolver
    {
        internal static int? ResolveBranchId(string excelBranch, IReadOnlyList<(int Id, string Name, string Code)> branches)
        {
            if (string.IsNullOrWhiteSpace(excelBranch) || branches == null || branches.Count == 0)
                return null;

            var raw = excelBranch.Trim();

            foreach (var (id, name, code) in branches)
            {
                if (!string.IsNullOrEmpty(code) && string.Equals(code, raw, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            foreach (var (id, name, _) in branches)
            {
                if (string.Equals(name, raw, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            var normQ = Normalize(raw);
            foreach (var (id, name, _) in branches)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var normB = Normalize(name);
                if (normQ.Equals(normB, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            foreach (var (id, name, code) in branches)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var normB = Normalize(name);
                if (normQ.Length < 2 || normB.Length < 2)
                    continue;
                if (normB.Contains(normQ, StringComparison.OrdinalIgnoreCase) || normQ.Contains(normB, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            if (normQ.Length >= 2)
            {
                foreach (var (id, _, code) in branches)
                {
                    if (string.IsNullOrEmpty(code) || code.Length < 2) continue;
                    if (code.Contains(raw, StringComparison.OrdinalIgnoreCase) || raw.Contains(code, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }


            var collapsedQ = RemoveAllWhitespace(normQ);
            if (collapsedQ.Length >= 2)
            {
                foreach (var (id, name, _) in branches)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    var collapsedB = RemoveAllWhitespace(Normalize(name));
                    if (collapsedB.Length < 2) continue;
                    if (collapsedB.Contains(collapsedQ, StringComparison.Ordinal) || collapsedQ.Contains(collapsedB, StringComparison.Ordinal))
                        return id;
                }
            }

            return null;
        }

        private static string RemoveAllWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            s = new string(s.Select(c => c is '\u00A0' or '\u2007' or '\u202F' ? ' ' : c).ToArray());
            s = new string(s.Where(c => c is not '\u200c' and not '\u200d' and not '\ufeff' and not '\u200e' and not '\u200f').ToArray());
            try
            {
                if (s.Length > 0)
                    s = s.Normalize(NormalizationForm.FormC);
            }
            catch { /* ignore invalid sequences */ }

            var chars = s.Where(c => c < '\u064B' || c > '\u065F').ToArray();
            s = new string(chars);
            const string al = "\u0627\u0644";
            while (s.StartsWith(al, StringComparison.Ordinal))
                s = s.Substring(al.Length).TrimStart();
            return s.Trim();
        }
    }

    public class CreateAssignmentDto
    {
        public string Title { get; set; }
        public int WaveId { get; set; }
        public decimal PharmacistMaxScore { get; set; }
        public decimal AssistantMaxScore { get; set; }
        public DateTime ScheduledStartTime { get; set; }
        public DateTime ScheduledEndTime { get; set; }
        public List<CreateAssignmentQuestionDto> Questions { get; set; } = new();
    }

    public class CreateAssignmentQuestionDto
    {
        public string QuestionType { get; set; } // "CategorySelect" or "ItemDefinitionMatch"
        public string TargetRole { get; set; } // "Pharmacist", "Assistant", "All"
        public decimal Points { get; set; }
        public string CategoryName { get; set; }
        public string GroupName { get; set; }
        public string SubcategoryName { get; set; }
        public int? RequiredItemsCount { get; set; }
        public string ItemDefinition { get; set; }
        public string CorrectItemNo { get; set; }
    }

    public class SyncRequest
    {
        public string Password { get; set; }
    }

    public class LocalItemDto
    {
        public string No_ { get; set; }
        public string Description { get; set; }
        public string Description2 { get; set; }
        public string StorageInstructions { get; set; }
        public string IncentiveValue { get; set; }
        public string Color { get; set; }
        public string ItemDefinition { get; set; }
        public DateTime? DateCreated { get; set; }
        public DateTime? LastDateModified { get; set; }
        public DateTime? LastSyncedAt { get; set; }
    }
}


