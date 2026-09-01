$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;";
try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr);
    $conn.Open();

    $cmd = $conn.CreateCommand();
    $cmd.CommandText = "SELECT COUNT(*) FROM UserWaves";
    Write-Output "Total UserWaves: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT ISNULL(IsActive, 1) as IsActiveVal, COUNT(*) FROM UserWaves GROUP BY ISNULL(IsActive, 1)";
    $reader = $cmd.ExecuteReader();
    while($reader.Read()){
        Write-Output "UserWaves IsActive $($reader[0]): $($reader[1])"
    }
    $reader.Close();

    $cmd.CommandText = "SELECT COUNT(*) FROM UserWaveCertificates";
    Write-Output "Total UserWaveCertificates: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT COUNT(*) FROM AttendanceSessions";
    Write-Output "Total AttendanceSessions: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT COUNT(*) FROM UserAttendance";
    Write-Output "Total UserAttendance: $($cmd.ExecuteScalar())";

    $conn.Close();
} catch {
    Write-Output "Error: $_"
}
