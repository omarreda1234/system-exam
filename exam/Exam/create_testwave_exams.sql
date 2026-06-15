USE [Eltarshouby-Exam];
GO

-- 1. Ensure Wave "Testwave" exists
DECLARE @WaveId INT;
SELECT @WaveId = Id FROM TrainingWaves WHERE WaveName = 'Testwave';

IF @WaveId IS NULL
BEGIN
    INSERT INTO TrainingWaves (WaveName, StartDate)
    VALUES ('Testwave', GETDATE());
    SET @WaveId = SCOPE_IDENTITY();
END

-- Clean up any existing exams for Testwave first to avoid duplicates
DELETE FROM ExamQuestions WHERE ExamId IN (SELECT Id FROM Exams WHERE WaveId = @WaveId);
DELETE FROM ExamAssignments WHERE ExamId IN (SELECT Id FROM Exams WHERE WaveId = @WaveId);
DELETE FROM UserExamAttempts WHERE ExamId IN (SELECT Id FROM Exams WHERE WaveId = @WaveId);
DELETE FROM Exams WHERE WaveId = @WaveId;

-- Helper table to iterate over the quizzes to create
DECLARE @Quizzes TABLE (
    Title NVARCHAR(255),
    CategoryId INT,
    TopicId INT
);

INSERT INTO @Quizzes (Title, CategoryId, TopicId) VALUES
('Skincare 1', 2013, 1022),
('Skincare 2', 2013, 1023),
('Haircare', 2013, 1024),
('Bodycare', 2013, 1025),
('ENT', 2017, 1026),
('GIT', 2017, 1027),
('Pediatrics', 2017, 1028),
('Women Health', 2017, 1029),
('Paramedical', 2017, 1030),
('Dermatology', 2017, 1031),
('Soft Skills', 2019, 1034);

-- Cursor to loop and create quizzes
DECLARE @Title NVARCHAR(255), @CategoryId INT, @TopicId INT;
DECLARE quiz_cursor CURSOR FOR SELECT Title, CategoryId, TopicId FROM @Quizzes;

OPEN quiz_cursor;
FETCH NEXT FROM quiz_cursor INTO @Title, @CategoryId, @TopicId;

WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @NewExamId INT;
    
    INSERT INTO Exams (Title, Description, StartTime, EndTime, DurationInMinutes, PassPercentage, IsActive, ExamTypeId, WaveId, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, AllowBackNavigation, TotalPoints, IsFinalExam)
    VALUES (
        @Title, 
        @Title + ' Quiz', 
        DATEADD(day, -1, GETDATE()), 
        DATEADD(day, 30, GETDATE()), 
        60, 
        70.0, 
        1, 
        2, -- Wave ExamType
        @WaveId, 
        1, 
        0, 
        1, 
        1, 
        0, 
        0
    );
    
    SET @NewExamId = SCOPE_IDENTITY();
    
    -- Copy questions from Exam 2125 (Wave 11 Final)
    IF @CategoryId = 2019 -- Soft Skills
    BEGIN
        INSERT INTO ExamQuestions (ExamId, QuestionId)
        SELECT @NewExamId, EQ.QuestionId
        FROM ExamQuestions EQ
        JOIN Questions Q ON EQ.QuestionId = Q.Id
        WHERE EQ.ExamId = 2125 AND Q.CategoryId = @CategoryId;
    END
    ELSE
    BEGIN
        INSERT INTO ExamQuestions (ExamId, QuestionId)
        SELECT @NewExamId, EQ.QuestionId
        FROM ExamQuestions EQ
        JOIN Questions Q ON EQ.QuestionId = Q.Id
        WHERE EQ.ExamId = 2125 AND Q.TopicId = @TopicId;
    END
    
    -- Update TotalPoints for the quiz
    UPDATE Exams 
    SET TotalPoints = ISNULL((SELECT SUM(Q.Points) FROM ExamQuestions EQ JOIN Questions Q ON EQ.QuestionId = Q.Id WHERE EQ.ExamId = @NewExamId), 0)
    WHERE Id = @NewExamId;
    
    FETCH NEXT FROM quiz_cursor INTO @Title, @CategoryId, @TopicId;
END

CLOSE quiz_cursor;
DEALLOCATE quiz_cursor;

-- Create Final Exam for Testwave
DECLARE @FinalExamId INT;
INSERT INTO Exams (Title, Description, StartTime, EndTime, DurationInMinutes, PassPercentage, IsActive, ExamTypeId, WaveId, IsGraded, TotalQuestionsToShow, ShowQuestionOverview, AllowBackNavigation, TotalPoints, IsFinalExam)
VALUES (
    'Testwave Final Exam', 
    'Testwave Final Exam (All Topics)', 
    DATEADD(day, -1, GETDATE()), 
    DATEADD(day, 30, GETDATE()), 
    120, 
    70.0, 
    1, 
    8, -- Wave Final ExamType
    @WaveId, 
    1, 
    0, 
    1, 
    1, 
    0, 
    1 -- IsFinalExam = 1
);
SET @FinalExamId = SCOPE_IDENTITY();

-- Copy all questions from Exam 2125 (Wave 11 Final)
INSERT INTO ExamQuestions (ExamId, QuestionId)
SELECT @FinalExamId, QuestionId
FROM ExamQuestions
WHERE ExamId = 2125;

-- Update TotalPoints for Final Exam
UPDATE Exams 
SET TotalPoints = ISNULL((SELECT SUM(Q.Points) FROM ExamQuestions EQ JOIN Questions Q ON EQ.QuestionId = Q.Id WHERE EQ.ExamId = @FinalExamId), 0)
WHERE Id = @FinalExamId;

PRINT 'Testwave and its 11 quizzes and Final Exam created successfully!';
GO
