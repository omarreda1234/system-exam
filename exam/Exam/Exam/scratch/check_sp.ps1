$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID('sp_AddnewExam'))"
$def = $cmd.ExecuteScalar()
Write-Output "=== sp_AddnewExam ==="
Write-Output $def

$cmd.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID('sp_UpdateExam'))"
$def2 = $cmd.ExecuteScalar()
Write-Output "=== sp_UpdateExam ==="
Write-Output $def2

$conn.Close()
