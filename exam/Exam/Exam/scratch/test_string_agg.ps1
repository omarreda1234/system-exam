$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT TOP 10 
    U.Id, U.UserName, ISNULL(U.FullName, U.UserName) as FullName, U.Email,
    ISNULL(R_CTE.RoleNames, 'User') as RoleName
FROM AspNetUsers U WITH(NOLOCK)
LEFT JOIN (
    SELECT UR.UserId, STRING_AGG(R.Name, ', ') as RoleNames
    FROM AspNetUserRoles UR WITH(NOLOCK)
    JOIN AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
    GROUP BY UR.UserId
) R_CTE ON U.Id = R_CTE.UserId
WHERE U.IsActive = 1
AND NOT EXISTS (
    SELECT 1 FROM AspNetUserRoles UR2 WITH(NOLOCK) 
    JOIN AspNetRoles R2 WITH(NOLOCK) ON UR2.RoleId = R2.Id 
    WHERE UR2.UserId = U.Id AND LOWER(R2.Name) IN ('admin', 'superadmin')
)
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        UserName = $reader["UserName"]
        RoleName = $reader["RoleName"]
    }
}
$reader.Close()
$conn.Close()
