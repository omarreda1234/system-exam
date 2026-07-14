$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()

$hash = "AQAAAAIAAYagAAAAEK33FDvUX4uh7p/dEYmcRd+zIE2o5v+fAvgF52MVq+oA7LjM/rzaENDq9gBrgVesjA=="
$query = "UPDATE AspNetUsers SET PasswordHash = @Hash WHERE Email IN ('testuser45@gmail.com', 'mohamedmeati893@gmail.com')"
$cmd = $conn.CreateCommand()
$cmd.CommandText = $query
$cmd.Parameters.AddWithValue("@Hash", $hash) | Out-Null
$rows = $cmd.ExecuteNonQuery()

Write-Host "Rows updated: $rows"
$conn.Close()
