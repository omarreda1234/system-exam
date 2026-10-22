$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, Name FROM AspNetRoles"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "Role: $($r['Name'])"
}
$r.Close()
$conn.Close()
