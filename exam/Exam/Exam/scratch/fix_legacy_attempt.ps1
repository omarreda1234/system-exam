$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
UPDATE UserExamAttempts
SET FinalScore = 6,
    Score = 60,
    CorrectAnswers = 3,
    TotalQuestions = 5
WHERE Id = 8664
"@
$rows = $cmd.ExecuteNonQuery()
Write-Output "Updated rows: $rows"
$conn.Close()
