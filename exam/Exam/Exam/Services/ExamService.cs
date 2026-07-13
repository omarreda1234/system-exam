using System.Data;
using System.Collections.Generic;
using System.Threading.Tasks;
using Exam.DTOs;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Linq;
using System;
using Microsoft.Extensions.Configuration;
using Exam.Models;

namespace Exam.Services
{
    public class ExamService : IExamService
    {
        private readonly string _connectionString;
        private readonly IEmailSender _emailSender;

        public ExamService(IConfiguration configuration, IEmailSender emailSender)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _emailSender = emailSender;
        }

        public async Task<ExamDetailsDto> GetExamDetailsAsync(int examId)
        {
            // 1. Get Exam basic info
            var examInfo = await GetExamByIdAsync(examId);

            var details = new ExamDetailsDto
            {
                ExamId = examId,
                Title = examInfo?.Title ?? "Unknown Exam",
                ExamTypeId = examInfo?.ExamTypeId,
                PassPercentage = examInfo?.PassPercentage ?? 50
            };

            // 2. Load Questions with choices using the dedicated SP for this
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<dynamic>("sp_GetAllQuestionsWithChoicesByExamId", new { ExamId = examId }, commandType: CommandType.StoredProcedure);

            // 3. To ensure TopicId and accurate Category names are available (in case SP is old)
            // We fetch the IDs from the rows and then get full details from Questions table
            var questionIds = rows.Select(r => (int?)r.QuestionId).Where(id => id.HasValue && id > 0).Distinct().ToList();
            IEnumerable<dynamic> fullDetails = new List<dynamic>();
            if (questionIds.Any())
            {
                fullDetails = await conn.QueryAsync<dynamic>(@"
                    SELECT Q.Id as QuestionId, Q.QuestionText, Q.Points, Q.CategoryId, Q.Difficulty, Q.TopicId,
                           C.CategoryName as CategoryName, T.TopicName, CH.Id as ChoiceId, CH.ChoiceText, CH.IsCorrect
                    FROM Questions Q
                    LEFT JOIN Categories C ON Q.CategoryId = C.Id
                    LEFT JOIN Topics T ON Q.TopicId = T.Id
                    LEFT JOIN Choices CH ON Q.Id = CH.QuestionId
                    WHERE Q.Id IN @Ids", new { Ids = questionIds });
            }

            foreach (var r in fullDetails)
            {
                int qId = (int)r.QuestionId;
                var question = details.Questions.FirstOrDefault(q => q.QuestionId == qId);
                if (question == null)
                {
                    question = new QuestionDetailDto 
                    { 
                        QuestionId = qId, 
                        QuestionText = r.QuestionText, 
                        Points = r.Points,
                        CategoryId = r.CategoryId,
                        CategoryName = r.CategoryName,
                        Difficulty = r.Difficulty,
                        TopicId = r.TopicId,
                        TopicName = r.TopicName
                    };
                    details.Questions.Add(question);
                }

                if (r.ChoiceId != null)
                {
                    bool isCorrect = false;
                    try { isCorrect = Convert.ToBoolean(r.IsCorrect); } catch { isCorrect = false; }
                    question.Choices.Add(new ChoiceDetailDto { ChoiceId = (int)r.ChoiceId, ChoiceText = r.ChoiceText, IsCorrect = isCorrect });
                }
            }

            return details;
        }

        public async Task<ExamDetailsDto> GetRandomExamWithChoicesAsync(int examId, string userId, int attemptId)
        {
            using var conn = new SqlConnection(_connectionString);
            
            // 1. Check if we already have questions for this attempt
            var currentAttemptSeenIds = await GetQuestionsForAttemptAsync(attemptId);
            if (currentAttemptSeenIds.Any())
            {
                var allRows = await GetAllQuestionsRawAsync(examId);
                var currentQuestions = MapToQuestionList(allRows)
                    .Where(q => currentAttemptSeenIds.Contains(q.QuestionId)).ToList();
                
                // If attempt questions are found in the exam-specific pool, return them
                if (currentQuestions.Count == currentAttemptSeenIds.Count())
                    return new ExamDetailsDto { ExamId = examId, Questions = currentQuestions };
                    
                // Fallback: search in global pool for these IDs
                var globalRows = await conn.QueryAsync<dynamic>(@"
                    SELECT Q.Id as QuestionId, Q.QuestionText, Q.Points, Q.CategoryId as QuestionTypeId, Q.Difficulty, Q.TopicId,
                           C.CategoryName as CategoryName, T.TopicName, CH.Id as ChoiceId, CH.ChoiceText, CH.IsCorrect
                    FROM Questions Q
                    LEFT JOIN Categories C ON Q.CategoryId = C.Id
                    LEFT JOIN Topics T ON Q.TopicId = T.Id
                    LEFT JOIN Choices CH ON Q.Id = CH.QuestionId
                    WHERE Q.Id IN @Ids", new { Ids = currentAttemptSeenIds });
                return new ExamDetailsDto { ExamId = examId, Questions = MapToQuestionList(globalRows) };
            }

            // 2. Initializing Selection
            var seenIds = await GetQuestionsUserHasSeenForExamAsync(examId, userId);
            var exam = await GetExamByIdAsync(examId);
            int totalToShow = (exam?.TotalQuestionsToShow != null && exam.TotalQuestionsToShow > 0)
                ? exam.TotalQuestionsToShow.Value
                : 5; // Default fallback

            var selected = new List<QuestionDetailDto>();
            var userRoles = await conn.QueryAsync<string>(
                "SELECT R.Name FROM AspNetRoles R JOIN AspNetUserRoles UR ON R.Id = UR.RoleId WHERE UR.UserId = @UserId",
                new { UserId = userId });
            string userRole = userRoles.FirstOrDefault() ?? "User";

            // 3. Apply Blueprint Rules (Smart Injection)
            bool hasSpecificRules = false;
            if (exam.GenerationRules != null && exam.GenerationRules.Any())
            {
                // Check if there are ANY rules for THIS user's specific role
                hasSpecificRules = exam.GenerationRules.Any(r => 
                    r.TargetRole != "All" && 
                    string.Equals(r.TargetRole, userRole, StringComparison.OrdinalIgnoreCase));

                foreach (var rule in exam.GenerationRules)
                {
                    // Skip if rule doesn't apply to user role
                    if (rule.TargetRole != "All" && !string.Equals(rule.TargetRole, userRole, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ruleSql = @"
                        SELECT TOP (@Limit) Q.Id
                        FROM Questions Q
                        WHERE Q.CategoryId = @CategoryId
                        AND (@TopicId IS NULL OR Q.TopicId = @TopicId)
                        AND Q.Difficulty = @Diff
                        AND Q.Id NOT IN @Excluded
                        ORDER BY NEWID()";

                    // Pick for each difficulty
                    var ruleExcluded = seenIds.Concat(selected.Select(q => q.QuestionId)).ToList();
                    if (!ruleExcluded.Any()) ruleExcluded.Add(-1);

                    var d3Ids = await conn.QueryAsync<int>(ruleSql, new { Limit = rule.HardCount, CategoryId = rule.CategoryId, TopicId = rule.TopicId, Diff = 3, Excluded = ruleExcluded });
                    var d2Ids = await conn.QueryAsync<int>(ruleSql, new { Limit = rule.MediumCount, CategoryId = rule.CategoryId, TopicId = rule.TopicId, Diff = 2, Excluded = ruleExcluded });
                    var d1Ids = await conn.QueryAsync<int>(ruleSql, new { Limit = rule.EasyCount, CategoryId = rule.CategoryId, TopicId = rule.TopicId, Diff = 1, Excluded = ruleExcluded });

                    var allRuleIds = d3Ids.Concat(d2Ids).Concat(d1Ids).ToList();
                    if (allRuleIds.Any())
                    {
                        var ruleRows = await conn.QueryAsync<dynamic>(@"
                            SELECT Q.Id as QuestionId, Q.QuestionText, Q.Points, Q.CategoryId as QuestionTypeId, Q.Difficulty, Q.TopicId,
                                   C.CategoryName as CategoryName, T.TopicName, CH.Id as ChoiceId, CH.ChoiceText, CH.IsCorrect
                            FROM Questions Q
                            LEFT JOIN Categories C ON Q.CategoryId = C.Id
                            LEFT JOIN Topics T ON Q.TopicId = T.Id
                            LEFT JOIN Choices CH ON Q.Id = CH.QuestionId
                            WHERE Q.Id IN @Ids", new { Ids = allRuleIds });
                        selected.AddRange(MapToQuestionList(ruleRows));
                    }
                }
            }

            // 4. Fallback: Only fill if no specific rules were applied for this role
            // This allows different roles to have different total question counts based on their blueprint rules.
            if (!hasSpecificRules)
            {
                int needed = totalToShow - selected.Count;
                if (needed > 0)
                {
                    var finalExcluded = seenIds.Concat(selected.Select(q => q.QuestionId)).ToList();
                    if (!finalExcluded.Any()) finalExcluded.Add(-1);

                    var fallbackIds = await conn.QueryAsync<int>(@"
                        SELECT TOP (@Limit) Q.Id FROM Questions Q
                        INNER JOIN Categories C ON Q.CategoryId = C.Id
                        WHERE Q.Id NOT IN @Excluded 
                        AND C.ExamTypeId = @ExamTypeId
                        ORDER BY NEWID()", new { Limit = needed, Excluded = finalExcluded, ExamTypeId = exam.ExamTypeId });

                    if (fallbackIds.Any())
                    {
                        var fallbackRows = await conn.QueryAsync<dynamic>(@"
                            SELECT Q.Id as QuestionId, Q.QuestionText, Q.Points, Q.CategoryId as QuestionTypeId, Q.Difficulty, Q.TopicId,
                                   C.CategoryName as CategoryName, T.TopicName, CH.Id as ChoiceId, CH.ChoiceText, CH.IsCorrect
                            FROM Questions Q
                            LEFT JOIN Categories C ON Q.CategoryId = C.Id
                            LEFT JOIN Topics T ON Q.TopicId = T.Id
                            LEFT JOIN Choices CH ON Q.Id = CH.QuestionId
                            WHERE Q.Id IN @Ids", new { Ids = fallbackIds });
                        selected.AddRange(MapToQuestionList(fallbackRows));
                    }
                }
            }
            else
            {
                // If we have specific rules, the total count is determined by those rules
                totalToShow = selected.Count;
            }

            // Shuffle final set
            selected = selected.OrderBy(x => Guid.NewGuid()).Take(totalToShow).ToList();

            // Save these questions for the attempt so they stay consistent
            foreach (var q in selected)
            {
                await conn.ExecuteAsync("INSERT INTO UserSeenQuestions (AttemptId, QuestionId, UserId) VALUES (@AttemptId, @QuestionId, @UserId)",
                    new { AttemptId = attemptId, QuestionId = q.QuestionId, UserId = userId });
            }

            return new ExamDetailsDto { ExamId = examId, Questions = selected };
        }

        private async Task<IEnumerable<dynamic>> GetAllQuestionsRawAsync(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryAsync<dynamic>("sp_GetAllQuestionsWithChoicesByExamId",
                new { ExamId = examId },
                commandType: CommandType.StoredProcedure);
        }
        // geting random pharmacist questions 
        private async Task<int> GetExamTypeIdByExamIdAsync(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryFirstOrDefaultAsync<int>(
                "sp_GetExamTypeByExamId", new { ExamId = examId }, commandType: CommandType.StoredProcedure);
        }

        private List<QuestionDetailDto> GetDifficultySubset(List<QuestionDetailDto> pool, int d3, int d2, int d1)
        {
            var result = new List<QuestionDetailDto>();
            
            // Randomly select 2@3, 4@2, 4@1
            var poolD3 = pool.Where(q => q.Difficulty == 3).OrderBy(x => Guid.NewGuid()).Take(d3).ToList();
            var poolD2 = pool.Where(q => q.Difficulty == 2).OrderBy(x => Guid.NewGuid()).Take(d2).ToList();
            var poolD1 = pool.Where(q => q.Difficulty == 1).OrderBy(x => Guid.NewGuid()).Take(d1).ToList();
            
            result.AddRange(poolD3);
            result.AddRange(poolD2);
            result.AddRange(poolD1);

            // Fill empty slots if not enough found in specific difficulties
            int needed = (d3 + d2 + d1) - result.Count;
            if (needed > 0)
            {
                var remaining = pool.Except(result).OrderBy(x => Guid.NewGuid()).Take(needed).ToList();
                result.AddRange(remaining);
            }

            return result.OrderBy(x => Guid.NewGuid()).ToList(); // Shuffle final set
        }

        public async Task<ExamDetailsDto> GetSmartQuestionsByRoleAsync(int examId, string userId, int attemptId, string role)
        {
            return await GetSmartQuestionsAsync(examId, userId, attemptId, role);
        }

        private async Task<ExamDetailsDto> GetSmartQuestionsAsync(int examId, string userId, int attemptId, string userRole)
        {
            var seenIds = await GetQuestionsUserHasSeenForExamAsync(examId, userId);
            var allRows = await GetAllQuestionsRawAsync(examId);
            var allQuestionsMap = MapToQuestionList(allRows);

            // 1. Check if attempt already has questions (Fixed selection for this specific attempt)
            var currentAttemptSeenIds = await GetQuestionsForAttemptAsync(attemptId);
            if (currentAttemptSeenIds.Any())
            {
                var currentQuestions = allQuestionsMap
                    .Where(q => currentAttemptSeenIds.Contains(q.QuestionId)).ToList();

                return new ExamDetailsDto { ExamId = examId, Questions = currentQuestions };
            }

            // 2. Filter out questions already seen in past attempts (only if pool allows it)
            // Note: If the pool is small, we might need to recycle, but for now we honor seenIds
            var availablePool = allQuestionsMap
                .Where(q => !seenIds.Contains(q.QuestionId)).ToList();

            // If available pool is too small after filtering seenIds, fallback to all questions to ensure student can at least take the exam
            if (availablePool.Count < 5) availablePool = allQuestionsMap.ToList();

            using var conn = new SqlConnection(_connectionString);
            var exam = await GetExamByIdAsync(examId);

            // 3. Fetch Generation Rules
            var rules = await conn.QueryAsync<ExamGenerationRuleDto>(
                "SELECT * FROM ExamGenerationRules WHERE ExamId = @ExamId", new { ExamId = examId });

            var selected = new List<QuestionDetailDto>();

            // 4. Apply Rules for this specific role
            var roleRules = rules.Where(r => {
                if (r.TargetRole == "All") return true;
                string dbR = r.TargetRole.ToLower().Trim();
                string userR = userRole.ToLower().Trim();
                
                // Dynamic matching: ignore case, trim, and handle optional 's' for plural
                if (dbR == userR) return true;
                
                // Smart matching for common plural variations (e.g., Pharmacist matches Pharmacists)
                string dbR_noS = dbR.EndsWith("s") ? dbR.Substring(0, dbR.Length - 1) : dbR;
                string userR_noS = userR.EndsWith("s") ? userR.Substring(0, userR.Length - 1) : userR;
                
                if (dbR_noS == userR_noS) return true;
                
                return false;
            }).ToList();

            if (roleRules.Any())
            {
                foreach (var rule in roleRules)
                {
                    // Filter pool by Category OR Topic
                    var pool = availablePool;
                    if (rule.TopicId.HasValue && rule.TopicId.Value > 0)
                    {
                        pool = pool.Where(q => q.TopicId == rule.TopicId).ToList();
                    }
                    else
                    {
                        pool = pool.Where(q => q.CategoryId == rule.CategoryId).ToList();
                    }
                    
                    // Select subsets based on difficulty rules
                    var ruleSelected = GetDifficultySubset(pool, rule.HardCount, rule.MediumCount, rule.EasyCount);
                    selected.AddRange(ruleSelected);
                }
            }
            else
            {
                // Fallback: If no rules exist, just take everything (limited by TotalQuestionsToShow if set)
                selected = availablePool.ToList();
            }

            // Final Global Safety: If rules existed but didn't find matched questions in categories, 
            // but the pool has questions, don't leave the student empty-handed.
            if (!selected.Any() && availablePool.Any())
            {
                // Strict isolation: Even in fallback, only take from categories defined in the rules for this role
                var roleCategories = rules.Where(r => {
                    string dbR = (r.TargetRole ?? "All").ToLower().Trim();
                    string userR = (userRole ?? "").ToLower().Trim();
                    
                    if (dbR == "all" || dbR == userR) return true;
                    
                    string dbR_noS = dbR.EndsWith("s") ? dbR.Substring(0, dbR.Length - 1) : dbR;
                    string userR_noS = userR.EndsWith("s") ? userR.Substring(0, userR.Length - 1) : userR;
                    return dbR_noS == userR_noS;
                }).Select(r => r.CategoryId).Distinct().ToList();

                var fallbackPool = availablePool;
                if (roleCategories.Any())
                {
                    fallbackPool = availablePool.Where(q => roleCategories.Contains(q.CategoryId)).ToList();
                }

                selected = fallbackPool
                    .OrderBy(x => Guid.NewGuid())
                    .Take(exam?.TotalQuestionsToShow ?? 10)
                    .ToList();
            }

            // 5. Final Shuffle and Limit Check (Global Exam Limit)
            var finalSelection = selected.DistinctBy(q => q.QuestionId).OrderBy(x => Guid.NewGuid()).ToList();

            if (exam?.TotalQuestionsToShow != null && exam.TotalQuestionsToShow > 0 && finalSelection.Count > exam.TotalQuestionsToShow)
            {
                finalSelection = finalSelection.Take(exam.TotalQuestionsToShow.Value).ToList();
            }

            return new ExamDetailsDto 
            { 
                ExamId = examId, 
                Questions = finalSelection,
                TotalPoints = finalSelection.Sum(q => q.Points) 
            };
        }
        private List<QuestionDetailDto> MapToQuestionList(IEnumerable<dynamic> rows)
        {
            // حددنا النوع هنا صراحة بدل dynamic عشان الـ Compiler ميزعلش
            var questionDictionary = new Dictionary<int, QuestionDetailDto>();

            foreach (var row in rows)
            {
                // استخدام النوع الصريح QuestionDetailDto بدل var في الـ out
                if (!questionDictionary.TryGetValue((int)row.QuestionId, out QuestionDetailDto? question))
                {
                    question = new QuestionDetailDto
                    {
                        QuestionId = (int)row.QuestionId,
                        QuestionText = (string)row.QuestionText,
                        Points = (int)row.Points,
                        CategoryId = (int)(row.QuestionTypeId ?? 0),
                        Difficulty = (int)(row.Difficulty ?? 1),
                        CategoryName = (string)(row.CategoryName ?? ""),
                        TopicId = (int?)(row.TopicId),
                        TopicName = (string)(row.TopicName ?? ""),
                        Choices = new List<ChoiceDetailDto>()
                    };
                    questionDictionary.Add(question.QuestionId, question);
                }

                if (row.ChoiceId != null)
                {
                    question.Choices.Add(new ChoiceDetailDto
                    {
                        ChoiceId = row.ChoiceId,
                        ChoiceText = row.ChoiceText,
                        IsCorrect = row.IsCorrect
                    });
                }
            }

            return questionDictionary.Values.ToList();
        }
        //public async Task<ExamDetailsDto> GetAssistantQuestionsAsync(int examId)
        //{
        //    var allRows = await GetAllQuestionsRawAsync(examId);
        //    var allQuestions = MapToQuestionList(allRows);

        //    var p1 = allQuestions.Where(q => q.QuestionTypeId == 1).OrderBy(x => Guid.NewGuid()).Take(7);
        //    var p2 = allQuestions.Where(q => q.QuestionTypeId == 2).OrderBy(x => Guid.NewGuid()).Take(3);
        //    var finalSelection = p1.Concat(p2).OrderBy(x => Guid.NewGuid()).ToList();

        //    return new ExamDetailsDto { ExamId = examId, Questions = finalSelection };
        //}






        public async Task<ExamDetailsDto> GetRandomQuestionsPoolAAsync(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<dynamic>("sp_GetRandomQuestions_PoolA", new { ExamId = examId }, commandType: CommandType.StoredProcedure);
            return MapRandomQuestionsRows(examId, rows);
        }

        public async Task<ExamDetailsDto> GetRandomQuestionsPoolBAsync(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<dynamic>("sp_GetRandomQuestions_PoolB", new { ExamId = examId }, commandType: CommandType.StoredProcedure);
            return MapRandomQuestionsRows(examId, rows);
        }

        private static ExamDetailsDto MapRandomQuestionsRows(int examId, IEnumerable<dynamic> rows)
        {
            var details = new ExamDetailsDto { ExamId = examId };

            foreach (var r in rows ?? Enumerable.Empty<dynamic>())
            {
                int qId = r.QuestionId == null ? 0 : (int)r.QuestionId;
                if (qId == 0) continue;

                var question = details.Questions.FirstOrDefault(q => q.QuestionId == qId);
                if (question == null)
                {
                    question = new QuestionDetailDto { 
                        QuestionId = qId, 
                        QuestionText = r.QuestionText, 
                        Points = r.Points,
                        Difficulty = r.Difficulty ?? 1
                    };
                    details.Questions.Add(question);
                }

                if (r.ChoiceId != null)
                {
                    bool isCorrect = false;
                    try { isCorrect = Convert.ToBoolean(r.IsCorrect); } catch { isCorrect = false; }
                    question.Choices.Add(new ChoiceDetailDto { ChoiceId = (int)r.ChoiceId, ChoiceText = r.ChoiceText, IsCorrect = isCorrect });
                }
            }

            return details;
        }

        public async Task UpdateQuestionAsync(int examId, int questionId, string questionText, int points, int difficulty, int categoryId, int? topicId = null)
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = "UPDATE Questions SET QuestionText = @questionText, Points = @points, Difficulty = @difficulty, CategoryId = @categoryId, TopicId = @topicId WHERE Id = @questionId";
            await conn.ExecuteAsync(sql, new { questionText, points, difficulty, categoryId, topicId, questionId });
            await UpdateExamTotalPointsAsync(examId);
        }

        public async Task UpdateChoiceAsync(int choiceId, int questionId, string choiceText, bool isCorrect)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                if (isCorrect)
                {
                    await conn.ExecuteAsync("UPDATE Choices SET IsCorrect = 0 WHERE QuestionId = @QuestionId", new { QuestionId = questionId }, transaction);
                }
                
                await conn.ExecuteAsync(
                    "UPDATE Choices SET ChoiceText = @ChoiceText, IsCorrect = @IsCorrect WHERE Id = @ChoiceId", 
                    new { ChoiceId = choiceId, ChoiceText = choiceText, IsCorrect = isCorrect }, 
                    transaction);
                    
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task<int> AddExamAsync(ExamCreateDTO dto)
        {
            using var conn = new SqlConnection(_connectionString);

            // sp_AddnewExam returns the new ExamId via SELECT SCOPE_IDENTITY()
            var examId = await conn.ExecuteScalarAsync<int>(
                "sp_AddnewExam",
                new
                {
                    Title = dto.Title,
                    Description = dto.Description,
                    StartTime = dto.StartTime,
                    EndTime = dto.EndTime,
                    DurationInMinutes = dto.Duration,
                    PassPercentage = dto.PassPercentage,
                    ExamTypeId = dto.ExamTypeId,
                    WaveId = dto.WaveId,
                    IsActive = dto.IsActive,
                    IsGraded = dto.IsGraded,
                    TotalQuestionsToShow = dto.TotalQuestionsToShow,
                    ShowQuestionOverview = dto.ShowQuestionOverview,
                    IsFinalExam = dto.IsFinalExam
                },
                commandType: CommandType.StoredProcedure);

            return examId;
        }

        public async Task<IEnumerable<Exam.DTOs.WaveDto>> GetAllWavesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<Exam.DTOs.WaveDto>(
                "sp_GetAllWaves",
                commandType: CommandType.StoredProcedure);
            return rows;
        }

        public async Task<IEnumerable<Exam.DTOs.LiveMonitorRowDto>> GetLiveMonitorDataAsync(
            int? branchId = null,
            int? shiftId = null,
            string roleName = null,
            string status = null,
            int? waveId = null,
            DateTime? date = null,
            int? examId = null)
        {
            using var conn = new SqlConnection(_connectionString);

            const string sql = @"
                WITH UserRoles AS (
                    SELECT UR.UserId,
                           MAX(CASE WHEN LOWER(R.Name) = 'pharmacist' OR R.Name LIKE N'%صيدل%' THEN 1 ELSE 0 END) as IsPharmacist,
                           MAX(CASE WHEN LOWER(R.Name) = 'assistant' OR R.Name LIKE N'%مساعد%' THEN 1 ELSE 0 END) as IsAssistant,
                           MAX(R.Name) as RoleName
                    FROM AspNetUserRoles UR
                    JOIN AspNetRoles R ON UR.RoleId = R.Id
                    GROUP BY UR.UserId
                ),
                AllLiveMonitor AS (
                    -- 1. Students with attempts (InProgress / Completed)
                    SELECT
                        u.Id               AS UserId,
                        ISNULL(u.FullName, u.UserName) AS StudentName,
                        u.Email            AS StudentEmail,
                        u.UserCode,
                        COALESCE(ur.RoleName, 'User') AS RoleName,
                        u.BranchId,
                        b.BranchName,
                        u.ShiftId,
                        s.ShiftName,
                        e.Title            AS ExamTitle,
                        et.TypeName        AS ExamType,
                        tw.WaveName,
                        e.WaveId           AS WaveId,
                        e.Id               AS ExamId,
                        uea.Status         AS Status,
                        ISNULL(uea.FinalScore, 0)          AS FinalScore,
                        COALESCE(
                            NULLIF((SELECT ISNULL(SUM(q.Points), 0) FROM UserSeenQuestions usq JOIN Questions q ON usq.QuestionId = q.Id WHERE usq.AttemptId = uea.Id), 0),
                            NULLIF(e.TotalQuestionsToShow, 0),
                            e.TotalPoints
                        ) AS TotalPoints,
                        ISNULL(uea.Score, 0)               AS Percentage,
                        uea.StartTime,
                        uea.EndTime,
                        ISNULL(uea.DurationInMinutes, 0)   AS DurationInMinutes,
                        ISNULL(uea.AttemptNumber, 0)       AS AttemptNumber,
                        uea.IsPassed,
                        uea.CertificateCode,
                        uea.Id             AS AttemptId,
                        uea.StartTime      AS FilterDate
                    FROM AspNetUsers u
                    INNER JOIN UserExamAttempts uea ON uea.UserId = u.Id
                    INNER JOIN Exams e              ON e.Id = uea.ExamId
                    LEFT  JOIN ExamTypes et         ON et.Id = e.ExamTypeId
                    LEFT  JOIN Branches b           ON b.Id = u.BranchId
                    LEFT  JOIN Shifts s             ON s.Id = u.ShiftId
                    LEFT  JOIN UserRoles ur         ON ur.UserId = u.Id
                    LEFT  JOIN TrainingWaves tw     ON tw.Id = e.WaveId
                    WHERE u.IsActive = 1

                    UNION ALL

                    -- 2. Students assigned to exams but have NOT started yet
                    SELECT
                        u.Id               AS UserId,
                        ISNULL(u.FullName, u.UserName) AS StudentName,
                        u.Email            AS StudentEmail,
                        u.UserCode,
                        COALESCE(ur.RoleName, 'User') AS RoleName,
                        u.BranchId,
                        b.BranchName,
                        u.ShiftId,
                        s.ShiftName,
                        e.Title            AS ExamTitle,
                        et.TypeName        AS ExamType,
                        tw.WaveName,
                        e.WaveId           AS WaveId,
                        e.Id               AS ExamId,
                        'Not Started'      AS Status,
                        0                  AS FinalScore,
                        COALESCE(
                            NULLIF(e.TotalQuestionsToShow, 0),
                            e.TotalPoints
                        )                  AS TotalPoints,
                        0                  AS Percentage,
                        NULL               AS StartTime,
                        NULL               AS EndTime,
                        0                  AS DurationInMinutes,
                        0                  AS AttemptNumber,
                        CAST(0 AS BIT)     AS IsPassed,
                        NULL               AS CertificateCode,
                        NULL               AS AttemptId,
                        ea.ScheduledStartTime AS FilterDate
                    FROM AspNetUsers u
                    INNER JOIN ExamAssignments ea   ON ea.StudentId = u.Id
                    INNER JOIN Exams e              ON e.Id = ea.ExamId
                    LEFT  JOIN ExamTypes et         ON et.Id = e.ExamTypeId
                    LEFT  JOIN Branches b           ON b.Id = u.BranchId
                    LEFT  JOIN Shifts s             ON s.Id = u.ShiftId
                    LEFT  JOIN UserRoles ur         ON ur.UserId = u.Id
                    LEFT  JOIN TrainingWaves tw     ON tw.Id = e.WaveId
                    WHERE u.IsActive = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM UserExamAttempts uea 
                          WHERE uea.UserId = u.Id AND uea.ExamId = e.Id
                      )
                )
                SELECT * FROM AllLiveMonitor
                WHERE (@BranchId  IS NULL OR BranchId = @BranchId)
                  AND (@ShiftId   IS NULL OR ShiftId  = @ShiftId)
                  AND (@RoleName  IS NULL OR RoleName = @RoleName)
                  AND (@Status    IS NULL OR Status   = @Status)
                  AND (@WaveId    IS NULL OR WaveId   = @WaveId)
                  AND (@ExamId    IS NULL OR ExamId   = @ExamId)
                  AND (@Date      IS NULL OR CAST(FilterDate AS DATE) = @Date)
                ORDER BY StartTime DESC, StudentName ASC";

            var rows = await conn.QueryAsync<Exam.DTOs.LiveMonitorRowDto>(sql, new
            {
                BranchId = branchId,
                ShiftId  = shiftId,
                RoleName = string.IsNullOrWhiteSpace(roleName) ? null : roleName,
                Status   = string.IsNullOrWhiteSpace(status)   ? null : status,
                WaveId   = waveId,
                ExamId   = examId,
                Date     = date?.Date
            });

            return rows;
        }

        /// <summary>
        /// Aggregate all exam results for every student in a wave.
        /// For each student we sum up their FinalScore across all completed exams
        /// in that wave (regardless of how many there are, but the denominator is
        /// the total TotalPoints of ALL exams in the wave so the percentage is fair).
        /// Certification thresholds:
        ///   Pharmacists  – certified ≥ 75% (150/200), pass ≥ 70% (140/200)
        ///   Assistants   – certified ≥ 75% (75/100),  pass ≥ 70% (70/100)
        /// </summary>
        public async Task<IEnumerable<Exam.DTOs.WaveStudentResultDto>> GetWaveAggregateResultsAsync(int waveId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1. Get wave name
            var waveName = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT WaveName FROM TrainingWaves WHERE Id = @WaveId",
                new { WaveId = waveId });

            // 2. All exams in this wave with details
            var waveExams = (await conn.QueryAsync<dynamic>(@"
                SELECT E.Id, E.TotalPoints, E.TotalQuestionsToShow, ET.TypeName AS ExamType, E.IsFinalExam 
                FROM Exams E 
                LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id 
                WHERE E.WaveId = @WaveId",
                new { WaveId = waveId })).ToList();

            int totalExamsInWave = waveExams.Count;

            var examIds = waveExams.Select(e => (int)e.Id).ToList();
            var rules = new List<dynamic>();
            if (examIds.Any())
            {
                rules = (await conn.QueryAsync<dynamic>(@"
                    SELECT ExamId, EasyCount, MediumCount, HardCount, CategoryId, TargetRole 
                    FROM ExamGenerationRules 
                    WHERE ExamId IN @ExamIds", 
                    new { ExamIds = examIds })).ToList();
            }

            // 3. All students assigned to this wave
            var students = (await conn.QueryAsync<dynamic>(@"
                SELECT DISTINCT
                    u.Id               AS UserId,
                    ISNULL(u.FullName, u.UserName) AS StudentName,
                    u.Email            AS StudentEmail,
                    u.UserCode,
                    b.BranchName,
                    r.Name             AS RoleName
                FROM UserWaves uw
                INNER JOIN AspNetUsers u  ON u.Id = uw.UserId
                LEFT  JOIN Branches b     ON b.Id = u.BranchId
                LEFT  JOIN AspNetUserRoles ur ON ur.UserId = u.Id
                LEFT  JOIN AspNetRoles r       ON r.Id = ur.RoleId
                WHERE uw.WaveId = @WaveId
                  AND (uw.IsDeactivated IS NULL OR uw.IsDeactivated = 0)",
                new { WaveId = waveId })).ToList();

            // 4. All attempts for all exams in this wave (best attempt per student per exam)
            var allAttempts = (await conn.QueryAsync<dynamic>(@"
                SELECT 
                    uea.UserId,
                    uea.ExamId,
                    uea.Status,
                    ISNULL(uea.FinalScore, 0) AS FinalScore,
                    uea.Score AS Percentage,
                    uea.CertificateCode,
                    uea.AttemptNumber,
                    uea.Id AS AttemptId,
                    ROW_NUMBER() OVER (PARTITION BY uea.UserId, uea.ExamId ORDER BY uea.AttemptNumber DESC) AS rn
                FROM UserExamAttempts uea
                INNER JOIN Exams e ON e.Id = uea.ExamId
                WHERE e.WaveId = @WaveId",
                new { WaveId = waveId })).ToList();

            // Keep only latest attempt per (user, exam)
            var latestAttempts = allAttempts.Where(a => (int)a.rn == 1).ToList();

            // Fetch seen question points for all attempts in this wave
            var seenPointsList = (await conn.QueryAsync<dynamic>(@"
                SELECT usq.AttemptId, SUM(q.Points) AS SeenPoints
                FROM UserSeenQuestions usq
                JOIN Questions q ON usq.QuestionId = q.Id
                JOIN UserExamAttempts uea ON usq.AttemptId = uea.Id
                INNER JOIN Exams e ON e.Id = uea.ExamId
                WHERE e.WaveId = @WaveId
                GROUP BY usq.AttemptId",
                new { WaveId = waveId })).ToList();
            
            var attemptSeenPoints = seenPointsList.ToDictionary(
                x => (int)x.AttemptId,
                x => (decimal)x.SeenPoints
            );

            var results = new List<Exam.DTOs.WaveStudentResultDto>();

            foreach (var student in students)
            {
                string userId = (string)student.UserId;
                string roleName = (string)student.RoleName ?? "";

                var studentAttempts = latestAttempts.Where(a => (string)a.UserId == userId).ToList();
                var targetExam = waveExams.FirstOrDefault(e => e.IsFinalExam != null && (bool)e.IsFinalExam) ?? waveExams.FirstOrDefault();

                int examsCompleted = 0;
                int examsAssigned = targetExam != null ? 1 : 0;
                decimal totalScore = 0;
                decimal studentTotalAvailablePoints = 0;
                var targetAttempt = targetExam != null 
                    ? studentAttempts.FirstOrDefault(a => (int)a.ExamId == (int)targetExam.Id)
                    : null;

                if (targetAttempt != null)
                {
                    if ((string)targetAttempt.Status == "Completed")
                    {
                        examsCompleted = 1;
                    }
                    totalScore = (decimal)targetAttempt.FinalScore;

                    if (attemptSeenPoints.TryGetValue((int)targetAttempt.AttemptId, out decimal sp))
                    {
                        studentTotalAvailablePoints = sp;
                    }
                    else
                    {
                        int questionsToShow = targetExam.TotalQuestionsToShow != null ? (int)targetExam.TotalQuestionsToShow : 0;
                        studentTotalAvailablePoints = questionsToShow > 0 ? questionsToShow * 1.0m : (targetExam.TotalPoints != null ? (decimal)targetExam.TotalPoints : 100.0m);
                    }
                }
                else if (targetExam != null)
                {
                    int questionsToShow = targetExam.TotalQuestionsToShow != null ? (int)targetExam.TotalQuestionsToShow : 0;
                    studentTotalAvailablePoints = questionsToShow > 0 ? questionsToShow * 1.0m : (targetExam.TotalPoints != null ? (decimal)targetExam.TotalPoints : 100.0m);
                }

                // Certificate code from any completed attempt (latest)
                string certCode = studentAttempts
                    .Where(a => (string)a.Status == "Completed" && !string.IsNullOrEmpty((string)a.CertificateCode))
                    .Select(a => (string)a.CertificateCode)
                    .FirstOrDefault();

                double percentage = studentTotalAvailablePoints > 0
                    ? (double)(totalScore / studentTotalAvailablePoints) * 100
                    : 0;
                double certThreshold = 75.0;
                double passThreshold = 70.0;

                string waveStatus;
                if (targetAttempt == null || (string)targetAttempt.Status != "Completed")
                    waveStatus = "INCOMPLETE";
                else if (percentage >= certThreshold)
                    waveStatus = "CERTIFIED";
                else if (percentage >= passThreshold)
                    waveStatus = "PASS";
                else
                    waveStatus = "FAILED";

                results.Add(new Exam.DTOs.WaveStudentResultDto
                {
                    UserId            = userId,
                    StudentName       = (string)student.StudentName ?? "",
                    StudentEmail      = (string)student.StudentEmail ?? "",
                    UserCode          = (string)student.UserCode ?? "",
                    BranchName        = (string)student.BranchName ?? "",
                    RoleName          = roleName,
                    WaveName          = waveName ?? "",
                    WaveId            = waveId,
                    TotalExamsInWave  = 1,
                    ExamsCompleted    = examsCompleted,
                    ExamsAssigned     = examsAssigned,
                    TotalScore        = totalScore,
                    TotalAvailable    = studentTotalAvailablePoints,
                    WaveStatus        = waveStatus,
                    CertificateCode   = certCode
                });
            }

            return results.OrderBy(r => r.StudentName);
        }


        public async Task<IEnumerable<adminExamDto>> GetAllExamsWithDetailsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = (await conn.QueryAsync<adminExamDto>(
                "sp_GetAllExamsWithDetails",
                commandType: CommandType.StoredProcedure)).ToList();
            
            return rows;
        }

        public async Task<IEnumerable<adminExamDto>> GetExamsByWaveIdAsync(int waveId)
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = (await conn.QueryAsync<adminExamDto>(
                "sp_GetExamsByWaveId",
                new { WaveId = waveId },
                commandType: CommandType.StoredProcedure)).ToList();

            return rows;
        }

        public async Task<IEnumerable<adminExamDto>> GetExamsByTypeAsync(int typeId)
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = (await conn.QueryAsync<adminExamDto>(
                "sp_GetExamsByType",
                new { TypeId = typeId },
                commandType: CommandType.StoredProcedure)).ToList();

            return rows;
        }

