$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, UserName, Email, SecurityStamp, LockoutEnabled, LockoutEnd, AccessFailedCount FROM AspNetUsers WHERE Email = 'omaraladeeb45@gmail.com'"
$reader = $cmd.ExecuteReader()
if ($reader.Read()) {
    Write-Output "User Details:"
    Write-Output "Id: $($reader['Id'])"
    Write-Output "UserName: $($reader['UserName'])"
    Write-Output "Email: $($reader['Email'])"
    Write-Output "SecurityStamp: $($reader['SecurityStamp'])"
    Write-Output "LockoutEnabled: $($reader['LockoutEnabled'])"
    Write-Output "LockoutEnd: $($reader['LockoutEnd'])"
    Write-Output "AccessFailedCount: $($reader['AccessFailedCount'])"
} else {
    Write-Output "User not found!"
}
$reader.Close()
$conn.Close()
