$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "
SELECT Q.Id AS QId, Q.QuestionText, C.Id AS ChoiceId, C.ChoiceText, C.IsCorrect
FROM Questions Q
JOIN ExamQuestions EQ ON Q.Id = EQ.QuestionId
JOIN Choices C ON Q.Id = C.QuestionId
WHERE EQ.ExamId = 5166 AND (C.ChoiceText LIKE '%Fragrance%' OR C.ChoiceText LIKE '%Alcohol%' OR C.ChoiceText LIKE N'%الاطنين%' OR C.ChoiceText LIKE N'%الاتنين%')
"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds)
$ds.Tables[0] | Format-Table -AutoSize
$conn.Close()
