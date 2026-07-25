$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT TOP 5 * FROM dbo.AssignmentQuestions"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Output "Id: $($reader['Id']), QuestionType: $($reader['QuestionType']), TargetRole: $($reader['TargetRole']), Points: $($reader['Points']), CorrectItemNo: $($reader['CorrectItemNo']), ItemDefinition: $($reader['ItemDefinition'])"
}
$reader.Close()
$conn.Close()
