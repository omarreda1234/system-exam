$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()

$sql = @'
;WITH BranchUsers AS (
    SELECT 
        u.Id AS UserId,
        ct.Id AS CompanyTraineeId,
        COALESCE(NULLIF(u.FullName, ''), NULLIF(u.UserName, ''), NULLIF(ct.FullName, ''), N'User') AS FullName,
        COALESCE(NULLIF(u.UserCode, ''), NULLIF(ct.UserCode, ''), N'--') AS UserCode,
        COALESCE(NULLIF(r.Name, ''), NULLIF(ct.JobTitle, ''), N'Pharmacist') AS JobTitle,
        COALESCE(NULLIF(b.BranchName, ''), NULLIF(ct.BranchName, ''), @BranchName) AS BranchName
    FROM AspNetUsers u WITH (NOLOCK)
    LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
    LEFT JOIN AspNetUserRoles ur ON u.Id = ur.UserId
    LEFT JOIN AspNetRoles r ON ur.RoleId = r.Id
    LEFT JOIN CompanyTrainees ct WITH (NOLOCK) ON ct.CompanyId = @CompanyId AND (ct.UserCode = u.UserCode OR (ct.Email = u.Email AND ct.Email <> '--'))
    WHERE (
        (@BranchId > 0 AND (u.BranchId = @BranchId OR b.Id = @BranchId))
        OR (@BranchName <> '' AND (b.BranchName = @BranchName OR LTRIM(RTRIM(b.BranchName)) = @BranchName))
        OR (@BranchName <> '' AND u.BranchId IN (SELECT Id FROM Branches WHERE BranchName = @BranchName OR LTRIM(RTRIM(BranchName)) = @BranchName))
    )
    AND u.IsActive = 1

    UNION

    SELECT 
        CAST(NULL AS NVARCHAR(450)) AS UserId,
        ct.Id AS CompanyTraineeId,
        ISNULL(NULLIF(ct.FullName, ''), N'Trainee') AS FullName,
        ISNULL(NULLIF(ct.UserCode, ''), N'--') AS UserCode,
        ISNULL(NULLIF(ct.JobTitle, ''), N'Trainee') AS JobTitle,
        ct.BranchName
    FROM CompanyTrainees ct WITH (NOLOCK)
    WHERE (
        (@BranchId > 0 AND ct.BranchName IN (SELECT BranchName FROM Branches WHERE Id = @BranchId))
        OR (@BranchName <> '' AND (ct.BranchName = @BranchName OR LTRIM(RTRIM(ct.BranchName)) = @BranchName))
    )
    AND NOT EXISTS (
        SELECT 1 FROM AspNetUsers u2 WITH (NOLOCK)
        LEFT JOIN Branches b2 WITH (NOLOCK) ON u2.BranchId = b2.Id
        WHERE (u2.UserCode = ct.UserCode OR (u2.Email = ct.Email AND ct.Email <> '--'))
          AND (
              (@BranchId > 0 AND (u2.BranchId = @BranchId OR b2.Id = @BranchId))
              OR (@BranchName <> '' AND (b2.BranchName = @BranchName OR LTRIM(RTRIM(b2.BranchName)) = @BranchName))
          )
    )
)
SELECT 
    bu.UserId,
    bu.CompanyTraineeId,
    CASE 
        WHEN bu.UserId IS NOT NULL THEN bu.UserId 
        ELSE CAST(bu.CompanyTraineeId AS NVARCHAR(100)) 
    END AS Value,
    bu.FullName,
    bu.UserCode,
    bu.JobTitle,
    bu.BranchName
FROM BranchUsers bu
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.CompanySessionTrainees cst WITH (NOLOCK)
    WHERE cst.SessionId = @SessionId
      AND cst.ApprovalStatus IN ('Approved', 'Pending')
      AND (
          (bu.CompanyTraineeId IS NOT NULL AND cst.CompanyTraineeId = bu.CompanyTraineeId)
          OR (bu.UserId IS NOT NULL AND cst.UserId = bu.UserId)
      )
)
ORDER BY bu.FullName ASC
'@

function Run-Test($sId, $cId, $bName, $bId, $label) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.Parameters.AddWithValue("@SessionId", $sId) | Out-Null
    $cmd.Parameters.AddWithValue("@CompanyId", $cId) | Out-Null
    $cmd.Parameters.AddWithValue("@BranchName", $bName) | Out-Null
    $cmd.Parameters.AddWithValue("@BranchId", $bId) | Out-Null
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
    $ds = New-Object System.Data.DataSet
    $adapter.Fill($ds) | Out-Null
    Write-Host ($label + ": Count = " + $ds.Tables[0].Rows.Count)
}

$cmdName = $conn.CreateCommand()
$cmdName.CommandText = "SELECT BranchName FROM Branches WHERE Id = 55"
$bName55 = [string]$cmdName.ExecuteScalar()

Run-Test 7093 3029 $bName55 55 "Test 1: Normal"
Run-Test 7093 3029 $bName55 0 "Test 2: BranchId=0"
Run-Test 7093 0 $bName55 55 "Test 3: CompanyId=0"
Run-Test 7093 0 $bName55 0 "Test 4: Both 0"
Run-Test 0 3029 $bName55 55 "Test 5: SessionId=0"
Run-Test 7093 3029 "" 0 "Test 6: Empty BranchName and BranchId=0"

$conn.Close()
