$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Id, Email, PasswordHash, SecurityStamp FROM AspNetUsers WHERE SecurityStamp IS NULL OR SecurityStamp = ''"
$reader = $cmd.ExecuteReader()
$count = 0
while ($reader.Read()) {
    $count++
    Write-Output "User with null SecurityStamp: Id=$($reader['Id']), Email=$($reader['Email'])"
}
$reader.Close()
$conn.Close()
Write-Output "Total users with null SecurityStamp: $count"
