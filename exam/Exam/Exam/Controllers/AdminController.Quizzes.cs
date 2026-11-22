using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dapper;
using Exam.DTOs;
using Exam.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Exam.Controllers
{
    public partial class AdminController
    {
        public class QuizCreateModel
        {
            public int? Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Description { get; set; }
            public int WaveId { get; set; }
            public int CategoryId { get; set; }
            public int? TopicId { get; set; }
            public int TotalPoints { get; set; } = 100;
            public decimal PassPercentage { get; set; } = 60;
            public int DurationInMinutes { get; set; } = 20;
            public DateTime? StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public bool IsActive { get; set; } = true;
            public List<int>? QuestionIds { get; set; }
        }

        public class SaveQuizQuestionsModel
        {
            public int QuizId { get; set; }
            public List<int> QuestionIds { get; set; } = new();
        }

        public class QuickQuestionModel
        {
            public int QuizId { get; set; }
            public int CategoryId { get; set; }
            public int? TopicId { get; set; }
            public string QuestionText { get; set; } = string.Empty;
            public int Points { get; set; } = 5;
            public int Difficulty { get; set; } = 1;
            public List<QuickChoiceModel> Choices { get; set; } = new();
        }

        public class QuickChoiceModel
        {
            public string ChoiceText { get; set; } = string.Empty;
            public bool IsCorrect { get; set; }
        }

        public class WaveWeightsModel
        {
            public int WaveId { get; set; }
            public decimal QuizzesWeight { get; set; } = 30;
            public decimal AssignmentsWeight { get; set; } = 20;
            public decimal FinalExamWeight { get; set; } = 50;
            public decimal PassingPercentage { get; set; } = 60;
        }

        // ==========================================
        // 1. Quizzes Main Index View
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Quizzes(int? waveId = null, int? categoryId = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Fetch Waves
            var waves = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, WaveName, StartDate, EndDate, IsOnline, Mode
                FROM TrainingWaves WITH (NOLOCK)
                ORDER BY Id DESC")).ToList();

            // Fetch Categories
            var categories = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, CategoryName
                FROM Categories WITH (NOLOCK)
                ORDER BY CategoryName ASC")).ToList();

            // Fetch Topics
            var topics = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, TopicName, CategoryId
                FROM Topics WITH (NOLOCK)
                ORDER BY TopicName ASC")).ToList();

            // Fetch Quizzes List
            var sql = @"
                SELECT 
                    e.Id,
                    e.Title,
                    e.Description,
                    e.StartTime,
                    e.EndTime,
                    e.DurationInMinutes,
                    e.PassPercentage,
                    e.IsActive,
                    e.IsGraded,
                    e.WaveId,
                    ISNULL(w.WaveName, N'عام / غير محدد') AS WaveName,
                    e.CategoryId,
                    ISNULL(c.CategoryName, N'عام') AS CategoryName,
                    e.TopicId,
                    ISNULL(t.TopicName, N'عام / غير محدد') AS TopicName,
                    ISNULL(e.TotalPoints, 100) AS TotalPoints,
                    (SELECT COUNT(1) FROM ExamQuestions eq WITH (NOLOCK) WHERE eq.ExamId = e.Id) AS QuestionCount,
                    (SELECT COUNT(DISTINCT ea.StudentId) FROM ExamAssignments ea WITH (NOLOCK) WHERE ea.ExamId = e.Id) AS AssignedStudentsCount,
                    (SELECT COUNT(DISTINCT uea.UserId) FROM UserExamAttempts uea WITH (NOLOCK) WHERE uea.ExamId = e.Id AND uea.Status IN ('Completed', 'Fail_Timeout')) AS CompletedAttemptsCount,
                    (SELECT ISNULL(ROUND(AVG(CAST(uea.Score AS FLOAT)), 1), 0) FROM UserExamAttempts uea WITH (NOLOCK) WHERE uea.ExamId = e.Id AND uea.Status IN ('Completed', 'Fail_Timeout')) AS AverageScore
                FROM Exams e WITH (NOLOCK)
                LEFT JOIN TrainingWaves w WITH (NOLOCK) ON e.WaveId = w.Id
                LEFT JOIN Categories c WITH (NOLOCK) ON e.CategoryId = c.Id
                LEFT JOIN Topics t WITH (NOLOCK) ON e.TopicId = t.Id
                WHERE (e.IsQuiz = 1 OR e.ExamTypeId = 9 OR e.Title LIKE '%Quiz%')
                  AND (@WaveId IS NULL OR e.WaveId = @WaveId)
                  AND (@CategoryId IS NULL OR e.CategoryId = @CategoryId)
                ORDER BY e.Id DESC";

            var quizzes = (await conn.QueryAsync<dynamic>(sql, new { WaveId = waveId, CategoryId = categoryId })).ToList();

            ViewBag.Waves = waves;
            ViewBag.Categories = categories;
            ViewBag.Topics = topics;
            ViewBag.SelectedWaveId = waveId;
            ViewBag.SelectedCategoryId = categoryId;

            return View(quizzes);
        }

        // ==========================================
        // 2. Create Quiz
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> CreateQuiz([FromBody] QuizCreateModel model)
        {
            if (model == null)
                return Json(new { success = false, message = "بيانات غير صالحة." });

            if (string.IsNullOrWhiteSpace(model.Title))
                return Json(new { success = false, message = "عنوان الكويز مطلوب." });

            if (model.WaveId <= 0)
                return Json(new { success = false, message = "يرجى تحديد الويف التابع لها الكويز." });

            if (model.CategoryId <= 0)
                return Json(new { success = false, message = "يرجى تحديد التصنيف (Category)." });

            DateTime startTime = model.StartTime ?? DateTime.Now;
            DateTime endTime = model.EndTime ?? startTime.AddDays(7);
            if (endTime <= startTime) endTime = startTime.AddHours(24);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = conn.BeginTransaction();
            try
            {
                // Ensure ExamType 9 exists
                var typeCheck = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT Id FROM ExamTypes WHERE Id = 9", transaction: tx);
                if (typeCheck == null)
                {
                    await conn.ExecuteAsync(
                        "SET IDENTITY_INSERT ExamTypes ON; INSERT INTO ExamTypes (Id, TypeName) VALUES (9, 'Quiz'); SET IDENTITY_INSERT ExamTypes OFF;",
                        transaction: tx);
                }

                // Insert into Exams
                string insertSql = @"
                    INSERT INTO Exams (
                        Title, Description, StartTime, EndTime, DurationInMinutes, 
                        PassPercentage, IsActive, ExamTypeId, WaveId, IsGraded, 
                        TotalPoints, IsFinalExam, IsQuiz, CategoryId, TopicId,
                        TotalQuestionsToShow, ShowQuestionOverview, AllowBackNavigation
                    ) VALUES (
                        @Title, @Description, @StartTime, @EndTime, @DurationInMinutes, 
                        @PassPercentage, @IsActive, @ExamTypeId, @WaveId, 1, 
                        @TotalPoints, 0, 1, @CategoryId, @TopicId,
                        @TotalQuestionsToShow, 1, 1
                    );
                    SELECT CAST(SCOPE_IDENTITY() AS INT);";

                int quizId = await conn.QuerySingleAsync<int>(insertSql, new
                {
                    Title = model.Title.Trim(),
                    Description = model.Description?.Trim(),
                    StartTime = startTime,
                    EndTime = endTime,
                    DurationInMinutes = model.DurationInMinutes > 0 ? model.DurationInMinutes : 20,
                    PassPercentage = model.PassPercentage > 0 ? model.PassPercentage : 60,
                    IsActive = model.IsActive,
                    ExamTypeId = 9,
                    WaveId = model.WaveId,
                    TotalPoints = model.TotalPoints > 0 ? model.TotalPoints : 100,
                    CategoryId = model.CategoryId,
                    TopicId = model.TopicId > 0 ? model.TopicId : (int?)null,
                    TotalQuestionsToShow = model.QuestionIds != null ? model.QuestionIds.Count : 0
                }, transaction: tx);

                // Attach questions if provided
                if (model.QuestionIds != null && model.QuestionIds.Any())
                {
                    foreach (var qId in model.QuestionIds)
                    {
                        await conn.ExecuteAsync(
                            "INSERT INTO ExamQuestions (ExamId, QuestionId) VALUES (@ExamId, @QuestionId)",
                            new { ExamId = quizId, QuestionId = qId }, transaction: tx);
                    }
                }

                // Automatically assign enrolled wave students
                string assignSql = @"
                    INSERT INTO ExamAssignments (ExamId, StudentId, IsEmailSent, ScheduledStartTime, ScheduledEndTime)
                    SELECT @ExamId, uw.UserId, 0, @StartTime, @EndTime
                    FROM dbo.UserWaves uw WITH (NOLOCK)
                    WHERE uw.WaveId = @WaveId AND uw.IsActive = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.ExamAssignments ea WITH (NOLOCK) 
                          WHERE ea.ExamId = @ExamId AND ea.StudentId = uw.UserId
                      )";

                int assignedCount = await conn.ExecuteAsync(assignSql, new
                {
                    ExamId = quizId,
                    WaveId = model.WaveId,
                    StartTime = startTime,
                    EndTime = endTime
                }, transaction: tx);

                tx.Commit();

                return Json(new
                {
                    success = true,
                    quizId = quizId,
                    assignedCount = assignedCount,
                    message = $"تم إنشاء الكويز بنجاح وربطه بالويف وتعيين {assignedCount} متدرب تلقائياً."
                });
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return Json(new { success = false, message = "حدث خطأ أثناء إنشاء الكويز: " + ex.Message });
            }
        }

        // ==========================================
        // 3. Update Quiz
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> UpdateQuiz([FromBody] QuizCreateModel model)
        {
            if (model == null || !model.Id.HasValue || model.Id.Value <= 0)
                return Json(new { success = false, message = "معرّف الكويز غير صالح." });

            if (string.IsNullOrWhiteSpace(model.Title))
                return Json(new { success = false, message = "عنوان الكويز مطلوب." });

            DateTime startTime = model.StartTime ?? DateTime.Now;
            DateTime endTime = model.EndTime ?? startTime.AddDays(7);
            if (endTime <= startTime) endTime = startTime.AddHours(24);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            string updateSql = @"
                UPDATE Exams
                SET Title = @Title,
                    Description = @Description,
                    StartTime = @StartTime,
                    EndTime = @EndTime,
                    DurationInMinutes = @DurationInMinutes,
                    PassPercentage = @PassPercentage,
                    IsActive = @IsActive,
                    WaveId = @WaveId,
                    CategoryId = @CategoryId,
                    TopicId = @TopicId,
                    TotalPoints = @TotalPoints
                WHERE Id = @Id";

            int rows = await conn.ExecuteAsync(updateSql, new
            {
                Id = model.Id.Value,
                Title = model.Title.Trim(),
                Description = model.Description?.Trim(),
                StartTime = startTime,
                EndTime = endTime,
                DurationInMinutes = model.DurationInMinutes > 0 ? model.DurationInMinutes : 20,
                PassPercentage = model.PassPercentage > 0 ? model.PassPercentage : 60,
                IsActive = model.IsActive,
                WaveId = model.WaveId,
                CategoryId = model.CategoryId,
                TopicId = model.TopicId > 0 ? model.TopicId : (int?)null,
                TotalPoints = model.TotalPoints > 0 ? model.TotalPoints : 100
            });

            if (rows > 0)
            {
                // Re-sync wave student assignments
                await conn.ExecuteAsync(@"
                    INSERT INTO ExamAssignments (ExamId, StudentId, IsEmailSent, ScheduledStartTime, ScheduledEndTime)
                    SELECT @ExamId, uw.UserId, 0, @StartTime, @EndTime
                    FROM dbo.UserWaves uw WITH (NOLOCK)
                    WHERE uw.WaveId = @WaveId AND uw.IsActive = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.ExamAssignments ea WITH (NOLOCK) 
                          WHERE ea.ExamId = @ExamId AND ea.StudentId = uw.UserId
                      )", new { ExamId = model.Id.Value, WaveId = model.WaveId, StartTime = startTime, EndTime = endTime });

                return Json(new { success = true, message = "تم تعديل بيانات الكويز بنجاح." });
            }

            return Json(new { success = false, message = "لم يتم العثور على الكويز المحدد." });
        }

        // ==========================================
        // 4. Delete Quiz
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> DeleteQuiz(int id)
        {
            if (id <= 0) return Json(new { success = false, message = "معرّف غير صالح." });

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = conn.BeginTransaction();
            try
            {
                // Delete user answers & attempts
                await conn.ExecuteAsync(@"
                    DELETE FROM dbo.UserExamAnswers 
                    WHERE AttemptId IN (SELECT Id FROM dbo.UserExamAttempts WHERE ExamId = @Id)",
                    new { Id = id }, transaction: tx);

                await conn.ExecuteAsync(
                    "DELETE FROM dbo.UserExamAttempts WHERE ExamId = @Id",
                    new { Id = id }, transaction: tx);

                // Delete assignments
                await conn.ExecuteAsync(
                    "DELETE FROM dbo.ExamAssignments WHERE ExamId = @Id",
                    new { Id = id }, transaction: tx);

                // Delete questions linking
                await conn.ExecuteAsync(
                    "DELETE FROM dbo.ExamQuestions WHERE ExamId = @Id",
                    new { Id = id }, transaction: tx);

                // Delete exam
                int rows = await conn.ExecuteAsync(
                    "DELETE FROM dbo.Exams WHERE Id = @Id",
                    new { Id = id }, transaction: tx);

                tx.Commit();

                return Json(new { success = rows > 0, message = rows > 0 ? "تم حذف الكويز بنجاح." : "الكويز غير موجود." });
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return Json(new { success = false, message = "حدث خطأ أثناء الحذف: " + ex.Message });
            }
        }

        // ==========================================
        // 5. Toggle Quiz Active Status
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> ToggleQuizStatus(int id, bool isActive)
        {
            using var conn = new SqlConnection(_connectionString);
            int rows = await conn.ExecuteAsync(
                "UPDATE dbo.Exams SET IsActive = @IsActive WHERE Id = @Id",
                new { Id = id, IsActive = isActive });

            return Json(new { success = rows > 0, isActive = isActive });
        }

        // ==========================================
        // 6. Get Quiz Questions & Bank
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetQuizQuestions(int id)
        {
            if (id <= 0) return Json(new { success = false, message = "معرف غير صالح." });

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var quiz = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    e.Id, e.Title, e.WaveId, e.CategoryId, e.TopicId, 
                    ISNULL(e.TotalPoints, 100) AS TotalPoints,
                    c.CategoryName, t.TopicName, w.WaveName
                FROM Exams e WITH (NOLOCK)
                LEFT JOIN Categories c WITH (NOLOCK) ON e.CategoryId = c.Id
                LEFT JOIN Topics t WITH (NOLOCK) ON e.TopicId = t.Id
                LEFT JOIN TrainingWaves w WITH (NOLOCK) ON e.WaveId = w.Id
                WHERE e.Id = @Id", new { Id = id });

            if (quiz == null)
                return Json(new { success = false, message = "لم يتم العثور على الكويز." });

            // Assigned Questions
            var assignedQuestions = (await conn.QueryAsync<dynamic>(@"
                SELECT 
                    q.Id, q.QuestionText, q.Points, q.Difficulty,
                    c.CategoryName, t.TopicName
                FROM ExamQuestions eq WITH (NOLOCK)
                JOIN Questions q WITH (NOLOCK) ON eq.QuestionId = q.Id
                LEFT JOIN Categories c WITH (NOLOCK) ON q.CategoryId = c.Id
                LEFT JOIN Topics t WITH (NOLOCK) ON q.TopicId = t.Id
                WHERE eq.ExamId = @Id
                ORDER BY eq.Id ASC", new { Id = id })).ToList();

            // Available Questions in the same Category / Topic
            int? catId = quiz.CategoryId;
            int? topId = quiz.TopicId;

            var availableQuestions = (await conn.QueryAsync<dynamic>(@"
                SELECT TOP 100
                    q.Id, q.QuestionText, q.Points, q.Difficulty,
                    c.CategoryName, t.TopicName
                FROM Questions q WITH (NOLOCK)
                LEFT JOIN Categories c WITH (NOLOCK) ON q.CategoryId = c.Id
                LEFT JOIN Topics t WITH (NOLOCK) ON q.TopicId = t.Id
                WHERE q.IsActive = 1
                  AND (@CategoryId IS NULL OR q.CategoryId = @CategoryId)
                  AND (@TopicId IS NULL OR q.TopicId = @TopicId)
                  AND q.Id NOT IN (SELECT QuestionId FROM ExamQuestions WITH (NOLOCK) WHERE ExamId = @Id)
                ORDER BY q.Id DESC", new { Id = id, CategoryId = catId, TopicId = topId })).ToList();

            return Json(new
            {
                success = true,
                quiz = quiz,
                assignedQuestions = assignedQuestions,
                availableQuestions = availableQuestions
            });
        }

        // ==========================================
        // 7. Save Quiz Questions
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> SaveQuizQuestions([FromBody] SaveQuizQuestionsModel model)
        {
            if (model == null || model.QuizId <= 0)
                return Json(new { success = false, message = "بيانات غير صالحة." });

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = conn.BeginTransaction();
            try
            {
                // Delete previous questions
                await conn.ExecuteAsync(
                    "DELETE FROM ExamQuestions WHERE ExamId = @ExamId",
                    new { ExamId = model.QuizId }, transaction: tx);

                // Insert new selection
                if (model.QuestionIds != null && model.QuestionIds.Any())
                {
                    foreach (var qId in model.QuestionIds.Distinct())
                    {
                        await conn.ExecuteAsync(
                            "INSERT INTO ExamQuestions (ExamId, QuestionId) VALUES (@ExamId, @QuestionId)",
                            new { ExamId = model.QuizId, QuestionId = qId }, transaction: tx);
                    }
                }

                int totalCount = model.QuestionIds?.Distinct().Count() ?? 0;
                await conn.ExecuteAsync(
                    "UPDATE Exams SET TotalQuestionsToShow = @TotalCount WHERE Id = @ExamId",
                    new { TotalCount = totalCount, ExamId = model.QuizId }, transaction: tx);

                tx.Commit();

                return Json(new
                {
                    success = true,
                    count = totalCount,
                    message = $"تم حفظ وتحديث بنك أسئلة الكويز بنجاح ({totalCount} سؤال)."
                });
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return Json(new { success = false, message = "حدث خطأ أثناء حفظ الأسئلة: " + ex.Message });
            }
        }

        // ==========================================
        // 8. Add Quick Question to Bank & Quiz
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> AddQuickQuestion([FromBody] QuickQuestionModel model)
        {
            if (model == null || model.QuizId <= 0)
                return Json(new { success = false, message = "بيانات غير صالحة." });

            if (string.IsNullOrWhiteSpace(model.QuestionText))
                return Json(new { success = false, message = "نص السؤال مطلوب." });

            if (model.Choices == null || model.Choices.Count < 2)
                return Json(new { success = false, message = "يجب إضافة خيارين على الأقل للسؤال." });

            if (!model.Choices.Any(c => c.IsCorrect))
                return Json(new { success = false, message = "يجب تحديد إجابة صحيحة واحدة على الأقل." });

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = conn.BeginTransaction();
            try
            {
                string insertQSql = @"
                    INSERT INTO Questions (QuestionText, Points, Difficulty, IsActive, CategoryId, TopicId)
                    VALUES (@QuestionText, @Points, @Difficulty, 1, @CategoryId, @TopicId);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);";

                int questionId = await conn.QuerySingleAsync<int>(insertQSql, new
                {
                    QuestionText = model.QuestionText.Trim(),
                    Points = model.Points > 0 ? model.Points : 5,
                    Difficulty = model.Difficulty,
                    CategoryId = model.CategoryId,
                    TopicId = model.TopicId > 0 ? model.TopicId : (int?)null
                }, transaction: tx);

                // Insert Choices
                foreach (var choice in model.Choices)
                {
                    if (string.IsNullOrWhiteSpace(choice.ChoiceText)) continue;
                    await conn.ExecuteAsync(@"
                        INSERT INTO Choices (QuestionId, ChoiceText, IsCorrect)
                        VALUES (@QuestionId, @ChoiceText, @IsCorrect)",
                        new
                        {
                            QuestionId = questionId,
                            ChoiceText = choice.ChoiceText.Trim(),
                            IsCorrect = choice.IsCorrect
                        }, transaction: tx);
                }

                // Link to Quiz
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT 1 FROM ExamQuestions WHERE ExamId = @ExamId AND QuestionId = @QuestionId)
                        INSERT INTO ExamQuestions (ExamId, QuestionId) VALUES (@ExamId, @QuestionId)",
                    new { ExamId = model.QuizId, QuestionId = questionId }, transaction: tx);

                await conn.ExecuteAsync(@"
                    UPDATE Exams 
                    SET TotalQuestionsToShow = (SELECT COUNT(1) FROM ExamQuestions WHERE ExamId = @ExamId)
                    WHERE Id = @ExamId", new { ExamId = model.QuizId }, transaction: tx);

                tx.Commit();

                return Json(new { success = true, questionId = questionId, message = "تمت إضافة السؤال بنجاح إلى بنك الأسئلة والكويز." });
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return Json(new { success = false, message = "حدث خطأ أثناء إضافة السؤال: " + ex.Message });
            }
        }

        // ==========================================
        // 9. Get Quiz Attempts
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetQuizAttempts(int id)
        {
            if (id <= 0) return Json(new { success = false, message = "معرّف غير صالح." });

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var quiz = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT e.Id, e.Title, e.PassPercentage, ISNULL(e.TotalPoints, 100) AS TotalPoints, w.WaveName
                FROM Exams e WITH (NOLOCK)
                LEFT JOIN TrainingWaves w WITH (NOLOCK) ON e.WaveId = w.Id
                WHERE e.Id = @Id", new { Id = id });

            if (quiz == null)
                return Json(new { success = false, message = "الكويز غير موجود." });

            var attempts = (await conn.QueryAsync<dynamic>(@"
                SELECT 
                    uea.Id,
                    uea.UserId,
                    u.FullName,
                    u.UserCode,
                    u.Email,
                    ISNULL(b.BranchName, N'غير محدد') AS BranchName,
                    u.RoleName,
                    uea.Score,
                    uea.FinalScore,
                    uea.IsPassed,
                    uea.Status,
                    uea.AttemptDate,
                    uea.DurationInMinutes,
                    uea.TotalQuestions,
                    uea.CorrectAnswers
                FROM UserExamAttempts uea WITH (NOLOCK)
                JOIN AspNetUsers u WITH (NOLOCK) ON uea.UserId = u.Id
                LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                WHERE uea.ExamId = @Id
                ORDER BY uea.Score DESC, uea.AttemptDate DESC", new { Id = id })).ToList();

            return Json(new
            {
                success = true,
                quiz = quiz,
                attempts = attempts
            });
        }

        // ==========================================
        // 10. Get Topics by Category (Cascading)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetTopicsByCategory(int categoryId)
        {
            using var conn = new SqlConnection(_connectionString);
            var topics = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, TopicName 
                FROM Topics WITH (NOLOCK)
                WHERE CategoryId = @CategoryId
                ORDER BY TopicName ASC", new { CategoryId = categoryId })).ToList();

            return Json(topics);
        }

        // ==========================================
        // 11. Wave Curriculum Grading Summary (Gradebook)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetWaveGradingSummary(int waveId)
        {
            if (waveId <= 0) return Json(new { success = false, message = "معرّف الويف غير صالح." });

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1. Weights
            var weights = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT QuizzesWeight, AssignmentsWeight, FinalExamWeight, PassingPercentage
                FROM WaveScoreWeights WITH (NOLOCK)
                WHERE WaveId = @WaveId", new { WaveId = waveId });

            decimal quizzesWeight = weights != null ? (decimal)weights.QuizzesWeight : 30m;
            decimal assignmentsWeight = weights != null ? (decimal)weights.AssignmentsWeight : 20m;
            decimal finalExamWeight = weights != null ? (decimal)weights.FinalExamWeight : 50m;
            decimal passingPercentage = weights != null ? (decimal)weights.PassingPercentage : 60m;

            // 2. Wave Quizzes
            var waveQuizzes = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, Title, PassPercentage, ISNULL(TotalPoints, 100) AS TotalPoints
                FROM Exams WITH (NOLOCK)
                WHERE WaveId = @WaveId AND (IsQuiz = 1 OR ExamTypeId = 9 OR Title LIKE '%Quiz%')
                ORDER BY Id ASC", new { WaveId = waveId })).ToList();

            // 3. Wave Assignments
            var waveAssignments = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, Title, ISNULL(PharmacistMaxScore, 10) AS PharmacistMaxScore, ISNULL(AssistantMaxScore, 10) AS AssistantMaxScore
                FROM Assignments WITH (NOLOCK)
                WHERE WaveId = @WaveId
                ORDER BY Id ASC", new { WaveId = waveId })).ToList();

            // 4. Wave Final Exam
            var waveFinalExam = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT TOP 1 Id, Title, PassPercentage, ISNULL(TotalPoints, 100) AS TotalPoints
                FROM Exams WITH (NOLOCK)
                WHERE WaveId = @WaveId 
                  AND (IsFinalExam = 1 OR ExamTypeId = 8 OR Title LIKE '%Final%')
                  AND IsQuiz = 0
                ORDER BY Id DESC", new { WaveId = waveId });

            // 5. Enrolled Trainees
            var trainees = (await conn.QueryAsync<dynamic>(@"
                SELECT 
                    u.Id AS UserId,
                    u.UserCode,
                    u.FullName,
                    u.Email,
                    u.RoleName,
                    ISNULL(b.BranchName, N'غير محدد') AS BranchName,
                    uwc.Score AS CurrentCertScore,
                    uwc.CertificateCode,
                    uwc.FileUploaded
                FROM UserWaves uw WITH (NOLOCK)
                JOIN AspNetUsers u WITH (NOLOCK) ON uw.UserId = u.Id
                LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                LEFT JOIN UserWaveCertificates uwc WITH (NOLOCK) ON uw.UserId = uwc.UserId AND uwc.WaveId = @WaveId
                WHERE uw.WaveId = @WaveId AND uw.IsActive = 1
                ORDER BY u.FullName ASC", new { WaveId = waveId })).ToList();

            // 6. Pre-fetch attempts
            var quizAttempts = (await conn.QueryAsync<dynamic>(@"
                SELECT uea.UserId, uea.ExamId, MAX(uea.Score) AS BestScore
                FROM UserExamAttempts uea WITH (NOLOCK)
                JOIN Exams e WITH (NOLOCK) ON uea.ExamId = e.Id
                WHERE e.WaveId = @WaveId AND (e.IsQuiz = 1 OR e.ExamTypeId = 9 OR e.Title LIKE '%Quiz%')
                  AND uea.Status IN ('Completed', 'Fail_Timeout')
                GROUP BY uea.UserId, uea.ExamId", new { WaveId = waveId })).ToList();

            var assignmentAttempts = (await conn.QueryAsync<dynamic>(@"
                SELECT saa.UserId, saa.AssignmentId, MAX(saa.Score) AS BestScore
                FROM StudentAssignmentAttempts saa WITH (NOLOCK)
                JOIN Assignments a WITH (NOLOCK) ON saa.AssignmentId = a.Id
                WHERE a.WaveId = @WaveId AND saa.Status = 'Completed'
                GROUP BY saa.UserId, saa.AssignmentId", new { WaveId = waveId })).ToList();

            var finalExamAttempts = waveFinalExam != null ? (await conn.QueryAsync<dynamic>(@"
                SELECT uea.UserId, MAX(uea.Score) AS BestScore
                FROM UserExamAttempts uea WITH (NOLOCK)
                WHERE uea.ExamId = @FinalExamId
                  AND uea.Status IN ('Completed', 'Fail_Timeout')
                GROUP BY uea.UserId", new { FinalExamId = (int)waveFinalExam.Id })).ToList() : new List<dynamic>();

            // Quick lookup dictionaries
            var quizMap = quizAttempts.ToLookup(x => (string)x.UserId, x => new { ExamId = (int)x.ExamId, BestScore = (decimal)x.BestScore });
            var assignMap = assignmentAttempts.ToLookup(x => (string)x.UserId, x => new { AssignmentId = (int)x.AssignmentId, BestScore = (decimal)x.BestScore });
            var finalMap = finalExamAttempts.ToDictionary(x => (string)x.UserId, x => (decimal)x.BestScore);

            // Compute effective normalized weights if some components do not exist
            decimal activeWeightSum = 0m;
            if (waveQuizzes.Any()) activeWeightSum += quizzesWeight;
            if (waveAssignments.Any()) activeWeightSum += assignmentsWeight;
            if (waveFinalExam != null) activeWeightSum += finalExamWeight;

            var studentRows = new List<object>();

            foreach (var t in trainees)
            {
                string uid = (string)t.UserId;

                // 1. Quizzes Average %
                decimal quizzesAvgPercent = 0m;
                int quizzesTakenCount = 0;
                if (waveQuizzes.Any())
                {
                    decimal totalQScore = 0m;
                    var userQAttempts = quizMap[uid].ToDictionary(x => x.ExamId, x => x.BestScore);
                    foreach (var q in waveQuizzes)
                    {
                        int qId = (int)q.Id;
                        if (userQAttempts.TryGetValue(qId, out decimal s))
                        {
                            totalQScore += s;
                            quizzesTakenCount++;
                        }
                    }
                    quizzesAvgPercent = Math.Round(totalQScore / waveQuizzes.Count, 2);
                }

                // 2. Assignments Average %
                decimal assignmentsAvgPercent = 0m;
                int assignmentsTakenCount = 0;
                if (waveAssignments.Any())
                {
                    decimal totalAScore = 0m;
                    var userAAttempts = assignMap[uid].ToDictionary(x => x.AssignmentId, x => x.BestScore);
                    string role = (string)(t.RoleName ?? "");
                    bool isPharm = role.ToLower().Contains("pharm");

                    foreach (var a in waveAssignments)
                    {
                        int aId = (int)a.Id;
                        decimal maxScore = isPharm ? (decimal)a.PharmacistMaxScore : (decimal)a.AssistantMaxScore;
                        if (maxScore <= 0) maxScore = 10m;

                        if (userAAttempts.TryGetValue(aId, out decimal earned))
                        {
                            decimal p = Math.Min(100m, (earned / maxScore) * 100m);
                            totalAScore += p;
                            assignmentsTakenCount++;
                        }
                    }
                    assignmentsAvgPercent = Math.Round(totalAScore / waveAssignments.Count, 2);
                }

                // 3. Final Exam %
                decimal finalExamPercent = 0m;
                bool finalTaken = false;
                if (waveFinalExam != null)
                {
                    if (finalMap.TryGetValue(uid, out decimal fs))
                    {
                        finalExamPercent = Math.Round(fs, 2);
                        finalTaken = true;
                    }
                }

                // 4. Composite Final Degree %
                decimal rawComposite = 0m;
                if (waveQuizzes.Any()) rawComposite += (quizzesAvgPercent * (quizzesWeight / 100m));
                if (waveAssignments.Any()) rawComposite += (assignmentsAvgPercent * (assignmentsWeight / 100m));
                if (waveFinalExam != null) rawComposite += (finalExamPercent * (finalExamWeight / 100m));

                decimal compositeScore = rawComposite;
                if (activeWeightSum > 0 && activeWeightSum < 100m)
                {
                    compositeScore = Math.Round((rawComposite / activeWeightSum) * 100m, 2);
                }
                else
                {
                    compositeScore = Math.Round(compositeScore, 2);
                }

                bool isPassed = compositeScore >= passingPercentage;

                studentRows.Add(new
                {
                    userId = uid,
                    userCode = (string)t.UserCode,
                    fullName = (string)t.FullName,
                    email = (string)t.Email,
                    branchName = (string)t.BranchName,
                    roleName = (string)t.RoleName,
                    quizzesAvgPercent = quizzesAvgPercent,
                    quizzesTakenCount = quizzesTakenCount,
                    totalQuizzesCount = waveQuizzes.Count,
                    assignmentsAvgPercent = assignmentsAvgPercent,
                    assignmentsTakenCount = assignmentsTakenCount,
                    totalAssignmentsCount = waveAssignments.Count,
                    finalExamPercent = finalExamPercent,
                    finalTaken = finalTaken,
                    compositeScore = compositeScore,
                    currentCertScore = t.CurrentCertScore != null ? (decimal)t.CurrentCertScore : (decimal?)null,
                    certificateCode = (string)t.CertificateCode,
                    fileUploaded = t.FileUploaded != null && (bool)t.FileUploaded,
                    isPassed = isPassed
                });
            }

            return Json(new
            {
                success = true,
                weights = new
                {
                    quizzesWeight = quizzesWeight,
                    assignmentsWeight = assignmentsWeight,
                    finalExamWeight = finalExamWeight,
                    passingPercentage = passingPercentage
                },
                breakdown = new
                {
                    quizzesCount = waveQuizzes.Count,
                    assignmentsCount = waveAssignments.Count,
                    hasFinalExam = waveFinalExam != null,
                    finalExamTitle = waveFinalExam?.Title ?? "لا يوجد امتحان فاينال مسجل"
                },
                trainees = studentRows
            });
        }

        // ==========================================
        // 12. Save Wave Score Weights
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> SaveWaveScoreWeights([FromBody] WaveWeightsModel model)
        {
            if (model == null || model.WaveId <= 0)
                return Json(new { success = false, message = "معرّف الويف غير صالح." });

            decimal totalWeights = model.QuizzesWeight + model.AssignmentsWeight + model.FinalExamWeight;
            if (Math.Abs(totalWeights - 100m) > 0.01m)
            {
                return Json(new { success = false, message = $"مجموع الأوزان يجب أن يساوي 100% (المجموع الحالي: {totalWeights}%)." });
            }

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            string mergeSql = @"
                MERGE INTO dbo.WaveScoreWeights AS target
                USING (SELECT @WaveId AS WaveId) AS source
                ON target.WaveId = source.WaveId
                WHEN MATCHED THEN
                    UPDATE SET QuizzesWeight = @QuizzesWeight, 
                               AssignmentsWeight = @AssignmentsWeight, 
                               FinalExamWeight = @FinalExamWeight, 
                               PassingPercentage = @PassingPercentage, 
                               UpdatedAt = GETDATE()
                WHEN NOT MATCHED THEN
                    INSERT (WaveId, QuizzesWeight, AssignmentsWeight, FinalExamWeight, PassingPercentage, UpdatedAt)
                    VALUES (@WaveId, @QuizzesWeight, @AssignmentsWeight, @FinalExamWeight, @PassingPercentage, GETDATE());";

            await conn.ExecuteAsync(mergeSql, new
            {
                WaveId = model.WaveId,
                QuizzesWeight = model.QuizzesWeight,
                AssignmentsWeight = model.AssignmentsWeight,
                FinalExamWeight = model.FinalExamWeight,
                PassingPercentage = model.PassingPercentage > 0 ? model.PassingPercentage : 60m
            });

            return Json(new { success = true, message = "تم حفظ وتحديث أوزان احتساب الدرجة النهائية بنجاح." });
        }

        // ==========================================
        // 13. RUN AUTOMATION: Calculate & Record Final Degree in Certification
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> RunWaveCertificationAutomation([FromBody] WaveWeightsModel model)
        {
            if (model == null || model.WaveId <= 0)
                return Json(new { success = false, message = "معرّف الويف غير صالح." });

            int waveId = model.WaveId;

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Save weights if submitted
            if (model.QuizzesWeight > 0 || model.AssignmentsWeight > 0 || model.FinalExamWeight > 0)
            {
                decimal totalWeights = model.QuizzesWeight + model.AssignmentsWeight + model.FinalExamWeight;
                if (Math.Abs(totalWeights - 100m) <= 0.01m)
                {
                    await conn.ExecuteAsync(@"
                        MERGE INTO dbo.WaveScoreWeights AS target
                        USING (SELECT @WaveId AS WaveId) AS source
                        ON target.WaveId = source.WaveId
                        WHEN MATCHED THEN
                            UPDATE SET QuizzesWeight = @QuizzesWeight, AssignmentsWeight = @AssignmentsWeight, FinalExamWeight = @FinalExamWeight, PassingPercentage = @PassingPercentage, UpdatedAt = GETDATE()
                        WHEN NOT MATCHED THEN
                            INSERT (WaveId, QuizzesWeight, AssignmentsWeight, FinalExamWeight, PassingPercentage, UpdatedAt)
                            VALUES (@WaveId, @QuizzesWeight, @AssignmentsWeight, @FinalExamWeight, @PassingPercentage, GETDATE());",
                        new { WaveId = waveId, QuizzesWeight = model.QuizzesWeight, AssignmentsWeight = model.AssignmentsWeight, FinalExamWeight = model.FinalExamWeight, PassingPercentage = model.PassingPercentage });
                }
            }

            // Fetch active weights
            var weights = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT QuizzesWeight, AssignmentsWeight, FinalExamWeight, PassingPercentage
                FROM WaveScoreWeights WITH (NOLOCK)
                WHERE WaveId = @WaveId", new { WaveId = waveId });

            decimal qWeight = weights != null ? (decimal)weights.QuizzesWeight : 30m;
            decimal aWeight = weights != null ? (decimal)weights.AssignmentsWeight : 20m;
            decimal fWeight = weights != null ? (decimal)weights.FinalExamWeight : 50m;
            decimal passThreshold = weights != null ? (decimal)weights.PassingPercentage : 60m;

            // Fetch Wave Details for Serial format
            var waveObj = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT Id, WaveName, StartDate, Mode, IsOnline
                FROM TrainingWaves WITH (NOLOCK) WHERE Id = @WaveId", new { WaveId = waveId });

            string wName = waveObj?.WaveName ?? $"Wave {waveId}";
            DateTime wDate = waveObj?.StartDate ?? DateTime.Now;
            string yearStr = wDate.Year.ToString();
            var digits = new string(wName.Where(char.IsDigit).ToArray());
            string waveNumStr = !string.IsNullOrEmpty(digits) ? digits.PadLeft(3, '0') : waveId.ToString().PadLeft(3, '0');
            bool isOnline = waveObj?.IsOnline ?? (wName.ToLower().Contains("online") || wName.Contains("أونلاين") || wName.Contains("اونلاين"));
            string modeCode = ExtractModeForSerial(waveObj?.Mode, isOnline);

            // Fetch Quizzes in this Wave
            var waveQuizzes = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, ISNULL(TotalPoints, 100) AS TotalPoints
                FROM Exams WITH (NOLOCK)
                WHERE WaveId = @WaveId AND (IsQuiz = 1 OR ExamTypeId = 9 OR Title LIKE '%Quiz%')",
                new { WaveId = waveId })).ToList();

            // Fetch Assignments in this Wave
            var waveAssignments = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, ISNULL(PharmacistMaxScore, 10) AS PharmacistMaxScore, ISNULL(AssistantMaxScore, 10) AS AssistantMaxScore
                FROM Assignments WITH (NOLOCK)
                WHERE WaveId = @WaveId",
                new { WaveId = waveId })).ToList();

            // Fetch Final Exam in this Wave
            var waveFinalExam = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT TOP 1 Id, PassPercentage, ISNULL(TotalPoints, 100) AS TotalPoints
                FROM Exams WITH (NOLOCK)
                WHERE WaveId = @WaveId 
                  AND (IsFinalExam = 1 OR ExamTypeId = 8 OR Title LIKE '%Final%')
                  AND IsQuiz = 0
                ORDER BY Id DESC", new { WaveId = waveId });

            // Fetch all enrolled Trainees
            var trainees = (await conn.QueryAsync<dynamic>(@"
                SELECT 
                    u.Id AS UserId,
                    u.UserCode,
                    u.FullName,
                    u.RoleName,
                    uwc.Id AS CertId,
                    uwc.CertificateCode,
                    uwc.Score AS CurrentCertScore
                FROM UserWaves uw WITH (NOLOCK)
                JOIN AspNetUsers u WITH (NOLOCK) ON uw.UserId = u.Id
                LEFT JOIN UserWaveCertificates uwc WITH (NOLOCK) ON uw.UserId = uwc.UserId AND uwc.WaveId = @WaveId
                WHERE uw.WaveId = @WaveId AND uw.IsActive = 1", new { WaveId = waveId })).ToList();

            if (!trainees.Any())
            {
                return Json(new { success = false, message = "لا يوجد متدربين مسجلين في هذه الويف لتشغيل الأتمتة عليهم." });
            }

            // Pre-fetch all attempts
            var quizAttempts = (await conn.QueryAsync<dynamic>(@"
                SELECT uea.UserId, uea.ExamId, MAX(uea.Score) AS BestScore
                FROM UserExamAttempts uea WITH (NOLOCK)
                JOIN Exams e WITH (NOLOCK) ON uea.ExamId = e.Id
                WHERE e.WaveId = @WaveId AND (e.IsQuiz = 1 OR e.ExamTypeId = 9 OR e.Title LIKE '%Quiz%')
                  AND uea.Status IN ('Completed', 'Fail_Timeout')
                GROUP BY uea.UserId, uea.ExamId", new { WaveId = waveId })).ToList();

            var assignmentAttempts = (await conn.QueryAsync<dynamic>(@"
                SELECT saa.UserId, saa.AssignmentId, MAX(saa.Score) AS BestScore
                FROM StudentAssignmentAttempts saa WITH (NOLOCK)
                JOIN Assignments a WITH (NOLOCK) ON saa.AssignmentId = a.Id
                WHERE a.WaveId = @WaveId AND saa.Status = 'Completed'
                GROUP BY saa.UserId, saa.AssignmentId", new { WaveId = waveId })).ToList();

            var finalExamAttempts = waveFinalExam != null ? (await conn.QueryAsync<dynamic>(@"
                SELECT uea.UserId, MAX(uea.Score) AS BestScore
                FROM UserExamAttempts uea WITH (NOLOCK)
                WHERE uea.ExamId = @FinalExamId
                  AND uea.Status IN ('Completed', 'Fail_Timeout')
                GROUP BY uea.UserId", new { FinalExamId = (int)waveFinalExam.Id })).ToList() : new List<dynamic>();

            var quizMap = quizAttempts.ToLookup(x => (string)x.UserId, x => new { ExamId = (int)x.ExamId, BestScore = (decimal)x.BestScore });
            var assignMap = assignmentAttempts.ToLookup(x => (string)x.UserId, x => new { AssignmentId = (int)x.AssignmentId, BestScore = (decimal)x.BestScore });
            var finalMap = finalExamAttempts.ToDictionary(x => (string)x.UserId, x => (decimal)x.BestScore);

            decimal activeWeightSum = 0m;
            if (waveQuizzes.Any()) activeWeightSum += qWeight;
            if (waveAssignments.Any()) activeWeightSum += aWeight;
            if (waveFinalExam != null) activeWeightSum += fWeight;

            int processedCount = 0;
            int updatedCount = 0;
            int passedCount = 0;
            int failedCount = 0;
            decimal totalScoresSum = 0m;

            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var t in trainees)
                {
                    string uid = (string)t.UserId;
                    string uCode = string.IsNullOrWhiteSpace((string)t.UserCode) ? "0000" : ((string)t.UserCode).Trim();

                    // 1. Quizzes
                    decimal pQuiz = 0m;
                    if (waveQuizzes.Any())
                    {
                        decimal totalQ = 0m;
                        var uQ = quizMap[uid].ToDictionary(x => x.ExamId, x => x.BestScore);
                        foreach (var q in waveQuizzes)
                        {
                            if (uQ.TryGetValue((int)q.Id, out decimal s)) totalQ += s;
                        }
                        pQuiz = totalQ / waveQuizzes.Count;
                    }

                    // 2. Assignments
                    decimal pAssign = 0m;
                    if (waveAssignments.Any())
                    {
                        decimal totalA = 0m;
                        var uA = assignMap[uid].ToDictionary(x => x.AssignmentId, x => x.BestScore);
                        string role = (string)(t.RoleName ?? "");
                        bool isPharm = role.ToLower().Contains("pharm");

                        foreach (var a in waveAssignments)
                        {
                            decimal maxScore = isPharm ? (decimal)a.PharmacistMaxScore : (decimal)a.AssistantMaxScore;
                            if (maxScore <= 0) maxScore = 10m;
                            if (uA.TryGetValue((int)a.Id, out decimal earned))
                            {
                                totalA += Math.Min(100m, (earned / maxScore) * 100m);
                            }
                        }
                        pAssign = totalA / waveAssignments.Count;
                    }

                    // 3. Final Exam
                    decimal pFinal = 0m;
                    if (waveFinalExam != null)
                    {
                        if (finalMap.TryGetValue(uid, out decimal fs))
                        {
                            pFinal = fs;
                        }
                    }

                    // 4. Composite Score
                    decimal rawComposite = 0m;
                    if (waveQuizzes.Any()) rawComposite += (pQuiz * (qWeight / 100m));
                    if (waveAssignments.Any()) rawComposite += (pAssign * (aWeight / 100m));
                    if (waveFinalExam != null) rawComposite += (pFinal * (fWeight / 100m));

                    decimal compositeScore = rawComposite;
                    if (activeWeightSum > 0 && activeWeightSum < 100m)
                    {
                        compositeScore = Math.Round((rawComposite / activeWeightSum) * 100m, 2);
                    }
                    else
                    {
                        compositeScore = Math.Round(compositeScore, 2);
                    }

                    totalScoresSum += compositeScore;
                    processedCount++;

                    bool isPassed = compositeScore >= passThreshold;
                    if (isPassed) passedCount++;
                    else failedCount++;

                    // Determine certificate serial
                    string existingCertCode = (string)t.CertificateCode;
                    string targetCertCode = !string.IsNullOrWhiteSpace(existingCertCode)
                        ? existingCertCode
                        : (isPassed ? $"WTTA-{yearStr}-{waveNumStr}-PB-{modeCode}-{uCode}" : null);

                    // MERGE into UserWaveCertificates
                    string certMergeSql = @"
                        MERGE INTO dbo.UserWaveCertificates AS target
                        USING (SELECT @UserId AS UserId, @WaveId AS WaveId) AS source
                        ON target.UserId = source.UserId AND target.WaveId = source.WaveId
                        WHEN MATCHED THEN
                            UPDATE SET 
                                Score = @Score,
                                CertificateCode = COALESCE(target.CertificateCode, @CertCode)
                        WHEN NOT MATCHED THEN
                            INSERT (UserId, WaveId, CertificateCode, Score, CreatedAt, EmailSent, FileUploaded)
                            VALUES (@UserId, @WaveId, @CertCode, @Score, GETDATE(), 0, 0);";

                    await conn.ExecuteAsync(certMergeSql, new
                    {
                        UserId = uid,
                        WaveId = waveId,
                        Score = compositeScore,
                        CertCode = targetCertCode
                    }, transaction: tx);

                    updatedCount++;
                }

                tx.Commit();

                decimal avgClassScore = processedCount > 0 ? Math.Round(totalScoresSum / processedCount, 2) : 0m;

                return Json(new
                {
                    success = true,
                    processedCount = processedCount,
                    updatedCount = updatedCount,
                    passedCount = passedCount,
                    failedCount = failedCount,
                    avgScore = avgClassScore,
                    message = $"⚡ تم تشغيل أتمتة احتساب الدرجات لعدد {processedCount} متدرب في الويف بنجاح! تم تسجيل الدرجات في الشهادات (ناجح: {passedCount}، لم يجتز: {failedCount}، متوسط الدرجات: {avgClassScore}%)."
                });
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return Json(new { success = false, message = "حدث خطأ أثناء تشغيل الأتمتة: " + ex.Message });
            }
        }
    }
}
