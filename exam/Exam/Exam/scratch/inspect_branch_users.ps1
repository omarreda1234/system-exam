$conn = New-Object System.Data.SqlClient.SqlConnection('Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;');
$conn.Open();

Write-Output "=== 1. Branch info ===";
$cmd1 = $conn.CreateCommand();
$cmd1.CommandText = "SELECT Id, BranchName FROM Branches WHERE BranchName LIKE '%الجيش%'";
$r1 = $cmd1.ExecuteReader();
$branchId = 0;
while($r1.Read()) {
    $branchId = $r1[0];
    Write-Output "Branch: Id=$($r1[0]), Name=$($r1[1])";
}
$r1.Close();

Write-Output "=== 2. AspNetUsers in this branch ===";
$cmd2 = $conn.CreateCommand();
$cmd2.CommandText = "SELECT u.Id, u.FullName, u.UserName, u.UserCode, u.BranchId FROM AspNetUsers u WHERE u.BranchId = $branchId";
$r2 = $cmd2.ExecuteReader();
while($r2.Read()) {
    Write-Output "User: Id=$($r2[0]), FullName=$($r2[1]), Code=$($r2[3])";
}
$r2.Close();

Write-Output "=== 3. CompanyTrainees in this branch ===";
$cmd3 = $conn.CreateCommand();
$cmd3.CommandText = "SELECT ct.Id, ct.FullName, ct.UserCode, ct.JobTitle, ct.BranchName, ct.CompanyId FROM CompanyTrainees ct WHERE ct.BranchName LIKE '%الجيش%'";
$r3 = $cmd3.ExecuteReader();
while($r3.Read()) {
    Write-Output "CompanyTrainee: Id=$($r3[0]), FullName=$($r3[1]), Code=$($r3[2]), Job=$($r3[3]), CompanyId=$($r3[5])";
}
$r3.Close();
