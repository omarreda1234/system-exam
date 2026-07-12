$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT DISTINCT TargetRole FROM dbo.AssignmentQuestions"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Output $reader["TargetRole"]
}
$reader.Close()
$conn.Close()
