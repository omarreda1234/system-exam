$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UserWaves'"
$reader = $cmd.ExecuteReader()
Write-Output "UserWaves columns:"
while ($reader.Read()) {
    Write-Output "$($reader['COLUMN_NAME']) ($($reader['DATA_TYPE']))"
}
$reader.Close()
$conn.Close()
