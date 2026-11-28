using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Exam.Controllers
{
    [Authorize]
    public class AssignmentsController : Controller
    {
        private readonly string _connectionString;

        public AssignmentsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        private static bool _assignmentFeedbackColumnsEnsured = false;
        private async Task EnsureAssignmentFeedbackColumnsAsync(SqlConnection conn)
        {
            if (_assignmentFeedbackColumnsEnsured) return;
            try
            {
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StudentAssignmentAttempts') AND name = 'TrainerComment')
                        ALTER TABLE dbo.StudentAssignmentAttempts ADD TrainerComment NVARCHAR(MAX) NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StudentAssignmentAttempts') AND name = 'TrainerCommentUpdatedAt')
                        ALTER TABLE dbo.StudentAssignmentAttempts ADD TrainerCommentUpdatedAt DATETIME NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StudentAssignmentAttempts') AND name = 'TrainerCommentBy')
                        ALTER TABLE dbo.StudentAssignmentAttempts ADD TrainerCommentBy NVARCHAR(256) NULL;
                ");
                _assignmentFeedbackColumnsEnsured = true;
            }
            catch { }
        }

        private string GetStudentRole()
        {
            var roleClaim = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value ?? "All";
            var r = roleClaim.ToLower();
            if (r.Contains("pharmacist") || r.Contains("صيدل") || r.Contains("doctor")) return "Pharmacist";
            if (r.Contains("assistant") || r.Contains("مساعد")) return "Assistant";
            return "All";
        }

        [HttpGet("Assignments")]
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Challenge();

            var studentRole = GetStudentRole();

            using var conn = new SqlConnection(_connectionString);
            
            // Get all enrolled waves for student
            var waves = (await conn.QueryAsync<dynamic>(@"
                SELECT uw.WaveId, w.WaveName 
                FROM dbo.UserWaves uw
                INNER JOIN dbo.TrainingWaves w ON uw.WaveId = w.Id
                WHERE uw.UserId = @UserId", 
                new { UserId = userId })).ToList();

            var waveIds = waves.Select(w => (int)w.WaveId).Distinct().ToList();

            if (!waveIds.Any())
            {
                var hasAnyAttempts = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(1) FROM dbo.StudentAssignmentAttempts WHERE UserId = @UserId", new { UserId = userId });
                if (hasAnyAttempts == 0)
                {
                    ViewBag.NoWave = true;
                    return View(new List<dynamic>());
                }
            }

            ViewBag.NoWave = false;
            var waveNames = waves.Select(w => (string)w.WaveName).Distinct().ToList();
            ViewBag.WaveName = waveNames.Any() ? (waveNames.Count == 1 ? waveNames.First() : string.Join(" | ", waveNames)) : "All Batches";
            ViewBag.StudentRole = studentRole;

            // List assignments for student across all enrolled waves, plus any with existing attempts
            var list = await conn.QueryAsync<dynamic>(@"
                SELECT a.Id, a.Title, a.Description, a.ScheduledStartTime, a.ScheduledEndTime, a.CreatedAt,
                       a.PharmacistMaxScore, a.AssistantMaxScore, a.WaveId,
                       ISNULL(w.WaveName, N'غير محدد') AS WaveName,
                       att.Status AS AttemptStatus, att.Score AS AttemptScore, att.Id AS AttemptId, att.TrainerComment
                FROM dbo.Assignments a
                LEFT JOIN dbo.TrainingWaves w ON a.WaveId = w.Id
                LEFT JOIN dbo.StudentAssignmentAttempts att ON a.Id = att.AssignmentId AND att.UserId = @UserId
                WHERE (a.WaveId IN @WaveIds OR a.Id IN (SELECT AssignmentId FROM dbo.StudentAssignmentAttempts WHERE UserId = @UserId))
                ORDER BY a.ScheduledStartTime DESC, a.Id DESC",
                new { WaveIds = waveIds.Any() ? waveIds : new List<int> { -1 }, UserId = userId });

            return View(list);
        }

        [HttpGet("Assignments/Take/{id}")]
        public async Task<IActionResult> Take(int id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Challenge();

            using var conn = new SqlConnection(_connectionString);

            var assignment = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM dbo.Assignments WHERE Id = @Id",
                new { Id = id });

            if (assignment == null) return NotFound();

            var now = DateTime.Now;
            if (now < (DateTime)assignment.ScheduledStartTime || now > (DateTime)assignment.ScheduledEndTime)
            {
                TempData["ErrorMessage"] = "Sorry, this assignment is currently unavailable.";
                return RedirectToAction("Index");
            }

            var studentRole = GetStudentRole();

            // Find or create attempt
            var attempt = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM dbo.StudentAssignmentAttempts 
                WHERE AssignmentId = @AssignmentId AND UserId = @UserId",
                new { AssignmentId = id, UserId = userId });

            int attemptId;
            if (attempt != null)
            {
                if ((string)attempt.Status == "Completed")
                {
                    return RedirectToAction("Result", new { id = (int)attempt.Id });
                }
                attemptId = (int)attempt.Id;
            }
            else
            {
                attemptId = await conn.QuerySingleAsync<int>(@"
                    INSERT INTO dbo.StudentAssignmentAttempts (AssignmentId, UserId, StartTime, Score, Status)
                    OUTPUT INSERTED.Id
                    VALUES (@AssignmentId, @UserId, GETDATE(), 0.00, 'InProgress')",
                    new { AssignmentId = id, UserId = userId });
            }

            // Get questions for assignment matching student's role or 'All'
            var questions = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, QuestionType, Points, CategoryName, GroupName, SubcategoryName, RequiredItemsCount, ItemDefinition,
                       CriteriaActiveIngredient, CriteriaManufacturer, CriteriaProductForm, 
                       CriteriaPriceMin, CriteriaPriceMax, CriteriaPriceOp, CriteriaKeyFeature, CriteriaHowToUse
                FROM dbo.AssignmentQuestions
                WHERE AssignmentId = @AssignmentId AND (TargetRole = @Role OR TargetRole = 'All')",
                new { AssignmentId = id, Role = studentRole })).ToList();

            // Get any already saved answers for this attempt
            var savedAnswers = (await conn.QueryAsync<dynamic>(@"
                SELECT QuestionId, SelectedItemNos FROM dbo.StudentAssignmentAnswers WHERE AttemptId = @AttemptId",
                new { AttemptId = attemptId })).ToDictionary(
                    x => (int)x.QuestionId,
                    x => (string)x.SelectedItemNos
                );

            ViewBag.Assignment = assignment;
            ViewBag.AttemptId = attemptId;
            ViewBag.StudentRole = studentRole;
            ViewBag.SavedAnswers = savedAnswers;

            return View(questions);
        }

        [HttpPost("Assignments/Submit")]
        public async Task<IActionResult> Submit([FromBody] SubmitAssignmentDto model)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Challenge();

            if (model == null || model.AttemptId <= 0)
            {
                return Json(new { success = false, message = "Invalid submission details." });
            }

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Check if attempt exists and belongs to user
            var attempt = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM dbo.StudentAssignmentAttempts WHERE Id = @Id AND UserId = @UserId",
                new { Id = model.AttemptId, UserId = userId });

            if (attempt == null)
            {
                return Json(new { success = false, message = "Attempt not found." });
            }

            if ((string)attempt.Status == "Completed")
            {
                return Json(new { success = true, message = "Assignment already submitted." });
            }

            using var transaction = conn.BeginTransaction();
            try
            {
                // Delete previous answers for this attempt just in case
                await conn.ExecuteAsync("DELETE FROM dbo.StudentAssignmentAnswers WHERE AttemptId = @AttemptId", 
                    new { AttemptId = model.AttemptId }, transaction: transaction);

                // Insert new answers
                if (model.Answers != null && model.Answers.Any())
                {
                    string insertSql = @"
                        INSERT INTO dbo.StudentAssignmentAnswers (AttemptId, QuestionId, SelectedItemNos, IsCorrect, EarnedPoints)
                        VALUES (@AttemptId, @QuestionId, @SelectedItemNos, 0, 0.00)";

                    foreach (var ans in model.Answers)
                    {
                        await conn.ExecuteAsync(insertSql, new
                        {
                            AttemptId = model.AttemptId,
                            ans.QuestionId,
                            SelectedItemNos = ans.SelectedItemNos ?? ""
                        }, transaction: transaction);
                    }
                }

                transaction.Commit();

                // Grade using stored procedure
                await conn.ExecuteAsync("dbo.sp_GradeAssignmentAttempt", 
                    new { AttemptId = model.AttemptId }, 
                    commandType: CommandType.StoredProcedure);

                return Json(new { success = true, message = "Assignment submitted and graded successfully!" });
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                return Json(new { success = false, message = "Error submitting: " + ex.Message });
            }
        }

        [HttpGet("Assignments/Result/{id}")]
        public async Task<IActionResult> Result(int id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Challenge();

            // Check if user is staff/admin
            var isAdmin = User.IsInRole("Admin") || 
                          User.IsInRole("HR") || 
                          User.IsInRole("Human Resources") || 
                          User.IsInRole("Branch Manager") || 
                          User.IsInRole("Branch Supervisor") || 
                          User.IsInRole("SoftSkills Specialist");

            using var conn = new SqlConnection(_connectionString);
            await EnsureAssignmentFeedbackColumnsAsync(conn);

            var attempt = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT att.*, a.Title, a.PharmacistMaxScore, a.AssistantMaxScore, u.FullName AS StudentName
                FROM dbo.StudentAssignmentAttempts att
                INNER JOIN dbo.Assignments a ON att.AssignmentId = a.Id
                INNER JOIN dbo.AspNetUsers u ON att.UserId = u.Id
                WHERE att.Id = @Id",
                new { Id = id });

            if (attempt == null) return NotFound();

            // Allow the trainee who completed this assignment OR Admin/Supervisor
            if (!isAdmin && (string)attempt.UserId != userId)
            {
                return Forbid();
            }

            // Find the student's role in the database rather than using the current Admin's identity claims
            string studentRole = "All";
            var roleName = await conn.QueryFirstOrDefaultAsync<string>(@"
                SELECT r.Name 
                FROM dbo.AspNetRoles r
                INNER JOIN dbo.AspNetUserRoles ur ON r.Id = ur.RoleId
                WHERE ur.UserId = @UserId", 
                new { UserId = (string)attempt.UserId });

            if (!string.IsNullOrEmpty(roleName))
            {
                var r = roleName.ToLower();
                if (r.Contains("pharmacist") || r.Contains("صيدل") || r.Contains("doctor")) studentRole = "Pharmacist";
                else if (r.Contains("assistant") || r.Contains("مساعد")) studentRole = "Assistant";
            }

            // Fetch questions and student answers
            var details = await conn.QueryAsync<dynamic>(@"
                SELECT q.Id AS QuestionId, q.QuestionType, q.Points, q.CategoryName, q.GroupName, q.SubcategoryName, q.RequiredItemsCount, q.ItemDefinition, q.CorrectItemNo,
                       q.CriteriaActiveIngredient, q.CriteriaManufacturer, q.CriteriaProductForm, 
                       q.CriteriaPriceMin, q.CriteriaPriceMax, q.CriteriaPriceOp, q.CriteriaKeyFeature, q.CriteriaHowToUse,
                       ans.SelectedItemNos, ans.IsCorrect, ans.EarnedPoints
                FROM dbo.AssignmentQuestions q
                LEFT JOIN dbo.StudentAssignmentAnswers ans ON q.Id = ans.QuestionId AND ans.AttemptId = @AttemptId
                WHERE q.AssignmentId = @AssignmentId AND (q.TargetRole = @Role OR q.TargetRole = 'All')",
                new { AttemptId = id, AssignmentId = (int)attempt.AssignmentId, Role = studentRole });

            ViewBag.Attempt = attempt;
            ViewBag.StudentRole = studentRole;
            ViewBag.IsAdmin = isAdmin;
            ViewBag.CurrentUserId = userId;

            return View(details);
        }

        public class SaveAssignmentFeedbackDto
        {
            public int AttemptId { get; set; }
            public string? Comment { get; set; }
        }

        [HttpPost("Assignments/SaveFeedback")]
        [Authorize(Roles = "Admin,HR,Human Resources,Branch Manager,Branch Supervisor,SoftSkills Specialist")]
        public async Task<IActionResult> SaveFeedback([FromBody] SaveAssignmentFeedbackDto model)
        {
            if (model == null || model.AttemptId <= 0)
                return BadRequest(new { success = false, message = "بيانات غير صالحة." });

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await EnsureAssignmentFeedbackColumnsAsync(conn);

                var userName = User.Identity?.Name ?? User.FindFirst(ClaimTypes.Name)?.Value ?? "Admin";

                int rows = await conn.ExecuteAsync(@"
                    UPDATE dbo.StudentAssignmentAttempts
                    SET TrainerComment = @Comment,
                        TrainerCommentUpdatedAt = GETDATE(),
                        TrainerCommentBy = @Trainer
                    WHERE Id = @AttemptId",
                    new { Comment = model.Comment?.Trim(), Trainer = userName, AttemptId = model.AttemptId });

                if (rows > 0)
                {
                    return Json(new { success = true, message = "تم حفظ ملاحظات وتوجيهات المدرب بنجاح." });
                }
                return Json(new { success = false, message = "لم يتم العثور على محاولة التكليف." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ: " + ex.Message });
            }
        }

        [HttpGet("Assignments/SearchItems")]
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
                itemDefinition = x.ItemDefinition,
                activeIngredient = x.ActiveIngredient,
                manufacturerName = x.ManufacturerName,
                productForm = x.ProductForm,
                howToUse = x.HowToUse,
                keyFeature = x.KeyFeature,
                unitPrice = x.UnitPrice
            });
            
            return Json(result);
        }
    }

    public class SubmitAssignmentDto
    {
        public int AttemptId { get; set; }
        public List<SubmitAnswerDto> Answers { get; set; } = new();
    }

    public class SubmitAnswerDto
    {
        public int QuestionId { get; set; }
        public string SelectedItemNos { get; set; }
    }
}
