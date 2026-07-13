$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT U.Email, U.UserName, R.Name as RoleName
FROM AspNetUsers U
JOIN AspNetUserRoles UR ON U.Id = UR.UserId
JOIN AspNetRoles R ON UR.RoleId = R.Id
WHERE R.Name = 'Admin' OR R.Name = 'Branch Manager'
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Output "Email: $($reader['Email']) | UserName: $($reader['UserName']) | Role: $($reader['RoleName'])"
}
$reader.Close()
$conn.Close()
