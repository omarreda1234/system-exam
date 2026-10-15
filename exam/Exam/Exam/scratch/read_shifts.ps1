$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, ShiftName, StartTime, EndTime FROM Shifts"
$r = $cmd.ExecuteReader()
while($r.Read()){
    Write-Output ($r[0].ToString() + " | " + $r[1] + " | " + $r[2].ToString() + " | " + $r[3].ToString())
}
$conn.Close()
