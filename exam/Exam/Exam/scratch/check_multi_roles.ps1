$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT UserId, COUNT(RoleId) as RoleCount FROM AspNetUserRoles GROUP BY UserId HAVING COUNT(RoleId) > 1"
$reader = $cmd.ExecuteReader()
$count = 0
while ($reader.Read()) {
    $count++
    [PSCustomObject]@{
        UserId = $reader["UserId"]
        RoleCount = $reader["RoleCount"]
    }
}
$reader.Close()
write-host "Total users with multiple roles: $count"
$conn.Close()
