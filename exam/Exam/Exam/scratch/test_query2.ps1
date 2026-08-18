$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT COUNT(*) AS TotalEltarshouby
FROM dbo.AspNetUsers
WHERE Email LIKE '%@eltarshouby.com'
"@
$count = $cmd.ExecuteScalar()
Write-Output "Total users with @eltarshouby.com: $count"

$cmd.CommandText = @"
SELECT TOP 5 U.UserName, U.Email, U.UserCode, B.BranchName, R.Name AS RoleName, U.ShiftId
FROM dbo.AspNetUsers U WITH(NOLOCK)
LEFT JOIN dbo.AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN dbo.AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
LEFT JOIN dbo.Branches B WITH(NOLOCK) ON U.BranchId = B.Id
WHERE U.Email LIKE '%@eltarshouby.com'
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Output "$($reader['UserCode']) | $($reader['UserName']) | $($reader['Email']) | $($reader['RoleName']) | $($reader['BranchName']) | ShiftId: $($reader['ShiftId'])"
}
$reader.Close()
$conn.Close()
