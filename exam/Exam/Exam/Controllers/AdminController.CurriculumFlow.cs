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

namespace Exam.Controllers
{
    public partial class AdminController
    {
        private static bool _curriculumFlowWeightsEnsured = false;
        private async Task EnsureCurriculumFlowWeightsColumnsAsync(SqlConnection conn)
        {
            if (_curriculumFlowWeightsEnsured) return;
            try
            {
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WaveScoreWeights')
                    BEGIN
                        CREATE TABLE dbo.WaveScoreWeights (
                            Id INT IDENTITY(1,1) PRIMARY KEY,
                            WaveId INT NOT NULL,
                            QuizzesWeight DECIMAL(5,2) NOT NULL DEFAULT 25,
                            AssignmentsWeight DECIMAL(5,2) NOT NULL DEFAULT 15,
                            FinalExamWeight DECIMAL(5,2) NOT NULL DEFAULT 35,
                            PassingPercentage DECIMAL(5,2) NOT NULL DEFAULT 60,
                            UpdatedAt DATETIME NULL DEFAULT GETDATE(),
                            CONSTRAINT UQ_WaveScoreWeights_WaveId UNIQUE (WaveId)
                        );
                    END

                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.WaveScoreWeights') AND name = 'CategoryExamsWeight')
                    BEGIN
                        ALTER TABLE dbo.WaveScoreWeights ADD CategoryExamsWeight DECIMAL(5,2) NOT NULL DEFAULT 25;
                    END
                ");
                _curriculumFlowWeightsEnsured = true;
            }
            catch { }
        }

