$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()

# Check branch 55 and 91
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, BranchName, CAST(BranchName AS VARBINARY(100)) as Bin FROM dbo.Branches WHERE Id IN (55, 91)"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    Write-Host ("Branch Id: " + $r["Id"] + " Name: " + $r["BranchName"])
}
$r.Close()

# Check users in branch 55
$cmd.CommandText = "SELECT Id, UserName, FullName, UserCode, IsActive FROM AspNetUsers WHERE BranchId = 55"
$r2 = $cmd.ExecuteReader()
Write-Host "=== Users in Branch 55 ==="
while ($r2.Read()) {
    Write-Host ("User in 55: Code=" + $r2["UserCode"] + " UserName=" + $r2["UserName"] + " Name=" + $r2["FullName"] + " Active=" + $r2["IsActive"])
}
$r2.Close()

# Check CompanyTrainees in branch 55
$cmd.CommandText = "SELECT ct.Id, ct.FullName, ct.UserCode, ct.BranchName, ct.CompanyId FROM CompanyTrainees ct WHERE ct.BranchName IN (SELECT BranchName FROM Branches WHERE Id = 55)"
$r3 = $cmd.ExecuteReader()
Write-Host "=== CompanyTrainees in Branch 55 ==="
while ($r3.Read()) {
    Write-Host ("Trainee: Id=" + $r3["Id"] + " Code=" + $r3["UserCode"] + " Name=" + $r3["FullName"] + " CompanyId=" + $r3["CompanyId"] + " Branch=" + $r3["BranchName"])
}
$r3.Close()

# Check user 5056
$cmd.CommandText = "SELECT u.Id, u.UserName, u.FullName, u.UserCode, u.BranchId, b.BranchName, u.IsActive FROM AspNetUsers u LEFT JOIN Branches b ON u.BranchId = b.Id WHERE u.UserCode = '5056'"
$r4 = $cmd.ExecuteReader()
Write-Host "=== User 5056 info ==="
while ($r4.Read()) {
    Write-Host ("User 5056: Code=" + $r4["UserCode"] + " UserName=" + $r4["UserName"] + " Name=" + $r4["FullName"] + " BranchId=" + $r4["BranchId"] + " BranchName=" + $r4["BranchName"])
}
$r4.Close()

$conn.Close()
