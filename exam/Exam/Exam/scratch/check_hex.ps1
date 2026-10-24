$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, BranchName, LEN(BranchName) as L, DATALENGTH(BranchName) as DL, CAST(BranchName as varbinary(100)) as Hex FROM Branches WHERE Id = 55"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    $bytes = [byte[]]$r["Hex"]
    $hexStr = ($bytes | ForEach-Object { $_.ToString("X2") }) -join " "
    Write-Host ("Branch 55: Len=" + $r["L"] + " DL=" + $r["DL"] + " Hex=" + $hexStr)
}
$r.Close()

# Also check ct.BranchName for omar_reda
$cmd.CommandText = "SELECT Id, BranchName, LEN(BranchName) as L, DATALENGTH(BranchName) as DL, CAST(BranchName as varbinary(100)) as Hex FROM CompanyTrainees WHERE Id = 6722"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    $bytes = [byte[]]$r["Hex"]
    $hexStr = ($bytes | ForEach-Object { $_.ToString("X2") }) -join " "
    Write-Host ("CT 6722: Len=" + $r["L"] + " DL=" + $r["DL"] + " Hex=" + $hexStr)
}
$r.Close()

$conn.Close()
