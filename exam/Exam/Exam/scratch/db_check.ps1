$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.sp_GetItemsForSearch')) AS SP_Definition"
$reader = $cmd.ExecuteReader()
if ($reader.Read()) {
    Write-Output $reader["SP_Definition"]
}
$reader.Close()
$conn.Close()
