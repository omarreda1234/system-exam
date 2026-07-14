$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT Id, WaveName FROM TrainingWaves WHERE WaveName LIKE '%WAVE13%'
"@
$reader = $cmd.ExecuteReader()
$waveId = 0
while ($reader.Read()) {
    $waveId = $reader["Id"]
    Write-Output "Wave Found: ID = $waveId, Name = $($reader['WaveName'])"
}
$reader.Close()

if ($waveId -gt 0) {
    Write-Output "`nExams in this Wave:"
    $cmd.CommandText = "SELECT Id, Title, IsFinalExam FROM Exams WHERE WaveId = $waveId"
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) {
        Write-Output "Exam ID: $($reader['Id']), Title: $($reader['Title']), IsFinal: $($reader['IsFinalExam'])"
    }
    $reader.Close()

    Write-Output "`nNumber of Users in this Wave:"
    $cmd.CommandText = "SELECT COUNT(*) FROM UserWaves WHERE WaveId = $waveId"
    $count = $cmd.ExecuteScalar()
    Write-Output "Count: $count"

    Write-Output "`nFirst 5 Users in this Wave:"
    $cmd.CommandText = "SELECT TOP 5 U.UserCode, U.FullName FROM UserWaves UW JOIN AspNetUsers U ON UW.UserId = U.Id WHERE UW.WaveId = $waveId"
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) {
        Write-Output "Code: $($reader['UserCode']), Name: $($reader['FullName'])"
    }
    $reader.Close()
}
$conn.Close()
