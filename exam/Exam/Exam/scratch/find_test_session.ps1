$conn = New-Object System.Data.SqlClient.SqlConnection('Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;');
$conn.Open();
$cmd = $conn.CreateCommand();
$cmd.CommandText = "SELECT s.Id, s.SessionName, s.CompanyId, s.WaveId, c.Name, cst.Id as CST_Id, cst.CompanyTraineeId, cst.ApprovalStatus, ct.FullName, ct.UserCode, ct.BranchName FROM AttendanceSessions s LEFT JOIN Companies c ON s.CompanyId = c.Id LEFT JOIN CompanySessionTrainees cst ON s.Id = cst.SessionId LEFT JOIN CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id WHERE s.SessionName LIKE '%TEST%'";
$r = $cmd.ExecuteReader();
while($r.Read()) {
    Write-Output ("SessionId={0}, SessionName={1}, CompanyId={2}, WaveId={3}, CST_Id={4}, TraineeId={5}, Status={6}, FullName={7}, UserCode={8}, BranchName={9}" -f $r[0], $r[1], $r[2], $r[3], $r[4], $r[5], $r[6], $r[7], $r[8], $r[9])
}
