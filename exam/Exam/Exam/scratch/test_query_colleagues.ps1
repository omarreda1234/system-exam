$conn = New-Object System.Data.SqlClient.SqlConnection('Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;');
$conn.Open();
$cmd = $conn.CreateCommand();

$trimmedBranch = "الجيش";
$sessionId = 7093;
$companyId = 3029;
$branchId = 0;
$allBranches = 0;

$cmd.CommandText = @"
    ;WITH BranchUsers AS (
        SELECT 
            u.Id AS UserId,
            ct.Id AS CompanyTraineeId,
            COALESCE(NULLIF(u.FullName, ''), NULLIF(u.UserName, ''), NULLIF(ct.FullName, ''), N'User') AS FullName,
            COALESCE(NULLIF(u.UserCode, ''), NULLIF(ct.UserCode, ''), N'--') AS UserCode,
            COALESCE(NULLIF(r.Name, ''), NULLIF(ct.JobTitle, ''), N'Pharmacist') AS JobTitle,
            COALESCE(NULLIF(b.BranchName, ''), NULLIF(ct.BranchName, ''), @BranchName) AS BranchName,
            ISNULL(u.IsActive, 1) AS IsActive
        FROM AspNetUsers u WITH (NOLOCK)
        LEFT JOIN Branches b WITH (NOLOCK) ON u.BranchId = b.Id
        LEFT JOIN AspNetUserRoles ur ON u.Id = ur.UserId
        LEFT JOIN AspNetRoles r ON ur.RoleId = r.Id
        LEFT JOIN CompanyTrainees ct WITH (NOLOCK) ON ct.CompanyId = @CompanyId AND (ct.UserCode = u.UserCode OR (ct.Email = u.Email AND ct.Email <> '--'))
        WHERE (
            @AllBranches = 1
            OR (@BranchId > 0 AND (u.BranchId = @BranchId OR b.Id = @BranchId))
            OR (@BranchName <> '' AND (b.BranchName = @BranchName OR LTRIM(RTRIM(b.BranchName)) = @BranchName))
            OR (@BranchName <> '' AND u.BranchId IN (SELECT Id FROM Branches WHERE BranchName = @BranchName OR LTRIM(RTRIM(BranchName)) = @BranchName))
        )

        UNION

        SELECT 
            CAST(NULL AS NVARCHAR(450)) AS UserId,
            ct.Id AS CompanyTraineeId,
            ISNULL(NULLIF(ct.FullName, ''), N'Trainee') AS FullName,
            ISNULL(NULLIF(ct.UserCode, ''), N'--') AS UserCode,
            ISNULL(NULLIF(ct.JobTitle, ''), N'Trainee') AS JobTitle,
            ct.BranchName,
            CAST(1 AS BIT) AS IsActive
        FROM CompanyTrainees ct WITH (NOLOCK)
        WHERE (
            @AllBranches = 1
            OR (@BranchId > 0 AND ct.BranchName IN (SELECT BranchName FROM Branches WHERE Id = @BranchId))
            OR (@BranchName <> '' AND (ct.BranchName = @BranchName OR LTRIM(RTRIM(ct.BranchName)) = @BranchName))
        )
        AND NOT EXISTS (
            SELECT 1 FROM AspNetUsers u2 WITH (NOLOCK)
            LEFT JOIN Branches b2 WITH (NOLOCK) ON u2.BranchId = b2.Id
            WHERE (u2.UserCode = ct.UserCode OR (u2.Email = ct.Email AND ct.Email <> '--'))
              AND (
                  @AllBranches = 1
                  OR (@BranchId > 0 AND (u2.BranchId = @BranchId OR b2.Id = @BranchId))
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
        bu.BranchName,
        bu.IsActive
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
"@;

$cmd.Parameters.AddWithValue("@SessionId", $sessionId);
$cmd.Parameters.AddWithValue("@CompanyId", $companyId);
$cmd.Parameters.AddWithValue("@BranchName", $trimmedBranch);
$cmd.Parameters.AddWithValue("@BranchId", $branchId);
$cmd.Parameters.AddWithValue("@AllBranches", $allBranches);

try {
    $r = $cmd.ExecuteReader();
    $count = 0;
    while($r.Read()) {
        $count++;
        Write-Output ("Row {0}: Name={1}, Code={2}, Role={3}, Branch={4}" -f $count, $r["FullName"], $r["UserCode"], $r["JobTitle"], $r["BranchName"])
    }
    Write-Output "Total rows found: $count"
} catch {
    Write-Output "SQL Error: $_"
}