        public async Task<ExamQuestionStatsDto> GetExamQuestionStatsAsync(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                SELECT 
                    C.CategoryName,
                    ISNULL(T.TopicName, 'ALL TOPICS') as TopicName,
                    COUNT(Q.Id) as QuestionCount
                FROM Questions Q
                INNER JOIN ExamQuestions EQ ON Q.Id = EQ.QuestionId
                LEFT JOIN Categories C ON Q.CategoryId = C.Id
                LEFT JOIN Topics T ON Q.TopicId = T.Id
                WHERE EQ.ExamId = @ExamId
                GROUP BY C.CategoryName, T.TopicName
                ORDER BY C.CategoryName, T.TopicName";
            
            var rows = await conn.QueryAsync<dynamic>(sql, new { ExamId = examId });
            
            var stats = new ExamQuestionStatsDto();
            foreach (var r in rows)
            {
                string catName = r.CategoryName ?? "Uncategorized";
                string topName = r.TopicName;
                int count = r.QuestionCount;
                
                stats.TotalQuestions += count;
                
                var cat = stats.Categories.FirstOrDefault(c => c.CategoryName == catName);
                if (cat == null)
                {
                    cat = new CategoryStatDto { CategoryName = catName };
                    stats.Categories.Add(cat);
                }
                cat.Count += count;
                
                cat.Topics.Add(new TopicStatDto { TopicName = topName, Count = count });
            }
            return stats;
        }

