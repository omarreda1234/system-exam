USE [Eltarshouby-Exam]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER PROCEDURE [dbo].[sp_Admin_DeactivateUser]
    @UserId NVARCHAR(450)
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM [dbo].[AspNetUsers] WHERE [Id] = @UserId)
    BEGIN
        UPDATE [dbo].[AspNetUsers]
        SET [IsActive] = 0
        WHERE [Id] = @UserId;
    END
END
GO

ALTER PROCEDURE [dbo].[sp_Admin_GetAllUsersWithRoles]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
	    U.Id,
        U.[UserName],
        U.[Email],
        U.[PhoneNumber] AS [Phone],
        U.[UserCode] AS [Code],           
        B.[BranchName],
		S.StartTime ,
		S.EndTime,
		S.ShiftName,
        U.[CertificateCode],
		U.IsActive,
        R.[Name] AS [RoleName]            
    FROM [dbo].[AspNetUsers] U
    LEFT JOIN [dbo].[AspNetUserRoles] UR ON U.[Id] = UR.[UserId]
    LEFT JOIN [dbo].[AspNetRoles] R ON UR.[RoleId] = R.[Id]
	left join [dbo].Shifts as S on S.Id = U.ShiftId
    LEFT JOIN [dbo].[Branches] B ON U.[BranchId] = B.[Id]
    
    ORDER BY B.[BranchName], R.[Name], U.[UserName];
END
GO

-- Branches list for admin UI / Excel matching (use BranchCode — do not reference deprecated Location column)
CREATE OR ALTER PROCEDURE [dbo].[sp_Admin_GetAllBranches]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        B.[Id],
        B.[BranchName],
        B.[BranchCode],
        B.[IsActive]
    FROM [dbo].[Branches] B
    ORDER BY B.[BranchName];
END
GO
