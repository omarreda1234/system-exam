USE [Eltarshouby-Exam];
GO

-- 1. vw_OverallExamResults: Flat view for holistic exam results per student
CREATE OR ALTER VIEW vw_OverallExamResults AS
SELECT 
    UA.Id AS AttemptId,
    E.Title AS ExamName,
    W.WaveName,
    ET.TypeName AS ExamType,
    U.UserName AS StudentName,
    U.Email AS StudentEmail,
    ISNULL(B.BranchName, 'Global') AS BranchName,
    ISNULL(S.ShiftName, 'Unassigned') AS ShiftName,
    R.Name AS UserRole,
    UA.AttemptNumber,
    ISNULL(UA.Score, 0) AS Score,
    ISNULL(UA.FinalScore, 0) AS FinalScore,
    ISNULL(UA.IsPassed, 0) AS IsPassed,
    UA.AttemptDate,
    ISNULL(UA.DurationInMinutes, 0) AS DurationInMinutes,
    ISNULL(UA.Status, 'Not Started') AS Status
FROM UserExamAttempts UA
INNER JOIN Exams E ON UA.ExamId = E.Id
INNER JOIN AspNetUsers U ON UA.UserId = U.Id
LEFT JOIN TrainingWaves W ON E.WaveId = W.Id
LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id
LEFT JOIN Branches B ON U.BranchId = B.Id
LEFT JOIN Shifts S ON U.ShiftId = S.Id
LEFT JOIN AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN AspNetRoles R ON UR.RoleId = R.Id;
GO

-- 2. vw_BranchPerformanceStats: Aggregated stats by Branch and Exam
CREATE OR ALTER VIEW vw_BranchPerformanceStats AS
SELECT 
    E.Title AS ExamName,
    ISNULL(B.BranchName, 'Global') AS BranchName,
    COUNT(UA.Id) AS TotalAttempts,
    SUM(CASE WHEN UA.IsPassed = 1 THEN 1 ELSE 0 END) AS PassedCount,
    SUM(CASE WHEN UA.IsPassed = 0 THEN 1 ELSE 0 END) AS FailedCount,
    AVG(UA.FinalScore) AS AverageScore,
    AVG(UA.DurationInMinutes) AS AverageDurationMinutes,
    CAST(SUM(CASE WHEN UA.IsPassed = 1 THEN 1.0 ELSE 0.0 END) / NULLIF(COUNT(UA.Id), 0) * 100 AS DECIMAL(5,2)) AS PassRatePercentage
FROM UserExamAttempts UA
INNER JOIN Exams E ON UA.ExamId = E.Id
INNER JOIN AspNetUsers U ON UA.UserId = U.Id
LEFT JOIN Branches B ON U.BranchId = B.Id
GROUP BY E.Title, B.BranchName;
GO

-- 3. vw_QuestionAccuracyAnalysis: Question-level aggregated accuracy and item analysis
CREATE OR ALTER VIEW vw_QuestionAccuracyAnalysis AS
SELECT 
    E.Title AS ExamName,
    Q.QuestionText,
    C.CategoryName,
    Q.Difficulty,
    COUNT(SQD.Id) AS TotalAnswers,
    SUM(CASE WHEN SQD.IsCorrect = 1 THEN 1 ELSE 0 END) AS CorrectAnswersCount,
    SUM(CASE WHEN SQD.IsCorrect = 0 THEN 1 ELSE 0 END) AS IncorrectAnswersCount,
    CAST(SUM(CASE WHEN SQD.IsCorrect = 1 THEN 1.0 ELSE 0.0 END) / NULLIF(COUNT(SQD.Id), 0) * 100 AS DECIMAL(5,2)) AS AccuracyPercentage
FROM StudentQuestionDetails SQD
INNER JOIN UserExamAttempts UA ON SQD.UserExamAttemptId = UA.Id
INNER JOIN Exams E ON UA.ExamId = E.Id
INNER JOIN Questions Q ON SQD.QuestionId = Q.Id
LEFT JOIN Categories C ON Q.CategoryId = C.Id
GROUP BY E.Title, Q.QuestionText, C.CategoryName, Q.Difficulty;
GO

-- 4. vw_StudentAnswerDetails: Granular flat table of every student's answer to each question
CREATE OR ALTER VIEW vw_StudentAnswerDetails AS
SELECT 
    UA.Id AS AttemptId,
    U.UserName AS StudentName,
    U.Email AS StudentEmail,
    ISNULL(B.BranchName, 'Global') AS BranchName,
    R.Name AS UserRole,
    E.Title AS ExamName,
    Q.QuestionText,
    ISNULL(C.CategoryName, 'Uncategorized') AS CategoryName,
    ISNULL(SQD.IsCorrect, 0) AS IsCorrect,
    UA.AttemptDate
FROM StudentQuestionDetails SQD
INNER JOIN UserExamAttempts UA ON SQD.UserExamAttemptId = UA.Id
INNER JOIN AspNetUsers U ON UA.UserId = U.Id
INNER JOIN Exams E ON UA.ExamId = E.Id
INNER JOIN Questions Q ON SQD.QuestionId = Q.Id
LEFT JOIN Categories C ON Q.CategoryId = C.Id
LEFT JOIN Branches B ON U.BranchId = B.Id
LEFT JOIN AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN AspNetRoles R ON UR.RoleId = R.Id;
GO

-- 5. vw_ExamParticipationTrends: Time-series aggregations for line charts and trends
CREATE OR ALTER VIEW vw_ExamParticipationTrends AS
SELECT 
    CAST(UA.AttemptDate AS DATE) AS AttemptDate,
    E.Title AS ExamName,
    ISNULL(ET.TypeName, 'Standard') AS ExamType,
    ISNULL(W.WaveName, 'Global') AS WaveName,
    COUNT(UA.Id) AS TotalParticipants,
    AVG(UA.FinalScore) AS DailyAverageScore,
    AVG(UA.DurationInMinutes) AS DailyAverageDuration
FROM UserExamAttempts UA
INNER JOIN Exams E ON UA.ExamId = E.Id
LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id
LEFT JOIN TrainingWaves W ON E.WaveId = W.Id
GROUP BY CAST(UA.AttemptDate AS DATE), E.Title, ET.TypeName, W.WaveName;
GO
