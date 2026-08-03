$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT u.Email, r.Name FROM AspNetUsers u JOIN AspNetUserRoles ur ON u.Id = ur.UserId JOIN AspNetRoles r ON ur.RoleId = r.Id WHERE u.Id IN (SELECT UserId FROM AspNetUserRoles GROUP BY UserId HAVING COUNT(RoleId) > 1)"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        Email = $reader["Email"]
        RoleName = $reader["Name"]
    }
}
$reader.Close()
$conn.Close()
