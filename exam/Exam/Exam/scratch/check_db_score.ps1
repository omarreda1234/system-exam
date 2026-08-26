$conn = New-Object System.Data.SqlClient.SqlConnection("Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True")
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT U.UserCode, U.FullName, UWC.Score, UWC.CertificateCode FROM dbo.UserWaveCertificates UWC JOIN AspNetUsers U ON UWC.UserId = U.Id WHERE U.UserCode IN ('4535', '4836')"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    Write-Host "$($r['UserCode']) | $($r['FullName']) | DB Score: $($r['Score']) | Cert: $($r['CertificateCode'])"
}
$conn.Close()
