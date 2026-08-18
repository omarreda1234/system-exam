$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, ShiftName, StartTime, EndTime FROM dbo.Shifts"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    $id = $reader["Id"]
    $name = $reader["ShiftName"]
    $start = $reader["StartTime"]
    $end = $reader["EndTime"]
    Write-Output "ID: $id | Shift: $name | Time: $start - $end"
}
$reader.Close()
$conn.Close()
