$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT TOP 10 Id, Title, ExamTypeId, WaveId, IsActive, StartTime, EndTime FROM Exams ORDER BY Id DESC"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "Id: $($r['Id']) | Title: $($r['Title']) | Type: $($r['ExamTypeId']) | Active: $($r['IsActive']) | Start: $($r['StartTime']) | End: $($r['EndTime'])"
}
$r.Close()
$conn.Close()
