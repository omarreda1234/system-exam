$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Exams'"
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "$($r['COLUMN_NAME']) | $($r['DATA_TYPE']) | $($r['IS_NULLABLE'])"
}
$r.Close()
$conn.Close()
