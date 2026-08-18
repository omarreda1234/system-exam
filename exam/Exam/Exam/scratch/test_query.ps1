$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT TOP 10 
    U.UserName,
    U.Email,
    U.PhoneNumber AS Phone,
    R.Name AS RoleName,
    U.UserCode,
    B.BranchName,
    U.ShiftId,
    U.CertificateCode
FROM dbo.AspNetUsers U WITH(NOLOCK)
LEFT JOIN dbo.AspNetUserRoles UR ON U.Id = UR.UserId
LEFT JOIN dbo.AspNetRoles R WITH(NOLOCK) ON UR.RoleId = R.Id
LEFT JOIN dbo.Branches B WITH(NOLOCK) ON U.BranchId = B.Id
WHERE U.Email LIKE '%284%' OR U.UserCode = '284'
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Output "User: $($reader['UserName']) | Email: $($reader['Email']) | Role: $($reader['RoleName']) | Branch: $($reader['BranchName']) | UserCode: $($reader['UserCode']) | ShiftId: $($reader['ShiftId'])"
}
$reader.Close()
$conn.Close()
