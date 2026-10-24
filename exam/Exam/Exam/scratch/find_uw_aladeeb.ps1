$conn = New-Object System.Data.SqlClient.SqlConnection('Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;');
$conn.Open();
$cmd = $conn.CreateCommand();
$cmd.CommandText = "SELECT uw.Id, uw.WaveId, uw.UserId, uw.ApprovalStatus, u.FullName, b.BranchName, u.BranchId FROM dbo.UserWaves uw JOIN AspNetUsers u ON uw.UserId = u.Id LEFT JOIN Branches b ON u.BranchId = b.Id WHERE u.FullName LIKE '%aladeeb%'";
$r = $cmd.ExecuteReader();
while($r.Read()) {
    Write-Output ("uw.Id={0}, WaveId={1}, UserId={2}, Status={3}, FullName={4}, BranchName={5}, BranchId={6}" -f $r[0], $r[1], $r[2], $r[3], $r[4], $r[5], $r[6])
}
