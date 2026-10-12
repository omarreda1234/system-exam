$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

Write-Output "=== ROLES ==="
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, Name FROM AspNetRoles"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "$($r['Id']) : $($r['Name'])"
}
$r.Close()

Write-Output "`n=== EXAM TYPES ==="
$cmd.CommandText = "SELECT Id, TypeName FROM ExamTypes"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "$($r['Id']) : $($r['TypeName'])"
}
$r.Close()

Write-Output "`n=== RECENT EXAMS (top 5) ==="
$cmd.CommandText = "SELECT TOP 5 Id, Title, StartTime, EndTime, WaveId, ExamTypeId, IsActive FROM Exams ORDER BY Id DESC"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "Id: $($r['Id']) | Title: $($r['Title']) | TypeId: $($r['ExamTypeId']) | WaveId: $($r['WaveId']) | IsActive: $($r['IsActive']) | Start: $($r['StartTime']) | End: $($r['EndTime'])"
}
$r.Close()

Write-Output "`n=== EXAM ASSIGNMENTS COLUMNS ==="
$cmd.CommandText = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ExamAssignments'"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "$($r['COLUMN_NAME']) | $($r['DATA_TYPE']) | Nullable: $($r['IS_NULLABLE'])"
}
$r.Close()

Write-Output "`n=== USER COUNTS BY ROLE ==="
$cmd.CommandText = "SELECT r.Name, COUNT(ur.UserId) as UserCount FROM AspNetRoles r LEFT JOIN AspNetUserRoles ur ON r.Id = ur.RoleId GROUP BY r.Name"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "$($r['Name']) : $($r['UserCount'])"
}
$r.Close()

$conn.Close()
