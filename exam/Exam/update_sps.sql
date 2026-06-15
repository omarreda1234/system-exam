USE [Eltarshouby-Exam]
GO

-- 1. sp_Student_SubmitFinal
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_Student_SubmitFinal]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_Student_SubmitFinal]
GO

CREATE PROCEDURE [dbo].[sp_Student_SubmitFinal]
    @AttemptId INT,
    @QuestionId INT,
    @SelectedChoiceId INT,
    @Status NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    
    BEGIN TRANSACTION;
    BEGIN TRY
        -- 1. تسجيل إجابة السؤال الأخير (فقط إذا لم تكن الحالة Fail)
        IF (@QuestionId IS NOT NULL AND @SelectedChoiceId IS NOT NULL AND @Status NOT LIKE 'Fail%')
        BEGIN
            DECLARE @IsCorrect BIT = 0;
            IF EXISTS (SELECT 1 FROM Choices WHERE Id = @SelectedChoiceId AND QuestionId = @QuestionId AND IsCorrect = 1)
                SET @IsCorrect = 1;

            IF NOT EXISTS (SELECT 1 FROM StudentQuestionDetails WHERE UserExamAttemptId = @AttemptId AND QuestionId = @QuestionId)
            BEGIN
                INSERT INTO StudentQuestionDetails (UserExamAttemptId, QuestionId, SelectedChoiceId, IsCorrect)
                VALUES (@AttemptId, @QuestionId, @SelectedChoiceId, @IsCorrect);
            END
        END

        -- 2. جلب بيانات الامتحان والمستخدم من جدول (UserExamAttempts)
        DECLARE @ExamId INT, @UserId NVARCHAR(450), @RequiredPass DECIMAL(5,2);
        SELECT @ExamId = UEA.ExamId, @UserId = UEA.UserId, @RequiredPass = E.PassPercentage
        FROM UserExamAttempts UEA
        JOIN Exams E ON UEA.ExamId = E.Id
        WHERE UEA.Id = @AttemptId;

        -- 3. حساب رقم المحاولة (AttemptNumber) تلقائياً
        DECLARE @ActualAttemptNumber INT;
        SELECT @ActualAttemptNumber = COUNT(*) FROM UserExamAttempts WHERE UserId = @UserId AND ExamId = @ExamId;

        -- 4. حساب الدرجات والأسئلة الصحيحة
        DECLARE @StudentScore DECIMAL(5,2) = 0, @CorrectCount INT = 0;

        IF (@Status NOT LIKE 'Fail%')
        BEGIN
            SELECT 
                @StudentScore = ISNULL(SUM(Q.Points), 0),
                @CorrectCount = COUNT(*)
            FROM StudentQuestionDetails SQD
            JOIN Questions Q ON SQD.QuestionId = Q.Id
            WHERE SQD.UserExamAttemptId = @AttemptId AND SQD.IsCorrect = 1;
        END

        -- 5. حساب إجمالي درجات الامتحان والأسئلة
        DECLARE @TotalPoints INT = 0, @TotalQuestions INT = 0;
        SELECT 
            @TotalPoints = ISNULL(SUM(Q.Points), 0),
            @TotalQuestions = COUNT(*)
        FROM ExamQuestions EQ
        JOIN Questions Q ON EQ.QuestionId = Q.Id
        WHERE EQ.ExamId = @ExamId;

        -- 6. النتيجة النهائية
        DECLARE @Passed BIT = 0;
        IF (@Status NOT LIKE 'Fail%' AND @TotalPoints > 0 AND (@StudentScore / @TotalPoints) * 100 >= @RequiredPass)
        BEGIN
            SET @Passed = 1;
        END

        -- 7. التحديث النهائي في الجدول (UserExamAttempts)
        UPDATE UserExamAttempts
        SET FinalScore = @StudentScore,
            Score = CASE WHEN @TotalPoints > 0 THEN (@StudentScore / @TotalPoints) * 100 ELSE 0 END,
            [Status] = @Status,
            IsPassed = @Passed,
            AttemptNumber = @ActualAttemptNumber,
            CorrectAnswers = @CorrectCount,
            TotalQuestions = @TotalQuestions,
            EndTime = GETDATE()
        WHERE Id = @AttemptId;

        COMMIT TRANSACTION;

        SELECT @StudentScore AS FinalScore, @ActualAttemptNumber AS Attempt, @Passed AS Result, @Status AS FinalStatus;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

