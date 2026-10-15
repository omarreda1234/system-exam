$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$sql = @"
-- 1. Create Departments Table if not exists
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Departments')
BEGIN
    CREATE TABLE dbo.Departments (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(150) NOT NULL,
        Code NVARCHAR(50) NULL,
        Description NVARCHAR(300) NULL,
        IsActive BIT DEFAULT 1,
        CreatedAt DATETIME DEFAULT GETDATE(),
        CreatedBy NVARCHAR(100) NULL
    );

    -- Insert core default departments
    INSERT INTO dbo.Departments (Name, Code) VALUES 
    (N'فارما (أدوية)', 'PHARMA'),
    (N'تجميل وعناية بالبشرة (Cosmetics)', 'COSMO'),
    (N'مكملات ومستلزمات طبية', 'SUPP'),
    (N'أجهزة طبية', 'DEVICES'),
    (N'إدارة وعمليات', 'MGMT');

    PRINT 'Departments table created and seeded successfully.';
END
ELSE
BEGIN
    PRINT 'Departments table already exists.';
END

-- 2. Add DepartmentId to AspNetUsers if not exists
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AspNetUsers') AND name = 'DepartmentId')
BEGIN
    ALTER TABLE dbo.AspNetUsers ADD DepartmentId INT NULL;
    ALTER TABLE dbo.AspNetUsers ADD CONSTRAINT FK_AspNetUsers_Departments FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments(Id);
    CREATE INDEX IX_AspNetUsers_DepartmentId ON dbo.AspNetUsers(DepartmentId);
    PRINT 'DepartmentId column and FK added to AspNetUsers.';
END
ELSE
BEGIN
    PRINT 'DepartmentId column already exists in AspNetUsers.';
END
"@

$cmd = $conn.CreateCommand()
$cmd.CommandText = $sql
$cmd.ExecuteNonQuery()

# Verify
$checkCmd = $conn.CreateCommand()
$checkCmd.CommandText = "SELECT Id, Name, Code FROM dbo.Departments"
$r = $checkCmd.ExecuteReader()
Write-Output "--- DEPARTMENTS IN DB ---"
while($r.Read()){
    Write-Output ($r[0].ToString() + " | " + $r[1] + " | " + $r[2])
}
$r.Close()

$conn.Close()
