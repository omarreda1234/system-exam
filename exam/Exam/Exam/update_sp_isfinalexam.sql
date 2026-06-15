ALTER PROCEDURE sp_AddnewExam 
    @Title NVARCHAR(200), 
    @Description NVARCHAR(MAX) = NULL, 
    @StartTime DATETIME = NULL, 
    @EndTime DATETIME = NULL, 
    @DurationInMinutes INT = 0, 
    @PassPercentage DECIMAL(5,2) = 0, 
    @ExamTypeId INT = 0, 
    @WaveId INT = NULL, 
    @IsActive BIT = 0, 
    @IsGraded BIT = 0, 
    @TotalQuestionsToShow INT = NULL, 
    @ShowQuestionOverview BIT = 1,
    @IsFinalExam BIT = 0
AS 
BEGIN 
    SET NOCOUNT ON; 
    INSERT INTO dbo.Exams (Title, Description, StartTime, EndTime, DurationInMinutes, PassPercentage, IsActive, ExamTypeId, WaveId, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, IsFinalExam) 
    VALUES (@Title, @Description, ISNULL(@StartTime, GETDATE()), ISNULL(@EndTime, DATEADD(hour, 1, GETDATE())), @DurationInMinutes, @PassPercentage, @IsActive, @ExamTypeId, @WaveId, @IsGraded, @TotalQuestionsToShow, @ShowQuestionOverview, @IsFinalExam); 
    
    SELECT SCOPE_IDENTITY() AS NewExamId; 
END;
GO

ALTER PROCEDURE [dbo].[sp_Admin_CloneExam]
    @OldExamId INT,
    @NewTitle NVARCHAR(200),
    @NewStartTime DATETIME,
    @NewEndTime DATETIME,
    @NewWaveId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @NewExamId INT;
    
    -- Insert new exam
    INSERT INTO Exams (Title, Description, DurationInMinutes, PassPercentage, ExamTypeId, WaveId, StartTime, EndTime, IsActive, CertificateTemplatePath, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, IsFinalExam)
    SELECT @NewTitle, Description, DurationInMinutes, PassPercentage, ExamTypeId, @NewWaveId, 
           @NewStartTime, @NewEndTime, 1, CertificateTemplatePath, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, IsFinalExam
    FROM Exams WHERE Id = @OldExamId;
    
    SET @NewExamId = SCOPE_IDENTITY();
    
    -- Clone mapping and questions
    INSERT INTO ExamQuestions (ExamId, QuestionId)
    SELECT @NewExamId, QuestionId
    FROM ExamQuestions WHERE ExamId = @OldExamId;
    
    SELECT @NewExamId;
END
GO
