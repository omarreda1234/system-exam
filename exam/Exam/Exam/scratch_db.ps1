$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True"
$connection = New-Object System.Data.SqlClient.SqlConnection($connString)
$connection.Open()

Write-Host "Creating tables..."

$ddl = @"
IF OBJECT_ID('dbo.StudentAssignmentAnswers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StudentAssignmentAnswers (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        AttemptId INT NOT NULL,
        QuestionId INT NOT NULL,
        SelectedItemNos NVARCHAR(MAX) NULL,
        IsCorrect BIT NOT NULL DEFAULT 0,
        EarnedPoints DECIMAL(5, 2) NOT NULL DEFAULT 0.00
    )
END

IF OBJECT_ID('dbo.StudentAssignmentAttempts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StudentAssignmentAttempts (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        AssignmentId INT NOT NULL,
        UserId NVARCHAR(450) NOT NULL,
        StartTime DATETIME NOT NULL DEFAULT GETDATE(),
        EndTime DATETIME NULL,
        Score DECIMAL(5, 2) NOT NULL DEFAULT 0.00,
        Status NVARCHAR(50) NOT NULL DEFAULT 'InProgress'
    )
END

IF OBJECT_ID('dbo.AssignmentQuestions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AssignmentQuestions (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        AssignmentId INT NOT NULL,
        QuestionType NVARCHAR(50) NOT NULL,
        TargetRole NVARCHAR(50) NOT NULL,
        Points DECIMAL(5, 2) NOT NULL DEFAULT 0.00,
        CategoryName NVARCHAR(200) NULL,
        GroupName NVARCHAR(200) NULL,
        SubcategoryName NVARCHAR(200) NULL,
        RequiredItemsCount INT NULL,
        ItemDefinition NVARCHAR(MAX) NULL,
        CorrectItemNo NVARCHAR(100) NULL
    )
END

IF OBJECT_ID('dbo.Assignments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Assignments (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Title NVARCHAR(200) NOT NULL,
        WaveId INT NOT NULL,
        PharmacistMaxScore DECIMAL(5, 2) NOT NULL DEFAULT 0.00,
        AssistantMaxScore DECIMAL(5, 2) NOT NULL DEFAULT 0.00,
        ScheduledStartTime DATETIME NOT NULL,
        ScheduledEndTime DATETIME NOT NULL,
        CreatedAt DATETIME NOT NULL DEFAULT GETDATE()
    )
END
"@

$cmd = $connection.CreateCommand()
$cmd.CommandText = $ddl
$cmd.ExecuteNonQuery()
Write-Host "Tables created successfully."

# Adding constraints if not already added
Write-Host "Adding Foreign Keys..."
$fkSql = @"
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_Assignments_TrainingWaves')
    ALTER TABLE dbo.Assignments ADD CONSTRAINT FK_Assignments_TrainingWaves FOREIGN KEY (WaveId) REFERENCES dbo.TrainingWaves(Id) ON DELETE CASCADE;

IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_AssignmentQuestions_Assignments')
    ALTER TABLE dbo.AssignmentQuestions ADD CONSTRAINT FK_AssignmentQuestions_Assignments FOREIGN KEY (AssignmentId) REFERENCES dbo.Assignments(Id) ON DELETE CASCADE;

IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_StudentAssignmentAttempts_Assignments')
    ALTER TABLE dbo.StudentAssignmentAttempts ADD CONSTRAINT FK_StudentAssignmentAttempts_Assignments FOREIGN KEY (AssignmentId) REFERENCES dbo.Assignments(Id) ON DELETE CASCADE;

IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_StudentAssignmentAttempts_AspNetUsers')
    ALTER TABLE dbo.StudentAssignmentAttempts ADD CONSTRAINT FK_StudentAssignmentAttempts_AspNetUsers FOREIGN KEY (UserId) REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE;

IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_StudentAssignmentAnswers_Attempts')
    ALTER TABLE dbo.StudentAssignmentAnswers ADD CONSTRAINT FK_StudentAssignmentAnswers_Attempts FOREIGN KEY (AttemptId) REFERENCES dbo.StudentAssignmentAttempts(Id) ON DELETE CASCADE;

IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_StudentAssignmentAnswers_Questions')
    ALTER TABLE dbo.StudentAssignmentAnswers ADD CONSTRAINT FK_StudentAssignmentAnswers_Questions FOREIGN KEY (QuestionId) REFERENCES dbo.AssignmentQuestions(Id) ON DELETE NO ACTION;
"@

$cmd.CommandText = $fkSql
$cmd.ExecuteNonQuery()
Write-Host "Foreign keys configured."

# Creating Stored Procedures
Write-Host "Creating Stored Procedures..."

$spSearch = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetItemsForSearch
    @SearchQuery NVARCHAR(250)