        public async Task<int> AddQuestionAsync(int examId, QuestionCreateDTO question)
        {
            using var conn = new SqlConnection(_connectionString);

            var result = await conn.ExecuteScalarAsync<int>(
                "sp_AddQuestion",
                new
                {
                    ExamId = examId,
                    QuestionText = question.QuestionText,
                    Points = question.Points,
                    Difficulty = question.Difficulty,
                    CategoryId = question.CategoryId
                },
                commandType: CommandType.StoredProcedure
            );

            return result;
        }

        public async Task<int> CreateExamWithQuestionsAsync(ExamCreateDTO dto)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                // 1. Create the exam
                var examId = await conn.ExecuteScalarAsync<int>(
                    "sp_AddnewExam",
                    new
                    {
                        Title = dto.Title,
                        Description = dto.Description,
                        StartTime = dto.StartTime,
                        EndTime = dto.EndTime,
                        DurationInMinutes = dto.Duration,
                        PassPercentage = dto.PassPercentage,
                        ExamTypeId = dto.ExamTypeId,
                        WaveId = dto.WaveId,
                        IsActive = dto.IsActive,
                        IsGraded = dto.IsGraded,
                        TotalQuestionsToShow = dto.TotalQuestionsToShow,
                        ShowQuestionOverview = dto.ShowQuestionOverview,
                        IsFinalExam = dto.IsFinalExam
                    },
                    transaction: transaction,
                    commandType: CommandType.StoredProcedure);

                // 1.5 Save Generation Rules if any
                if (dto.GenerationRules != null && dto.GenerationRules.Any())
                {
                    foreach (var rule in dto.GenerationRules)
                    {
                        var sql = @"INSERT INTO ExamGenerationRules 
                                   (ExamId, CategoryId, TopicId, TargetRole, EasyCount, MediumCount, HardCount)
                                   VALUES (@ExamId, @CategoryId, @TopicId, @TargetRole, @EasyCount, @MediumCount, @HardCount)";
                        await conn.ExecuteAsync(sql, new
                        {
                            ExamId = examId,
                            CategoryId = rule.CategoryId,
                            TopicId = rule.TopicId,
                            TargetRole = rule.TargetRole ?? "All",
                            EasyCount = rule.EasyCount,
                            MediumCount = rule.MediumCount,
                            HardCount = rule.HardCount
                        }, transaction: transaction);
                    }
                }

                // 2. Add each question + its choices
                if (dto.Questions != null)
                {
                    foreach (var q in dto.Questions)
                    {
                        if (q == null) continue;

                        var questionId = await conn.ExecuteScalarAsync<int>(
                            "sp_AddQuestion",
                            new
                            {
                                ExamId = examId,
                                QuestionText = q.QuestionText,
                                Points = q.Points,
                                Difficulty = q.Difficulty,
                                CategoryId = q.CategoryId
                            },
                            transaction: transaction,
                            commandType: CommandType.StoredProcedure);

                        if (q.Choices != null)
                        {
                            foreach (var c in q.Choices)
                            {
                                if (c == null) continue;
                                await conn.ExecuteAsync(
                                    "sp_Admin_AddChoice",
                                    new { QuestionId = questionId, ChoiceText = c.ChoiceText, IsCorrect = c.IsCorrect },
                                    transaction: transaction,
                                    commandType: CommandType.StoredProcedure);
                            }
                        }
                    }
                }

                transaction.Commit();
                return examId;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task<IEnumerable<ExamTypeDto>> GetAllExamTypesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<ExamTypeDto>(
                "sp_GetAllExamTypes",
                commandType: CommandType.StoredProcedure);
            return rows;
        }

        public async Task<IEnumerable<QuestionTypeDto>> GetAllQuestionTypesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<QuestionTypeDto>(
                "sp_GetAllQuestionTypes",
                commandType: CommandType.StoredProcedure);
            return rows;
        }

        public async Task<IEnumerable<CategoryDto>> GetAllCategoriesAsync(int? examTypeId = null)
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<CategoryDto>(
                "sp_GetAllCategories",
                new { ExamTypeId = examTypeId },
                commandType: CommandType.StoredProcedure);
            return rows;
        }

        public async Task<UserWithRoleDto?> GetUserWithRoleByIdAsync(string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
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
FROM dbo.AspNetUsers U
LEFT JOIN dbo.AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN dbo.AspNetRoles R ON UR.RoleId = R.Id
LEFT JOIN dbo.Shifts S ON S.Id = U.ShiftId
LEFT JOIN dbo.Branches B ON U.BranchId = B.Id
WHERE U.Id = @UserId;";
            return await conn.QueryFirstOrDefaultAsync<UserWithRoleDto>(sql, new { UserId = userId });
        }
        
        public async Task<IEnumerable<UserWithRoleDto>> GetAllUsersWithRolesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            const string inlineSql = @"
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
FROM dbo.AspNetUsers U WITH(NOLOCK)
LEFT JOIN dbo.AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN dbo.AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
LEFT JOIN dbo.Shifts S WITH(NOLOCK) ON S.Id = U.ShiftId
LEFT JOIN dbo.Branches B WITH(NOLOCK) ON U.BranchId = B.Id
ORDER BY B.BranchName, R.Name, U.UserName;";

            try
            {
                // Directly using optimized inline SQL for guaranteed performance
                return await conn.QueryAsync<UserWithRoleDto>(inlineSql);
            }
            catch (SqlException ex) when (ex.Number == 207)
            {
                // Deployed SP may still reference removed columns (e.g. Location → BranchCode).
                return await conn.QueryAsync<UserWithRoleDto>(inlineSql);
            }
        }

        public async Task<IEnumerable<RoleDto>> GetAllRolesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<RoleDto>(
                "sp_Admin_GetAllRoles",
                commandType: CommandType.StoredProcedure);
            return rows;
        }

        public async Task<IEnumerable<BranchDto>> GetAllBranchesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            const string inlineBranches = @"
