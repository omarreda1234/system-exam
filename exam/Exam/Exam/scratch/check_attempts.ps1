$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT 
    UA.Id AS AttemptId,
    U.FullName,
    U.Email,
    UA.ExamId,
    E.Title AS ExamTitle,
    E.DurationInMinutes AS ExamDuration,
    UA.StartTime,
    UA.EndTime,
    UA.[Status],
    UA.Score,
    UA.FinalScore
FROM UserExamAttempts UA
INNER JOIN AspNetUsers U ON UA.UserId = U.Id
INNER JOIN Exams E ON UA.ExamId = E.Id
WHERE U.Email IN ('zeyadibrahim211@gmail.com', 'maiawad433@gmail.com')
ORDER BY UA.StartTime DESC
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        AttemptId = $reader["AttemptId"]
        FullName = $reader["FullName"]
        Email = $reader["Email"]
        ExamId = $reader["ExamId"]
        ExamTitle = $reader["ExamTitle"]
        ExamDuration = $reader["ExamDuration"]
        StartTime = $reader["StartTime"]
        EndTime = $reader["EndTime"]
        Status = $reader["Status"]
        Score = $reader["Score"]
        FinalScore = $reader["FinalScore"]
    } | Format-List
}
$reader.Close()
$conn.Close()
