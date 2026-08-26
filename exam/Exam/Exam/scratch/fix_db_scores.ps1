$conn = New-Object System.Data.SqlClient.SqlConnection("Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True")
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "UPDATE dbo.UserWaveCertificates SET Score = Score / 10.0 WHERE Score > 100.0 AND Score <= 1000.0"
$r1 = $cmd.ExecuteNonQuery()
Write-Host "Updated > 100 rows: $r1"

$cmd.CommandText = "UPDATE dbo.UserWaveCertificates SET Score = Score * 100.0 WHERE Score > 0 AND Score <= 1.0"
$r2 = $cmd.ExecuteNonQuery()
Write-Host "Updated <= 1 rows: $r2"

$cmd.CommandText = "SELECT U.UserCode, U.FullName, UWC.Score, UWC.CertificateCode FROM dbo.UserWaveCertificates UWC JOIN AspNetUsers U ON UWC.UserId = U.Id WHERE U.UserCode IN ('4535', '4836')"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    Write-Host "$($r['UserCode']) | DB Score Now: $($r['Score']) | Cert: $($r['CertificateCode'])"
}
$conn.Close()