SELECT
    CAST(B.Id AS NVARCHAR(32)) AS Id,
    B.BranchName,
    B.BranchCode,
    B.IsActive
FROM dbo.Branches B
ORDER BY B.BranchName;";

            try
            {
                return await conn.QueryAsync<BranchDto>(
                    "sp_Admin_GetAllBranches",
                    commandType: CommandType.StoredProcedure);
            }
            catch (SqlException ex) when (ex.Number == 207)
            {
                return await conn.QueryAsync<BranchDto>(inlineBranches);
            }
        }

        public async Task UpdateUserRoleByIdAsync(string userId, string roleId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                "sp_Admin_UpdateUserRoleById",
                new { UserId = userId, RoleId = roleId },
                commandType: CommandType.StoredProcedure);
        }

        public async Task<IEnumerable<ShiftDto>> GetAllShiftsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<ShiftDto>(
                "sp_GetAllShifts",
                commandType: CommandType.StoredProcedure);
            return rows;
        }

        public async Task UpdateUserShiftAsync(string userId, int newShiftId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                "sp_Admin_UpdateUserShift",
                new { UserId = userId, NewShiftId = newShiftId },
                commandType: CommandType.StoredProcedure);
        }

        public async Task<UserShiftDto?> GetUserShiftAsync(string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = @"
SELECT
    U.ShiftId, 
    S.ShiftName, 
    S.StartTime, 
    S.EndTime
FROM AspNetUsers U
LEFT JOIN Shifts S ON U.ShiftId = S.Id
WHERE U.Id = @UserId;";

            var row = await conn.QueryFirstOrDefaultAsync<UserShiftDto>(sql, new { UserId = userId });
            return row;
        }

        public async Task<int> CloneExamAsync(int oldExamId, int newWaveId, DateTime newStartTime, DateTime newEndTime, string newTitle)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var newExamId = await conn.ExecuteScalarAsync<int>(
                    "sp_Admin_CloneExam",
                    new { OldExamId = oldExamId, NewWaveId = newWaveId, NewStartTime = newStartTime, NewEndTime = newEndTime, NewTitle = newTitle },
                    transaction,
                    commandType: CommandType.StoredProcedure);

                // Clone Injection Rules
                const string cloneRulesSql = @"
                    INSERT INTO ExamGenerationRules (ExamId, CategoryId, TopicId, EasyCount, MediumCount, HardCount, TargetRole)
                    SELECT @NewId, CategoryId, TopicId, EasyCount, MediumCount, HardCount, TargetRole
                    FROM ExamGenerationRules
                    WHERE ExamId = @OldId";
                
                await conn.ExecuteAsync(cloneRulesSql, new { NewId = newExamId, OldId = oldExamId }, transaction);

                transaction.Commit();
                return newExamId;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task<int> CloneWaveAsync(int oldWaveId, string newWaveName, DateTime newStartDate)
        {
            using var conn = new SqlConnection(_connectionString);
            var newWaveId = await conn.ExecuteScalarAsync<int>(
                "sp_Admin_CloneWave",
                new { OldWaveId = oldWaveId, NewWaveName = newWaveName, NewStartDate = newStartDate },
                commandType: CommandType.StoredProcedure);

            return newWaveId;
        }

        public async Task AddChoiceAsync(int questionId, ChoiceCreateDTO choice)
        {
            using var conn = new SqlConnection(_connectionString);

            await conn.ExecuteAsync(
                "sp_Admin_AddChoice",
                new { QuestionId = questionId, ChoiceText = choice.ChoiceText, IsCorrect = choice.IsCorrect },
                commandType: CommandType.StoredProcedure
            );
        }

        public async Task<IEnumerable<StudentDto>> GetAllStudentsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            // Ultra-lightweight direct SQL for the main registry
            return await conn.QueryAsync<StudentDto>(@"
                SELECT U.Id, U.UserName, U.Email, RL.Name as RoleName, U.UserCode
                FROM AspNetUsers U WITH(NOLOCK)
                INNER JOIN AspNetUserRoles UR WITH(NOLOCK) ON U.Id = UR.UserId
                INNER JOIN AspNetRoles RL WITH(NOLOCK) ON RL.Id = UR.RoleId
                WHERE RL.Name IN ('doctor', 'assistant', 'pharmacist')
                ORDER BY U.UserName");
        }



        public async Task<adminExamDto> GetExamByIdAsync(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = @"
                SELECT E.*, E.WaveId, ET.TypeName as ExamType, ISNULL(E.IsFinalExam, 0) as IsFinalExam
                FROM Exams E
                LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id
                WHERE E.Id = @Id";
            var exam = await conn.QueryFirstOrDefaultAsync<adminExamDto>(sql, new { Id = id });

            if (exam != null)
            {
                // Load Rules
                exam.GenerationRules = (await conn.QueryAsync<ExamGenerationRuleDto>(
                    "SELECT * FROM ExamGenerationRules WHERE ExamId = @ExamId",
                    new { ExamId = id })).ToList();

                // Load Total Possible Questions in Bank (Linked to this Exam)
                exam.TotalQuestionsAvailable = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(DISTINCT QuestionId) FROM ExamQuestions WHERE ExamId = @ExamId", 
                    new { ExamId = id });
            }

            return exam;
        }

        public async Task UpdateAttemptStatusOnlyAsync(int attemptId, string status)
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = "UPDATE UserExamAttempts SET [Status] = @status, EndTime = GETDATE()";
            
            // Fix: If it's a cheating status, force scores to 0
            if (status != null && status.StartsWith("Fail_") && status != "Fail_Timeout")
            {
                sql += ", Score = 0, FinalScore = 0, IsPassed = 0";
            }
            
            sql += " WHERE Id = @Id";
            await conn.ExecuteAsync(sql, new { status, Id = attemptId });
        }

        public async Task<IEnumerable<adminExamDto>> GetAllExamsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var exams = await conn.QueryAsync<adminExamDto>("sp_GetAllExams", commandType: CommandType.StoredProcedure);
            return exams;
        }

        private async Task CleanupStaleAttemptsAsync()
        {
            try 
            {
                using var conn = new SqlConnection(_connectionString);
                // Identify attempts stuck in 'InProgress' more than 5 minutes past their allowed duration
                var staleAttemptIds = await conn.QueryAsync<int>(@"
                    SELECT UA.Id 
                    FROM UserExamAttempts UA
                    INNER JOIN Exams E ON UA.ExamId = E.Id
                    WHERE UA.[Status] = 'InProgress'
                    AND GETDATE() > DATEADD(MINUTE, E.DurationInMinutes + 5, UA.StartTime)");

                foreach (var id in staleAttemptIds)
                {
                    // Use the existing submission logic to calculate scores and finalize properly
                    await SubmitFinalAsync(id, "Fail_Abandoned");
                }
            }
            catch { /* Fail silently to not block the main request */ }
        }

        public async Task<IEnumerable<ExamDto>> GetStudentExamsByStudentIdAsync(string studentId)
        {
            // Auto-cleanup stale attempts before showing the list
            await CleanupStaleAttemptsAsync();

            using var conn = new SqlConnection(_connectionString);
            
            // Fetch assignments for this student
            var manualAssignments = await conn.QueryAsync<ExamDto>(@"
                SELECT DISTINCT E.Id as ExamId, E.Title as ExamTitle, E.Description as ExamDescription, 
                    ISNULL(A.ScheduledStartTime, E.StartTime) as ExamDate,
                    ISNULL(A.ScheduledEndTime, E.EndTime) as EndTime,
                    E.DurationInMinutes, E.PassPercentage, E.IsActive,
                    (SELECT COUNT(*) FROM ExamAssignments WHERE StudentId = @StudentId AND ExamId = E.Id) as AssignmentCount
                FROM ExamAssignments A
                INNER JOIN Exams E ON A.ExamId = E.Id
                WHERE A.StudentId = @StudentId AND E.IsActive = 1
                AND A.Id = (SELECT TOP 1 Id FROM ExamAssignments WHERE StudentId = @StudentId AND ExamId = E.Id ORDER BY Id DESC)", 
                new { StudentId = studentId });

            var result = new List<ExamDto>();

            foreach(var ma in manualAssignments)
            {
                var latestAttempt = await conn.QueryFirstOrDefaultAsync<Exam.DTOs.UserAttemptSummaryDto>(@"
                    SELECT TOP 1 Id, [Status], StartTime, AttemptDate, AttemptNumber
                    FROM UserExamAttempts 
                    WHERE UserId = @UserId AND ExamId = @ExamId 
                    ORDER BY AttemptNumber DESC, Id DESC", 
                    new { UserId = studentId, ExamId = ma.ExamId });

                bool isFinished = false;
                if (latestAttempt != null)
                {
                    if (latestAttempt.Status != "InProgress")
                    {
                        var assignmentInfo = await conn.QueryFirstOrDefaultAsync(@"
                            SELECT ScheduledStartTime 
                            FROM ExamAssignments 
                            WHERE StudentId = @StudentId AND ExamId = @ExamId 
                            ORDER BY Id DESC", 
                            new { StudentId = studentId, ExamId = ma.ExamId });

                        DateTime? scheduledStartTime = assignmentInfo?.ScheduledStartTime;

                        bool eligibleForReattempt = false;
                        if ((scheduledStartTime.HasValue && latestAttempt.AttemptDate < scheduledStartTime.Value) ||
                            (ma.AssignmentCount > latestAttempt.AttemptNumber))
                        {
                            eligibleForReattempt = true;
                        }

                        if (!eligibleForReattempt)
                        {
                            isFinished = true;
                        }
                    }
                }

                if (!isFinished)
                {
                    result.Add(ma);
                }
            }

            return result;
        }

        public async Task<IEnumerable<ExamDropdownDto>> GetActiveExamsForDropdownAsync(int? typeId = null, int? month = null, int? year = null)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = @"
                SELECT E.Id, E.Title, ET.TypeName, E.StartTime, W.WaveName
                FROM Exams E
                LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id
                LEFT JOIN TrainingWaves W ON E.WaveId = W.Id
                WHERE 1=1";
            
            if (typeId.HasValue && typeId > 0) sql += " AND E.ExamTypeId = @TypeId";
            if (month.HasValue && month > 0) sql += " AND MONTH(E.StartTime) = @Month";
            if (year.HasValue && year > 0) sql += " AND YEAR(E.StartTime) = @Year";

            sql += " ORDER BY ET.TypeName, E.StartTime DESC";
            
            var exams = await conn.QueryAsync<ExamDropdownDto>(sql, new { TypeId = typeId, Month = month, Year = year });
            return exams;
        }

        public async Task<IEnumerable<ExamResultRowDto>> GetExamResultsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var results = await conn.QueryAsync<ExamResultRowDto>(
                "sp_Admin_GetAllExamResults",
                commandType: CommandType.StoredProcedure);
            return results;
        }

        public async Task<IEnumerable<ExamResultRowDto>> GetExamResultsByExamIdAsync(int examId)
        {
            // Removed CleanupStaleAttemptsAsync from here to prevent blocking UI
            using var conn = new SqlConnection(_connectionString);
            
            // Precision Query: Show EVERY attempt made, plus ONE 'Not Started' row if the LATEST assignment hasn't been started.
            var sql = @"
                WITH ExamMeta AS (
                    SELECT E.Id, E.Title, ET.TypeName, ISNULL(E.TotalQuestionsToShow, 0) as TotalQuestionsToShow, W.WaveName, E.WaveId, ISNULL(E.IsFinalExam, 0) as IsFinalExam,
                    ISNULL((SELECT SUM(Q.Points) FROM ExamQuestions EQ JOIN Questions Q ON EQ.QuestionId = Q.Id WHERE EQ.ExamId = E.Id), 0) as StaticTotalPoints
                    FROM Exams E
                    LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id
                    LEFT JOIN TrainingWaves W ON E.WaveId = W.Id
                    WHERE E.Id = @ExamId
                ),
                UserRoles AS (
                    SELECT UR.UserId,
                           MAX(CASE WHEN LOWER(R.Name) = 'pharmacist' OR R.Name LIKE N'%صيدل%' THEN 1 ELSE 0 END) as IsPharmacist,
                           MAX(CASE WHEN LOWER(R.Name) = 'assistant' OR R.Name LIKE N'%مساعد%' THEN 1 ELSE 0 END) as IsAssistant,
                           MAX(R.Name) as RoleName
                    FROM AspNetUserRoles UR
                    JOIN AspNetRoles R ON UR.RoleId = R.Id
                    GROUP BY UR.UserId
                ),
                AllResults AS (
                    -- 1. All Attempts
                    SELECT 
                        U.Id, 
                        ISNULL(U.FullName, U.UserName) as StudentName, 
                        U.Email as StudentEmail,
                        EM.Title as ExamName,
                        EM.TypeName as ExamType,
                        CASE 
                            WHEN UWC.CertificateCode IS NOT NULL OR UWC.Score IS NOT NULL THEN 'Completed'
                            WHEN U.CertificateCode IS NOT NULL OR U.CertificateScore IS NOT NULL THEN 'Completed'
                            WHEN UA.EndTime IS NOT NULL THEN 'Completed' 
                            ELSE 'InProgress' 
                        END as Status,
                        ISNULL(UWC.Score, ISNULL(U.CertificateScore, ISNULL(UA.Score, 0))) as Score,
                        ISNULL(UA.FinalScore, 0) as FinalScore,
                        CASE 
                            WHEN UA.StartTime IS NOT NULL AND UA.EndTime IS NOT NULL THEN DATEDIFF(MINUTE, UA.StartTime, UA.EndTime)
                            ELSE ISNULL(UA.DurationInMinutes, 0) 
                        END as DurationInMinutes,
                        ISNULL(UA.IsPassed, 0) as IsPassed, 
                        ISNULL(UWC.CertificateCode, ISNULL(U.CertificateCode, UA.CertificateCode)) as CertificateCode, 
                        UA.EmailSent,
                        ISNULL(UA.AttemptNumber, 0) as AttemptNumber, 
                        UA.AttemptDate as CompletionDate, 
                        UA.Id as AttemptId,
                        CASE
                            WHEN EM.IsFinalExam = 1 AND LOWER(EM.TypeName) NOT LIKE '%wave%' THEN 
                                CASE 
                                    WHEN UR.IsPharmacist = 1 THEN 200
                                    WHEN UR.IsAssistant = 1 THEN 100
                                    ELSE 100
                                  END
                            ELSE 
                                CASE 
                                    WHEN EM.TotalQuestionsToShow > 0 THEN EM.TotalQuestionsToShow
                                    ELSE EM.StaticTotalPoints 
                                END
                        END as TotalScoreAvailable,
                        UA.StartTime as ActualStartTime, 
                        UA.EndTime as ActualEndTime,
                        U.UserCode, 
                        B.BranchName, 
                        EM.WaveName, 
                        COALESCE(UR.RoleName, 'User') as RoleName
                    FROM UserExamAttempts UA
                    CROSS JOIN ExamMeta EM
                    INNER JOIN AspNetUsers U ON UA.UserId = U.Id
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    LEFT JOIN UserRoles UR ON U.Id = UR.UserId
                    LEFT JOIN UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = EM.WaveId
                    WHERE UA.ExamId = @ExamId
 
                    UNION ALL
 
                    -- 2. Assignments without attempts
                    SELECT 
                        U.Id, 
                        ISNULL(U.FullName, U.UserName) as StudentName, 
                        U.Email as StudentEmail, 
                        EM.Title as ExamName, 
                        EM.TypeName as ExamType,
                        CASE 
                            WHEN UWC.CertificateCode IS NOT NULL OR UWC.Score IS NOT NULL THEN 'Completed'
                            WHEN U.CertificateCode IS NOT NULL OR U.CertificateScore IS NOT NULL THEN 'Completed'
                            ELSE 'Not Started' 
                        END as Status, 
                        ISNULL(UWC.Score, ISNULL(U.CertificateScore, 0)) as Score, 
                        CAST(0 AS DECIMAL(18,2)) as FinalScore, 
                        0 as DurationInMinutes, 
                        CAST(0 AS BIT) as IsPassed, 
                        ISNULL(UWC.CertificateCode, U.CertificateCode) as CertificateCode, 
                        CAST(0 AS BIT) as EmailSent, 
                        0 as AttemptNumber, 
                        CAST(NULL AS DATETIME) as CompletionDate, 
                        CAST(NULL AS INT) as AttemptId,
                        CASE
                            WHEN EM.IsFinalExam = 1 AND LOWER(EM.TypeName) NOT LIKE '%wave%' THEN 
                                CASE 
                                    WHEN UR.IsPharmacist = 1 THEN 200
                                    WHEN UR.IsAssistant = 1 THEN 100
                                    ELSE 100
                                END
                            ELSE 
                                CASE 
                                    WHEN EM.TotalQuestionsToShow > 0 THEN EM.TotalQuestionsToShow
                                    ELSE EM.StaticTotalPoints 
                                END
                        END as TotalScoreAvailable,
                        CAST(NULL AS DATETIME) as ActualStartTime, 
                        CAST(NULL AS DATETIME) as ActualEndTime, 
                        U.UserCode, 
                        B.BranchName, 
                        EM.WaveName, 
                        COALESCE(UR.RoleName, 'User') as RoleName
                    FROM ExamAssignments EA
                    CROSS JOIN ExamMeta EM
                    INNER JOIN AspNetUsers U ON EA.StudentId = U.Id
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    LEFT JOIN UserRoles UR ON U.Id = UR.UserId
                    LEFT JOIN UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = EM.WaveId
                    WHERE EA.ExamId = @ExamId
                    AND NOT EXISTS (SELECT 1 FROM UserExamAttempts UA WHERE UA.UserId = EA.StudentId AND UA.ExamId = EA.ExamId)
                )
                SELECT * FROM AllResults ORDER BY StudentName, AttemptNumber DESC, CompletionDate DESC";
            var results = (await conn.QueryAsync<ExamResultRowDto>(sql, new { ExamId = examId })).ToList();
 
            // Dynamic Wave Aggregate Score Override
            var exam = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT E.Id, E.Title, E.WaveId, E.IsFinalExam, ET.TypeName " +
                "FROM Exams E " +
                "LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id " +
                "WHERE E.Id = @ExamId", 
                new { ExamId = examId });
 
            if (exam != null && (bool)exam.IsFinalExam && exam.WaveId != null && (int)exam.WaveId > 0 && 
                !((string)(exam.TypeName ?? "")).ToLower().Contains("wave"))
            {
                int waveId = (int)exam.WaveId;
                var waveExams = (await conn.QueryAsync<dynamic>(
                    @"SELECT E.Id, E.Title, E.IsFinalExam, E.TotalQuestionsToShow, E.TotalPoints, ET.TypeName 
                      FROM Exams E 
                      LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id 
                      WHERE E.WaveId = @WaveId", 
                    new { WaveId = waveId })).ToList();
                var quizzes = waveExams.Where(e => !(bool)e.IsFinalExam).ToList();

                var waveAttempts = (await conn.QueryAsync(
                    "SELECT UA.UserId, UA.ExamId, UA.Score, UA.IsPassed, UA.FinalScore " +
                    "FROM UserExamAttempts UA " +
                    "JOIN Exams E ON UA.ExamId = E.Id " +
                    "WHERE E.WaveId = @WaveId", 
                    new { WaveId = waveId })).ToList();

                var attemptsByUser = waveAttempts
                    .GroupBy(a => (string)a.UserId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var examPoints = new Dictionary<int, double>();
                foreach (var e in waveExams)
                {
                    double maxPoints = 0.0;
                    int questionsToShow = e.TotalQuestionsToShow != null ? Convert.ToInt32(e.TotalQuestionsToShow) : 0;
                    double staticPoints = e.TotalPoints != null ? Convert.ToDouble(e.TotalPoints) : 0.0;
                    
                    if (questionsToShow > 0)
                    {
                        string typeName = e.TypeName ?? "";
                        maxPoints = typeName.ToLower().Contains("wave") ? questionsToShow * 2.0 : questionsToShow * 1.0;
                    }
                    else
                    {
                        maxPoints = staticPoints;
                    }
                    if (maxPoints <= 0) maxPoints = 100.0; // fallback safety
                    examPoints[(int)e.Id] = maxPoints;
                }

                foreach (var row in results)
                {
                    string userId = row.Id;

                    double totalMaxPoints = 0.0;
                    double totalStudentPoints = 0.0;

                    // 1. Calculate Quiz Points
                    foreach (var q in quizzes)
                    {
                        int qId = (int)q.Id;
                        double maxQuizPts = examPoints.ContainsKey(qId) ? examPoints[qId] : 100.0;
                        totalMaxPoints += maxQuizPts;

                        double bestQuizPercent = 0.0;
                        if (attemptsByUser.TryGetValue(userId, out var userAttempts))
                        {
                            var quizAttempts = userAttempts.Where(a => (int)a.ExamId == qId).ToList();
                            if (quizAttempts.Any())
                            {
                                bestQuizPercent = quizAttempts.Max(a => a.Score != null ? Convert.ToDouble(a.Score) : 0.0);
                            }
                        }
                        double studentQuizPts = (bestQuizPercent / 100.0) * maxQuizPts;
                        totalStudentPoints += studentQuizPts;
                    }

                    // 2. Calculate Final Exam Points
                    double maxFinalPts = examPoints.ContainsKey(examId) ? examPoints[examId] : 100.0;
                    totalMaxPoints += maxFinalPts;

                    double bestFinalPercent = 0.0;
                    if (attemptsByUser.TryGetValue(userId, out var userAttemptsList))
                    {
                        var finalAttempts = userAttemptsList.Where(a => (int)a.ExamId == examId).ToList();
                        if (finalAttempts.Any())
                        {
                            bestFinalPercent = finalAttempts.Max(a => a.Score != null ? Convert.ToDouble(a.Score) : 0.0);
                        }
                    }
                    double studentFinalPts = (bestFinalPercent / 100.0) * maxFinalPts;
                    totalStudentPoints += studentFinalPts;

                    // 3. Overwrite properties on the row
                    row.FinalScore = (decimal)totalStudentPoints;
                    row.TotalScoreAvailable = (decimal)totalMaxPoints;
                    row.Score = totalMaxPoints > 0 ? (decimal)((totalStudentPoints / totalMaxPoints) * 100.0) : 0;
                    
                    row.IsPassed = row.Status == "Completed" && totalStudentPoints >= (totalMaxPoints * 0.70);
                }
            }

            return results;
        }

        public async Task<IEnumerable<StudentExamReviewRowDto>> GetStudentExamReviewAsync(int examId, string studentId, int? attemptId = null)
        {
            using var conn = new SqlConnection(_connectionString);
            var rows = await conn.QueryAsync<StudentExamReviewRowDto>(
                "sp_GetStudentExamReviews",
                new { ExamId = examId, StudentId = studentId, AttemptId = attemptId },
                commandType: CommandType.StoredProcedure);
            return rows;
        }

        public async Task UpdateExamAsync(adminExamDto dto)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                "sp_UpdateExam",
                new
                {
                    Id = dto.Id,
                    Title = dto.Title,
                    Description = dto.Description,
                    StartTime = dto.StartTime,
                    EndTime = dto.EndTime,
                    Duration = dto.DurationInMinutes,
                    PassPercentage = dto.PassPercentage,
                    IsActive = dto.IsActive,
                    WaveId = dto.WaveId,
                    ExamTypeId = dto.ExamTypeId,
                    IsGraded = dto.IsGraded,
                    TotalQuestionsToShow = dto.TotalQuestionsToShow,
                    ShowQuestionOverview = dto.ShowQuestionOverview,
                    IsFinalExam = dto.IsFinalExam
                },
                commandType: CommandType.StoredProcedure
            );

            // Sync Rules: Delete existing and re-insert
            await conn.ExecuteAsync("DELETE FROM ExamGenerationRules WHERE ExamId = @Id", new { Id = dto.Id });
            if (dto.GenerationRules != null && dto.GenerationRules.Any())
            {
                foreach (var rule in dto.GenerationRules)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO ExamGenerationRules (ExamId, CategoryId, TopicId, TargetRole, EasyCount, MediumCount, HardCount)
                        VALUES (@ExamId, @CategoryId, @TopicId, @TargetRole, @EasyCount, @MediumCount, @HardCount)",
                        new { ExamId = dto.Id, rule.CategoryId, rule.TopicId, TargetRole = rule.TargetRole ?? "All", rule.EasyCount, rule.MediumCount, rule.HardCount });
                }
            }

            // Sync dates for existing assignments that haven't been started yet
            var updateAssignmentsSql = @"
                UPDATE ExamAssignments 
                SET ScheduledStartTime = @StartTime, ScheduledEndTime = @EndTime
                WHERE ExamId = @Id 
                AND NOT EXISTS (
                    SELECT 1 FROM UserExamAttempts UA 
                    WHERE UA.ExamId = ExamAssignments.ExamId 
                      AND UA.UserId = ExamAssignments.StudentId
                      AND UA.AttemptDate >= ISNULL(ExamAssignments.ScheduledStartTime, @StartTime)
                )";
            await conn.ExecuteAsync(updateAssignmentsSql, new { Id = dto.Id, StartTime = dto.StartTime, EndTime = dto.EndTime });
        }

        public async Task<IEnumerable<ResultDetailDto>> GetResultReportAsync(int attemptId)
        {
            using var conn = new SqlConnection(_connectionString);
            var result = await conn.QueryAsync<ResultDetailDto>("sp_Student_GetResultReport", new { AttemptId = attemptId }, commandType: CommandType.StoredProcedure);
            return result;
        }

        public async Task SubmitFinalAsync(int attemptId, string status = "Completed")
        {
            int maxRetries = 3;
            int delayMs = 500;
            
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using var conn = new SqlConnection(_connectionString);
                    // 1. Call the stored procedure to finalize the exam (standard logic)
                    await conn.ExecuteAsync("sp_Student_SubmitFinal", 
                        new { AttemptId = attemptId, Status = status }, 
                        commandType: CommandType.StoredProcedure);

                    // 2. Dynamic Scoring & Integrity Enforcement & Certificate Generation
                    var sql = @"
                DECLARE @UserId NVARCHAR(450);
                DECLARE @ExamId INT;
                DECLARE @WaveId INT = 0;
                DECLARE @IsFinalExam BIT = 0;
                DECLARE @PassPercentage INT = 70;
                DECLARE @IsGraded BIT = 1;
                
                SELECT TOP 1 @UserId = UserId, @ExamId = ExamId 
                FROM UserExamAttempts WHERE Id = @AttemptId;
                
                SELECT TOP 1 @WaveId = ISNULL(WaveId, 0), @IsFinalExam = ISNULL(IsFinalExam, 0), @PassPercentage = ISNULL(PassPercentage, 70), @IsGraded = ISNULL(IsGraded, 1)
                FROM Exams WHERE Id = @ExamId;
                
                DECLARE @RoleName NVARCHAR(256) = (
                    SELECT TOP 1 R.Name 
                    FROM AspNetUserRoles UR 
                    JOIN AspNetRoles R ON UR.RoleId = R.Id 
                    WHERE UR.UserId = @UserId
                );

                DECLARE @TotalPoints DECIMAL(18,2) = (
                    SELECT ISNULL(SUM(Q.Points), 0)
                    FROM UserSeenQuestions USQ
                    JOIN Questions Q ON USQ.QuestionId = Q.Id
                    WHERE USQ.AttemptId = @AttemptId
                );

                IF @TotalPoints = 0
                BEGIN
                    SELECT TOP 1 @TotalPoints = ISNULL(E.TotalPoints, 0)
                    FROM Exams E WHERE E.Id = @ExamId;
                END

                DECLARE @AbsolutePoints DECIMAL(18,2) = (SELECT ISNULL(FinalScore, 0) FROM UserExamAttempts WHERE Id = @AttemptId);
                DECLARE @AchievementPercentage DECIMAL(18,2) = 0;

                -- Force score to 0 for cheating/blur violations FIRST
                IF @Status LIKE 'Fail_%' AND @Status <> 'Fail_Timeout'
                BEGIN
                    SET @AbsolutePoints = 0;
                END

                -- IF THIS IS A FINAL EXAM, CALCULATE OVERALL WAVE SCORE DYNAMICALLY (EXCEPT WAVE TYPE EXAMS)
                IF @IsFinalExam = 1 AND @WaveId > 0 AND EXISTS (SELECT 1 FROM Exams e JOIN ExamTypes et ON e.ExamTypeId = et.Id WHERE e.Id = @ExamId AND LOWER(et.TypeName) NOT LIKE '%wave%')
                BEGIN
                    DECLARE @WaveExams TABLE (
                        ExamId INT,
                        MaxPoints DECIMAL(18,2)
                    );

                    INSERT INTO @WaveExams (ExamId, MaxPoints)
                    SELECT 
                        E.Id,
                        COALESCE(
                            (SELECT NULLIF(SUM(q.Points), 0) FROM UserSeenQuestions usq JOIN Questions q ON usq.QuestionId = q.Id JOIN UserExamAttempts uea ON usq.AttemptId = uea.Id WHERE uea.ExamId = E.Id AND uea.UserId = @UserId),
                            NULLIF((SELECT SUM(egr.EasyCount + egr.MediumCount + egr.HardCount) * 2 FROM ExamGenerationRules egr WHERE egr.ExamId = E.Id AND (egr.TargetRole = 'All' OR egr.TargetRole = @RoleName OR @RoleName LIKE '%' + egr.TargetRole + '%')), 0),
                            NULLIF(CASE WHEN ISNULL(E.TotalQuestionsToShow, 0) > 0 THEN 
                                CASE WHEN LOWER(ET.TypeName) LIKE '%wave%' THEN E.TotalQuestionsToShow * 2.0 ELSE E.TotalQuestionsToShow * 1.0 END
                                ELSE 0 END, 0),
                            NULLIF(E.TotalPoints, 0),
                            CASE WHEN LOWER(ET.TypeName) LIKE '%wave%' THEN 10.0 ELSE 5.0 END
                        )
                    FROM Exams E
                    LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id
                    WHERE E.WaveId = @WaveId;

                    DECLARE @SumMaxPoints DECIMAL(18,2) = 0;
                    DECLARE @SumEarnedPoints DECIMAL(18,2) = 0;

                    SELECT 
                        @SumMaxPoints = ISNULL(SUM(WE.MaxPoints), 0),
                        @SumEarnedPoints = ISNULL(SUM((ISNULL(T.BestScore, 0) / 100.0) * WE.MaxPoints), 0)
                    FROM @WaveExams WE
                    LEFT JOIN (
                        SELECT ExamId, MAX(Score) as BestScore
                        FROM UserExamAttempts
                        WHERE UserId = @UserId
                        GROUP BY ExamId
                    ) T ON WE.ExamId = T.ExamId;

                    IF @SumMaxPoints > 0
                    BEGIN
                        SET @TotalPoints = @SumMaxPoints;
                        SET @AbsolutePoints = @SumEarnedPoints;
                    END
                    
                    SET @PassPercentage = 70; 
                END

                IF @TotalPoints > 0
                BEGIN
                    SET @AchievementPercentage = (@AbsolutePoints / @TotalPoints) * 100.0;
                END

                UPDATE UserExamAttempts
                SET FinalScore = CASE WHEN @IsFinalExam = 1 THEN @AbsolutePoints ELSE (CASE WHEN @AchievementPercentage >= @PassPercentage THEN FinalScore ELSE FinalScore END) END,
                    Score = @AchievementPercentage, 
                    IsPassed = CASE 
                        WHEN @Status LIKE 'Fail_%' AND @Status <> 'Fail_Timeout' THEN 0
                        WHEN @IsFinalExam = 1 AND @WaveId > 0 THEN (CASE WHEN @AchievementPercentage >= @PassPercentage THEN 1 ELSE 0 END)
                        WHEN @IsGraded = 0 THEN 1
                        ELSE (CASE WHEN @AchievementPercentage >= @PassPercentage THEN 1 ELSE 0 END) 
                    END
                WHERE Id = @AttemptId;
            ";

                    await conn.ExecuteAsync(sql, new { AttemptId = attemptId, Status = status });
                    
                    // If successful, break out of the retry loop
                    break;
                }
                catch (SqlException ex) when (ex.Number == 1205 || ex.Message.Contains("deadlocked"))
                {
                    if (i == maxRetries - 1)
                    {
                        throw; // Re-throw if all retries failed
                    }
                    // Wait before retrying (exponential backoff)
                    await Task.Delay(delayMs * (i + 1));
                }
            }
        }

        public async Task SaveStudentAnswerAsync(int attemptId, int questionId, int selectedChoiceId)
        {
            using var conn = new SqlConnection(_connectionString);
            // Logic to save individual answer to StudentQuestionDetails
            // This ensures every click/auto-save is recorded properly
            await conn.ExecuteAsync("sp_Student_SaveAnswer", 
                new { AttemptId = attemptId, QuestionId = questionId, ChoiceId = selectedChoiceId }, 
                commandType: CommandType.StoredProcedure);
        }
        public async Task BatchSaveStudentAnswersAsync(int attemptId, List<AnswerModel> answers)
        {
            if (answers == null || !answers.Any()) return;

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var sqlList = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("@AttemptId", attemptId);

                int index = 0;
                foreach (var a in answers)
                {
                    var qParam = $"@q{index}";
                    var cParam = $"@c{index}";
                    parameters.Add(qParam, a.QuestionId);
                    parameters.Add(cParam, a.SelectedChoiceId);
                    sqlList.Add($"({qParam}, {cParam})");
                    index++;
                }

                if (sqlList.Any())
                {
                    string valuesList = string.Join(",", sqlList);
                    string sql = $@"
                        -- 1. Clear existing answers
                        DELETE FROM StudentQuestionDetails WHERE UserExamAttemptId = @AttemptId;

                        -- 2. Bulk Insert all answers at once
                        INSERT INTO StudentQuestionDetails (UserExamAttemptId, QuestionId, SelectedChoiceId, IsCorrect)
                        SELECT @AttemptId, V.QId, V.CId, ISNULL(C.IsCorrect, 0)
                        FROM (VALUES {valuesList}) AS V(QId, CId)
                        LEFT JOIN dbo.Choices C ON V.CId = C.Id;";

                    await conn.ExecuteAsync(sql, parameters, transaction);
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task<UserAttemptSummaryDto?> GetExistingAttemptAsync(int examId, string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            // Return only the LATEST attempt
            var sql = "SELECT TOP 1 Id, [Status], IsPassed, AttemptDate, StartTime, AttemptNumber FROM UserExamAttempts WHERE ExamId = @ExamId AND UserId = @UserId ORDER BY AttemptNumber DESC, Id DESC";
            return await conn.QueryFirstOrDefaultAsync<UserAttemptSummaryDto>(sql, new { ExamId = examId, UserId = userId });
        }

        public async Task AssignExamToStudentAsync(int examId, string studentId, DateTime? startTime = null, DateTime? endTime = null)
        {
            using var conn = new SqlConnection(_connectionString);
            
            // Check if there is an existing assignment that hasn't been completed yet
            var existingId = await conn.ExecuteScalarAsync<int?>(@"
                SELECT TOP 1 EA.Id FROM ExamAssignments EA
                WHERE EA.ExamId = @ExamId AND EA.StudentId = @StudentId
                AND NOT EXISTS (
                    SELECT 1 FROM UserExamAttempts UA 
                    WHERE UA.UserId = EA.StudentId AND UA.ExamId = EA.ExamId
                    AND UA.AttemptDate >= ISNULL(EA.ScheduledStartTime, '2000-01-01')
                    AND UA.[Status] IN ('Completed', 'Fail_Cheating', 'Fail_ProhibitedActions', 'Fail_Timeout', 'Fail_Abandoned')
                )
                ORDER BY EA.Id DESC", new { ExamId = examId, StudentId = studentId });

            if (existingId.HasValue)
            {
                // Update existing one
                await conn.ExecuteAsync(@"
                    UPDATE ExamAssignments 
                    SET ScheduledStartTime = @StartTime, ScheduledEndTime = @EndTime, IsEmailSent = 0
                    WHERE Id = @Id", new { Id = existingId.Value, StartTime = startTime, EndTime = endTime });
            }
            else
            {
                // Insert new row if no pending assignment exists
                var sql = @"
                    INSERT INTO ExamAssignments (ExamId, StudentId, ScheduledStartTime, ScheduledEndTime, IsEmailSent)
                    VALUES (@ExamId, @StudentId, @StartTime, @EndTime, 0)";
                await conn.ExecuteAsync(sql, new { ExamId = examId, StudentId = studentId, StartTime = startTime, EndTime = endTime });
            }
        }

        public async Task<ExamAssignmentDto?> GetStudentAssignmentAsync(int examId, string studentId)
        {
            using var conn = new SqlConnection(_connectionString);
            // Fetch the LATEST assignment for this student/exam combo
            var sql = "SELECT TOP 1 * FROM ExamAssignments WHERE ExamId = @ExamId AND StudentId = @StudentId ORDER BY Id DESC";
            return await conn.QueryFirstOrDefaultAsync<ExamAssignmentDto>(sql, new { ExamId = examId, StudentId = studentId });
        }

        public async Task<int> GetAssignmentCountAsync(int examId, string studentId)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = "SELECT COUNT(*) FROM ExamAssignments WHERE ExamId = @ExamId AND StudentId = @StudentId";
            return await conn.ExecuteScalarAsync<int>(sql, new { ExamId = examId, StudentId = studentId });
        }

        public async Task<int> CreateStudentAttemptAsync(int examId, string studentId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                // Acquire exclusive application lock at the DB level for this student/exam combination
                var lockSql = @"
                    DECLARE @LockRes NVARCHAR(255) = 'CreateAttempt_' + CAST(@ExamId AS VARCHAR) + '_' + @StudentId;
                    EXEC sp_getapplock @Resource = @LockRes, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;";
                await conn.ExecuteAsync(lockSql, new { ExamId = examId, StudentId = studentId }, transaction);

                // Double check if an active InProgress attempt already exists
                var activeAttemptId = await conn.ExecuteScalarAsync<int?>(@"
                    SELECT TOP 1 Id FROM UserExamAttempts 
                    WHERE ExamId = @ExamId AND UserId = @StudentId AND [Status] = 'InProgress'", 
                    new { ExamId = examId, StudentId = studentId }, transaction);

                if (activeAttemptId.HasValue)
                {
                    transaction.Commit();
                    return activeAttemptId.Value;
                }

                // Increment AttemptNumber by counting existing attempts for this user/exam
                var sql = @"
                    DECLARE @AttemptNo INT = (SELECT ISNULL(COUNT(*), 0) + 1 FROM UserExamAttempts WHERE ExamId = @ExamId AND UserId = @StudentId);
                    INSERT INTO UserExamAttempts (ExamId, UserId, AttemptNumber, StartTime, [Status], AttemptDate) 
                    VALUES (@ExamId, @StudentId, @AttemptNo, GETDATE(), 'InProgress', GETDATE()); 
                    SELECT CAST(SCOPE_IDENTITY() AS INT);";
                var id = await conn.ExecuteScalarAsync<int>(sql, new { ExamId = examId, StudentId = studentId }, transaction);
                
                transaction.Commit();
                return id;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task RecordSeenQuestionsAsync(int attemptId, string userId, IEnumerable<int> questionIds)
        {
            if (questionIds == null || !questionIds.Any()) return;

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var sqlList = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("@UserId", userId);
                parameters.Add("@AttemptId", attemptId);

                int index = 0;
                foreach (var qId in questionIds)
                {
                    var paramName = $"@q{index}";
                    parameters.Add(paramName, qId);
                    sqlList.Add($"({paramName})");
                    index++;
                }

                if (sqlList.Any())
                {
                    string insertValues = string.Join(",", sqlList);
                    string sql = $@"
                        -- 1. Delete existing for this attempt
                        DELETE FROM UserSeenQuestions WHERE AttemptId = @AttemptId;

                        -- 2. Remove stale rows from old attempts for these questions
                        DELETE FROM UserSeenQuestions 
                        WHERE UserId = @UserId 
                          AND QuestionId IN ({string.Join(",", sqlList.Select((_, i) => $"@q{i}"))}) 
                          AND AttemptId <> @AttemptId;

                        -- 3. Insert new records
                        INSERT INTO UserSeenQuestions (UserId, QuestionId, AttemptId)
                        SELECT @UserId, Q.QId, @AttemptId
                        FROM (VALUES {insertValues}) AS Q(QId);";

                    await conn.ExecuteAsync(sql, parameters, transaction);
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

       


        public async Task<IEnumerable<int>> GetSeenQuestionIdsAsync(int attemptId)
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryAsync<int>("SELECT QuestionId FROM UserSeenQuestions WHERE AttemptId = @AttemptId", new { AttemptId = attemptId });
        }

        // Admin edit: add/delete questions and choices on existing exams
        public async Task<int> AddQuestionForExistingExamAsync(int examId, string questionText, int points, int QuestionTypeId, int difficulty, int? topicId = null)
        {
            using var conn = new SqlConnection(_connectionString);

            // Duplicate prevention
            var existingId = await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT q.Id FROM Questions q INNER JOIN ExamQuestions eq ON q.Id = eq.QuestionId WHERE eq.ExamId = @examId AND q.QuestionText = @questionText", 
                new { examId, questionText });
            
            if (existingId.HasValue && existingId.Value > 0)
                return existingId.Value;

            var newId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO Questions(QuestionText, Points, CategoryId, Difficulty, TopicId) 
                  VALUES(@QuestionText, @Points, @CategoryId, @Difficulty, @TopicId); 
                  SELECT CAST(SCOPE_IDENTITY() as int)",
                new { QuestionText = questionText, Points = points, CategoryId = QuestionTypeId, Difficulty = difficulty, TopicId = topicId });

            await conn.ExecuteAsync("INSERT INTO ExamQuestions(ExamId, QuestionId) VALUES(@ExamId, @QuestionId)", new { ExamId = examId, QuestionId = newId });
            await UpdateExamTotalPointsAsync(examId);
            return newId;

            return newId;
        }

        public async Task<int> AddChoiceForExistingQuestionAsync(int questionId, string choiceText, bool isCorrect)
        {
            using var conn = new SqlConnection(_connectionString);
            
            // Check for existing duplicate to prevent accidental double-clicks from creating new rows
            var existingId = await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT Id FROM Choices WHERE QuestionId = @questionId AND ChoiceText = @choiceText",
                new { questionId, choiceText });
            
            if (existingId.HasValue && existingId.Value > 0)
                return existingId.Value;

            return await conn.ExecuteScalarAsync<int>(
                "sp_AddChoice",
                new { QuestionId = questionId, ChoiceText = choiceText, IsCorrect = isCorrect },
                commandType: CommandType.StoredProcedure);
        }

        public async Task WipeStudentExamDataAsync(int examId, string studentId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                // 1. Get all attempts for this user and exam
                var attemptIds = await conn.QueryAsync<int>(
                    "SELECT Id FROM UserExamAttempts WHERE ExamId = @ExamId AND UserId = @UserId",
                    new { ExamId = examId, UserId = studentId }, transaction);

                if (attemptIds.Any())
                {
                    // 2. Delete answers
                    await conn.ExecuteAsync(
                        "DELETE FROM StudentQuestionDetails WHERE UserExamAttemptId IN @Ids",
                        new { Ids = attemptIds }, transaction);

                    // 3. Delete seen questions
                    await conn.ExecuteAsync(
                        "DELETE FROM UserSeenQuestions WHERE AttemptId IN @Ids",
                        new { Ids = attemptIds }, transaction);

                    // 4. Delete attempts
                    await conn.ExecuteAsync(
                        "DELETE FROM UserExamAttempts WHERE Id IN @Ids",
                        new { Ids = attemptIds }, transaction);
                }

                // 5. Delete Assignment
                await conn.ExecuteAsync(
                    "DELETE FROM ExamAssignments WHERE ExamId = @ExamId AND StudentId = @UserId",
                    new { ExamId = examId, UserId = studentId }, transaction);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        public async Task DeleteAllQuestionsForExamAsync(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            var questionIds = await conn.QueryAsync<int>(
                "SELECT QuestionId FROM ExamQuestions WHERE ExamId = @examId", new { examId });

            if (questionIds.Any())
            {
                foreach (var qId in questionIds)
                {
                    await conn.ExecuteAsync(
                        "sp_DeleteQuestion",
                        new { QuestionId = qId },
                        commandType: CommandType.StoredProcedure);
                }
                await UpdateExamTotalPointsAsync(examId);
            }
        }

        public async Task DeleteQuestionAsync(int questionId)
        {
            using var conn = new SqlConnection(_connectionString);
            var examId = await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT ExamId FROM ExamQuestions WHERE QuestionId = @questionId", new { questionId });

            await conn.ExecuteAsync(
                "sp_DeleteQuestion",
                new { QuestionId = questionId },
                commandType: CommandType.StoredProcedure);

            if (examId.HasValue)
                await UpdateExamTotalPointsAsync(examId.Value);
        }

        public async Task<UserAttemptSummaryDto?> GetAttemptByIdAsync(int attemptId)
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryFirstOrDefaultAsync<UserAttemptSummaryDto>(
                "SELECT Id as AttemptId, ExamId, UserId, Status, Score, FinalScore, AttemptNumber FROM UserExamAttempts WHERE Id = @Id",
                new { Id = attemptId });
        }

        public async Task DeleteChoiceAsync(int choiceId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                "sp_DeleteChoice",
                new { ChoiceId = choiceId },
                commandType: CommandType.StoredProcedure);
        }

        public async Task<int> AssignUsersToWaveAsync(int waveId, List<string> userIds, string siteUrl = "http://41.33.149.186:5208")
        {
            if (userIds == null || !userIds.Any()) return 0;

            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                
                // 1. Get wave info once (including StartDate)
                var waveInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT TOP 1 WaveName, StartDate FROM TrainingWaves WHERE Id = @Id", new { Id = waveId });

                // 2. Perform DB assignment in ONE BATCH
                string sqlBatch = @"
                    INSERT INTO UserWaves (UserId, WaveId, JoinDate)
                    SELECT Id, @WaveId, @JoinDate
                    FROM AspNetUsers
                    WHERE Id IN @UserIds
                    AND Id NOT IN (SELECT UserId FROM UserWaves WHERE WaveId = @WaveId)";

                int count = await conn.ExecuteAsync(sqlBatch, 
                    new { WaveId = waveId, JoinDate = DateTime.Now, UserIds = userIds }, 
                    commandTimeout: 120);

                 // 2b. Automatically assign all active exams of this wave to the newly assigned users
                 try
                 {
                     var activeExams = (await conn.QueryAsync<dynamic>(
                         "SELECT Id, StartTime, EndTime FROM Exams WHERE WaveId = @WaveId AND IsActive = 1", 
                         new { WaveId = waveId })).ToList();

                     foreach (var exam in activeExams)
                     {
                         foreach (var userId in userIds)
                         {
                             try
                             {
                                 await AssignExamToStudentAsync((int)exam.Id, userId, (DateTime?)exam.StartTime, (DateTime?)exam.EndTime);
                             }
                             catch (Exception)
                             {
                                 // Suppress exam-specific assignment errors to prevent failing the entire wave enrollment
                             }
                         }
                     }
                 }
                 catch (Exception)
                 {
                     // Suppress to not break main wave registration flow
                 }

                // 3. Materialize ONLY NEWLY ASSIGNED user data for background emails
                var usersForEmail = (await conn.QueryAsync<UserEmailInfo>(
                    @"SELECT UserName, Email FROM AspNetUsers 
                      WHERE Id IN @Ids 
                      AND Id IN (SELECT UserId FROM UserWaves WHERE WaveId = @WaveId AND JoinDate >= @RecentLimit)", 
                    new { Ids = userIds, WaveId = waveId, RecentLimit = DateTime.Now.AddMinutes(-1) }))
                    .Where(u => !string.IsNullOrEmpty(u.Email))
                    .ToList();

                // 4. Send Professional Emails in BACKGROUND
                _ = Task.Run(async () => 
                {
                    string waveName = waveInfo?.WaveName ?? "New Batch";
                    DateTime? startDate = waveInfo?.StartDate;
                    string formattedDate = startDate.HasValue ? startDate.Value.ToString("MMMM dd, yyyy - hh:mm tt") : "To be announced";

                    foreach (var user in usersForEmail)
                    {
                        try 
                        {
                            string firstName = user.UserName.Split(' ')[0];

                            string subject = $"Welcome to {waveName} - Registration Confirmed";

                            string siteButton = (!string.IsNullOrEmpty(siteUrl) ? $@"
                                <div style='text-align: center; margin: 30px 0;'>
                                    <a href='{siteUrl}' style='background-color: #10b981; color: white; padding: 14px 28px; text-decoration: none; border-radius: 8px; font-weight: bold; font-size: 16px; box-shadow: 0 4px 6px rgba(16, 185, 129, 0.2);'>Go to Portal Instance</a>
                                </div>" : "");

                            string htmlBody = $@"
<div style='background-color: #f4f7fa; padding: 40px; font-family: ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;'>
    <div style='max-width: 600px; margin: 0 auto; background: white; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.08);'>
        
        <div style='background: #10b981; padding: 30px; text-align: center;'>
            <h2 style='color: white; margin: 0; font-size: 22px;'>Pharmacy Basics Program</h2>
        </div>

        <div style='padding: 30px; color: #374151; line-height: 1.6;'>
            <p style='font-size: 18px;'>Dear <b>{firstName}</b>,</p>
            
            <p>Welcome to <b>{waveName}</b> of Pharmacy Basics Program — we’re glad to have you with us!</p>
            
            <p>Your registration has been successfully confirmed. Here are your session details:</p>

            <div style='background: #f9fafb; border: 1px solid #e5e7eb; border-radius: 12px; padding: 20px; margin: 25px 0;'>
                <p style='margin: 0 0 10px 0;'>📅 <b>Date & Time:</b> {formattedDate}</p>
                <p style='margin: 0;'>📍 <b>Location:</b> offline in Main Branch</p>
            </div>

            {siteButton}

            <hr style='border: 0; border-top: 1px solid #eee; margin: 30px 0;'>

            <p style='font-weight: bold; color: #111827;'>A quick note before we start:</p>
            <ul style='padding-left: 20px;'>
                <li style='margin-bottom: 10px;'>Please arrive 10–15 minutes early to ensure a smooth check-in.</li>
                <li>Keep an eye on your email for any further updates or announcements.</li>
            </ul>

            <p>If you have any questions or face any issues, feel free to reach out to us.</p>
            
            <p>Looking forward to seeing you and having a great Training Program together.</p>

            <p style='margin-top: 40px; line-height: 1.2;'>
                Best regards,<br>
                <span style='color: #10b981; font-weight: bold;'>Eltarshoubi Training Academy Team</span>
            </p>
        </div>

        <div style='background: #f9fafb; padding: 20px; text-align: center; color: #9ca3af; font-size: 12px; border-top: 1px solid #f3f4f6;'>
            <p>&copy; {DateTime.Now.Year} Eltarshouby Pharmacies Group. All rights reserved.</p>
        </div>
    </div>
</div>";
                            await _emailSender.SendEmailAsync(user.Email, subject, htmlBody);
                        }
                        catch { /* Background failure safety */ }
                    }
                });

                return count;
            }
        }

        // Simple helper for background thread safety
        private class UserEmailInfo { public string UserName { get; set; } = ""; public string Email { get; set; } = ""; }

        public async Task<IEnumerable<UserDto>> GetUsersByWaveIdAsync(int waveId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT 
                    u.Id, u.UserName, u.Email, u.PhoneNumber as Phone, u.UserCode, 
                    b.BranchName, u.CertificateCode, r.Name as RoleName, u.IsActive, tw.WaveName,
                    '' as LastExamStatus, -- Removed for performance
                    uw.JoinDate,
                    0 as IsAlreadyAssigned
                FROM dbo.AspNetUsers u WITH(NOLOCK)
                INNER JOIN dbo.UserWaves uw WITH(NOLOCK) ON u.Id = uw.UserId 
                INNER JOIN dbo.TrainingWaves tw WITH(NOLOCK) ON uw.WaveId = tw.Id
                LEFT JOIN dbo.AspNetUserRoles ur WITH(NOLOCK) ON u.Id = UR.UserId
                LEFT JOIN dbo.AspNetRoles r WITH(NOLOCK) ON ur.RoleId = R.Id
                LEFT JOIN dbo.Branches b WITH(NOLOCK) ON u.BranchId = b.Id
                WHERE uw.WaveId = @WaveId AND uw.IsActive = 1";

            var users = await conn.QueryAsync<UserDto>(sql, new { WaveId = waveId });
            return users;
        }

        public async Task<int> AssignExamToStudentsAsync(int examId, List<string> studentIds, string siteUrl = "")
        {
            if (studentIds == null || !studentIds.Any()) return 0;

            var distinctStudentIds = studentIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (!distinctStudentIds.Any()) return 0;

            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                // 1. Fetch exam details once
                var exam = await conn.QueryFirstOrDefaultAsync<adminExamDto>(
                    "SELECT Title, StartTime, EndTime FROM Exams WHERE Id = @Id", new { Id = examId });

                // 2. Perform DB assignment with default times from exam inside a transaction
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        foreach (var studentId in distinctStudentIds)
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
                                ORDER BY EA.Id DESC", new { ExamId = examId, StudentId = studentId }, transaction);

                            if (existingId.HasValue)
                            {
                                await conn.ExecuteAsync(@"
                                    UPDATE ExamAssignments 
                                    SET ScheduledStartTime = @StartTime, ScheduledEndTime = @EndTime, IsEmailSent = 0
                                    WHERE Id = @Id", new { Id = existingId.Value, StartTime = exam?.StartTime, EndTime = exam?.EndTime }, transaction);
                            }
                            else
                            {
                                var sql = @"
                                    INSERT INTO ExamAssignments (ExamId, StudentId, ScheduledStartTime, ScheduledEndTime, IsEmailSent)
                                    VALUES (@ExamId, @StudentId, @StartTime, @EndTime, 0)";
                                await conn.ExecuteAsync(sql, new { ExamId = examId, StudentId = studentId, StartTime = exam?.StartTime, EndTime = exam?.EndTime }, transaction);
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

                // 3. Materialize user data for background emails
                var users = (await conn.QueryAsync<UserDto>(
                    "SELECT Id, UserName, Email FROM AspNetUsers WHERE Id IN @Ids", new { Ids = distinctStudentIds }))
                    .ToList();

                _ = Task.Run(async () =>
                {
                    foreach (var userMatch in users)
                    {
                        if (string.IsNullOrEmpty(userMatch.Email)) continue;
                        await SendExamAssignmentEmailAsync(userMatch.Id, examId, siteUrl);
                    }
                });

                return distinctStudentIds.Count;
            }
        }

        public async Task SendExamAssignmentEmailAsync(string userId, int examId, string siteUrl = "")
        {
            using var conn = new SqlConnection(_connectionString);
            
            // Check if this is a weekly or wave exam
            var examDetails = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT ET.TypeName as ExamType, E.WaveId 
                  FROM Exams E 
                  LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id 
                  WHERE E.Id = @Id", new { Id = examId });

            if (examDetails != null)
            {
                string examType = examDetails.ExamType ?? "";
                int? waveId = examDetails.WaveId;

                // Skip sending email for weekly exams (any exam type that contains 'weekly', does not contain 'wave', or doesn't have a WaveId)
                if (examType.ToLower().Contains("weekly") || !examType.ToLower().Contains("wave") || !waveId.HasValue)
                {
                    return; // Do not send email for weekly exams
                }
            }

            var exam = await conn.QueryFirstOrDefaultAsync<adminExamDto>(
                "SELECT Title, StartTime, EndTime FROM Exams WHERE Id = @Id", new { Id = examId });
            var user = await conn.QueryFirstOrDefaultAsync<UserDto>(
                "SELECT UserName, Email FROM AspNetUsers WHERE Id = @Id", new { Id = userId });

            if (exam != null && user != null && !string.IsNullOrEmpty(user.Email))
            {
                var subject = $"New Exam Assignment: {exam.Title}";
                var htmlBody = $@"
                    <div style='background-color: #f8fafc; padding: 40px; font-family: sans-serif;'>
                        <div style='max-width: 600px; margin: 0 auto; background: white; border-radius: 16px; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1); overflow: hidden; border: 1px solid #e2e8f0;'>
                            <div style='background-color: #2563eb; padding: 30px; text-align: center;'>
                                <h2 style='color: white; margin: 0; font-size: 20px; letter-spacing: 1px;'>EXAM NOTIFICATION</h2>
                            </div>
                            <div style='padding: 30px;'>
                                <p style='color: #1e293b; font-size: 16px;'>Hello <b>{user.UserName}</b>,</p>
                                <p style='color: #475569; line-height: 1.6;'>A new exam has been officially assigned to your profile.</p>
                                <div style='margin: 25px 0; border: 1px dashed #cbd5e1; padding: 20px; border-radius: 12px; background-color: #f1f5f9;'>
                                    <p style='margin: 0; color: #64748b; font-size: 12px; text-transform: uppercase; font-weight: bold;'>Exam Title</p>
                                    <p style='margin: 5px 0 15px 0; color: #0f172a; font-size: 18px; font-weight: bold;'>{exam.Title.ToUpper()}</p>
                                    <p style='margin: 0; color: #64748b; font-size: 12px; text-transform: uppercase; font-weight: bold;'>Start Date & Time</p>
                                    <p style='margin: 5px 0 0 0; color: #2563eb; font-size: 16px; font-weight: 600;'>{exam.StartTime:MMMM dd, yyyy - hh:mm tt}</p>
                                </div>
                                <div style='text-align: center; margin-top: 30px;'>
                                    <a href='{siteUrl}' style='background-color: #2563eb; color: white; padding: 14px 28px; text-decoration: none; border-radius: 8px; font-weight: bold; font-size: 16px; display: inline-block;'>Access Exam Portal</a>
                                </div>
                            </div>
                        </div>
                    </div>";

                await _emailSender.SendEmailAsync(user.Email, subject, htmlBody);
            }
        }

        public async Task<DashboardDto> GetDashboardDataAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var dashboard = new DashboardDto();

            // 1. Staff Counts (Pharmacists vs Assistants)
            var staffCounts = (await conn.QueryAsync<dynamic>(@"
                SELECT R.Name as RoleName, COUNT(U.Id) as Count
                FROM AspNetUsers U
                INNER JOIN AspNetUserRoles UR ON U.Id = UR.UserId
                INNER JOIN AspNetRoles R ON UR.RoleId = R.Id
                WHERE R.Name IN ('pharmacist', 'assistant')
                GROUP BY R.Name")).ToList();

            dashboard.TotalPharmacists = staffCounts.Where(x => x.RoleName == "pharmacist").Sum(x => (int)x.Count);
            dashboard.TotalAssistants = staffCounts.Where(x => x.RoleName == "assistant").Sum(x => (int)x.Count);

            // 2. Active Exams
            dashboard.ActiveExamsCount = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM Exams WHERE IsActive = 1 AND EndTime > GETDATE()");

            // 3. Assigned Assistants to Active Exams
            dashboard.AssignedAssistantsCount = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(DISTINCT SA.UserId) 
                FROM UserExamAttempts SA
                JOIN AspNetUserRoles UR ON SA.UserId = UR.UserId
                JOIN AspNetRoles R ON UR.RoleId = R.Id
                JOIN Exams E ON SA.ExamId = E.Id
                WHERE R.Name = 'assistant' AND E.IsActive = 1 AND E.EndTime > GETDATE()" );

            // 4. Total Waves
            dashboard.TotalWavesCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM TrainingWaves");

            // 5. Pass Rate Overview (Dynamic)
            var passStats = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    COUNT(*) as Total,
                    SUM(CASE WHEN IsPassed = 1 THEN 1 ELSE 0 END) as Passed,
                    SUM(CASE WHEN IsPassed = 0 THEN 1 ELSE 0 END) as Failed
                FROM UserExamAttempts
                WHERE Status = 'Completed' AND Score IS NOT NULL");

            if (passStats != null && passStats.Total > 0)
            {
                dashboard.PassedAttempts = (int)(passStats.Passed ?? 0);
                dashboard.FailedAttempts = (int)(passStats.Failed ?? 0);
                dashboard.OverallPassRate = Math.Round((double)dashboard.PassedAttempts / (int)passStats.Total * 100, 1);
            }

            // 6. Pharmacists per Branch (Dynamic calculation)
            dashboard.PharmacistsPerBranch = (await conn.QueryAsync<BranchStatsDto>(@"
                SELECT B.BranchName, 
                       (SELECT COUNT(*) FROM AspNetUsers U WHERE U.BranchId = B.Id) as UserCount,
                       ISNULL(PassedFailed.Passed, 0) as Passed,
                       ISNULL(PassedFailed.Failed, 0) as Failed
                FROM Branches B
                LEFT JOIN (
                    SELECT U.BranchId,
                           SUM(CASE WHEN SA.IsPassed = 1 THEN 1 ELSE 0 END) as Passed,
                           SUM(CASE WHEN SA.IsPassed = 0 THEN 1 ELSE 0 END) as Failed
                    FROM AspNetUsers U
                    INNER JOIN UserExamAttempts SA ON U.Id = SA.UserId
                    WHERE SA.Status = 'Completed'
                    GROUP BY U.BranchId
                ) PassedFailed ON B.Id = PassedFailed.BranchId
                ORDER BY UserCount DESC")).ToList();

            // 7. Wave Enrollment Trend
            try
            {
                var trend = await conn.QueryAsync<dynamic>(@"
                    SELECT TOP 12 YEAR(JoinDate) as Yr, MONTH(JoinDate) as Mn, COUNT(*) as EnrollmentCount
                    FROM UserWaves
                    WHERE JoinDate IS NOT NULL
                    GROUP BY YEAR(JoinDate), MONTH(JoinDate)
                    ORDER BY Yr, Mn");
                dashboard.WaveEnrollmentTrend = trend.Select(x => new WaveEnrollmentDto
                {
                    Month = new System.DateTime((int)x.Yr, (int)x.Mn, 1).ToString("MMM yyyy"),
                    EnrollmentCount = (int)x.EnrollmentCount
                }).ToList();
            }
            catch { }

            // 8. Top Performing Pharmacists
            dashboard.TopPerformingPharmacists = (await conn.QueryAsync<TopPharmacistDto>(@"
                SELECT TOP 5 U.UserName as Name, U.UserCode, E.Title as ExamTitle, SA.Score as Score
                FROM UserExamAttempts SA
                JOIN AspNetUsers U ON SA.UserId = U.Id
                JOIN Exams E ON SA.ExamId = E.Id
                WHERE SA.Status = 'Completed' AND SA.Score IS NOT NULL
                ORDER BY SA.Score DESC")).ToList();
            
            // 9. Global Question Bank Stats
            dashboard.TotalQuestionsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Questions");
            
            var categoryStats = (await conn.QueryAsync<dynamic>(@"
                SELECT 
                    ISNULL(C.CategoryName, 'Uncategorized') as CategoryName,
                    ISNULL(T.TopicName, 'General') as TopicName,
                    COUNT(Q.Id) as Count
                FROM Questions Q
                LEFT JOIN Categories C ON Q.CategoryId = C.Id
                LEFT JOIN Topics T ON Q.TopicId = T.Id
                GROUP BY C.CategoryName, T.TopicName
                ORDER BY C.CategoryName, T.TopicName")).ToList();

            foreach (var r in categoryStats)
            {
                var catName = (string)r.CategoryName;
                var topName = (string)r.TopicName;
                var count = (int)r.Count;

                var cat = dashboard.QuestionsPerCategory.FirstOrDefault(c => c.CategoryName == catName);
                if (cat == null)
                {
                    cat = new CategoryStatDto { CategoryName = catName, Count = 0 };
                    dashboard.QuestionsPerCategory.Add(cat);
                }
                cat.Count += count;
                cat.Topics.Add(new TopicStatDto { TopicName = topName, Count = count });
            }

            // 10. Data Integrity: Find Mismatched Category/Topic
            dashboard.MismatchedQuestions = (await conn.QueryAsync<QuestionAnomaliesDto>(@"
                SELECT Q.Id as QuestionId, Q.QuestionText, C.CategoryName, T.TopicName, EC.CategoryName as ExpectedCategory
                FROM Questions Q
                INNER JOIN Topics T ON Q.TopicId = T.Id
                INNER JOIN Categories C ON Q.CategoryId = C.Id
                INNER JOIN Categories EC ON T.CategoryId = EC.Id
                WHERE Q.CategoryId <> T.CategoryId")).ToList();

            return dashboard;
        }
        public async Task DeactivateUserAsync(string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE AspNetUsers SET IsActive = 0 WHERE Id = @userId", new { userId });
        }
        public async Task ActivateUserAsync(string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE AspNetUsers SET IsActive = 1 WHERE Id = @userId", new { userId });
        }

        public async Task DeleteUserCascadeAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User id is required.", nameof(userId));

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(@"
DELETE FROM UserSeenQuestions WHERE AttemptId IN (SELECT Id FROM UserExamAttempts WHERE UserId = @UserId);
DELETE FROM UserSeenQuestions WHERE UserId = @UserId;
DELETE FROM UserExamAttempts WHERE UserId = @UserId;
DELETE FROM ExamAssignments WHERE StudentId = @UserId;
DELETE FROM UserWaves WHERE UserId = @UserId;
DELETE FROM AspNetUserRoles WHERE UserId = @UserId;
DELETE FROM AspNetUserClaims WHERE UserId = @UserId;
DELETE FROM AspNetUserLogins WHERE UserId = @UserId;
DELETE FROM AspNetUserTokens WHERE UserId = @UserId;
DELETE FROM AspNetUsers WHERE Id = @UserId;",
                    new { UserId = userId }, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        public async Task<IEnumerable<int>> GetQuestionsUserHasSeenForExamAsync(int examId, string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = @"SELECT DISTINCT q.QuestionId 
                        FROM UserSeenQuestions q
                        INNER JOIN UserExamAttempts a ON q.AttemptId = a.Id
                        WHERE a.UserId = @UserId AND a.ExamId = @ExamId";
            return await conn.QueryAsync<int>(sql, new { ExamId = examId, UserId = userId });
        }

        public async Task<IEnumerable<int>> GetQuestionsForAttemptAsync(int attemptId)
        {
            using var conn = new SqlConnection(_connectionString);
            var sql = "SELECT QuestionId FROM UserSeenQuestions WHERE AttemptId = @AttemptId";
            return await conn.QueryAsync<int>(sql, new { AttemptId = attemptId });
        }

        public async Task Createnewcategory(string categoryname, int? examTypeId = null)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
               await connection.ExecuteAsync("sp_AddCategory", new { CategoryName = categoryname, ExamTypeId = examTypeId }, commandType: CommandType.StoredProcedure);
                
            }

        }

        public async Task<IEnumerable<ExamTypeDto>> getallexamtypes()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var examtypes =  await connection.QueryAsync<ExamTypeDto>("sp_GetAllExamTypes" , commandType:CommandType.StoredProcedure);
                return examtypes;
            }
        }


        public Task createexamtype(string examtype)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.ExecuteAsync("sp_AddExamType", new { examtype = examtype }, commandType: CommandType.StoredProcedure);
            }
        }

        public async Task DeleteCategoryAsync(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM Categories WHERE Id = @id", new { id });
        }

        public async Task UpdateCategoryAsync(int id, string name, int? examTypeId = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE Categories SET Name = @name, ExamTypeId = @examTypeId WHERE Id = @id", new { name, examTypeId, id });
        }

        public async Task DeleteExamTypeAsync(int id)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM ExamTypes WHERE Id = @id", new { id });
        }

        public async Task UpdateExamTypeAsync(int id, string type)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE ExamTypes SET Name = @type WHERE Id = @id", new { type, id });
        }

        public async Task<string> GetInstructionsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryFirstOrDefaultAsync<string>("SELECT TOP 1 instructions FROM examinstructions") ?? "No instructions provided.";
        }

        public async Task UpdateInstructionsAsync(string instructions)
        {
            using var conn = new SqlConnection(_connectionString);
            var exists = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM examinstructions");
            if (exists > 0)
            {
                await conn.ExecuteAsync("UPDATE examinstructions SET Instructions = @instructions", new { instructions });
            }
            else
            {
                await conn.ExecuteAsync("INSERT INTO examinstructions (Instructions) VALUES (@instructions)", new { instructions });
            }
        }

        public async Task DeleteWaveAsync(int waveid)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            // Delete associated attendance first to prevent FK reference constraint errors
            await conn.ExecuteAsync("DELETE FROM UserAttendance WHERE SessionId IN (SELECT Id FROM AttendanceSessions WHERE WaveId = @WaveId)", new { WaveId = waveid });
            await conn.ExecuteAsync("DELETE FROM AttendanceSessions WHERE WaveId = @WaveId", new { WaveId = waveid });

            await conn.ExecuteAsync("sp_DeleteTrainingWaveSetNull",
                new { WaveId = waveid },
                commandType: CommandType.StoredProcedure);
        }

        public async Task DeleteExamAsync(int examid)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("sp_fullyDeleteExam", new { ExamId = examid }, commandType: CommandType.StoredProcedure);
        }
        public async Task<IEnumerable<TopicDto>> GetTopicsByCategoryAsync(int categoryId)
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryAsync<TopicDto>(
                "SELECT Id, TopicName, CategoryId, CreatedAt FROM Topics WHERE CategoryId = @categoryId ORDER BY CreatedAt DESC",
                new { categoryId });
        }

        public async Task CreateTopicAsync(string topicName, int categoryId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                "INSERT INTO Topics (TopicName, CategoryId) VALUES (@topicName, @categoryId)",
                new { topicName, categoryId });
        }

        public async Task UpdateTopicAsync(int topicId, string topicName)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE Topics SET TopicName = @topicName WHERE Id = @topicId", new { topicId, topicName });
        }

        public async Task DeleteTopicAsync(int topicId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM Topics WHERE Id = @topicId", new { topicId });
        }

        public async Task UpdateExamTotalPointsAsync(int examId)
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                UPDATE E 
                SET TotalPoints = (
                    SELECT ISNULL(SUM(Points), 0) 
                    FROM Questions Q 
                    INNER JOIN ExamQuestions EQ ON Q.Id = EQ.QuestionId 
                    WHERE EQ.ExamId = E.Id
                )
                FROM Exams E 
                WHERE E.Id = @examId";
            await conn.ExecuteAsync(sql, new { examId });
        }

        public async Task EnsureDatabaseSchemaUpdatedAsync(System.IServiceProvider serviceProvider = null)
        {
            using var conn = new SqlConnection(_connectionString);
            try
            {
                await conn.OpenAsync();

                // 1. Create Companies Table
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Companies')
                    BEGIN
                        CREATE TABLE Companies (
                            Id INT IDENTITY(1,1) PRIMARY KEY,
                            Name NVARCHAR(255) NOT NULL,
                            Description NVARCHAR(MAX) NULL,
                            CreatedAt DATETIME NOT NULL DEFAULT GETDATE()
                        );
                    END");

                // 2. Create CompanyUsers Join Table
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CompanyUsers')
                    BEGIN
                        CREATE TABLE CompanyUsers (
                            CompanyId INT NOT NULL,
                            UserId NVARCHAR(450) NOT NULL,
                            PRIMARY KEY (CompanyId, UserId),
                            FOREIGN KEY (CompanyId) REFERENCES Companies(Id) ON DELETE CASCADE,
                            FOREIGN KEY (UserId) REFERENCES AspNetUsers(Id) ON DELETE CASCADE
                        );
                    END");

                // 3. Alter AttendanceSessions Table to make WaveId nullable
                await conn.ExecuteAsync(@"
                    IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('AttendanceSessions') AND name = 'WaveId')
                    BEGIN
                        ALTER TABLE AttendanceSessions ALTER COLUMN WaveId INT NULL;
                    END");

                // 4. Add CompanyId to AttendanceSessions Table
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('AttendanceSessions') AND name = 'CompanyId')
                    BEGIN
                        ALTER TABLE AttendanceSessions ADD CompanyId INT NULL;
                        ALTER TABLE AttendanceSessions ADD CONSTRAINT FK_AttendanceSessions_Companies FOREIGN KEY (CompanyId) REFERENCES Companies(Id) ON DELETE CASCADE;
                    END");

                // 5. Add CheckInTime and CheckOutTime to UserAttendance
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UserAttendance') AND name = 'CheckInTime')
                    BEGIN
                        ALTER TABLE UserAttendance ADD CheckInTime NVARCHAR(50) NULL;
                    END");

                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UserAttendance') AND name = 'CheckOutTime')
                    BEGIN
                        ALTER TABLE UserAttendance ADD CheckOutTime NVARCHAR(50) NULL;
                    END");

                // 6. Seed "Branch Manager" role if not exists
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT 1 FROM AspNetRoles WHERE Name = 'Branch Manager')
                    BEGIN
                        INSERT INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp) 
                        VALUES (NEWID(), 'Branch Manager', 'BRANCH MANAGER', NEWID());
                    END");

                // 7. Seed a Branch Manager user for each branch
                if (serviceProvider != null)
                {
                    var userManager = (Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>)serviceProvider.GetService(typeof(Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>));
                    if (userManager != null)
                    {
                        var branches = await conn.QueryAsync<(int Id, string BranchName)>("SELECT Id, BranchName FROM Branches");
                        foreach (var branch in branches)
                        {
                            // Generate unique username & email for branch manager
                            string englishName = string.Concat(branch.BranchName.Where(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9')).ToLower();
                            if (string.IsNullOrEmpty(englishName))
                            {
                                englishName = "branch" + branch.Id;
                            }
                            
                            string userName = "manager_" + englishName;
                            string email = userName + "@exam.com";
                            
                            // Check if a branch manager user already exists for this branch in AspNetUsers
                            var existingManager = await conn.QueryFirstOrDefaultAsync<string>(
                                @"SELECT U.Id FROM AspNetUsers U 
                                  INNER JOIN AspNetUserRoles UR ON U.Id = UR.UserId
                                  INNER JOIN AspNetRoles R ON UR.RoleId = R.Id
                                  WHERE U.BranchId = @BranchId AND R.Name = 'Branch Manager'", 
                                new { BranchId = branch.Id });
                                
                            if (existingManager == null)
                            {
                                // Check if user with this email or username already exists (for other roles)
                                var userByName = await userManager.FindByNameAsync(userName);
                                if (userByName == null)
                                {
                                    var newManager = new ApplicationUser
                                    {
                                        UserName = userName,
                                        Email = email,
                                        EmailConfirmed = true,
                                        IsActive = true,
                                        BranchId = branch.Id,
                                        FullName = "Manager of " + branch.BranchName,
                                        UserCode = "MGR" + branch.Id.ToString("00")
                                    };
                                    
                                    var createRes = await userManager.CreateAsync(newManager, "manager123");
                                    if (createRes.Succeeded)
                                    {
                                        await userManager.AddToRoleAsync(newManager, "Branch Manager");
                                    }
                                }
                            }
                        }
                    }
                }

                // 8. Create Indexes for Dashboard Performance Optimization
                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserExamAttempts_Status_Score_IsPassed' AND object_id = OBJECT_ID('UserExamAttempts'))
                    BEGIN
                        CREATE NONCLUSTERED INDEX IX_UserExamAttempts_Status_Score_IsPassed 
                        ON UserExamAttempts(Status, Score) 
                        INCLUDE(UserId, ExamId, IsPassed);
                    END

                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UserWaves_JoinDate' AND object_id = OBJECT_ID('UserWaves'))
                    BEGIN
                        CREATE NONCLUSTERED INDEX IX_UserWaves_JoinDate 
                        ON UserWaves(JoinDate);
                    END

                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_AspNetUsers_BranchId' AND object_id = OBJECT_ID('AspNetUsers'))
                    BEGIN
                        CREATE NONCLUSTERED INDEX IX_AspNetUsers_BranchId 
                        ON AspNetUsers(BranchId);
                    END

                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Questions_CategoryId_TopicId' AND object_id = OBJECT_ID('Questions'))
                    BEGIN
                        CREATE NONCLUSTERED INDEX IX_Questions_CategoryId_TopicId 
                        ON Questions(CategoryId, TopicId);
                    END
                ");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("Database schema auto-migration failed: " + ex.Message);
            }
        }
    }
}