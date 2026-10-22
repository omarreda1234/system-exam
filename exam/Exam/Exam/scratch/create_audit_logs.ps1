$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$sql = @"
IF OBJECT_ID('dbo.NominationAuditLogs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NominationAuditLogs (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        EntityType NVARCHAR(50) NOT NULL, -- 'Wave' or 'CompanySession'
        EntityId INT NOT NULL,           -- WaveId or SessionId
        EntityName NVARCHAR(250) NOT NULL, -- WaveName or SessionName
        CompanyName NVARCHAR(250) NULL,
        TraineeId NVARCHAR(100) NOT NULL,
        TraineeName NVARCHAR(250) NOT NULL,
        TraineeCode NVARCHAR(100) NULL,
        TraineeRole NVARCHAR(100) NULL,
        BranchId INT NULL,
        BranchName NVARCHAR(250) NOT NULL,
        Action NVARCHAR(50) NOT NULL,     -- 'Nominated', 'Approved', 'Rejected', 'Replaced'
        ActionReason NVARCHAR(MAX) NULL,
        ReplacementTraineeId NVARCHAR(100) NULL,
        ReplacementTraineeName NVARCHAR(250) NULL,
        ActionByUserId NVARCHAR(450) NULL,
        ActionByUserName NVARCHAR(250) NULL,
        ActionByUserRole NVARCHAR(100) NULL,
        ActionDate DATETIME NOT NULL DEFAULT GETDATE()
    );

    CREATE INDEX IX_NominationAuditLogs_ActionDate ON dbo.NominationAuditLogs (ActionDate DESC);
    CREATE INDEX IX_NominationAuditLogs_EntityType ON dbo.NominationAuditLogs (EntityType);
    CREATE INDEX IX_NominationAuditLogs_Action ON dbo.NominationAuditLogs (Action);
    CREATE INDEX IX_NominationAuditLogs_BranchName ON dbo.NominationAuditLogs (BranchName);
    
    PRINT 'Created dbo.NominationAuditLogs table successfully.';
END
ELSE
BEGIN
    PRINT 'Table dbo.NominationAuditLogs already exists.';
END
"@

$cmd = $conn.CreateCommand()
$cmd.CommandText = $sql
$cmd.ExecuteNonQuery()

# Backfill from UserWaves
$backfillWave = @"
INSERT INTO dbo.NominationAuditLogs (
    EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
    BranchId, BranchName, Action, ActionReason, ReplacementTraineeId, ReplacementTraineeName,
    ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
)
SELECT 
    'Wave' AS EntityType,
    uw.WaveId AS EntityId,
    tw.WaveName AS EntityName,
    NULL AS CompanyName,
    uw.UserId AS TraineeId,
    ISNULL(u.FullName, u.UserName) AS TraineeName,
    ISNULL(u.UserCode, '--') AS TraineeCode,
    'Trainee' AS TraineeRole,
    u.BranchId AS BranchId,
    ISNULL(b.BranchName, N'بدون فرع') AS BranchName,
    ISNULL(uw.ApprovalStatus, 'Approved') AS Action,
    uw.RejectionReason AS ActionReason,
    uw.ReplacedWithUserId AS ReplacementTraineeId,
    (SELECT TOP 1 ISNULL(ru.FullName, ru.UserName) FROM AspNetUsers ru WHERE ru.Id = uw.ReplacedWithUserId) AS ReplacementTraineeName,
    uw.ActionedByUserId AS ActionByUserId,
    (SELECT TOP 1 ISNULL(au.FullName, au.UserName) FROM AspNetUsers au WHERE au.Id = uw.ActionedByUserId) AS ActionByUserName,
    'Branch Manager' AS ActionByUserRole,
    ISNULL(uw.ActionedAt, ISNULL(uw.JoinDate, GETDATE())) AS ActionDate
FROM dbo.UserWaves uw
JOIN dbo.TrainingWaves tw ON uw.WaveId = tw.Id
JOIN AspNetUsers u ON uw.UserId = u.Id
LEFT JOIN Branches b ON u.BranchId = b.Id
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.NominationAuditLogs nal 
    WHERE nal.EntityType = 'Wave' AND nal.EntityId = uw.WaveId AND nal.TraineeId = uw.UserId AND nal.Action = ISNULL(uw.ApprovalStatus, 'Approved')
)
"@

$cmd.CommandText = $backfillWave
$wRows = $cmd.ExecuteNonQuery()
Write-Output "Backfilled $wRows rows from UserWaves."

# Backfill from CompanySessionTrainees
$backfillCompany = @"
INSERT INTO dbo.NominationAuditLogs (
    EntityType, EntityId, EntityName, CompanyName, TraineeId, TraineeName, TraineeCode, TraineeRole,
    BranchId, BranchName, Action, ActionReason, ReplacementTraineeId, ReplacementTraineeName,
    ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
)
SELECT 
    'CompanySession' AS EntityType,
    cst.SessionId AS EntityId,
    s.SessionName AS EntityName,
    c.Name AS CompanyName,
    CAST(cst.CompanyTraineeId AS NVARCHAR(100)) AS TraineeId,
    ct.FullName AS TraineeName,
    ISNULL(ct.UserCode, '--') AS TraineeCode,
    ISNULL(ct.JobTitle, 'Trainee') AS TraineeRole,
    b.Id AS BranchId,
    ISNULL(ct.BranchName, N'بدون فرع') AS BranchName,
    ISNULL(cst.ApprovalStatus, 'Approved') AS Action,
    cst.RejectionReason AS ActionReason,
    CAST(cst.ReplacedWithTraineeId AS NVARCHAR(100)) AS ReplacementTraineeId,
    (SELECT TOP 1 rct.FullName FROM dbo.CompanyTrainees rct WHERE rct.Id = cst.ReplacedWithTraineeId) AS ReplacementTraineeName,
    cst.ActionedByUserId AS ActionByUserId,
    (SELECT TOP 1 ISNULL(au.FullName, au.UserName) FROM AspNetUsers au WHERE au.Id = cst.ActionedByUserId) AS ActionByUserName,
    'Branch Manager' AS ActionByUserRole,
    ISNULL(cst.ActionedAt, ISNULL(cst.AssignedAt, GETDATE())) AS ActionDate
FROM dbo.CompanySessionTrainees cst
JOIN dbo.AttendanceSessions s ON cst.SessionId = s.Id
LEFT JOIN dbo.Companies c ON s.CompanyId = c.Id
JOIN dbo.CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id
LEFT JOIN dbo.Branches b ON b.BranchName = ct.BranchName
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.NominationAuditLogs nal 
    WHERE nal.EntityType = 'CompanySession' AND nal.EntityId = cst.SessionId AND nal.TraineeId = CAST(cst.CompanyTraineeId AS NVARCHAR(100)) AND nal.Action = ISNULL(cst.ApprovalStatus, 'Approved')
)
"@

$cmd.CommandText = $backfillCompany
$cRows = $cmd.ExecuteNonQuery()
Write-Output "Backfilled $cRows rows from CompanySessionTrainees."

$conn.Close()
