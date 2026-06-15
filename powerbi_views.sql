USE [Eltarshouby-Exam];
GO

-- 1. Dimension: Users (Students / Doctors / Assistants)
CREATE OR ALTER VIEW vw_Dim_Users AS
SELECT 
    U.Id AS UserId,
    U.UserName,
    U.Email,
    U.UserCode,
    U.IsActive AS UserIsActive,
    ISNULL(B.BranchName, 'Global') AS BranchName,
    B.BranchCode AS BranchLocation,
    ISNULL(S.ShiftName, 'Unassigned') AS ShiftName,
    S.StartTime AS ShiftStartTime,
    S.EndTime AS ShiftEndTime,
    ISNULL(R.Name, 'No Role') AS UserRole
FROM AspNetUsers U
LEFT JOIN Branches B ON U.BranchId = B.Id
LEFT JOIN Shifts S ON U.ShiftId = S.Id
LEFT JOIN AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN AspNetRoles R ON UR.RoleId = R.Id;
GO

-- 2. Dimension: Exams
CREATE OR ALTER VIEW vw_Dim_Exams AS
SELECT 
    E.Id AS ExamId,
    E.Title AS ExamTitle,
    E.StartTime AS ExamStartTime,
    E.EndTime AS ExamEndTime,
    E.DurationInMinutes AS ExamDuration,
    E.PassPercentage,
    E.IsActive AS ExamIsActive,
    ISNULL(ET.TypeName, 'Standard') AS ExamType,
    ISNULL(W.WaveName, 'Global') AS WaveName
FROM Exams E
LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id
LEFT JOIN TrainingWaves W ON E.WaveId = W.Id;
GO

-- 3. Dimension: Questions
CREATE OR ALTER VIEW vw_Dim_Questions AS
SELECT 
    Q.Id AS QuestionId,
    Q.QuestionText,
    Q.Points,
    Q.Difficulty,
    Q.IsActive AS QuestionIsActive,
    ISNULL(C.CategoryName, 'Uncategorized') AS CategoryName
FROM Questions Q
LEFT JOIN Categories C ON Q.CategoryId = C.Id;
GO

-- 4. Fact: Exam Attempts
CREATE OR ALTER VIEW vw_Fact_ExamAttempts AS
SELECT 
    UA.Id AS AttemptId,
    UA.UserId,
    UA.ExamId,
    UA.AttemptNumber,
    ISNULL(UA.Score, 0) AS Score,
    ISNULL(UA.FinalScore, 0) AS FinalScore,
    ISNULL(UA.IsPassed, 0) AS IsPassed,
    UA.AttemptDate,
    UA.StartTime AS AttemptStartTime,
    UA.EndTime AS AttemptEndTime,
    ISNULL(UA.DurationInMinutes, 0) AS DurationInMinutes,
    ISNULL(UA.Status, 'Not Started') AS Status,
    ISNULL(UA.TotalQuestions, 0) AS TotalQuestions,
    ISNULL(UA.CorrectAnswers, 0) AS CorrectAnswers
FROM UserExamAttempts UA;
GO

-- 5. Fact: Student Question Details (Item Analysis)
CREATE OR ALTER VIEW vw_Fact_StudentQuestionDetails AS
SELECT 
    SQD.Id AS DetailId,
    SQD.UserExamAttemptId AS AttemptId,
    SQD.QuestionId,
    SQD.SelectedChoiceId,
    ISNULL(SQD.IsCorrect, 0) AS IsCorrect,
    UA.UserId,
    UA.ExamId
FROM StudentQuestionDetails SQD
INNER JOIN UserExamAttempts UA ON SQD.UserExamAttemptId = UA.Id;
GO
