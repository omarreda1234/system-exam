$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;TrustServerCertificate=True"
try {
    $connection = New-Object System.Data.SqlClient.SqlConnection($connString)
    $connection.Open()
    Write-Host "Connected to DB successfully."

    $alterSql = @"
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'IsCustomDefinition')
    BEGIN
        ALTER TABLE dbo.Items ADD IsCustomDefinition BIT NOT NULL DEFAULT 0;
    END

    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'CustomDefinition')
    BEGIN
        ALTER TABLE dbo.Items ADD CustomDefinition NVARCHAR(MAX) NULL;
    END
"@
    $cmd = $connection.CreateCommand()
    $cmd.CommandText = $alterSql
    $cmd.ExecuteNonQuery()
    Write-Host "Columns IsCustomDefinition & CustomDefinition ensured on dbo.Items."

    $spSql = @"
    CREATE OR ALTER PROCEDURE dbo.sp_GetItemsForSearch
        @SearchQuery NVARCHAR(250)
    AS
    BEGIN
        SET NOCOUNT ON;
        
        SELECT TOP 20 
            No_ AS ItemCode,
            Description AS Description,
            [Description 2] AS DescriptionAr,
            [Storage Instructions] AS Category,
            [Incentive value] AS [Group],
            Color AS Subcategory,
            CASE 
                WHEN IsCustomDefinition = 1 AND ISNULL(CustomDefinition, '') <> '' THEN CustomDefinition 
                ELSE [Item Definition] 
            END AS ItemDefinition,
            IsCustomDefinition,
            CustomDefinition
        FROM dbo.Items WITH (NOLOCK)
        WHERE No_ LIKE '%' + @SearchQuery + '%'
           OR Description LIKE '%' + @SearchQuery + '%'
           OR [Description 2] LIKE '%' + @SearchQuery + '%';
    END;
"@
    $cmd.CommandText = $spSql
    $cmd.ExecuteNonQuery()
    Write-Host "sp_GetItemsForSearch updated successfully."

    $connection.Close()
} catch {
    Write-Host "Error executing DB update: $_"
}
