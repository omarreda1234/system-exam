USE [Eltarshouby-Exam]
GO

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
        ELSE
        BEGIN
            SET @StudentScore = 0;
            SET @CorrectCount = 0;
        END

        -- 5. حساب إجمالي درجات الامتحان والأسئلة
        DECLARE @TotalPoints INT, @TotalQuestions INT;
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
