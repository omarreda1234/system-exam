$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT Id, WaveName FROM TrainingWaves WHERE Id = 3021
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Output "Wave Found: ID = $($reader['Id']), Name = $($reader['WaveName'])"
}
$reader.Close()

Write-Output "`nExams in Wave 3021:"
$cmd.CommandText = "SELECT Id, Title, IsFinalExam FROM Exams WHERE WaveId = 3021"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Output "Exam ID: $($reader['Id']), Title: $($reader['Title']), IsFinal: $($reader['IsFinalExam'])"
}
$reader.Close()

Write-Output "`nNumber of Users in Wave 3021:"
$cmd.CommandText = "SELECT COUNT(*) FROM UserWaves WHERE WaveId = 3021"
$count = $cmd.ExecuteScalar()
Write-Output "Count: $count"

Write-Output "`nNumber of Certificates in UserWaveCertificates for Wave 3021:"
$cmd.CommandText = "SELECT COUNT(*) FROM UserWaveCertificates WHERE WaveId = 3021"
$countCert = $cmd.ExecuteScalar()
Write-Output "Count: $countCert"

$conn.Close()
