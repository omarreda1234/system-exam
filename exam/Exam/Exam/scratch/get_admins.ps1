$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT u.Email, u.UserName FROM AspNetUsers u JOIN AspNetUserRoles ur ON u.Id = ur.UserId JOIN AspNetRoles r ON ur.RoleId = r.Id WHERE r.Name = 'Admin'"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        Email = $reader["Email"]
        UserName = $reader["UserName"]
    }
}
$reader.Close()
$conn.Close()
