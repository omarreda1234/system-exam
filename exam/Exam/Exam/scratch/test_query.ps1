$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
try {
    $conn.Open()
    Write-Host "Connected successfully."

    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter
    $cmd = $conn.CreateCommand()
    $adapter.SelectCommand = $cmd

    # 1. Pending CompanySessionTrainees
    $cmd.CommandText = "SELECT TOP 10 cst.Id, cst.SessionId, cst.CompanyTraineeId, cst.UserId, cst.ApprovalStatus, ct.FullName, ct.UserCode, ct.BranchName, s.SessionName, s.CompanyId FROM CompanySessionTrainees cst LEFT JOIN CompanyTrainees ct ON cst.CompanyTraineeId = ct.Id LEFT JOIN AttendanceSessions s ON cst.SessionId = s.Id WHERE cst.ApprovalStatus = 'Pending'"
    $ds4 = New-Object System.Data.DataSet
    $adapter.Fill($ds4) | Out-Null
    Write-Host "=== Pending CompanySessionTrainees ==="
    foreach ($row in $ds4.Tables[0].Rows) {
        Write-Host ("PendingCST: Id=" + $row["Id"] + " SessionId=" + $row["SessionId"] + " CompanyId=" + $row["CompanyId"] + " Trainee=" + $row["FullName"] + " UserCode=" + $row["UserCode"] + " Branch=" + $row["BranchName"])
    }

    # 2. Users with Code 5056 or Name omar
    $cmd.CommandText = "SELECT Id, UserName, FullName, UserCode, BranchId, IsActive FROM AspNetUsers WHERE UserCode LIKE '%5056%' OR UserName LIKE '%omar%'"
    $ds2 = New-Object System.Data.DataSet
    $adapter.Fill($ds2) | Out-Null
    Write-Host "=== AspNetUsers matching 5056 or omar ==="
    foreach ($row in $ds2.Tables[0].Rows) {
        Write-Host ("User: Id=" + $row["Id"] + " UserName=" + $row["UserName"] + " FullName=" + $row["FullName"] + " Code=" + $row["UserCode"] + " BranchId=" + $row["BranchId"] + " IsActive=" + $row["IsActive"])
    }

    # 3. Check Branches
    $cmd.CommandText = "SELECT Id, BranchName FROM dbo.Branches"
    $ds1 = New-Object System.Data.DataSet
    $adapter.Fill($ds1) | Out-Null
    Write-Host "=== Branches ==="
    foreach ($row in $ds1.Tables[0].Rows) {
        # print all branches
        Write-Host ("Branch: Id=" + $row["Id"] + " Name=" + $row["BranchName"])
    }

} catch {
    Write-Host "Error: " $_.Exception.ToString()
} finally {
    $conn.Close()
}
