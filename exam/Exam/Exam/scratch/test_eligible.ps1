$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

# Check what would be assigned for Exam 9177
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT COUNT(DISTINCT u.Id)
FROM AspNetUsers u WITH(NOLOCK)
INNER JOIN AspNetUserRoles ur WITH(NOLOCK) ON u.Id = ur.UserId
INNER JOIN AspNetRoles r WITH(NOLOCK) ON ur.RoleId = r.Id
WHERE (u.IsActive = 1 OR u.IsActive IS NULL)
  AND (
      LOWER(r.Name) IN ('assistant', 'pharmacist')
      OR r.Name LIKE '%assistant%'
      OR r.Name LIKE '%pharmacist%'
      OR r.Name LIKE N'%صيدل%'
      OR r.Name LIKE N'%مساعد%'
  )
  AND NOT EXISTS (
      SELECT 1 FROM AspNetUserRoles urAdmin WITH(NOLOCK)
      INNER JOIN AspNetRoles rAdmin WITH(NOLOCK) ON urAdmin.RoleId = rAdmin.Id
      WHERE urAdmin.UserId = u.Id AND LOWER(rAdmin.Name) IN ('admin', 'superadmin')
  )
"@
$cnt = $cmd.ExecuteScalar()
Write-Output "Eligible Pharmacists + Assistants total: $cnt"

$conn.Close()
