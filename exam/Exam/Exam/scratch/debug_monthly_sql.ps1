$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;";
try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr);
    $conn.Open();

    $cmd = $conn.CreateCommand();

    $cmd.CommandText = "SELECT COUNT(*) FROM TrainingWaves WHERE StartDate IS NOT NULL";
    Write-Output "Waves with StartDate NOT NULL: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT COUNT(*) FROM TrainingWaves WHERE StartDate IS NOT NULL AND ISNULL(IsActive, 1) = 1";
    Write-Output "Active Waves with StartDate NOT NULL: $($cmd.ExecuteScalar())";

    $cmd.CommandText = "SELECT Id, WaveName, StartDate, ISNULL(IsActive, 1) as IsActive FROM TrainingWaves";
    $reader = $cmd.ExecuteReader();
    while($reader.Read()){
        Write-Output "Wave $($reader['Id']): '$($reader['WaveName'])', StartDate: '$($reader['StartDate'])', IsActive: '$($reader['IsActive'])'"
    }
    $reader.Close();

    $conn.Close();
} catch {
    Write-Output "Error: $_"
}
