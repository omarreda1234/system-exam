ALTER TABLE Categories ADD ExamTypeId INT NULL;
GO
ALTER TABLE Categories ADD CONSTRAINT FK_Categories_ExamTypes FOREIGN KEY (ExamTypeId) REFERENCES ExamTypes(Id);
GO
ALTER PROCEDURE sp_GetAllCategories @ExamTypeId INT = NULL AS BEGIN SET NOCOUNT ON; SELECT C.Id, C.CategoryName, C.ExamTypeId, ET.TypeName AS ExamTypeName FROM Categories C LEFT JOIN ExamTypes ET ON C.ExamTypeId = ET.Id WHERE (@ExamTypeId IS NULL OR C.ExamTypeId = @ExamTypeId) ORDER BY C.CategoryName END;
GO