AS
BEGIN
    SET NOCOUNT ON;
    
    SELECT TOP 20 
        No_ AS ItemCode,
        Description AS Description,
        [Description 2] AS DescriptionAr,
        [Storage Instructions] AS Category,
        [Incentive value] AS [Group],
        Color AS Subcategory,
        [Item Definition] AS ItemDefinition
    FROM dbo.Items WITH (NOLOCK)
    WHERE No_ LIKE '%' + @SearchQuery + '%'
       OR Description LIKE '%' + @SearchQuery + '%'
       OR [Description 2] LIKE '%' + @SearchQuery + '%';
END;
"@

$cmd.CommandText = $spSearch
$cmd.ExecuteNonQuery()
Write-Host "sp_GetItemsForSearch created."

$spGrade = @"
CREATE OR ALTER PROCEDURE dbo.sp_GradeAssignmentAttempt
    @AttemptId INT
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @AnswerId INT;
    DECLARE @QuestionId INT;
    DECLARE @QuestionType NVARCHAR(50);
    DECLARE @Points DECIMAL(5,2);
    DECLARE @SelectedItemNos NVARCHAR(MAX);
    
    DECLARE @CategoryName NVARCHAR(200);
    DECLARE @GroupName NVARCHAR(200);
    DECLARE @SubcategoryName NVARCHAR(200);
    DECLARE @RequiredItemsCount INT;
    
    DECLARE @CorrectItemNo NVARCHAR(100);
    
    DECLARE @IsCorrect BIT;
    DECLARE @EarnedPoints DECIMAL(5,2);
    
    DECLARE AnswerCursor CURSOR FOR 
    SELECT 
        a.Id, 
        a.QuestionId, 
        q.QuestionType, 
        q.Points, 
        a.SelectedItemNos,
        q.CategoryName,
        q.GroupName,
        q.SubcategoryName,
        q.RequiredItemsCount,
        q.CorrectItemNo
    FROM dbo.StudentAssignmentAnswers a
    INNER JOIN dbo.AssignmentQuestions q ON a.QuestionId = q.Id
    WHERE a.AttemptId = @AttemptId;
    
    OPEN AnswerCursor;
    FETCH NEXT FROM AnswerCursor INTO 
        @AnswerId, @QuestionId, @QuestionType, @Points, @SelectedItemNos,
        @CategoryName, @GroupName, @SubcategoryName, @RequiredItemsCount, @CorrectItemNo;
        
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @IsCorrect = 0;
        SET @EarnedPoints = 0.00;
        
        IF @QuestionType = 'CategorySelect'
        BEGIN
            DECLARE @MatchedCount INT = 0;
            DECLARE @TotalSelected INT = 0;
            
            SELECT @TotalSelected = COUNT(1) FROM STRING_SPLIT(@SelectedItemNos, ',');
            
            SELECT @MatchedCount = COUNT(1)
            FROM STRING_SPLIT(@SelectedItemNos, ',') s
            INNER JOIN dbo.Items i ON LTRIM(RTRIM(s.value)) = i.No_
            WHERE i.[Storage Instructions] = @CategoryName
              AND i.[Incentive value] = @GroupName
              AND i.Color = @SubcategoryName;
              
            IF @RequiredItemsCount > 0
            BEGIN
                IF @MatchedCount >= @RequiredItemsCount
                BEGIN
                    SET @IsCorrect = 1;
                    SET @EarnedPoints = @Points;
                END
                ELSE
                BEGIN
                    SET @EarnedPoints = CAST(@MatchedCount AS DECIMAL(5,2)) / CAST(@RequiredItemsCount AS DECIMAL(5,2)) * @Points;
                    IF @MatchedCount > 0 SET @IsCorrect = 1; -- Partial success is marked as correct (positive progress)
                END
            END
        END
        ELSE IF @QuestionType = 'ItemDefinitionMatch'
        BEGIN
            IF LTRIM(RTRIM(@SelectedItemNos)) = LTRIM(RTRIM(@CorrectItemNo))
            BEGIN
                SET @IsCorrect = 1;
                SET @EarnedPoints = @Points;
            END
        END
        
        UPDATE dbo.StudentAssignmentAnswers
        SET IsCorrect = @IsCorrect,
            EarnedPoints = @EarnedPoints
        WHERE Id = @AnswerId;
        
        FETCH NEXT FROM AnswerCursor INTO 
            @AnswerId, @QuestionId, @QuestionType, @Points, @SelectedItemNos,
            @CategoryName, @GroupName, @SubcategoryName, @RequiredItemsCount, @CorrectItemNo;
    END;
    
    CLOSE AnswerCursor;
    DEALLOCATE AnswerCursor;
    
    DECLARE @TotalScore DECIMAL(5,2);
    SELECT @TotalScore = SUM(EarnedPoints) FROM dbo.StudentAssignmentAnswers WHERE AttemptId = @AttemptId;
    
    UPDATE dbo.StudentAssignmentAttempts
    SET Score = ISNULL(@TotalScore, 0.00),
        EndTime = GETDATE(),
        Status = 'Completed'
    WHERE Id = @AttemptId;
END;
"@

$cmd.CommandText = $spGrade
$cmd.ExecuteNonQuery()
Write-Host "sp_GradeAssignmentAttempt created."

$connection.Close()
Write-Host "All Database configurations executed successfully!"