-- 2. sp_Admin_GetExamResultsByExamId
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_Admin_GetExamResultsByExamId]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_Admin_GetExamResultsByExamId]
GO

CREATE PROCEDURE [dbo].[sp_Admin_GetExamResultsByExamId]
    @ExamId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        U.Id,
        U.UserName AS StudentName,
        U.Email AS StudentEmail,
        ISNULL(UEA.[Status], 'Not Started') AS [Status],
        ISNULL(UEA.Score, 0) AS Score,
        ISNULL(UEA.IsPassed, 0) AS IsPassed,
        UEA.EndTime AS CompletionDate
    FROM dbo.ExamAssignments EA
    INNER JOIN dbo.AspNetUsers U ON EA.StudentId = U.Id
    LEFT JOIN dbo.UserExamAttempts UEA ON EA.ExamId = UEA.ExamId AND EA.StudentId = UEA.UserId
    WHERE EA.ExamId = @ExamId
    ORDER BY Score DESC, StudentName ASC;
END
GO

-- 3. sp_GetStudentExamReviews
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_GetStudentExamReviews]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_GetStudentExamReviews]
GO

CREATE PROCEDURE [dbo].[sp_GetStudentExamReviews]
    @ExamId INT,
    @StudentId NVARCHAR(450)
AS
BEGIN
    DECLARE @AttemptId INT;
    SELECT TOP 1 @AttemptId = Id 
    FROM UserExamAttempts 
    WHERE ExamId = @ExamId AND UserId = @StudentId
	ORDER BY EndTime DESC;

    SELECT 
        q.Id AS QuestionId,
        q.QuestionText,
        cat.CategoryName AS QuestionType,
        q.Points,
        c.Id AS ChoiceId,
        c.ChoiceText,
        c.IsCorrect AS IsRightAnswer,
        CASE 
            WHEN sqd.SelectedChoiceId = c.Id THEN 1 
            ELSE 0 
        END AS IsStudentSelection
    FROM ExamQuestions eq
	INNER JOIN Questions q ON eq.QuestionId = q.Id
    LEFT JOIN Categories cat ON q.CategoryId = cat.Id
    INNER JOIN Choices c ON q.Id = c.QuestionId
    LEFT JOIN StudentQuestionDetails sqd ON q.Id = sqd.QuestionId AND sqd.UserExamAttemptId = @AttemptId
    WHERE eq.ExamId = @ExamId
    ORDER BY q.Id, c.Id;
END
GO

-- 4. sp_Admin_GetAllExamResults
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_Admin_GetAllExamResults]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_Admin_GetAllExamResults]
GO

CREATE PROCEDURE [dbo].[sp_Admin_GetAllExamResults]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        E.Title AS ExamName,
        U.Id,
        U.UserName AS StudentName,
        U.Email AS StudentEmail,
        ISNULL(UEA.[Status], 'Not Started') AS [Status],
        ISNULL(UEA.Score, 0) AS Score,
        ISNULL(UEA.IsPassed, 0) AS IsPassed,
        UEA.EndTime AS CompletionDate
    FROM dbo.ExamAssignments EA
    INNER JOIN dbo.AspNetUsers U ON EA.StudentId = U.Id
    INNER JOIN dbo.Exams E ON EA.ExamId = E.Id
    LEFT JOIN dbo.UserExamAttempts UEA ON EA.ExamId = UEA.ExamId AND EA.StudentId = UEA.UserId
    ORDER BY E.Title ASC, Score DESC;
END
GO

-- 5. sp_CalculateFinalResult
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_CalculateFinalResult]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_CalculateFinalResult]
GO

