$connStr = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True;";
try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr);
    $conn.Open();

    $cmd = $conn.CreateCommand();

    $cmd.CommandText = "
        SELECT 
            S.Id as SessionId,
            S.SessionName,
            S.SessionDate,
            ISNULL(W.WaveName, N'عام / Global') as WaveName,
            ISNULL((
                CASE 
                    WHEN S.WaveId IS NOT NULL AND S.WaveId > 0 THEN (SELECT COUNT(DISTINCT UW.UserId) FROM dbo.UserWaves UW WHERE UW.WaveId = S.WaveId)
                    ELSE (SELECT COUNT(DISTINCT UA.UserId) FROM dbo.UserAttendance UA WHERE UA.SessionId = S.Id)
                END
            ), 0) as RawEnrolled,
            ISNULL((SELECT COUNT(DISTINCT UA.UserId) FROM dbo.UserAttendance UA WHERE UA.SessionId = S.Id AND UA.IsPresent = 1), 0) as PresentCount
        FROM dbo.AttendanceSessions S
        LEFT JOIN dbo.TrainingWaves W ON S.WaveId = W.Id
        ORDER BY S.SessionDate DESC";

    $reader = $cmd.ExecuteReader();
    $count = 0;
    while($reader.Read()){
        $count++;
    }
    $reader.Close();
    Write-Output "Session Rows returned for All Waves: $count";

    $cmd.CommandText = "
        SELECT 
            FORMAT(W.StartDate, 'MMM yyyy') as MonthLabel,
            MIN(W.StartDate) as MonthDate,
            COUNT(UW.UserId) as TotalTrainees,
            SUM(CASE WHEN wc.Score > 75 THEN 1 ELSE 0 END) as CertifiedCount,
            SUM(CASE WHEN wc.Score >= 70 AND wc.Score <= 75 THEN 1 ELSE 0 END) as PassedNoCertCount,
            SUM(CASE WHEN wc.Score > 0 AND wc.Score < 70 THEN 1 ELSE 0 END) as FailedCount
        FROM TrainingWaves W
        JOIN UserWaves UW ON W.Id = UW.WaveId
        LEFT JOIN UserWaveCertificates wc ON wc.UserId = UW.UserId AND wc.WaveId = W.Id
        WHERE W.StartDate IS NOT NULL
        GROUP BY FORMAT(W.StartDate, 'MMM yyyy')
        ORDER BY MIN(W.StartDate) ASC";

    $reader = $cmd.ExecuteReader();
    $mCount = 0;
    while($reader.Read()){
        $mCount++;
        Write-Output "Month: $($reader['MonthLabel']), TotalTrainees: $($reader['TotalTrainees']), Certified: $($reader['CertifiedCount'])";
    }
    $reader.Close();
    Write-Output "Monthly Trends returned: $mCount";

    $conn.Close();
} catch {
    Write-Output "Error: $_"
}