        // ==========================================
        // 1. Wave Curriculum Flow View
        // ==========================================
        [HttpGet("Admin/WaveCurriculumFlow/{waveId?}")]
        [Authorize(Roles = "Admin,HR,Human Resources,Branch Manager,Branch Supervisor,SoftSkills Specialist")]
        public async Task<IActionResult> WaveCurriculumFlow(int? waveId)
        {
            ViewBag.Title = "Wave Curriculum & Assessment Flow Tracker";
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await EnsureCurriculumFlowWeightsColumnsAsync(conn);

            var waves = (await conn.QueryAsync<dynamic>(@"
                SELECT Id, WaveName, StartDate, EndDate, IsOnline, Mode
                FROM TrainingWaves WITH (NOLOCK)
                ORDER BY Id DESC")).ToList();

            ViewBag.Waves = waves;
            int selectedWaveId = waveId ?? (waves.FirstOrDefault() != null ? (int)waves.First().Id : 0);
            ViewBag.ActiveWaveId = selectedWaveId;

            return View();
        }

        // ==========================================
        // 2. Get Full Wave Curriculum Flow Data (Hierarchy, Quizzes, Category Exams, Final Exam, Assignments & Trainees Rollup)
        // ==========================================
        [HttpGet("Admin/GetWaveCurriculumFlowData")]
        [Authorize(Roles = "Admin,HR,Human Resources,Branch Manager,Branch Supervisor,SoftSkills Specialist")]
        public async Task<IActionResult> GetWaveCurriculumFlowData(int waveId)
        {
            if (waveId <= 0)
                return Json(new { success = false, message = "معرّف الويف غير صالح." });

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureCurriculumFlowWeightsColumnsAsync(conn);

                // 1. Fetch Wave
                var wave = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT Id, WaveName, StartDate, EndDate, Mode, IsOnline
                    FROM TrainingWaves WITH (NOLOCK)
                    WHERE Id = @WaveId", new { WaveId = waveId });

                if (wave == null)
                    return Json(new { success = false, message = "الويف المحدد غير موجود." });

                // 2. Fetch Weights
                var weightsRow = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT QuizzesWeight, ISNULL(CategoryExamsWeight, 25) AS CategoryExamsWeight, 
                           AssignmentsWeight, FinalExamWeight, PassingPercentage
                    FROM WaveScoreWeights WITH (NOLOCK)
                    WHERE WaveId = @WaveId", new { WaveId = waveId });

                decimal quizzesWeight = weightsRow != null ? (decimal)weightsRow.QuizzesWeight : 25m;
                decimal catExamsWeight = weightsRow != null ? (decimal)weightsRow.CategoryExamsWeight : 25m;
                decimal assignmentsWeight = weightsRow != null ? (decimal)weightsRow.AssignmentsWeight : 15m;
                decimal finalExamWeight = weightsRow != null ? (decimal)weightsRow.FinalExamWeight : 35m;
                decimal passPercentage = weightsRow != null ? (decimal)weightsRow.PassingPercentage : 60m;

                // 3. Fetch Wave Categories and their Topics
                var categoriesRaw = (await conn.QueryAsync<dynamic>(@"
                    SELECT Id, CategoryName
                    FROM Categories WITH (NOLOCK)
                    WHERE ExamTypeId = 2 OR Id IN (SELECT DISTINCT CategoryId FROM Exams WITH (NOLOCK) WHERE WaveId = @WaveId AND CategoryId IS NOT NULL)
                    ORDER BY CategoryName ASC", new { WaveId = waveId })).ToList();

                var topicsRaw = (await conn.QueryAsync<dynamic>(@"
                    SELECT t.Id, t.TopicName, t.CategoryId
                    FROM Topics t WITH (NOLOCK)
                    ORDER BY t.TopicName ASC")).ToList();

                // 4. Fetch All Wave Exams (Quizzes, Category Exams, and Comprehensive Final)
                var waveExams = (await conn.QueryAsync<dynamic>(@"
                    SELECT 
                        e.Id,
                        e.Title,
                        e.CategoryId,
                        e.TopicId,
                        ISNULL(e.IsQuiz, 0) AS IsQuiz,
                        ISNULL(e.IsFinalExam, 0) AS IsFinalExam,
                        e.ExamTypeId,
                        ISNULL(e.TotalPoints, 100) AS TotalPoints,
                        e.DurationInMinutes,
                        e.PassPercentage,
                        e.IsActive,
                        (SELECT COUNT(1) FROM ExamQuestions eq WITH (NOLOCK) WHERE eq.ExamId = e.Id) AS QuestionsCount,
                        (SELECT COUNT(DISTINCT uea.UserId) FROM UserExamAttempts uea WITH (NOLOCK) WHERE uea.ExamId = e.Id AND uea.Status IN ('Completed', 'Fail_Timeout')) AS CompletedAttemptsCount,
                        (SELECT ISNULL(ROUND(AVG(CAST(uea.Score AS FLOAT)), 1), 0) FROM UserExamAttempts uea WITH (NOLOCK) WHERE uea.ExamId = e.Id AND uea.Status IN ('Completed', 'Fail_Timeout')) AS AverageScore
                    FROM Exams e WITH (NOLOCK)
                    WHERE e.WaveId = @WaveId
                    ORDER BY e.Id ASC", new { WaveId = waveId })).ToList();

                // 5. Fetch Wave Continuous Assignments
                var waveAssignments = (await conn.QueryAsync<dynamic>(@"
                    SELECT 
                        a.Id,
                        a.Title,
                        a.ScheduledStartTime,
                        a.ScheduledEndTime,
                        ISNULL(a.PharmacistMaxScore, 10) AS PharmacistMaxScore,
                        ISNULL(a.AssistantMaxScore, 10) AS AssistantMaxScore,
                        (SELECT COUNT(DISTINCT saa.UserId) FROM StudentAssignmentAttempts saa WITH (NOLOCK) WHERE saa.AssignmentId = a.Id AND saa.Status = 'Completed') AS SubmissionsCount,
                        (SELECT ISNULL(ROUND(AVG(CAST(saa.Score AS FLOAT)), 1), 0) FROM StudentAssignmentAttempts saa WITH (NOLOCK) WHERE saa.AssignmentId = a.Id AND saa.Status = 'Completed') AS AverageScore
                    FROM Assignments a WITH (NOLOCK)
                    WHERE a.WaveId = @WaveId
                    ORDER BY a.ScheduledStartTime ASC, a.Id ASC", new { WaveId = waveId })).ToList();

                // 6. Build Category & Topic Tree
                var categoriesTree = new List<object>();
                int totalTopicQuizzesConfigured = 0;
                int totalCategoryExamsConfigured = 0;

                foreach (var cat in categoriesRaw)
                {
                    int catId = (int)cat.Id;
                    var catTopics = topicsRaw.Where(t => (int)t.CategoryId == catId).ToList();
                    bool hasTopics = catTopics.Any();

                    var topicsList = new List<object>();

                    if (hasTopics)
                    {
                        foreach (var top in catTopics)
                        {
                            int topId = (int)top.Id;
                            // Find Topic Quiz
                            var quizExam = waveExams.FirstOrDefault(e => (int?)e.TopicId == topId && ((bool)e.IsQuiz || (int)e.ExamTypeId == 9 || ((string)e.Title ?? "").ToLower().Contains("quiz")));

                            if (quizExam != null) totalTopicQuizzesConfigured++;

                            topicsList.Add(new
                            {
                                topicId = topId,
                                topicName = (string)top.TopicName,
                                hasQuiz = quizExam != null,
                                quiz = quizExam != null ? new
                                {
                                    examId = (int)quizExam.Id,
                                    title = (string)quizExam.Title,
                                    duration = (int)quizExam.DurationInMinutes,
                                    totalPoints = (decimal)quizExam.TotalPoints,
                                    questionsCount = (int)quizExam.QuestionsCount,
                                    attemptsCount = (int)quizExam.CompletedAttemptsCount,
                                    averageScore = (double)quizExam.AverageScore,
                                    isActive = (bool)quizExam.IsActive
                                } : null
                            });
                        }
                    }

                    // Category-level exam (where TopicId is NULL)
                    // If Category has topics -> Category Final Exam
                    // If Category has NO topics -> Direct Category Exam (e.g. Health Insurance)
                    var catExam = waveExams.FirstOrDefault(e => (int?)e.CategoryId == catId && e.TopicId == null && (!(bool)e.IsQuiz && (int)e.ExamTypeId != 9));
                    if (catExam != null) totalCategoryExamsConfigured++;

                    categoriesTree.Add(new
                    {
                        categoryId = catId,
                        categoryName = (string)cat.CategoryName,
                        hasTopics = hasTopics,
                        topicsCount = catTopics.Count,
                        topics = topicsList,
                        categoryExam = catExam != null ? new
                        {
                            examId = (int)catExam.Id,
                            title = (string)catExam.Title,
                            duration = (int)catExam.DurationInMinutes,
                            totalPoints = (decimal)catExam.TotalPoints,
                            questionsCount = (int)catExam.QuestionsCount,
                            attemptsCount = (int)catExam.CompletedAttemptsCount,
                            averageScore = (double)catExam.AverageScore,
                            isActive = (bool)catExam.IsActive,
                            isFinalOfCategory = hasTopics,
                            isDirectExam = !hasTopics
                        } : null
                    });
                }

                // 7. Comprehensive Wave Final Exam
                var comprehensiveFinalExam = waveExams.FirstOrDefault(e => (bool)e.IsFinalExam || (int)e.ExamTypeId == 8 || ((string)e.Title ?? "").ToLower().Contains("final exam") || ((string)e.Title ?? "").ToLower().Contains("فاينال"));

                // 8. Fetch Enrolled Trainees & Calculate Cumulative Rollup Degrees
                var traineesRaw = (await conn.QueryAsync<dynamic>(@"
                    SELECT 
                        u.Id AS UserId,
                        u.UserCode,
                        ISNULL(NULLIF(u.FullName, ''), u.UserName) AS FullName,
                        u.Email,
                        ISNULL((
                            SELECT TOP 1 r.Name 
                            FROM dbo.AspNetUserRoles ur WITH (NOLOCK)
                            JOIN dbo.AspNetRoles r WITH (NOLOCK) ON ur.RoleId = r.Id 
                            WHERE ur.UserId = u.Id
                        ), 'Trainee') AS RoleName,
                        ISNULL(b.BranchName, N'غير محدد') AS BranchName,
                        uwc.Score AS CurrentCertScore,
                        uwc.CertificateCode,
                        ISNULL(uwc.FileUploaded, 0) AS FileUploaded
                    FROM UserWaves uw WITH (NOLOCK)
                    JOIN AspNetUsers u WITH (NOLOCK) ON uw.UserId = u.Id
                    LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
                    LEFT JOIN UserWaveCertificates uwc WITH (NOLOCK) ON uw.UserId = uwc.UserId AND uwc.WaveId = @WaveId
                    WHERE uw.WaveId = @WaveId AND (uw.IsActive = 1 OR uw.IsActive IS NULL)
                    ORDER BY u.FullName ASC", new { WaveId = waveId })).ToList();

                // Pre-fetch all exam attempts for this wave
                var allWaveExamAttempts = (await conn.QueryAsync<dynamic>(@"
                    SELECT uea.UserId, uea.ExamId, MAX(uea.Score) AS BestScore
                    FROM UserExamAttempts uea WITH (NOLOCK)
                    JOIN Exams e WITH (NOLOCK) ON uea.ExamId = e.Id
                    WHERE e.WaveId = @WaveId AND uea.Status IN ('Completed', 'Fail_Timeout')
                    GROUP BY uea.UserId, uea.ExamId", new { WaveId = waveId })).ToList();

                // Pre-fetch all assignment attempts for this wave
                var allWaveAssignAttempts = (await conn.QueryAsync<dynamic>(@"
                    SELECT saa.UserId, saa.AssignmentId, MAX(saa.Score) AS BestScore, MAX(ISNULL(saa.TrainerComment, '')) AS TrainerComment
                    FROM StudentAssignmentAttempts saa WITH (NOLOCK)
                    JOIN Assignments a WITH (NOLOCK) ON saa.AssignmentId = a.Id
                    WHERE a.WaveId = @WaveId AND saa.Status = 'Completed'
                    GROUP BY saa.UserId, saa.AssignmentId", new { WaveId = waveId })).ToList();

                var examAttemptsLookup = allWaveExamAttempts.ToLookup(x => (string)x.UserId, x => new { ExamId = (int)x.ExamId, Score = (decimal)x.BestScore });
                var assignAttemptsLookup = allWaveAssignAttempts.ToLookup(x => (string)x.UserId, x => new { AssignmentId = (int)x.AssignmentId, Score = (decimal)x.BestScore });

                // Identify list of Quiz exams and Category exams
                var topicQuizExamIds = waveExams.Where(e => e.TopicId != null && ((bool)e.IsQuiz || (int)e.ExamTypeId == 9 || ((string)e.Title ?? "").ToLower().Contains("quiz"))).Select(e => (int)e.Id).ToList();
                var catExamIds = waveExams.Where(e => e.CategoryId != null && e.TopicId == null && (!(bool)e.IsQuiz && (int)e.ExamTypeId != 9)).Select(e => (int)e.Id).ToList();
                int? finalExamId = comprehensiveFinalExam != null ? (int)comprehensiveFinalExam.Id : (int?)null;

                var traineesGradebook = new List<object>();
                int eligibleCount = 0;
                int certifiedCount = 0;

                foreach (var tr in traineesRaw)
                {
                    string uid = (string)tr.UserId;
                    var userExams = examAttemptsLookup[uid].ToDictionary(x => x.ExamId, x => x.Score);
                    var userAssigns = assignAttemptsLookup[uid].ToDictionary(x => x.AssignmentId, x => x.Score);

                    // 1. Quizzes Avg %
                    decimal quizzesAvg = 0m;
                    int quizzesTaken = 0;
                    if (topicQuizExamIds.Any())
                    {
                        decimal qSum = 0m;
                        foreach (var qid in topicQuizExamIds)
                        {
                            if (userExams.TryGetValue(qid, out decimal score))
                            {
                                qSum += score;
                                quizzesTaken++;
                            }
                        }
                        quizzesAvg = Math.Round(qSum / topicQuizExamIds.Count, 1);
                    }

                    // 2. Category Exams Avg %
                    decimal catExamsAvg = 0m;
                    int catExamsTaken = 0;
                    if (catExamIds.Any())
                    {
                        decimal cSum = 0m;
                        foreach (var cid in catExamIds)
                        {
                            if (userExams.TryGetValue(cid, out decimal score))
                            {
                                cSum += score;
                                catExamsTaken++;
                            }
                        }
                        catExamsAvg = Math.Round(cSum / catExamIds.Count, 1);
                    }

                    // 3. Assignments Avg %
                    decimal assignmentsAvg = 0m;
                    int assignsTaken = 0;
                    if (waveAssignments.Any())
                    {
                        decimal aSum = 0m;
                        string role = (string)(tr.RoleName ?? "");
                        bool isPharm = role.ToLower().Contains("pharm");

                        foreach (var a in waveAssignments)
                        {
                            int aId = (int)a.Id;
                            decimal maxScore = isPharm ? (decimal)a.PharmacistMaxScore : (decimal)a.AssistantMaxScore;
                            if (maxScore <= 0) maxScore = 10m;

                            if (userAssigns.TryGetValue(aId, out decimal earned))
                            {
                                decimal p = Math.Min(100m, (earned / maxScore) * 100m);
                                aSum += p;
                                assignsTaken++;
                            }
                        }
                        assignmentsAvg = Math.Round(aSum / waveAssignments.Count, 1);
                    }

                    // 4. Wave Final Exam %
                    decimal finalExamScore = 0m;
                    bool finalTaken = false;
                    if (finalExamId.HasValue && userExams.TryGetValue(finalExamId.Value, out decimal fScore))
                    {
                        finalExamScore = Math.Round(fScore, 1);
                        finalTaken = true;
                    }

                    // 5. Total Cumulative Weighted Degree (out of 100)
                    decimal activeWeightSum = 0m;
                    decimal rawTotal = 0m;

                    if (topicQuizExamIds.Any()) { rawTotal += (quizzesAvg * (quizzesWeight / 100m)); activeWeightSum += quizzesWeight; }
                    if (catExamIds.Any()) { rawTotal += (catExamsAvg * (catExamsWeight / 100m)); activeWeightSum += catExamsWeight; }
                    if (waveAssignments.Any()) { rawTotal += (assignmentsAvg * (assignmentsWeight / 100m)); activeWeightSum += assignmentsWeight; }
                    if (finalExamId.HasValue) { rawTotal += (finalExamScore * (finalExamWeight / 100m)); activeWeightSum += finalExamWeight; }

                    decimal totalDegree = activeWeightSum > 0 && activeWeightSum < 100m
                        ? Math.Round((rawTotal / activeWeightSum) * 100m, 1)
                        : Math.Round(rawTotal, 1);

                    bool isPassed = totalDegree >= passPercentage;
                    bool hasCert = !string.IsNullOrEmpty((string)tr.CertificateCode);
                    if (isPassed) eligibleCount++;
                    if (hasCert) certifiedCount++;

                    string eligibilityStatus = isPassed ? "مؤهل للاعتماد والشهادة" : "أقل من درجة النجاح المطلوبة";

                    traineesGradebook.Add(new
                    {
                        userId = uid,
                        userCode = (string)tr.UserCode,
                        fullName = (string)tr.FullName,
                        email = (string)tr.Email,
                        roleName = (string)tr.RoleName,
                        branchName = (string)tr.BranchName,
                        quizzesAvg = quizzesAvg,
                        quizzesTaken = quizzesTaken,
                        totalQuizzes = topicQuizExamIds.Count,
                        catExamsAvg = catExamsAvg,
                        catExamsTaken = catExamsTaken,
                        totalCatExams = catExamIds.Count,
                        assignmentsAvg = assignmentsAvg,
                        assignmentsTaken = assignsTaken,
                        totalAssignments = waveAssignments.Count,
                        finalExamScore = finalExamScore,
                        finalTaken = finalTaken,
                        totalDegree = totalDegree,
                        isPassed = isPassed,
                        eligibilityStatus = eligibilityStatus,
                        currentCertScore = tr.CurrentCertScore != null ? (decimal)tr.CurrentCertScore : (decimal?)null,
                        certificateCode = (string)tr.CertificateCode,
                        fileUploaded = tr.FileUploaded != null && (bool)tr.FileUploaded
                    });
                }

                return Json(new
                {
                    success = true,
                    wave = new
                    {
                        id = (int)wave.Id,
                        waveName = (string)wave.WaveName,
                        startDate = wave.StartDate != null ? ((DateTime)wave.StartDate).ToString("yyyy-MM-dd") : null,
                        endDate = wave.EndDate != null ? ((DateTime)wave.EndDate).ToString("yyyy-MM-dd") : null,
                        mode = (string)wave.Mode,
                        isOnline = (bool)wave.IsOnline
                    },
                    weights = new
                    {
                        quizzesWeight = quizzesWeight,
                        categoryExamsWeight = catExamsWeight,
                        assignmentsWeight = assignmentsWeight,
                        finalExamWeight = finalExamWeight,
                        passingPercentage = passPercentage
                    },
                    summaryStats = new
                    {
                        totalCategories = categoriesRaw.Count,
                        totalTopics = topicsRaw.Count,
                        totalQuizzesConfigured = totalTopicQuizzesConfigured,
                        totalCategoryExamsConfigured = totalCategoryExamsConfigured,
                        totalAssignments = waveAssignments.Count,
                        hasWaveFinalExam = comprehensiveFinalExam != null,
                        totalEnrolled = traineesRaw.Count,
                        eligibleCount = eligibleCount,
                        certifiedCount = certifiedCount
                    },
                    categories = categoriesTree,
                    waveFinalExam = comprehensiveFinalExam != null ? new
                    {
                        examId = (int)comprehensiveFinalExam.Id,
                        title = (string)comprehensiveFinalExam.Title,
                        totalPoints = (decimal)comprehensiveFinalExam.TotalPoints,
                        duration = (int)comprehensiveFinalExam.DurationInMinutes,
                        questionsCount = (int)comprehensiveFinalExam.QuestionsCount,
                        attemptsCount = (int)comprehensiveFinalExam.CompletedAttemptsCount,
                        averageScore = (double)comprehensiveFinalExam.AverageScore,
                        isActive = (bool)comprehensiveFinalExam.IsActive
                    } : null,
                    assignments = waveAssignments.Select(a => new
                    {
                        id = (int)a.Id,
                        title = (string)a.Title,
                        scheduledStartTime = a.ScheduledStartTime != null ? ((DateTime)a.ScheduledStartTime).ToString("yyyy-MM-dd HH:mm") : null,
                        scheduledEndTime = a.ScheduledEndTime != null ? ((DateTime)a.ScheduledEndTime).ToString("yyyy-MM-dd HH:mm") : null,
                        pharmacistMaxScore = (decimal)a.PharmacistMaxScore,
                        assistantMaxScore = (decimal)a.AssistantMaxScore,
                        submissionsCount = (int)a.SubmissionsCount,
                        averageScore = (double)a.AverageScore
                    }).ToList(),
                    trainees = traineesGradebook
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء تحميل بيانات فلو المنهج: " + ex.Message });
            }
        }

        // ==========================================
        // 3. Save Custom Curriculum Weights
        // ==========================================
        public class CurriculumWeightsModel
        {
            public int WaveId { get; set; }
            public decimal QuizzesWeight { get; set; }
            public decimal CategoryExamsWeight { get; set; }
            public decimal AssignmentsWeight { get; set; }
            public decimal FinalExamWeight { get; set; }
            public decimal PassingPercentage { get; set; }
        }

        [HttpPost("Admin/SaveCurriculumFlowWeights")]
        [Authorize(Roles = "Admin,HR,Human Resources,Branch Manager,Branch Supervisor,SoftSkills Specialist")]
        public async Task<IActionResult> SaveCurriculumFlowWeights([FromBody] CurriculumWeightsModel model)
        {
            if (model == null || model.WaveId <= 0)
                return Json(new { success = false, message = "معرّف الويف غير صالح." });

            decimal totalWeights = model.QuizzesWeight + model.CategoryExamsWeight + model.AssignmentsWeight + model.FinalExamWeight;
            if (Math.Abs(totalWeights - 100m) > 0.01m)
            {
                return Json(new { success = false, message = $"مجموع الأوزان والدرجات يجب أن يساوي 100 درجة بالضبط (المجموع الحالي: {totalWeights} درجة)." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureCurriculumFlowWeightsColumnsAsync(conn);

                string mergeSql = @"
                    MERGE INTO dbo.WaveScoreWeights AS target
                    USING (SELECT @WaveId AS WaveId) AS source
                    ON target.WaveId = source.WaveId
                    WHEN MATCHED THEN
                        UPDATE SET QuizzesWeight = @QuizzesWeight, 
                                   CategoryExamsWeight = @CategoryExamsWeight,
                                   AssignmentsWeight = @AssignmentsWeight, 
                                   FinalExamWeight = @FinalExamWeight, 
                                   PassingPercentage = @PassingPercentage, 
                                   UpdatedAt = GETDATE()
                    WHEN NOT MATCHED THEN
                        INSERT (WaveId, QuizzesWeight, CategoryExamsWeight, AssignmentsWeight, FinalExamWeight, PassingPercentage, UpdatedAt)
                        VALUES (@WaveId, @QuizzesWeight, @CategoryExamsWeight, @AssignmentsWeight, @FinalExamWeight, @PassingPercentage, GETDATE());";

                await conn.ExecuteAsync(mergeSql, new
                {
                    WaveId = model.WaveId,
                    QuizzesWeight = model.QuizzesWeight,
                    CategoryExamsWeight = model.CategoryExamsWeight,
                    AssignmentsWeight = model.AssignmentsWeight,
                    FinalExamWeight = model.FinalExamWeight,
                    PassingPercentage = model.PassingPercentage > 0 ? model.PassingPercentage : 60m
                });

                return Json(new { success = true, message = "تم حفظ توزيع درجات فلو المنهج بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "خطأ في حفظ الأوزان: " + ex.Message });
            }
        }

        // ==========================================
        // 4. Interactive Flow Simulator & Test Lab
        // ==========================================
        public class SimulationInputModel
        {
            public decimal QuizzesAvg { get; set; } = 85m;
            public decimal CategoryExamsAvg { get; set; } = 80m;
            public decimal AssignmentsAvg { get; set; } = 90m;
            public decimal WaveFinalScore { get; set; } = 75m;

            public decimal QuizzesWeight { get; set; } = 25m;
            public decimal CategoryExamsWeight { get; set; } = 25m;
            public decimal AssignmentsWeight { get; set; } = 15m;
            public decimal FinalExamWeight { get; set; } = 35m;
            public decimal PassingThreshold { get; set; } = 60m;
        }

        [HttpPost("Admin/SimulateWaveCurriculumFlow")]
        [Authorize(Roles = "Admin,HR,Human Resources,Branch Manager,Branch Supervisor,SoftSkills Specialist")]
        public IActionResult SimulateWaveCurriculumFlow([FromBody] SimulationInputModel model)
        {
            if (model == null) model = new SimulationInputModel();

            // Calculate contributions
            decimal qContrib = Math.Round((model.QuizzesAvg * model.QuizzesWeight) / 100m, 2);
            decimal cContrib = Math.Round((model.CategoryExamsAvg * model.CategoryExamsWeight) / 100m, 2);
            decimal aContrib = Math.Round((model.AssignmentsAvg * model.AssignmentsWeight) / 100m, 2);
            decimal fContrib = Math.Round((model.WaveFinalScore * model.FinalExamWeight) / 100m, 2);

            decimal totalDegree = qContrib + cContrib + aContrib + fContrib;
            totalDegree = Math.Min(100m, Math.Max(0m, Math.Round(totalDegree, 2)));

            bool isPassed = totalDegree >= model.PassingThreshold;

            string gradeRating = "راسب (Fail)";
            string ratingColor = "rose";
            if (totalDegree >= 85m) { gradeRating = "ممتاز (Excellent)"; ratingColor = "emerald"; }
            else if (totalDegree >= 75m) { gradeRating = "جيد جداً (Very Good)"; ratingColor = "blue"; }
            else if (totalDegree >= 65m) { gradeRating = "جيد (Good)"; ratingColor = "indigo"; }
            else if (totalDegree >= model.PassingThreshold) { gradeRating = "مقبول (Pass)"; ratingColor = "amber"; }

            string mockCertificateCode = isPassed 
                ? $"WTTA-{DateTime.Now.Year}-SIM-{new Random().Next(1000, 9999)}"
                : "غير مؤهل لصدور الشهادة";

            var stepsLog = new List<string>
            {
                $"المرحلة 1: كويزات التوبيكس ({model.QuizzesAvg}% × وزن {model.QuizzesWeight}%) = {qContrib} درجة من إجمالي الشهادة.",
                $"المرحلة 2: امتحانات الكاتيجوريز ({model.CategoryExamsAvg}% × وزن {model.CategoryExamsWeight}%) = {cContrib} درجة من إجمالي الشهادة.",
                $"المرحلة 3: التكليفات الدورية ({model.AssignmentsAvg}% × وزن {model.AssignmentsWeight}%) = {aContrib} درجة من إجمالي الشهادة.",
                $"المرحلة 4: الامتحان الفاينال الشامل ({model.WaveFinalScore}% × وزن {model.FinalExamWeight}%) = {fContrib} درجة من إجمالي الشهادة.",
                $"النتيجة المركبة: {qContrib} + {cContrib} + {aContrib} + {fContrib} = {totalDegree} / 100 درجة.",
                isPassed 
                    ? $"القرار: المتدرب حقق {totalDegree}% (أعلى من حد النجاح {model.PassingThreshold}%) ⬅️ اعتماد الشهادة بنجاح!"
                    : $"القرار: المتدرب حقق {totalDegree}% (أقل من حد النجاح {model.PassingThreshold}%) ⬅️ يتطلب إعادة أو تحسين إحدى المراحل."
            };

            return Json(new
            {
                success = true,
                simulation = new
                {
                    quizzesContribution = qContrib,
                    categoryExamsContribution = cContrib,
                    assignmentsContribution = aContrib,
                    finalExamContribution = fContrib,
                    totalDegree = totalDegree,
                    isPassed = isPassed,
                    gradeRating = gradeRating,
                    ratingColor = ratingColor,
                    mockCertificateCode = mockCertificateCode,
                    stepsLog = stepsLog
                }
            });
        }
    }
}

