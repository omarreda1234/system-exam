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
            public decimal PassPercentage { get; set; } = 0; // Quizzes are formative/marks-only (no pass/fail threshold)
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

            // Auto-sync active wave trainees into ExamAssignments for any active wave-linked quiz
            try
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO ExamAssignments (ExamId, StudentId, IsEmailSent, ScheduledStartTime, ScheduledEndTime)
                    SELECT e.Id, uw.UserId, 0, ISNULL(e.StartTime, GETDATE()), ISNULL(e.EndTime, DATEADD(day, 30, GETDATE()))
                    FROM Exams e WITH (NOLOCK)
                    INNER JOIN dbo.UserWaves uw WITH (NOLOCK) ON e.WaveId = uw.WaveId AND uw.IsActive = 1
                    WHERE e.WaveId IS NOT NULL AND e.IsActive = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.ExamAssignments ea WITH (NOLOCK)
                          WHERE ea.ExamId = e.Id AND ea.StudentId = uw.UserId
                      )");
            }
            catch { }

            // Fetch Waves
            var waves = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, WaveName, StartDate, EndDate, IsOnline, Mode
                FROM TrainingWaves WITH (NOLOCK)
                ORDER BY Id DESC")).ToList();

            // Fetch Categories (Strictly Wave Categories - ExamTypeId = 2)
            var categories = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, CategoryName
                FROM Categories WITH (NOLOCK)
                WHERE ExamTypeId = 2
                ORDER BY CategoryName ASC")).ToList();

            // Fetch Topics (Strictly Topics belonging to Wave Categories)
            var topics = (await conn.QueryAsync<dynamic>(@"
                SELECT t.Id, t.TopicName, t.CategoryId
                FROM Topics t WITH (NOLOCK)
                INNER JOIN Categories c WITH (NOLOCK) ON t.CategoryId = c.Id
                WHERE c.ExamTypeId = 2
                ORDER BY t.TopicName ASC")).ToList();

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
                    ISNULL(w.WaveName, 'General / All Waves') AS WaveName,
                    e.CategoryId,
                    ISNULL(c.CategoryName, 'General') AS CategoryName,
                    e.TopicId,
                    ISNULL(t.TopicName, 'General / Unspecified') AS TopicName,
                    ISNULL(e.TotalPoints, 100) AS TotalPoints,
                    (SELECT COUNT(1) FROM ExamQuestions eq WITH (NOLOCK) WHERE eq.ExamId = e.Id) AS QuestionCount,
                    (SELECT COUNT(DISTINCT uid) 
                     FROM (
                         SELECT ea.StudentId AS uid FROM dbo.ExamAssignments ea WITH (NOLOCK) WHERE ea.ExamId = e.Id
                         UNION
                         SELECT uw.UserId AS uid FROM dbo.UserWaves uw WITH (NOLOCK) WHERE uw.WaveId = e.WaveId AND uw.IsActive = 1
                     ) combined) AS AssignedStudentsCount,
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
                    PassPercentage = model.PassPercentage, // Formative assessment / no pass or fail
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
                PassPercentage = model.PassPercentage,
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
                    DELETE FROM dbo.StudentQuestionDetails 
                    WHERE UserExamAttemptId IN (SELECT Id FROM dbo.UserExamAttempts WHERE ExamId = @Id)",
                    new { Id = id }, transaction: tx);

                await conn.ExecuteAsync(@"
                    DELETE FROM dbo.UserSeenQuestions 
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
        // 8b. Download Quiz Questions Excel Template
        // ==========================================
        [HttpGet]
        public IActionResult DownloadQuizQuestionsTemplate()
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("QuizQuestions");

            // Header labels
            var headers = new[]
            {
                "QuestionText",      // نص السؤال
                "Points",            // درجة السؤال (افتراضي 5)
                "Difficulty",        // مستوى الصعوبة (1=سهل, 2=متوسط, 3=صعب)
                "Choice1",           // الخيار الأول
                "Choice2",           // الخيار الثاني
                "Choice3",           // الخيار الثالث
                "Choice4",           // الخيار الرابع
                "CorrectChoice"      // رقم الإجابة الصحيحة (1 أو 2 أو 3 أو 4)
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#7C3AED"); // Purple
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // Sample Row 1
            ws.Cell(2, 1).Value = "ما هي الخطوة الأولى الموصى بها في روتين العناية بالبشرة؟";
            ws.Cell(2, 2).Value = 5;
            ws.Cell(2, 3).Value = 1; // Easy
            ws.Cell(2, 4).Value = "استخدام الغسول المناسب (Cleanser)";
            ws.Cell(2, 5).Value = "وضع واقي الشمس مباشرة";
            ws.Cell(2, 6).Value = "استخدام المقشر الكيميائي";
            ws.Cell(2, 7).Value = "وضع كريم الترطيب بدون تنظيف";
            ws.Cell(2, 8).Value = 1;

            // Sample Row 2
            ws.Cell(3, 1).Value = "أي من المكونات التالية يعتبر مضاد أكسدة فعال ومرطب للبشرة؟";
            ws.Cell(3, 2).Value = 5;
            ws.Cell(3, 3).Value = 2; // Medium
            ws.Cell(3, 4).Value = "فيتامين سي مع حمض الهيالورونيك";
            ws.Cell(3, 5).Value = "الكحول النقي";
            ws.Cell(3, 6).Value = "الزيوت المعدنية الثقيلة فقط";
            ws.Cell(3, 7).Value = "حمض الكبريتيك";
            ws.Cell(3, 8).Value = 1;

            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            stream.Position = 0;

            return File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "Quiz_Questions_Template.xlsx");
        }

        // ==========================================
        // 8c. Upload Quiz Questions from Excel
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> UploadQuizQuestionsExcel(int quizId, IFormFile excelFile)
        {
            if (quizId <= 0)
                return Json(new { success = false, message = "معرّف الكويز غير صالح." });

            if (excelFile == null || excelFile.Length == 0)
                return Json(new { success = false, message = "يرجى اختيار ملف إكسيل صالح." });

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var quiz = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT Id, Title, CategoryId, TopicId, WaveId 
                FROM Exams WITH (NOLOCK) 
                WHERE Id = @Id", new { Id = quizId });

            if (quiz == null)
                return Json(new { success = false, message = "لم يتم العثور على الكويز المحدد." });

            int quizCategoryId = quiz.CategoryId != null ? (int)quiz.CategoryId : 2013;
            int? quizTopicId = quiz.TopicId != null ? (int?)quiz.TopicId : null;

            try
            {
                using var memoryStream = new MemoryStream();
                await excelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using var workbook = new XLWorkbook(memoryStream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                    return Json(new { success = false, message = "ملف الإكسيل لا يحتوي على أوراق عمل." });

                var headerRow = worksheet.Row(1);
                var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 1; c <= worksheet.ColumnsUsed().Count(); c++)
                {
                    string h = headerRow.Cell(c).GetString()?.Trim().ToLower()
                        .Replace(" ", "").Replace("_", "").Replace("-", "");
                    if (!string.IsNullOrEmpty(h)) colMap[h] = c;
                }

                int GetCol(params string[] names)
                {
                    foreach (var n in names)
                    {
                        string key = n.ToLower().Replace(" ", "").Replace("_", "").Replace("-", "");
                        if (colMap.ContainsKey(key)) return colMap[key];
                    }
                    return -1;
                }

                int qCol = GetCol("questiontext", "question", "text", "السؤال", "نصالسؤال");
                int ptsCol = GetCol("points", "point", "score", "الدرجة", "درجة", "النقاط");
                int diffCol = GetCol("difficulty", "diffculty", "الصعوبة", "مستوىالصعوبة");
                int topCol = GetCol("topicid", "topic", "التوبيك", "الموضوع");

                int c1Col = GetCol("choice1", "option1", "الخيار1", "الاختيار1", "خيار1");
                int c2Col = GetCol("choice2", "option2", "الخيار2", "الاختيار2", "خيار2");
                int c3Col = GetCol("choice3", "option3", "الخيار3", "الاختيار3", "خيار3");
                int c4Col = GetCol("choice4", "option4", "الخيار4", "الاختيار4", "خيار4");
                int corrCol = GetCol("correctchoice", "correctoption", "correct", "الخيارالصحيح", "الإجابةالصحيحة", "الصح");

                if (qCol == -1)
                    return Json(new { success = false, message = "لم يتم العثور على عمود نص السؤال (QuestionText) في ملف الإكسيل." });

                var rows = worksheet.RowsUsed().Skip(1);
                int successCount = 0;
                int skippedCount = 0;

                using var tx = conn.BeginTransaction();
                try
                {
                    foreach (var row in rows)
                    {
                        string qText = row.Cell(qCol).GetString()?.Trim();
                        if (string.IsNullOrWhiteSpace(qText))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Points
                        int points = 5;
                        if (ptsCol != -1)
                        {
                            var pCell = row.Cell(ptsCol);
                            if (pCell.DataType == XLDataType.Number) points = (int)pCell.GetDouble();
                            else if (int.TryParse(pCell.GetString()?.Trim(), out int parsedPts) && parsedPts > 0) points = parsedPts;
                        }

                        // Difficulty
                        int difficulty = 1;
                        if (diffCol != -1)
                        {
                            string diffStr = row.Cell(diffCol).GetString()?.Trim().ToLower();
                            if (diffStr == "2" || diffStr == "medium" || diffStr == "mid" || diffStr == "متوسط") difficulty = 2;
                            else if (diffStr == "3" || diffStr == "hard" || diffStr == "صعب") difficulty = 3;
                        }

                        // TopicId
                        int? rowTopicId = quizTopicId;
                        if (topCol != -1)
                        {
                            string tStr = row.Cell(topCol).GetString()?.Trim();
                            if (!string.IsNullOrEmpty(tStr) && int.TryParse(tStr, out int parsedTid) && parsedTid > 0)
                                rowTopicId = parsedTid;
                        }

                        // Insert Question
                        string insertQSql = @"
                            INSERT INTO Questions (QuestionText, Points, Difficulty, IsActive, CategoryId, TopicId)
                            VALUES (@QuestionText, @Points, @Difficulty, 1, @CategoryId, @TopicId);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);";

                        int questionId = await conn.QuerySingleAsync<int>(insertQSql, new
                        {
                            QuestionText = qText,
                            Points = points,
                            Difficulty = difficulty,
                            CategoryId = quizCategoryId,
                            TopicId = rowTopicId
                        }, transaction: tx);

                        // Correct choice index (1 to 4)
                        int correctIdx = 1;
                        if (corrCol != -1)
                        {
                            var corrCell = row.Cell(corrCol);
                            if (corrCell.DataType == XLDataType.Number)
                            {
                                correctIdx = (int)corrCell.GetDouble();
                            }
                            else
                            {
                                string cStr = corrCell.GetString()?.Trim().ToLower();
                                if (int.TryParse(cStr, out int cVal)) correctIdx = cVal;
                                else if (cStr == "b" || cStr == "2") correctIdx = 2;
                                else if (cStr == "c" || cStr == "3") correctIdx = 3;
                                else if (cStr == "d" || cStr == "4") correctIdx = 4;
                            }
                        }

                        // Collect choices
                        var choiceCols = new[] { c1Col, c2Col, c3Col, c4Col };
                        for (int i = 0; i < choiceCols.Length; i++)
                        {
                            int colIdx = choiceCols[i];
                            string choiceText = colIdx != -1 ? row.Cell(colIdx).GetString()?.Trim() : "";
                            if (string.IsNullOrWhiteSpace(choiceText)) continue;

                            bool isCorrect = (i + 1 == correctIdx);

                            await conn.ExecuteAsync(@"
                                INSERT INTO Choices (QuestionId, ChoiceText, IsCorrect)
                                VALUES (@QuestionId, @ChoiceText, @IsCorrect)",
                                new
                                {
                                    QuestionId = questionId,
                                    ChoiceText = choiceText,
                                    IsCorrect = isCorrect
                                }, transaction: tx);
                        }

                        // Link Question to Exam/Quiz
                        await conn.ExecuteAsync(@"
                            IF NOT EXISTS (SELECT 1 FROM ExamQuestions WHERE ExamId = @ExamId AND QuestionId = @QuestionId)
                                INSERT INTO ExamQuestions (ExamId, QuestionId) VALUES (@ExamId, @QuestionId)",
                            new { ExamId = quizId, QuestionId = questionId }, transaction: tx);

                        successCount++;
                    }

                    // Update TotalQuestionsToShow in Exams
                    await conn.ExecuteAsync(@"
                        UPDATE Exams 
                        SET TotalQuestionsToShow = (SELECT COUNT(1) FROM ExamQuestions WHERE ExamId = @ExamId)
                        WHERE Id = @ExamId", new { ExamId = quizId }, transaction: tx);

                    tx.Commit();

                    return Json(new
                    {
                        success = true,
                        count = successCount,
                        skipped = skippedCount,
                        message = $"تم بنجاح رفع واستيراد {successCount} سؤال وإضافتها لبنك أسئلة الكويز."
                    });
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    return Json(new { success = false, message = "حدث خطأ أثناء معالجة شيت الأسئلة: " + ex.Message });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "فشل في قراءة ملف الإكسيل: " + ex.Message });
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
        // 9b. Get Quiz Assigned Trainees
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetQuizAssignedTrainees(int id)
        {
            if (id <= 0) return Json(new { success = false, message = "Invalid Quiz ID." });

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var quiz = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT e.Id, e.Title, e.WaveId, ISNULL(w.WaveName, 'General / All Waves') AS WaveName, e.TotalPoints, e.PassPercentage
                    FROM Exams e WITH (NOLOCK)
                    LEFT JOIN TrainingWaves w WITH (NOLOCK) ON e.WaveId = w.Id
                    WHERE e.Id = @QuizId", new { QuizId = id });

                if (quiz == null)
                    return Json(new { success = false, message = "Quiz not found." });

                // Auto-sync missing wave trainees into assignments
                if (quiz.WaveId != null)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO ExamAssignments (ExamId, StudentId, IsEmailSent, ScheduledStartTime, ScheduledEndTime)
                        SELECT @QuizId, uw.UserId, 0, GETDATE(), DATEADD(day, 30, GETDATE())
                        FROM dbo.UserWaves uw WITH (NOLOCK)
                        WHERE uw.WaveId = @WaveId AND uw.IsActive = 1
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.ExamAssignments ea WITH (NOLOCK)
                              WHERE ea.ExamId = @QuizId AND ea.StudentId = uw.UserId
                          )", new { QuizId = id, WaveId = quiz.WaveId });
                }

                var trainees = (await conn.QueryAsync<dynamic>(@"
                    WITH AssignedUsers AS (
                        SELECT DISTINCT StudentId AS UserId FROM dbo.ExamAssignments WITH (NOLOCK) WHERE ExamId = @QuizId
                        UNION
                        SELECT DISTINCT uw.UserId FROM dbo.UserWaves uw WITH (NOLOCK)
                        INNER JOIN dbo.Exams e WITH (NOLOCK) ON e.Id = @QuizId AND e.WaveId = uw.WaveId
                        WHERE uw.IsActive = 1
                    ),
                    LatestAttempts AS (
                        SELECT UserId, ExamId, Score, Status, StartTime, AttemptNumber,
                               ROW_NUMBER() OVER (PARTITION BY UserId, ExamId ORDER BY AttemptNumber DESC, StartTime DESC) as rn
                        FROM dbo.UserExamAttempts WITH (NOLOCK)
                        WHERE ExamId = @QuizId
                    ),
                    UserRoles AS (
                        SELECT ur.UserId, MAX(r.Name) as RoleName
                        FROM AspNetUserRoles ur WITH (NOLOCK)
                        JOIN AspNetRoles r WITH (NOLOCK) ON ur.RoleId = r.Id
                        GROUP BY ur.UserId
                    )
                    SELECT 
                        u.Id AS UserId,
                        ISNULL(NULLIF(u.FullName, ''), u.UserName) AS StudentName,
                        u.FullName,
                        u.UserName,
                        u.UserCode,
                        u.Email,
                        u.PhoneNumber,
                        ISNULL(b.BranchName, '--') AS BranchName,
                        ISNULL(s.ShiftName, 'Any Shift') AS ShiftName,
                        COALESCE(NULLIF(u.RoleName, ''), ur.RoleName, 'Trainee') AS RoleName,
                        CASE 
                            WHEN la.Status = 'Completed' THEN 'Completed'
                            WHEN la.Status = 'InProgress' THEN 'InProgress'
                            WHEN la.Status = 'Fail_Timeout' THEN 'Timeout'
                            ELSE 'Not Started'
                        END AS AttemptStatus,
                        la.Score AS Score,
                        la.StartTime AS AttemptDate
                    FROM AssignedUsers au
                    INNER JOIN AspNetUsers u WITH (NOLOCK) ON au.UserId = u.Id
                    LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                    LEFT JOIN Shifts s WITH (NOLOCK) ON u.ShiftId = s.Id
                    LEFT JOIN UserRoles ur ON u.Id = ur.UserId
                    LEFT JOIN LatestAttempts la ON u.Id = la.UserId AND la.rn = 1
                    ORDER BY u.UserCode ASC", new { QuizId = id })).ToList();

                var traineeList = trainees.Select(t => new
                {
                    userId = (string)(t.UserId?.ToString() ?? ""),
                    studentName = (string)(t.StudentName ?? t.FullName ?? t.UserName ?? "-"),
                    fullName = (string)(t.FullName ?? t.StudentName ?? t.UserName ?? "-"),
                    userCode = (string)(t.UserCode ?? ""),
                    email = (string)(t.Email ?? ""),
                    phoneNumber = (string)(t.PhoneNumber ?? ""),
                    branchName = (string)(t.BranchName ?? "--"),
                    shiftName = (string)(t.ShiftName ?? "Any Shift"),
                    roleName = (string)(t.RoleName ?? "Trainee"),
                    attemptStatus = (string)(t.AttemptStatus ?? "Not Started"),
                    score = t.Score != null ? (decimal?)Convert.ToDecimal(t.Score) : null,
                    attemptDate = t.AttemptDate != null ? ((DateTime)t.AttemptDate).ToString("yyyy-MM-dd HH:mm") : null
                }).ToList();

                int total = traineeList.Count;
                int completed = traineeList.Count(t => t.attemptStatus == "Completed");
                int notStarted = traineeList.Count(t => t.attemptStatus == "Not Started");
                int inProgress = traineeList.Count(t => t.attemptStatus == "InProgress");

                return Json(new
                {
                    success = true,
                    quizId = quiz.Id,
                    quizTitle = (string)quiz.Title,
                    waveName = (string)quiz.WaveName,
                    totalAssigned = total,
                    completedCount = completed,
                    notStartedCount = notStarted,
                    inProgressCount = inProgress,
                    trainees = traineeList
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ==========================================
        // 9c. Sync Wave Trainees to Quiz
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> SyncWaveTraineesToQuiz(int id)
        {
            if (id <= 0) return Json(new { success = false, message = "Invalid Quiz ID." });

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var quiz = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT Id, Title, WaveId, StartTime, EndTime FROM Exams WHERE Id = @Id", new { Id = id });

                if (quiz == null) return Json(new { success = false, message = "Quiz not found." });
                if (quiz.WaveId == null) return Json(new { success = false, message = "This quiz is not linked to any Wave." });

                int synced = await conn.ExecuteAsync(@"
                    INSERT INTO ExamAssignments (ExamId, StudentId, IsEmailSent, ScheduledStartTime, ScheduledEndTime)
                    SELECT @ExamId, uw.UserId, 0, @StartTime, @EndTime
                    FROM dbo.UserWaves uw WITH (NOLOCK)
                    WHERE uw.WaveId = @WaveId AND uw.IsActive = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.ExamAssignments ea WITH (NOLOCK)
                          WHERE ea.ExamId = @ExamId AND ea.StudentId = uw.UserId
                      )", new
                {
                    ExamId = id,
                    WaveId = (int)quiz.WaveId,
                    StartTime = (DateTime?)quiz.StartTime ?? DateTime.Now,
                    EndTime = (DateTime?)quiz.EndTime ?? DateTime.Now.AddDays(30)
                });

                return Json(new { success = true, message = $"Successfully synced {synced} new trainee(s) to this quiz.", count = synced });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
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
                return Json(new { success = false, message = $"مجموع الدرجات يجب أن يساوي 100 درجة (المجموع الحالي: {totalWeights} درجة)." });
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

            return Json(new { success = true, message = "تم حفظ وتحديث توزيع درجات الشهادة بنجاح." });
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
