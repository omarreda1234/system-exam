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
    
    DECLARE @OldWaveName NVARCHAR(255);
    SELECT @OldWaveName = WaveName FROM dbo.TrainingWaves WHERE Id = @OldWaveId;
    
    DECLARE examCursor CURSOR FOR 
    SELECT Id, Title, Description, DurationInMinutes, PassPercentage, ExamTypeId, StartTime, EndTime, IsActive, CertificateTemplatePath, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, IsFinalExam
    FROM dbo.Exams WHERE WaveId = @OldWaveId;
    
    OPEN examCursor;
    FETCH NEXT FROM examCursor INTO @OldExamId, @ExamTitle, @Description, @DurationInMinutes, @PassPercentage, @ExamTypeId, @StartTime, @EndTime, @IsActive, @CertificateTemplatePath, @IsGraded, @TotalQuestionsToShow, @ShowQuestionOverview, @IsFinalExam;
    
    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @NewTitle NVARCHAR(200);
        DECLARE @FinalPos INT = CHARINDEX('final', LOWER(@ExamTitle));

        IF @FinalPos > 0
        BEGIN
            -- Extract "Final..." and everything after it
            SET @NewTitle = @NewWaveName + ' ' + SUBSTRING(@ExamTitle, @FinalPos, LEN(@ExamTitle));
        END
        ELSE IF @IsFinalExam = 1
        BEGIN
            -- Fallback if it is marked as final exam but doesn't have "final" in the title
            SET @NewTitle = @NewWaveName + ' Final';
        END
        ELSE
        BEGIN
            -- Regular exam: strip old wave name, then prepend new wave name
            DECLARE @CleanedTitle NVARCHAR(200) = @ExamTitle;
            IF @OldWaveName IS NOT NULL AND LEN(@OldWaveName) > 0
            BEGIN
                IF LEFT(LOWER(@CleanedTitle), LEN(@OldWaveName) + 3) = LOWER(@OldWaveName) + ' - '
                BEGIN
                    SET @CleanedTitle = SUBSTRING(@CleanedTitle, LEN(@OldWaveName) + 4, LEN(@CleanedTitle));
                END
                ELSE IF LEFT(LOWER(@CleanedTitle), LEN(@OldWaveName) + 1) = LOWER(@OldWaveName) + ' '
                BEGIN
                    SET @CleanedTitle = SUBSTRING(@CleanedTitle, LEN(@OldWaveName) + 2, LEN(@CleanedTitle));
                END
            END
            SET @NewTitle = @NewWaveName + ' - ' + @CleanedTitle;
        END

        -- Insert new exam mapped to the new WaveId
        INSERT INTO dbo.Exams (Title, Description, DurationInMinutes, PassPercentage, ExamTypeId, WaveId, StartTime, EndTime, IsActive, CertificateTemplatePath, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, IsFinalExam)
        VALUES (@NewTitle, @Description, @DurationInMinutes, @PassPercentage, @ExamTypeId, @NewWaveId, @StartTime, @EndTime, @IsActive, @CertificateTemplatePath, @IsGraded, @TotalQuestionsToShow, @ShowQuestionOverview, @IsFinalExam);
        
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
