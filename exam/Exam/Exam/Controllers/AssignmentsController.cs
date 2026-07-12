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
            
            // Get active wave for student
            var wave = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT uw.WaveId, w.WaveName 
                FROM dbo.UserWaves uw
                INNER JOIN dbo.TrainingWaves w ON uw.WaveId = w.Id
                WHERE uw.UserId = @UserId AND uw.IsActive = 1", 
                new { UserId = userId });

            if (wave == null)
            {
                ViewBag.NoWave = true;
                return View(new List<dynamic>());
            }

            ViewBag.NoWave = false;
            ViewBag.WaveName = wave.WaveName;
            ViewBag.StudentRole = studentRole;

            // List active assignments for this wave
            var list = await conn.QueryAsync<dynamic>(@"
                SELECT a.Id, a.Title, a.ScheduledStartTime, a.ScheduledEndTime, a.CreatedAt,
                       a.PharmacistMaxScore, a.AssistantMaxScore,
                       att.Status AS AttemptStatus, att.Score AS AttemptScore, att.Id AS AttemptId
                FROM dbo.Assignments a
                LEFT JOIN dbo.StudentAssignmentAttempts att ON a.Id = att.AssignmentId AND att.UserId = @UserId
                WHERE a.WaveId = @WaveId AND (att.Status IS NULL OR att.Status <> 'Completed')
                ORDER BY a.ScheduledStartTime DESC",
                new { WaveId = (int)wave.WaveId, UserId = userId });

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
                    TempData["ErrorMessage"] = "You have already completed this assignment.";
                    return RedirectToAction("Index");
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
                SELECT Id, QuestionType, Points, CategoryName, GroupName, SubcategoryName, RequiredItemsCount, ItemDefinition
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

            // Restrict Result view to Admin/HR/Branch Manager roles
            var isAdmin = User.IsInRole("Admin") || 
                          User.IsInRole("HR") || 
                          User.IsInRole("Human Resources") || 
                          User.IsInRole("Branch Manager") || 
                          User.IsInRole("SoftSkills Specialist");

            if (!isAdmin)
            {
                return Forbid();
            }

            using var conn = new SqlConnection(_connectionString);

            var attempt = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT att.*, a.Title, a.PharmacistMaxScore, a.AssistantMaxScore, u.FullName AS StudentName
                FROM dbo.StudentAssignmentAttempts att
                INNER JOIN dbo.Assignments a ON att.AssignmentId = a.Id
                INNER JOIN dbo.AspNetUsers u ON att.UserId = u.Id
                WHERE att.Id = @Id",
                new { Id = id });

            if (attempt == null) return NotFound();

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
                       ans.SelectedItemNos, ans.IsCorrect, ans.EarnedPoints
                FROM dbo.AssignmentQuestions q
                LEFT JOIN dbo.StudentAssignmentAnswers ans ON q.Id = ans.QuestionId AND ans.AttemptId = @AttemptId
                WHERE q.AssignmentId = @AssignmentId AND (q.TargetRole = @Role OR q.TargetRole = 'All')",
                new { AttemptId = id, AssignmentId = (int)attempt.AssignmentId, Role = studentRole });

            ViewBag.Attempt = attempt;
            ViewBag.StudentRole = studentRole;

            return View(details);
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
                itemDefinition = x.ItemDefinition
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
