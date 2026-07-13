$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT 
    ua.Id AS AttemptId,
    u.Email,
    e.Title,
    ua.FinalScore,
    ua.Score,
    (SELECT SUM(q.Points) FROM UserSeenQuestions usq INNER JOIN Questions q ON usq.QuestionId = q.Id WHERE usq.AttemptId = ua.Id) AS SeenPoints,
    (SELECT SUM(q.Points) FROM StudentQuestionDetails sqd INNER JOIN Questions q ON sqd.QuestionId = q.Id WHERE sqd.UserExamAttemptId = ua.Id AND sqd.IsCorrect = 1) AS EarnedPoints
FROM UserExamAttempts ua
INNER JOIN AspNetUsers u ON ua.UserId = u.Id
INNER JOIN Exams e ON ua.ExamId = e.Id
WHERE e.WaveId IS NOT NULL AND e.WaveId > 0
  AND ua.FinalScore > (SELECT ISNULL(SUM(q.Points), 0) FROM UserSeenQuestions usq INNER JOIN Questions q ON usq.QuestionId = q.Id WHERE usq.AttemptId = ua.Id)
ORDER BY ua.StartTime DESC
"@
$reader = $cmd.ExecuteReader()
$count = 0
while ($reader.Read()) {
    $count++
    [PSCustomObject]@{
        AttemptId = $reader["AttemptId"]
        Email = $reader["Email"]
        Title = $reader["Title"]
        FinalScore = $reader["FinalScore"]
        Score = $reader["Score"]
        SeenPoints = $reader["SeenPoints"]
        EarnedPoints = $reader["EarnedPoints"]
    } | Format-List
}
Write-Output "Total discrepant attempts: $count"
$reader.Close()
$conn.Close()
