$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT ROUTINE_NAME FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_DEFINITION LIKE '%UPDATE%Exams%IsActive%' OR ROUTINE_DEFINITION LIKE '%UPDATE dbo.Exams%'"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output $r[0]
}
$r.Close()
$conn.Close()