CREATE PROCEDURE [dbo].[sp_CalculateFinalResult]
    @AttemptId INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @StudentScore DECIMAL(5,2);
    DECLARE @PassPercentage DECIMAL(5,2);
    DECLARE @TotalQuestions INT;
    DECLARE @CorrectCount INT;
	DECLARE @TotalPoints INT = 0;
	DECLARE @ExamId INT;

    SELECT @ExamId = ExamId FROM UserExamAttempts WHERE Id = @AttemptId;

    SELECT 
        @StudentScore = ISNULL(SUM(CASE WHEN SQD.IsCorrect = 1 THEN Q.Points ELSE 0 END), 0),
        @CorrectCount = ISNULL(SUM(CASE WHEN SQD.IsCorrect = 1 THEN 1 ELSE 0 END), 0),
        @TotalQuestions = COUNT(SQD.Id)
    FROM StudentQuestionDetails SQD
    JOIN Questions Q ON SQD.QuestionId = Q.Id
    WHERE SQD.UserExamAttemptId = @AttemptId;

	SELECT @TotalPoints = ISNULL(SUM(Q.Points), 0)
    FROM ExamQuestions EQ
    JOIN Questions Q ON EQ.QuestionId = Q.Id
    WHERE EQ.ExamId = @ExamId;

    SELECT @PassPercentage = PassPercentage
    FROM Exams
    WHERE Id = @ExamId;

    UPDATE UserExamAttempts
    SET FinalScore = @StudentScore,
		Score = CASE WHEN @TotalPoints > 0 THEN (@StudentScore / @TotalPoints) * 100 ELSE 0 END,
        CorrectAnswers = @CorrectCount,
        TotalQuestions = @TotalQuestions,
        EndTime = GETDATE(),
        [Status] = 'Completed',
        IsPassed = CASE WHEN @TotalPoints > 0 AND (@StudentScore / @TotalPoints) * 100 >= @PassPercentage THEN 1 ELSE 0 END
    WHERE Id = @AttemptId;

    SELECT 
        FinalScore, 
		Score,
        CorrectAnswers, 
        TotalQuestions, 
        IsPassed, 
        [Status] 
    FROM UserExamAttempts 
    WHERE Id = @AttemptId;
END
GO

-- 6. sp_GetAllExams
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_GetAllExams]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_GetAllExams]
GO

CREATE PROCEDURE [dbo].[sp_GetAllExams]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        E.[Id], 
        E.[Title], 
        E.[Description], 
        E.[StartTime], 
        E.[EndTime], 
        E.[DurationInMinutes], 
        E.[PassPercentage], 
        E.[IsActive],
        E.[ExamTypeId],
        ET.[TypeName] AS [ExamTypeName],
        (SELECT COUNT(*) FROM [ExamQuestions] EQ WHERE EQ.[ExamId] = E.[Id]) AS [TotalQuestions],
        (SELECT COUNT(*) FROM [UserExamAttempts] UEA WHERE UEA.[ExamId] = E.[Id]) AS [TotalAttempts]
    FROM [Exams] E
    LEFT JOIN [ExamTypes] ET ON E.[ExamTypeId] = ET.[Id]
    ORDER BY E.[StartTime] DESC;
END;
GO

-- 7. sp_GetStudentExamsByStudentId
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_GetStudentExamsByStudentId]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_GetStudentExamsByStudentId]
GO

CREATE PROCEDURE [dbo].[sp_GetStudentExamsByStudentId]
    @StudentId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        E.Id AS ExamId,
        E.Title AS ExamTitle,
        E.Description AS ExamDescription,
        E.StartTime AS ExamDate,
        E.EndTime,
        E.DurationInMinutes,
        E.PassPercentage,
        ISNULL(UEA.[Status], 'Not Started') AS StudentStatus
    FROM dbo.Exams E
    INNER JOIN dbo.ExamAssignments EA ON E.Id = EA.ExamId
    LEFT JOIN dbo.UserExamAttempts UEA ON E.Id = UEA.ExamId AND UEA.UserId = @StudentId
    WHERE EA.StudentId = @StudentId
      AND E.IsActive = 1
      AND (UEA.[Status] IS NULL OR (UEA.[Status] NOT IN ('Completed', 'Fail_TabSwitch', 'Fail_Timeout') AND UEA.[Status] NOT LIKE 'Fail%'))
      AND E.EndTime >= GETDATE() 
    ORDER BY E.StartTime ASC; 
END;
GO
