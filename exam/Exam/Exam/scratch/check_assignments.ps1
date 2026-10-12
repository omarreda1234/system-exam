$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COUNT(*) FROM ExamAssignments WHERE ExamId = 9177"
$cnt = $cmd.ExecuteScalar()
Write-Output "Exam 9177 assignment count: $cnt"

$cmd.CommandText = "SELECT COUNT(*) FROM ExamAssignments WHERE ExamId = 8177"
$cnt2 = $cmd.ExecuteScalar()
Write-Output "Exam 8177 assignment count: $cnt2"

$conn.Close()
