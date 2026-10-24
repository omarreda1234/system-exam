$conn = New-Object System.Data.SqlClient.SqlConnection('Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;');
$conn.Open();
$cmd = $conn.CreateCommand();
$cmd.CommandText = "SELECT cst.Id, cst.SessionId, cst.CompanyTraineeId, cst.UserId, cst.ApprovalStatus, ct.FullName, ct.UserCode, ct.BranchName, u.FullName as UserFullName FROM dbo.CompanySessionTrainees cst LEFT JOIN CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id LEFT JOIN AspNetUsers u ON cst.UserId = u.Id WHERE cst.SessionId = 7093";
$r = $cmd.ExecuteReader();
while($r.Read()) {
    Write-Output ("cst.Id={0}, SessionId={1}, TraineeId={2}, UserId={3}, Status={4}, ct.Name={5}, Code={6}, Branch={7}, u.Name={8}" -f $r[0], $r[1], $r[2], $r[3], $r[4], $r[5], $r[6], $r[7], $r[8])
}
