$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT 
    r.Name as RoleName,
    COUNT(*) as TotalUsers,
    SUM(CASE WHEN u.IsActive = 1 THEN 1 ELSE 0 END) as Active1,
    SUM(CASE WHEN u.IsActive = 0 THEN 1 ELSE 0 END) as Active0,
    SUM(CASE WHEN u.IsActive IS NULL THEN 1 ELSE 0 END) as ActiveNull
FROM AspNetUsers u
JOIN AspNetUserRoles ur ON u.Id = ur.UserId
JOIN AspNetRoles r ON ur.RoleId = r.Id
WHERE r.Name IN ('Pharmacist', 'Assistant')
GROUP BY r.Name
"@

$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "$($r['RoleName']) | Total: $($r['TotalUsers']) | IsActive=1: $($r['Active1']) | IsActive=0: $($r['Active0']) | IsActive=NULL: $($r['ActiveNull'])"
}
$r.Close()
$conn.Close()
