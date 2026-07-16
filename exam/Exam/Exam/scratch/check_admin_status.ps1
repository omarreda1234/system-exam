$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()

$query = @"
SELECT U.Email, U.UserName, U.PasswordHash, R.Name as RoleName
FROM AspNetUsers U
JOIN AspNetUserRoles UR ON U.Id = UR.UserId
JOIN AspNetRoles R ON UR.RoleId = R.Id
WHERE R.Name IN ('Admin', 'HR', 'Human Resources')
"@

$cmd = $conn.CreateCommand()
$cmd.CommandText = $query
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$dt = New-Object System.Data.DataTable
$adapter.Fill($dt)

$dt | Format-List
$conn.Close()
