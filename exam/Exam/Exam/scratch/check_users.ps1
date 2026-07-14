$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COUNT(*) FROM AspNetUsers WHERE UserCode = '3310'"
$count = $cmd.ExecuteScalar()
Write-Output "Users with code 3310: $count"

$cmd.CommandText = "SELECT TOP 10 UserCode, UserName, FullName FROM AspNetUsers"
$reader = $cmd.ExecuteReader()
Write-Output "`nFirst 10 Users in AspNetUsers:"
while ($reader.Read()) {
    Write-Output "Code: $($reader['UserCode']), UserName: $($reader['UserName']), FullName: $($reader['FullName'])"
}
$reader.Close()
$conn.Close()
