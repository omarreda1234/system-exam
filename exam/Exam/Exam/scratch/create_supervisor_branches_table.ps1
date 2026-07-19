$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SupervisorBranches')
BEGIN
    CREATE TABLE dbo.SupervisorBranches (
        UserId NVARCHAR(450) NOT NULL,
        BranchId INT NOT NULL,
        CreatedAt DATETIME NOT NULL DEFAULT GETDATE(),
        PRIMARY KEY (UserId, BranchId),
        CONSTRAINT FK_SupervisorBranches_AspNetUsers FOREIGN KEY (UserId) REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE,
        CONSTRAINT FK_SupervisorBranches_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches(Id) ON DELETE CASCADE
    );
    PRINT 'Created SupervisorBranches table';
END
ELSE
BEGIN
    PRINT 'SupervisorBranches table already exists';
END
"@

$result = $cmd.ExecuteNonQuery()
Write-Output "Table check/creation completed successfully."
$conn.Close()
