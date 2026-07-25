
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

