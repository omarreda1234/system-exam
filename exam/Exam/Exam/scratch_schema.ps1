$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True"
$connection = New-Object System.Data.SqlClient.SqlConnection($connString)
$connection.Open()

$cmd = $connection.CreateCommand()
$cmd.CommandText = "SELECT * FROM dbo.Items WHERE No_ = '102580'"
$reader = $cmd.ExecuteReader()
$dt = $reader.GetSchemaTable()
while ($reader.Read()) {
    foreach ($row in $dt.Rows) {
        $col = $row["ColumnName"]
        Write-Host ($col + ": " + $reader[$col])
    }
}
$reader.Close()
$connection.Close()
