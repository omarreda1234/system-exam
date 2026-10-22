$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE 
FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_NAME IN ('UserWaves', 'CompanySessionTrainees', 'NominationHistory', 'AuditLogs')
ORDER BY TABLE_NAME, ORDINAL_POSITION
"@
$r = $cmd.ExecuteReader()
while($r.Read()) {
    Write-Output "$($r['TABLE_NAME']) | $($r['COLUMN_NAME']) | $($r['DATA_TYPE'])"
}
$r.Close()
$conn.Close()
