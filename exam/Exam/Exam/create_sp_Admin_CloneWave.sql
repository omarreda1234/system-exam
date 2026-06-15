CREATE OR ALTER PROCEDURE sp_Admin_CloneWave
    @OldWaveId INT,
    @NewWaveName NVARCHAR(255),
    @NewStartDate DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @NewWaveId INT;
    
    IF @NewStartDate IS NULL SET @NewStartDate = GETDATE();

    -- 1. Create the new wave
    INSERT INTO dbo.TrainingWaves (WaveName, StartDate)
    VALUES (@NewWaveName, @NewStartDate);
    
    SET @NewWaveId = SCOPE_IDENTITY();
    
    -- 2. Clone each exam that belonged to the old wave
    DECLARE @OldExamId INT;
    DECLARE @NewExamId INT;
    DECLARE @ExamTitle NVARCHAR(200);
    DECLARE @Description NVARCHAR(MAX);
    DECLARE @DurationInMinutes INT;
    DECLARE @PassPercentage DECIMAL(5,2);
    DECLARE @ExamTypeId INT;
    DECLARE @StartTime DATETIME;
    DECLARE @EndTime DATETIME;
    DECLARE @IsActive BIT;
    DECLARE @CertificateTemplatePath NVARCHAR(MAX);
    DECLARE @IsGraded BIT;
    DECLARE @TotalQuestionsToShow INT;
    DECLARE @ShowQuestionOverview BIT;
    DECLARE @IsFinalExam BIT;
    
    DECLARE examCursor CURSOR FOR 
    SELECT Id, Title, Description, DurationInMinutes, PassPercentage, ExamTypeId, StartTime, EndTime, IsActive, CertificateTemplatePath, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, IsFinalExam
    FROM dbo.Exams WHERE WaveId = @OldWaveId;
    
    OPEN examCursor;
    FETCH NEXT FROM examCursor INTO @OldExamId, @ExamTitle, @Description, @DurationInMinutes, @PassPercentage, @ExamTypeId, @StartTime, @EndTime, @IsActive, @CertificateTemplatePath, @IsGraded, @TotalQuestionsToShow, @ShowQuestionOverview, @IsFinalExam;
    
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Insert new exam mapped to the new WaveId
        INSERT INTO dbo.Exams (Title, Description, DurationInMinutes, PassPercentage, ExamTypeId, WaveId, StartTime, EndTime, IsActive, CertificateTemplatePath, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, IsFinalExam)
        VALUES (@ExamTitle, @Description, @DurationInMinutes, @PassPercentage, @ExamTypeId, @NewWaveId, @StartTime, @EndTime, @IsActive, @CertificateTemplatePath, @IsGraded, @TotalQuestionsToShow, @ShowQuestionOverview, @IsFinalExam);
        
        SET @NewExamId = SCOPE_IDENTITY();
        
        -- Clone ExamGenerationRules
        INSERT INTO dbo.ExamGenerationRules (ExamId, CategoryId, TopicId, EasyCount, MediumCount, HardCount, TargetRole)
        SELECT @NewExamId, CategoryId, TopicId, EasyCount, MediumCount, HardCount, TargetRole
        FROM dbo.ExamGenerationRules
        WHERE ExamId = @OldExamId;
        
        FETCH NEXT FROM examCursor INTO @OldExamId, @ExamTitle, @Description, @DurationInMinutes, @PassPercentage, @ExamTypeId, @StartTime, @EndTime, @IsActive, @CertificateTemplatePath, @IsGraded, @TotalQuestionsToShow, @ShowQuestionOverview, @IsFinalExam;
    END
    
    CLOSE examCursor;
    DEALLOCATE examCursor;
    
    SELECT @NewWaveId AS NewWaveId;
END
GO
