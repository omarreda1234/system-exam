$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()

# 1. Columns of AttendanceSessions
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'AttendanceSessions'"
$r = $cmd.ExecuteReader()
Write-Host "=== AttendanceSessions columns ==="
$cols = @()
while ($r.Read()) {
    $cols += $r["COLUMN_NAME"]
}
$r.Close()
Write-Host ($cols -join ", ")

# 2. Check pending CompanySessionTrainees query exactly as executed in GetPendingBranchNominations
$cmd2 = $conn.CreateCommand()
$cmd2.CommandText = @"
SELECT 
    cst.Id AS CompanySessionTraineeId,
    cst.SessionId,
    cst.CompanyTraineeId,
    ct.FullName AS TraineeName,
    ISNULL(ct.UserCode, '--') AS UserCode,
    ISNULL(ct.JobTitle, 'Trainee') AS JobTitle,
    ISNULL(ct.BranchName, N'بدون فرع') AS BranchName,
    ISNULL(ct.Email, '--') AS Email,
    ISNULL(ct.Phone, '--') AS PhoneNumber,
    b.Id AS BranchId,
    s.SessionName,
    s.SessionDate,
    s.StartTime,
    s.EndTime,
    c.Id AS CompanyId,
    ISNULL(c.Name, 'Company') AS CompanyName,
    cst.ApprovalStatus,
    cst.AssignedAt
FROM dbo.CompanySessionTrainees cst
JOIN dbo.CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id
LEFT JOIN dbo.Branches b ON b.BranchName = ct.BranchName
JOIN dbo.AttendanceSessions s ON cst.SessionId = s.Id
LEFT JOIN dbo.Companies c ON s.CompanyId = c.Id
WHERE cst.ApprovalStatus = 'Pending'
"@

$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd2)
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null
Write-Host ("=== Pending company nominations count: " + $ds.Tables[0].Rows.Count)
foreach ($row in $ds.Tables[0].Rows) {
    Write-Host ("cstId=" + $row["CompanySessionTraineeId"] + " sessionId=" + $row["SessionId"] + " companyId=" + $row["CompanyId"] + " trainee=" + $row["TraineeName"] + " branchName=" + $row["BranchName"] + " branchId=" + $row["BranchId"])
}

$conn.Close()
