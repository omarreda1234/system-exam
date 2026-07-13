$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE 
FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_NAME IN ('UserWaves', 'AspNetUsers', 'UserExamAttempts')
ORDER BY TABLE_NAME, ORDINAL_POSITION
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        TableName = $reader["TABLE_NAME"]
        ColumnName = $reader["COLUMN_NAME"]
        DataType = $reader["DATA_TYPE"]
    } | Format-Table
}
$reader.Close()
$conn.Close()
