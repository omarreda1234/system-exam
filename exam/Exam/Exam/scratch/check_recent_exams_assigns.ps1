$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT E.Id, E.Title, E.ExamTypeId, ET.TypeName, E.WaveId, E.IsActive, (SELECT COUNT(*) FROM ExamAssignments EA WHERE EA.ExamId = E.Id) as AssignCount FROM Exams E LEFT JOIN ExamTypes ET ON E.ExamTypeId = ET.Id WHERE E.Id >= 9000 ORDER BY E.Id DESC"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "Id: $($r['Id']) | Title: $($r['Title']) | TypeId: $($r['ExamTypeId']) | TypeName: $($r['TypeName']) | WaveId: $($r['WaveId']) | Active: $($r['IsActive']) | AssignCount: $($r['AssignCount'])"
}
$r.Close()
$conn.Close()
