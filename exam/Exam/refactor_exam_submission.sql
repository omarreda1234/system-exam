USE [Eltarshouby-Exam]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- 1. sp_Student_RecordAnswer (تسجيل إجابة واحدة)
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_Student_RecordAnswer]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_Student_RecordAnswer]
GO

CREATE PROCEDURE [dbo].[sp_Student_RecordAnswer]
    @AttemptId INT,
    @QuestionId INT,
    @SelectedChoiceId INT
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @IsCorrect BIT = 0;
    -- تأكد من صحة الإجابة
    IF EXISTS (SELECT 1 FROM Choices WHERE Id = @SelectedChoiceId AND QuestionId = @QuestionId AND IsCorrect = 1)
        SET @IsCorrect = 1;

    -- استخدام MERGE أو IF EXISTS للتحديث أو الإدخال
    IF EXISTS (SELECT 1 FROM StudentQuestionDetails WHERE UserExamAttemptId = @AttemptId AND QuestionId = @QuestionId)
    BEGIN
        UPDATE StudentQuestionDetails 
        SET SelectedChoiceId = @SelectedChoiceId, 
            IsCorrect = @IsCorrect 
        WHERE UserExamAttemptId = @AttemptId AND QuestionId = @QuestionId;
    END
    ELSE
    BEGIN
        INSERT INTO StudentQuestionDetails (UserExamAttemptId, QuestionId, SelectedChoiceId, IsCorrect)
        VALUES (@AttemptId, @QuestionId, @SelectedChoiceId, @IsCorrect);
    END
END
GO

-- 2. sp_Student_SubmitFinal (إنهاء الامتحان وحساب النتيجة)
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[sp_Student_SubmitFinal]') AND type in (N'P', N'PC'))
    DROP PROCEDURE [dbo].[sp_Student_SubmitFinal]
GO

CREATE PROCEDURE [dbo].[sp_Student_SubmitFinal]
    @AttemptId INT,
    @Status NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    
    BEGIN TRANSACTION;
    BEGIN TRY
        DECLARE @ExamId INT, @UserId NVARCHAR(450), @RequiredPass DECIMAL(5,2);
        SELECT @ExamId = UEA.ExamId, @UserId = UEA.UserId, @RequiredPass = E.PassPercentage
        FROM UserExamAttempts UEA
        JOIN Exams E ON UEA.ExamId = E.Id
        WHERE UEA.Id = @AttemptId;

        -- 1. حساب الدرجات والأسئلة الصحيحة
        DECLARE @StudentScore DECIMAL(5,2) = 0, @CorrectCount INT = 0;
        
        -- نحسب النتيجة فقط إذا لم تكن الحالة "فشل" بسبب الغش مثلاً (إلا لو كنت تريد رصد الدرجة حتى مع الغش)
        -- هنا سنحسبها دائماً طالما تم الإنهاء
        SELECT 
            @StudentScore = ISNULL(SUM(Q.Points), 0),
            @CorrectCount = COUNT(*)
        FROM StudentQuestionDetails SQD
        JOIN Questions Q ON SQD.QuestionId = Q.Id
        WHERE SQD.UserExamAttemptId = @AttemptId AND SQD.IsCorrect = 1;

        -- 2. حساب إجمالي درجات الامتحان
        DECLARE @TotalPoints INT = 0, @TotalQuestions INT = 0;
        SELECT 
            @TotalPoints = ISNULL(SUM(Q.Points), 0),
            @TotalQuestions = COUNT(*)
        FROM ExamQuestions EQ
        JOIN Questions Q ON EQ.QuestionId = Q.Id
        WHERE EQ.ExamId = @ExamId;

        -- 3. حساب النتيجة النهائية والنجاح
        DECLARE @Passed BIT = 0;
        IF (@TotalPoints > 0 AND (@StudentScore / @TotalPoints) * 100 >= @RequiredPass)
        BEGIN
            SET @Passed = 1;
        END

        -- 4. تحديث المحاولة
        UPDATE UserExamAttempts
        SET FinalScore = @StudentScore,
            Score = CASE WHEN @TotalPoints > 0 THEN (@StudentScore / @TotalPoints) * 100 ELSE 0 END,
            [Status] = @Status,
            IsPassed = @Passed,
            CorrectAnswers = @CorrectCount,
            TotalQuestions = @TotalQuestions,
            EndTime = GETDATE() -- إنهاء الوقت الآن
        WHERE Id = @AttemptId;

        COMMIT TRANSACTION;

        SELECT @StudentScore AS FinalScore, @Passed AS Result, @Status AS FinalStatus;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

-- 3. تحديث sp_GetStudentExamsByStudentId لاستبعاد الامتحانات المنتهية بشكل صحيح
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
      -- استبعاد المنتهي أو الفاشل تماماً
      AND (UEA.[Status] IS NULL OR (UEA.[Status] NOT IN ('Completed', 'Fail_TabSwitch', 'Fail_Timeout') AND UEA.[Status] NOT LIKE 'Fail%'))
      AND E.EndTime >= GETDATE() 
    ORDER BY E.StartTime ASC; 
END;
GO
