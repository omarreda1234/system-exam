$conn = New-Object System.Data.SqlClient.SqlConnection('Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;');
$conn.Open();

$cmd1 = $conn.CreateCommand();
$cmd1.CommandText = "SELECT Id, BranchName FROM Branches WHERE Id = 55";
$r1 = $cmd1.ExecuteReader();
while($r1.Read()) {
    Write-Output ("Branch: Id=" + $r1[0] + ", Name=" + $r1[1]);
}
$r1.Close();

$cmd2 = $conn.CreateCommand();
$cmd2.CommandText = "SELECT u.Id, u.FullName, u.UserName, u.UserCode, u.BranchId FROM AspNetUsers u WHERE u.BranchId = 55";
$r2 = $cmd2.ExecuteReader();
while($r2.Read()) {
    Write-Output ("User: Id=" + $r2[0] + ", FullName=" + $r2[1] + ", Code=" + $r2[3]);
}
$r2.Close();

$cmd3 = $conn.CreateCommand();
$cmd3.CommandText = "SELECT ct.Id, ct.FullName, ct.UserCode, ct.JobTitle, ct.BranchName, ct.CompanyId FROM CompanyTrainees ct WHERE ct.BranchName = (SELECT BranchName FROM Branches WHERE Id = 55)";
$r3 = $cmd3.ExecuteReader();
while($r3.Read()) {
    Write-Output ("CompanyTrainee: Id=" + $r3[0] + ", FullName=" + $r3[1] + ", Code=" + $r3[2] + ", CompanyId=" + $r3[5]);
}
$r3.Close();
