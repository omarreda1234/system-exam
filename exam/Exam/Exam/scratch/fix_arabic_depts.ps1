$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
UPDATE dbo.Departments SET Name = N'فارما (أدوية)' WHERE Code = 'PHARMA';
UPDATE dbo.Departments SET Name = N'تجميل وعناية بالبشرة (Cosmetics)' WHERE Code = 'COSMO';
UPDATE dbo.Departments SET Name = N'مكملات ومستلزمات طبية' WHERE Code = 'SUPP';
UPDATE dbo.Departments SET Name = N'أجهزة طبية' WHERE Code = 'DEVICES';
UPDATE dbo.Departments SET Name = N'إدارة وعمليات' WHERE Code = 'MGMT';
"@
$cmd.ExecuteNonQuery()

$conn.Close()
