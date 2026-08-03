$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT 
    U.Email,
    ISNULL(R_CTE.RoleNames, 'User') as RoleName
FROM AspNetUsers U WITH(NOLOCK)
LEFT JOIN (
    SELECT UR.UserId, STRING_AGG(R.Name, ', ') as RoleNames
    FROM AspNetUserRoles UR WITH(NOLOCK)
    JOIN AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
    GROUP BY UR.UserId
) R_CTE ON U.Id = R_CTE.UserId
WHERE U.Email = 'amlmuhamd.88@gmail.com'
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        Email = $reader["Email"]
        RoleName = $reader["RoleName"]
    }
}
$reader.Close()
$conn.Close()
