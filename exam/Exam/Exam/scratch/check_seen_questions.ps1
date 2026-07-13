$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT 
    usq.QuestionId,
    q.QuestionText,
    q.Points,
    sqd.SelectedChoiceId,
    sqd.IsCorrect
FROM UserSeenQuestions usq
INNER JOIN Questions q ON usq.QuestionId = q.Id
LEFT JOIN StudentQuestionDetails sqd ON usq.AttemptId = sqd.UserExamAttemptId AND usq.QuestionId = sqd.QuestionId
WHERE usq.AttemptId = 8664
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        QuestionId = $reader["QuestionId"]
        QuestionText = $reader["QuestionText"]
        Points = $reader["Points"]
        SelectedChoiceId = $reader["SelectedChoiceId"]
        IsCorrect = $reader["IsCorrect"]
    } | Format-List
}
$reader.Close()
$conn.Close()
