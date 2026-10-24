$conn = New-Object System.Data.SqlClient.SqlConnection('Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;');
$conn.Open();
$cmd = $conn.CreateCommand();
$cmd.CommandText = "SELECT cst.Id, cst.SessionId, cst.CompanyTraineeId, cst.ApprovalStatus, ct.FullName, ct.BranchName, s.CompanyId FROM dbo.CompanySessionTrainees cst JOIN CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id LEFT JOIN AttendanceSessions s ON cst.SessionId = s.Id WHERE cst.ApprovalStatus = 'Pending'";
$r = $cmd.ExecuteReader();
while($r.Read()) {
    Write-Output ("cst.Id={0}, SessionId={1}, CompanyId={2}, TraineeId={3}, Status={4}, FullName={5}, Branch={6}" -f $r[0], $r[1], $r[6], $r[2], $r[3], $r[4], $r[5])
}
