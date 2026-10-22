$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT S.Id, S.SessionName, S.SessionDate, S.CompanyId,
       (SELECT COUNT(*) FROM dbo.CompanySessionTrainees CST WHERE CST.SessionId = S.Id) as SpecificAssignedCount,
       (SELECT COUNT(*) FROM dbo.UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1) as PresentCount
FROM AttendanceSessions S
WHERE S.SessionName LIKE '%test%'
"@
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "Session: $($r['Id']) | Name: $($r['SessionName']) | Date: $($r['SessionDate']) | Assigned: $($r['SpecificAssignedCount']) | Present: $($r['PresentCount'])"
}
$r.Close()

$cmd.CommandText = @"
SELECT CST.SessionId, CST.CompanyTraineeId, CT.FullName, CT.BranchName, CST.AssignedAt
FROM dbo.CompanySessionTrainees CST
JOIN CompanyTrainees CT ON CST.CompanyTraineeId = CT.Id
JOIN AttendanceSessions S ON CST.SessionId = S.Id
WHERE S.SessionName LIKE '%test%'
"@
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "CST: Session $($r['SessionId']) | Trainee $($r['CompanyTraineeId']) | Name: $($r['FullName']) | Branch: $($r['BranchName'])"
}
$r.Close()

$cmd.CommandText = @"
SELECT UA.* FROM UserAttendance UA
JOIN AttendanceSessions S ON UA.SessionId = S.Id
WHERE S.SessionName LIKE '%test%'
"@
$r = $cmd.ExecuteReader()
if (-not $r.HasRows) {
    Write-Output "NO UserAttendance records found for test sessions!"
}
while($r.Read()) {
    Write-Output "UA for test: Id $($r['Id']) | IsPresent: $($r['IsPresent'])"
}
$r.Close()

$conn.Close()
