$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT 
    (SELECT COUNT(*) FROM UserSeenQuestions WHERE AttemptId = 8664) AS SeenQuestionsCount,
    (SELECT SUM(q.Points) FROM UserSeenQuestions usq INNER JOIN Questions q ON usq.QuestionId = q.Id WHERE usq.AttemptId = 8664) AS SeenQuestionsPointsSum,
    (SELECT COUNT(*) FROM StudentQuestionDetails WHERE UserExamAttemptId = 8664 AND IsCorrect = 1) AS CorrectAnswersCount,
    (SELECT SUM(q.Points) FROM StudentQuestionDetails sqd INNER JOIN Questions q ON sqd.QuestionId = q.Id WHERE sqd.UserExamAttemptId = 8664 AND sqd.IsCorrect = 1) AS CorrectAnswersPointsSum,
    (SELECT Score FROM UserExamAttempts WHERE Id = 8664) AS AttemptScorePercent,
    (SELECT FinalScore FROM UserExamAttempts WHERE Id = 8664) AS AttemptFinalScorePoints
"@
$reader = $cmd.ExecuteReader()
if ($reader.Read()) {
    [PSCustomObject]@{
        SeenQuestionsCount = $reader["SeenQuestionsCount"]
        SeenQuestionsPointsSum = $reader["SeenQuestionsPointsSum"]
        CorrectAnswersCount = $reader["CorrectAnswersCount"]
        CorrectAnswersPointsSum = $reader["CorrectAnswersPointsSum"]
        AttemptScorePercent = $reader["AttemptScorePercent"]
        AttemptFinalScorePoints = $reader["AttemptFinalScorePoints"]
    } | Format-List
}
$reader.Close()
$conn.Close()
