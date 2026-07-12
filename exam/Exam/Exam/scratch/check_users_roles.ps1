$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT TOP 20 u.Email, r.Name AS RoleName, w.WaveName, uw.IsActive
FROM AspNetUsers u
LEFT JOIN AspNetUserRoles ur ON u.Id = ur.UserId
LEFT JOIN AspNetRoles r ON ur.RoleId = r.Id
LEFT JOIN UserWaves uw ON u.Id = uw.UserId
LEFT JOIN TrainingWaves w ON uw.WaveId = w.Id
ORDER BY u.Email
"@
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    [PSCustomObject]@{
        Email = $reader["Email"]
        RoleName = $reader["RoleName"]
        WaveName = $reader["WaveName"]
        IsActive = $reader["IsActive"]
    } | Format-Table
}
$reader.Close()
$conn.Close()
