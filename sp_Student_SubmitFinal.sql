
CREATE PROCEDURE [dbo].[sp_Student_SubmitFinal]
    @AttemptId INT,
    @Status NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        -- 1. Calculate Actual Student Score (Sum of points of correct answers in this attempt)
        DECLARE @StudentScore DECIMAL(18,2) = 0;
        DECLARE @CorrectCount INT = 0;

        SELECT 
            @CorrectCount = COUNT(*),
            @StudentScore = ISNULL(SUM(Q.Points), 0)
        FROM StudentQuestionDetails SQD
        JOIN Questions Q ON SQD.QuestionId = Q.Id
        WHERE SQD.UserExamAttemptId = @AttemptId AND SQD.IsCorrect = 1;

        -- 2. Calculate Total Possible Points (Sum of points of all questions assigned to this attempt)
        DECLARE @TotalPoints DECIMAL(18,2) = 0;
        DECLARE @TotalQuestions INT = 0;

        SELECT 
            @TotalQuestions = COUNT(*),
            @TotalPoints = ISNULL(SUM(Q.Points), 0)
        FROM UserSeenQuestions USQ
        JOIN Questions Q ON USQ.QuestionId = Q.Id
        WHERE USQ.AttemptId = @AttemptId;

        -- Fallback to Exam config if no seen questions recorded (safety)
        IF @TotalPoints = 0
        BEGIN
            SELECT @TotalQuestions = TotalQuestionsToShow, @TotalPoints = TotalQuestionsToShow * 1.0 -- assume 1pt if missing
            FROM Exams E JOIN UserExamAttempts UA ON E.Id = UA.ExamId WHERE UA.Id = @AttemptId;
        END

        -- 3. Calculate Percentage & Passed logic
        DECLARE @IsPassed BIT = 0; 
        DECLARE @Percentage DECIMAL(18,2) = 0;
        DECLARE @RequiredPass DECIMAL(5,2), @IsGraded BIT;

        SELECT @RequiredPass = E.PassPercentage, @IsGraded = E.IsGraded
        FROM UserExamAttempts UEA
        JOIN Exams E ON UEA.ExamId = E.Id
        WHERE UEA.Id = @AttemptId;

        IF @TotalPoints > 0 
        BEGIN
            SET @Percentage = (@StudentScore / @TotalPoints) * 100;
            IF @IsGraded = 1 AND @Percentage >= @RequiredPass SET @IsPassed = 1;
        END

        -- 4. Update Attempt
        UPDATE UserExamAttempts
        SET FinalScore = @StudentScore,
            Score = @Percentage,
            [Status] = @Status,
            IsPassed = @IsPassed,
            CorrectAnswers = @CorrectCount,
            TotalQuestions = @TotalQuestions,
            EndTime = GETDATE()   
        WHERE Id = @AttemptId;

        COMMIT TRANSACTION;
        SELECT @StudentScore AS FinalScore, @Percentage AS Percentage, @Status AS NewStatus;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
