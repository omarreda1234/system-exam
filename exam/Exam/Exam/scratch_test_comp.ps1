$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT Id, Name, Description FROM Companies WHERE Name LIKE '%TEST%'
SELECT ct.Id, ct.CompanyId, ct.FullName, ct.UserCode, ct.BranchName, c.Name as CompanyName 
FROM CompanyTrainees ct 
JOIN Companies c ON ct.CompanyId = c.Id
WHERE c.Name LIKE '%TEST%';

SELECT s.Id as SessionId, s.SessionName, s.CompanyId, c.Name as CompanyName
FROM AttendanceSessions s
JOIN Companies c ON s.CompanyId = c.Id
WHERE c.Name LIKE '%TEST%';
"@
$reader = $cmd.ExecuteReader()
while ($reader.HasRows) {
    $dt = New-Object System.Data.DataTable
    $dt.Load($reader)
    $dt | Format-Table -AutoSize
}
$conn.Close()
Remove-Item -Path 'c:\exam final\exam\Exam\Exam\scratch_find_506.ps1' -Force -ErrorAction SilentlyContinue
