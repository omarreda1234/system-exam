$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;";
try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr);
    $conn.Open();

    $cmd = $conn.CreateCommand();
    $cmd.CommandText = "SELECT COUNT(*) FROM AttendanceSessions";
    Write-Output "Total AttendanceSessions: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT COUNT(*) FROM TrainingWaves WHERE ISNULL(IsActive, 1) = 1";
    Write-Output "Total Active TrainingWaves: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT COUNT(*) FROM TrainingWaves";
    Write-Output "Total All TrainingWaves: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT S.WaveId, W.WaveName, ISNULL(W.IsActive, 1) as IsActive, COUNT(*) as SessionCount FROM AttendanceSessions S LEFT JOIN TrainingWaves W ON S.WaveId = W.Id GROUP BY S.WaveId, W.WaveName, W.IsActive";
    $reader = $cmd.ExecuteReader();
    while($reader.Read()){
        Write-Output "WaveId: '$($reader['WaveId'])', WaveName: '$($reader['WaveName'])', IsActive: '$($reader['IsActive'])', SessionCount: $($reader['SessionCount'])"
    }
    $reader.Close();
    $conn.Close();
} catch {
    Write-Output "Error: $_"
}
