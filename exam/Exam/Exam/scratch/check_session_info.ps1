$config = Get-Content "c:\exam final\exam\Exam\Exam\appsettings.json" | ConvertFrom-Json
$connStr = $config.ConnectionStrings.DefaultConnection

Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT S.Id, S.SessionName, S.SessionDate, S.WaveId, W.WaveName FROM AttendanceSessions S LEFT JOIN TrainingWaves W ON S.WaveId = W.Id WHERE S.Id = 7055"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$dt = New-Object System.Data.DataTable
$adapter.Fill($dt)
$dt | Format-Table -AutoSize
$conn.Close()
