USE [Eltarshouby-Exam]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- تحديث sp_Student_SubmitFinal لحساب النتيجة بناءً على الأسئلة التي رآها الطالب فعلياً
ALTER PROCEDURE [dbo].[sp_Student_SubmitFinal]
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

        -- 1. حساب درجات إجابات الطالب الصحيحة
        DECLARE @StudentScore DECIMAL(5,2) = 0, @CorrectCount INT = 0;
        
        SELECT 
            @StudentScore = ISNULL(SUM(Q.Points), 0),
            @CorrectCount = COUNT(*)
        FROM StudentQuestionDetails SQD
        JOIN Questions Q ON SQD.QuestionId = Q.Id
        WHERE SQD.StudentAnswerId = @AttemptId AND SQD.IsCorrect = 1;

        -- 2. حساب إجمالي درجات "الأسئلة التي تم تعيينها لهذا الطالب في هذه المحاولة"
        -- بدلاً من حساب كل أسئلة الامتحان (التي قد تكون 100 والمخصص 10 فقط)
        DECLARE @TotalPoints INT = 0, @TotalQuestions INT = 0;
        
        -- إذا كان هناك سجلات في UserSeenQuestions لهذه المحاولة، نستخدمها هي فقط
        IF EXISTS (SELECT 1 FROM UserSeenQuestions WHERE AttemptId = @AttemptId)
        BEGIN
            SELECT 
                @TotalPoints = ISNULL(SUM(Q.Points), 0),
                @TotalQuestions = COUNT(*)
            FROM UserSeenQuestions USQ
            JOIN Questions Q ON USQ.QuestionId = Q.Id
            WHERE USQ.AttemptId = @AttemptId;
        END
        ELSE
        BEGIN
            -- Fallback: لو لم توجد سجلات (مثلاً امتحانات قديمة)، نستخدم كل أسئلة الامتحان المربوطة
            SELECT 
                @TotalPoints = ISNULL(SUM(Q.Points), 0),
                @TotalQuestions = COUNT(*)
            FROM ExamQuestions EQ
            JOIN Questions Q ON EQ.QuestionId = Q.Id
            WHERE EQ.ExamId = @ExamId;
        END

        -- 3. حساب النتيجة النهائية والنجاح
        DECLARE @Passed BIT = 0;
        IF (@TotalPoints > 0 AND (@StudentScore / @TotalPoints) * 100 >= @RequiredPass)
        BEGIN
            SET @Passed = 1;
        END

        -- 4. التحديث النهائي للمحاولة
        UPDATE UserExamAttempts
        SET FinalScore = @StudentScore,
            Score = CASE WHEN @TotalPoints > 0 THEN (@StudentScore / @TotalPoints) * 100 ELSE 0 END,
            [Status] = @Status,
            IsPassed = @Passed,
            CorrectAnswers = @CorrectCount,
            TotalQuestions = @TotalQuestions,
            EndTime = GETDATE()
        WHERE Id = @AttemptId;

        COMMIT TRANSACTION;

        SELECT @StudentScore AS FinalScore, @Passed AS Result, @Status AS FinalStatus, @TotalPoints AS OutOf;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
