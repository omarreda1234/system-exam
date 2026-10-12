$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT COUNT(*) 
FROM AspNetUsers u
WHERE (u.IsActive = 1 OR u.IsActive IS NULL)
  AND (u.UserRoleCustom LIKE '%صيدل%' OR u.UserRoleCustom LIKE '%مساعد%' OR u.UserRoleCustom LIKE '%Pharmacist%' OR u.UserRoleCustom LIKE '%Assistant%')
  AND NOT EXISTS (
      SELECT 1 FROM AspNetUserRoles ur 
      JOIN AspNetRoles r ON ur.RoleId = r.Id 
      WHERE ur.UserId = u.Id AND (r.Name LIKE '%Pharmacist%' OR r.Name LIKE '%Assistant%')
  )
"@
$cnt = $cmd.ExecuteScalar()
Write-Output "Users with pharmacist/assistant UserRoleCustom but NOT in AspNetUserRoles: $cnt"

$conn.Close()
