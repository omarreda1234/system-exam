$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "UPDATE AspNetUsers SET PasswordHash = 'AQAAAAIAAYagAAAAELUPRlkWJ9E6KVwtnXdHLw/91hk2XOkPg/B8XALt9YsAUoIpkEGsDkds6NNUGqjhrg==' WHERE Email = 'mohamedmeati893@gmail.com'"
$rows = $cmd.ExecuteNonQuery()
Write-Host "Updated rows: $rows"
$conn.Close()
