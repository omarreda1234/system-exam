$cs = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True;"
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, TypeName FROM ExamTypes"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    Write-Host "Id: $($r['Id']) | TypeName: $($r['TypeName'])"
}
$conn.Close()
