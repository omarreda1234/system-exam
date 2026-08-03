$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()

function Measure-Query($name, $sql) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    try {
        $reader = $cmd.ExecuteReader()
        $rows = 0
        while ($reader.Read()) {
            $rows++
        }
        $reader.Close()
        $sw.Stop()
        Write-Output "$name`: $($sw.ElapsedMilliseconds) ms (Rows: $rows)"
    } catch {
        $sw.Stop()
        Write-Output "Error in $name`: $_"
    }
}

Write-Output "Profiling dashboard queries..."

Measure-Query "1. Staff Counts" @"
    SELECT R.Name as RoleName, COUNT(U.Id) as Count
    FROM AspNetUsers U
    INNER JOIN AspNetUserRoles UR ON U.Id = UR.UserId
    INNER JOIN AspNetRoles R ON UR.RoleId = R.Id
    WHERE R.Name IN ('pharmacist', 'assistant')
    GROUP BY R.Name
"@

Measure-Query "2. Active Exams" @"
    SELECT COUNT(*) FROM Exams WHERE IsActive = 1 AND EndTime > GETDATE()
"@

Measure-Query "3. Assigned Assistants to Active Exams" @"
    SELECT COUNT(DISTINCT SA.UserId) 
    FROM UserExamAttempts SA
    JOIN AspNetUserRoles UR ON SA.UserId = UR.UserId
    JOIN AspNetRoles R ON UR.RoleId = R.Id
    JOIN Exams E ON SA.ExamId = E.Id
    WHERE R.Name = 'assistant' AND E.IsActive = 1 AND E.EndTime > GETDATE()
"@

Measure-Query "4. Total Waves" @"
    SELECT COUNT(*) FROM TrainingWaves
"@

Measure-Query "5. Pass Rate Overview" @"
    SELECT 
        COUNT(*) as Total,
        SUM(CASE WHEN IsPassed = 1 THEN 1 ELSE 0 END) as Passed,
        SUM(CASE WHEN IsPassed = 0 THEN 1 ELSE 0 END) as Failed
    FROM UserExamAttempts
    WHERE Status = 'Completed' AND Score IS NOT NULL
"@

Measure-Query "6. Pharmacists per Branch" @"
    SELECT B.BranchName, 
           (SELECT COUNT(*) FROM AspNetUsers U WHERE U.BranchId = B.Id) as UserCount,
           ISNULL(PassedFailed.Passed, 0) as Passed,
           ISNULL(PassedFailed.Failed, 0) as Failed
    FROM Branches B
    LEFT JOIN (
        SELECT U.BranchId,
               SUM(CASE WHEN SA.IsPassed = 1 THEN 1 ELSE 0 END) as Passed,
               SUM(CASE WHEN SA.IsPassed = 0 THEN 1 ELSE 0 END) as Failed
        FROM AspNetUsers U
        INNER JOIN UserExamAttempts SA ON U.Id = SA.UserId
        WHERE SA.Status = 'Completed'
        GROUP BY U.BranchId
    ) PassedFailed ON B.Id = PassedFailed.BranchId
    ORDER BY UserCount DESC
"@

Measure-Query "7. Wave Enrollment Trend" @"
    SELECT TOP 12 YEAR(JoinDate) as Yr, MONTH(JoinDate) as Mn, COUNT(*) as EnrollmentCount
    FROM UserWaves
    WHERE JoinDate IS NOT NULL
    GROUP BY YEAR(JoinDate), MONTH(JoinDate)
    ORDER BY Yr, Mn
"@

Measure-Query "8. Top Performing Pharmacists" @"
    SELECT TOP 5 U.UserName as Name, U.UserCode, E.Title as ExamTitle, SA.Score as Score
    FROM UserExamAttempts SA
    JOIN AspNetUsers U ON SA.UserId = U.Id
    JOIN Exams E ON SA.ExamId = E.Id
    WHERE SA.Status = 'Completed' AND SA.Score IS NOT NULL
    ORDER BY SA.Score DESC
"@

Measure-Query "9. Global Question Bank Stats (Total)" @"
    SELECT COUNT(*) FROM Questions
"@

Measure-Query "9b. Global Question Bank Stats (Detailed)" @"
    SELECT 
        ISNULL(C.CategoryName, 'Uncategorized') as CategoryName,
        ISNULL(T.TopicName, 'General') as TopicName,
        COUNT(Q.Id) as Count
    FROM Questions Q
    LEFT JOIN Categories C ON Q.CategoryId = C.Id
    LEFT JOIN Topics T ON Q.TopicId = T.Id
    GROUP BY C.CategoryName, T.TopicName
    ORDER BY C.CategoryName, T.TopicName
"@

$conn.Close()
