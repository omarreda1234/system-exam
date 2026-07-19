$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Name FROM AspNetRoles"
$reader = $cmd.ExecuteReader()
Write-Output "--- ROLES ---"
while ($reader.Read()) {
    Write-Output $reader["Name"]
}
$reader.Close()

$cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME LIKE '%Branch%' OR TABLE_NAME LIKE '%Supervisor%' OR TABLE_NAME LIKE '%User%'"
$reader = $cmd.ExecuteReader()
Write-Output "--- TABLES ---"
while ($reader.Read()) {
    Write-Output $reader["TABLE_NAME"]
}
$reader.Close()

$conn.Close()
