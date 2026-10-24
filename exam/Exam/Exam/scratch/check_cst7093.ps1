$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT cst.Id, cst.SessionId, cst.CompanyTraineeId, cst.UserId, cst.ApprovalStatus FROM CompanySessionTrainees cst WHERE cst.SessionId = 7093"
$r = $cmd.ExecuteReader()
Write-Host "=== CST for Session 7093 ==="
while ($r.Read()) {
    Write-Host ("cstId=" + $r["Id"] + " TraineeId=" + $r["CompanyTraineeId"] + " UserId=" + $r["UserId"] + " Status=" + $r["ApprovalStatus"])
}
$r.Close()

$conn.Close()
