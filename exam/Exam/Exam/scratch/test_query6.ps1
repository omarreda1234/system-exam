$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE COLUMN_NAME IN ('BranchName', 'BranchId')"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    Write-Host ("Table: " + $r["TABLE_NAME"] + " Col: " + $r["COLUMN_NAME"] + " Type: " + $r["DATA_TYPE"] + " Len: " + $r["CHARACTER_MAXIMUM_LENGTH"] + " Collation: " + $r["COLLATION_NAME"])
}
$conn.Close()
