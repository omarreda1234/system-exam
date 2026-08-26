$config = Get-Content "c:\exam final\exam\Exam\Exam\appsettings.json" | ConvertFrom-Json
$connStr = $config.ConnectionStrings.DefaultConnection

Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT UA.Id, UA.UserId, UA.CompanyTraineeId, UA.IsPresent, UA.CheckInTime, UA.CheckOutTime, UA.RecordedAt, S.SessionName, S.SessionDate FROM UserAttendance UA JOIN AttendanceSessions S ON UA.SessionId = S.Id WHERE UA.SessionId = 7055"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$dt = New-Object System.Data.DataTable
$adapter.Fill($dt)
$dt | Format-Table -AutoSize
$conn.Close()
