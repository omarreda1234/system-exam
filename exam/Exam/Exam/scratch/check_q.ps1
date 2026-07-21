$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "
SELECT Q.Id AS QId, Q.QuestionText, C.Id AS ChoiceId, C.ChoiceText, C.IsCorrect
FROM Questions Q
JOIN Choices C ON Q.Id = C.QuestionId
WHERE Q.ExamId = 5166 AND Q.QuestionText LIKE N'%البشرة الحساسة%'
"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds)
$ds.Tables[0] | Format-Table -AutoSize
$conn.Close()
