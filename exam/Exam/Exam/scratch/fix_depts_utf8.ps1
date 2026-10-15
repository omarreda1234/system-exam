$cs = 'Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

# Base64 encoded UTF8 strings to prevent any PowerShell file encoding issues:
# 1. فارما (أدوية) -> 2YHYp9ix2YXYpyAo2KPYr9mI2YrYqSk=
# 2. تجميل وعناية بالبشرة (Cosmetics) -> 2KrYrNmF2YrZhCDZiNi52YbYp9mK2Kkg2KjYp9mE2KjYtNix2KkgKENvc21ldGljcyk=
# 3. مكملات ومستلزمات طبية -> 2YXZg9mF2YTYp9iqINmI2YXYs9iq2YTYstmF2KfYqiDYt9io2YrYqQ==
# 4. أجهزة طبية -> 2KPYrNmH2LLYqSDYt9io2YrYqQ==
# 5. إدارة وعمليات -> 2KXZrNin2LHYqSDZiNi52YXZhNmK2KfYqg==

$list = @(
    @('PHARMA', '2YHYp9ix2YXYpyAo2KPYr9mI2YrYqSk='),
    @('COSMO', '2KrYrNmF2YrZhCDZiNi52YbYp9mK2Kkg2KjYp9mE2KjYtNix2KkgKENvc21ldGljcyk='),
    @('SUPP', '2YXZg9mF2YTYp9iqINmI2YXYs9iq2YTYstmF2KfYqiDYt9io2YrYqQ=='),
    @('DEVICES', '2KPYrNmH2LLYqSDYt9io2YrYqQ=='),
    @('MGMT', '2KXZrNin2LHYqSDZiNi52YXZhNmK2KfYqg==')
)

foreach ($item in $list) {
    $code = $item[0]
    $name = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($item[1]))
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "UPDATE dbo.Departments SET Name = @name WHERE Code = @code"
    $p1 = $cmd.Parameters.Add("@name", [System.Data.SqlDbType]::NVarChar, 150)
    $p1.Value = $name
    $p2 = $cmd.Parameters.Add("@code", [System.Data.SqlDbType]::NVarChar, 50)
    $p2.Value = $code
    $cmd.ExecuteNonQuery()
}

$conn.Close()
Write-Output "Done updating department names successfully."
