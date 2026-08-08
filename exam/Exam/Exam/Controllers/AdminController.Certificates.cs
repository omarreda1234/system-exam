using ClosedXML.Excel;
using Exam.Services;
using Exam.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using Exam.Hubs;
using System.Data;
using Exam.DTOs;
using System;
using Microsoft.AspNetCore.Http;

namespace Exam.Controllers
{
    public partial class AdminController
    {
        [HttpGet]
        public async Task<IActionResult> Certificates(int? waveId, int? examId = null, int? typeId = null, int? month = null, int? year = null)
        {
            var examTypes = (await _examService.GetAllExamTypesAsync())
                .Where(t => t.TypeName != null && t.TypeName.ToLower().Contains("wave"))
                .ToList();
            ViewBag.ExamTypes = examTypes;
            ViewBag.SelectedTypeId = typeId;
            ViewBag.SelectedMonth = month;
            ViewBag.SelectedYear = year;

            var allWaves = (await _examService.GetAllWavesAsync()).ToList();
            var waves = allWaves;

            // Apply year/month filters on Wave StartDate if provided
            if (year.HasValue && year.Value > 0)
            {
                waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Year == year.Value).ToList();
            }
            if (month.HasValue && month.Value > 0)
            {
                waves = waves.Where(w => w.StartDate.HasValue && w.StartDate.Value.Month == month.Value).ToList();
            }

            ViewBag.Waves = waves;

            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("DELETE FROM dbo.UserWaveCertificates WHERE Score IS NULL AND EmailSent = 0 AND CreatedAt >= CAST(GETDATE() AS DATE)");
            int selectedWaveId = waveId ?? 0;
            if (selectedWaveId == 0)
            {
                if (examId.HasValue && examId.Value > 0)
                {
                    // Try to find the WaveId from the examId
                    selectedWaveId = await conn.QueryFirstOrDefaultAsync<int>(
                        "SELECT WaveId FROM Exams WHERE Id = @ExamId",
                        new { ExamId = examId.Value });
                }

                if (selectedWaveId == 0)
                {
                    selectedWaveId = waves.FirstOrDefault()?.Id ?? 0;
                }
            }

            ViewBag.SelectedWaveId = selectedWaveId;

            if (selectedWaveId == -1 || selectedWaveId > 0)
            {
                var waveIds = selectedWaveId == -1 ? waves.Select(w => w.Id).ToList() : new List<int> { selectedWaveId };

                if (!waveIds.Any())
                {
                    ViewBag.ExamTitle = selectedWaveId == -1 ? "All Waves" : "No Wave";
                    return View(new List<Exam.DTOs.ExamResultRowDto>());
                }

                var fallbackSql = @"
                    WITH UserRoles AS (
                        SELECT UR.UserId,
                               MAX(CASE WHEN LOWER(R.Name) = 'pharmacist' OR R.Name LIKE N'%صيدل%' THEN 1 ELSE 0 END) as IsPharmacist,
                               MAX(CASE WHEN LOWER(R.Name) = 'assistant' OR R.Name LIKE N'%مساعد%' THEN 1 ELSE 0 END) as IsAssistant,
                               MAX(R.Name) as RoleName
                        FROM AspNetUserRoles UR
                        JOIN AspNetRoles R ON UR.RoleId = R.Id
                        GROUP BY UR.UserId
                    )
                    SELECT 
                        U.Id, 
                        ISNULL(U.FullName, U.UserName) as StudentName, 
                        U.Email as StudentEmail, 
                        N'Training Batch' as ExamName, 
                        N'Wave' as ExamType,
                        CASE 
                            WHEN UWC.CertificateCode IS NOT NULL THEN 'Completed'
                            ELSE 'Not Started' 
                        END as Status, 
                        ISNULL(UWC.Score, 0) as Score, 
                        CAST(0 AS DECIMAL(18,2)) as FinalScore, 
                        0 as DurationInMinutes, 
                        CASE WHEN UWC.CertificateCode IS NOT NULL THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END as IsPassed, 
                        UWC.CertificateCode as CertificateCode, 
                        ISNULL(UWC.EmailSent, 0) as EmailSent, 
                        0 as AttemptNumber, 
                        UWC.CreatedAt as CompletionDate, 
                        CAST(NULL AS INT) as AttemptId,
                        100 as TotalScoreAvailable,
                        W.StartDate as ActualStartTime, 
                        CAST(NULL AS DATETIME) as ActualEndTime, 
                        U.UserCode, 
                        B.BranchName, 
                        W.WaveName, 
                        COALESCE(UR.RoleName, 'User') as RoleName
                    FROM dbo.UserWaves UW
                    INNER JOIN AspNetUsers U ON UW.UserId = U.Id
                    INNER JOIN TrainingWaves W ON UW.WaveId = W.Id
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    LEFT JOIN UserRoles UR ON U.Id = UR.UserId
                    LEFT JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = UW.WaveId
                    WHERE UW.WaveId IN @WaveIds AND UW.IsActive = 1";

                var results = await conn.QueryAsync<Exam.DTOs.ExamResultRowDto>(fallbackSql, new { WaveIds = waveIds });
                
                if (User.IsInRole("Branch Manager") || User.IsInRole("Branch Supervisor"))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    if (currentUser != null && currentUser.BranchId.HasValue)
                    {
                        var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                            "SELECT BranchName FROM Branches WHERE Id = @Id", 
                            new { Id = currentUser.BranchId.Value });
                            
                        if (!string.IsNullOrEmpty(branchName))
                            results = results.Where(r => string.Equals(r.BranchName, branchName, StringComparison.OrdinalIgnoreCase));
                        else
                            results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                    }
                    else
                        results = Enumerable.Empty<Exam.DTOs.ExamResultRowDto>();
                }

                var resultList = results.ToList();
                foreach (var row in resultList)
                {
                    if (!string.IsNullOrWhiteSpace(row.CertificateCode) && row.CertificateCode.Trim() != "/")
                    {
                        string wName = row.WaveName ?? "";
                        DateTime wDate = row.ActualStartTime ?? DateTime.Now;
                        string yearStr = wDate.Year.ToString();
                        string waveNumStr = "001";
                        var digits = new string(wName.Where(char.IsDigit).ToArray());
                        if (!string.IsNullOrEmpty(digits))
                        {
                            waveNumStr = digits.PadLeft(3, '0');
                        }
                        else if (selectedWaveId > 0)
                        {
                            waveNumStr = selectedWaveId.ToString().PadLeft(3, '0');
                        }

                        var matchingWave = waves.FirstOrDefault(w => w.WaveName == row.WaveName || w.Id == selectedWaveId);
                        string modeStr = matchingWave?.Mode;
                        bool isOnline = matchingWave?.IsOnline ?? (wName.ToLower().Contains("online") || wName.Contains("أونلاين") || wName.Contains("اونلاين"));
                        string modeCode = ExtractModeForSerial(modeStr, isOnline);
                        string uCode = string.IsNullOrWhiteSpace(row.UserCode) ? "0000" : row.UserCode.Trim();

                        row.CertificateCode = $"WTTA-{yearStr}-{waveNumStr}-PB-{modeCode}-{uCode}";
                        row.Status = "Completed";
                        row.IsPassed = true;
                    }
                    else
                    {
                        row.CertificateCode = null;
                        row.Status = "Not Started";
                        row.IsPassed = false;
                    }
                }

                ViewBag.ExamTitle = selectedWaveId == -1 ? "All Waves" : (waves.FirstOrDefault(w => w.Id == selectedWaveId)?.WaveName ?? "No Wave");
                return View(resultList);
            }

            return View(Enumerable.Empty<Exam.DTOs.ExamResultRowDto>());
        }

        [HttpPost]
        public async Task<IActionResult> SendCertificates(int waveId, [FromBody] List<string> selectedIds = null)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                var resultsSql = @"
                    WITH UserRoles AS (
                        SELECT UR.UserId,
                               MAX(CASE WHEN LOWER(R.Name) = 'pharmacist' OR R.Name LIKE N'%صيدل%' THEN 1 ELSE 0 END) as IsPharmacist,
                               MAX(CASE WHEN LOWER(R.Name) = 'assistant' OR R.Name LIKE N'%مساعد%' THEN 1 ELSE 0 END) as IsAssistant,
                               MAX(R.Name) as RoleName
                        FROM AspNetUserRoles UR
                        JOIN AspNetRoles R ON UR.RoleId = R.Id
                        GROUP BY UR.UserId
                    )
                    SELECT 
                        U.Id, 
                        ISNULL(U.FullName, U.UserName) as StudentName, 
                        U.Email as StudentEmail, 
                        UWC.CertificateCode,
                        UWC.Score,
                        U.UserCode,
                        B.BranchName,
                        W.WaveName,
                        COALESCE(UR.RoleName, 'User') as RoleName,
                        W.StartDate as ActualStartTime
                    FROM dbo.UserWaves UW
                    INNER JOIN AspNetUsers U ON UW.UserId = U.Id
                    INNER JOIN TrainingWaves W ON UW.WaveId = W.Id
                    LEFT JOIN Branches B ON U.BranchId = B.Id
                    LEFT JOIN UserRoles UR ON U.Id = UR.UserId
                    LEFT JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = UW.WaveId
                    WHERE UW.WaveId = @WaveId AND UW.IsActive = 1";
                
                var results = (await conn.QueryAsync<dynamic>(resultsSql, new { WaveId = waveId })).ToList();

                if (User.IsInRole("Branch Manager") || User.IsInRole("Branch Supervisor"))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    if (currentUser != null && currentUser.BranchId.HasValue)
                    {
                        var branchName = await conn.QueryFirstOrDefaultAsync<string>(
                            "SELECT BranchName FROM Branches WHERE Id = @Id", 
                            new { Id = currentUser.BranchId.Value });
                            
                        if (!string.IsNullOrEmpty(branchName))
                        {
                            results = results.Where(r => string.Equals((string)r.BranchName, branchName, StringComparison.OrdinalIgnoreCase)).ToList();
                        }
                        else
                        {
                            results = new List<dynamic>();
                        }
                    }
                    else
                    {
                        results = new List<dynamic>();
                    }
                }

                if (selectedIds != null && selectedIds.Any())
                {
                    results = results.Where(r => selectedIds.Contains((string)r.Id)).ToList();
                }

                var trainees = results.ToList();
                if (!trainees.Any()) return Json(new { success = false, message = "لم يتم اختيار موظفين لإرسال الشهادات لهم." });

                int count = 0;

                string templatePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "certificate_template.png");

                if (!System.IO.File.Exists(templatePath))
                    return Json(new { success = false, message = "لم يتم العثور على تصميم الشهادة الافتراضي." });

                string courseType = "PB";

                foreach (var student in trainees)
                {
                    string code = student.CertificateCode;
                    if (string.IsNullOrEmpty(code))
                    {
                        string waveNum = "001";
                        var digits = new string(((string)(student.WaveName ?? "")).Where(char.IsDigit).ToArray());
                        if (!string.IsNullOrEmpty(digits)) waveNum = digits.PadLeft(3, '0');
                        
                        DateTime waveDate = student.ActualStartTime ?? DateTime.Now;
                        string yearStr = waveDate.Year.ToString();
                        string userCodeStr = student.UserCode ?? "0000";
                        
                        string roleAbbr = "PH";
                        string roleName = student.RoleName;
                        if (roleName != null && (roleName.ToLower().Contains("assistant") || roleName.Contains("مساعد")))
                        {
                            roleAbbr = "AS";
                        }
                        
                        code = $"WTTA-{yearStr}-{waveNum}-{courseType}-{roleAbbr}-{userCodeStr}";
                        
                        if (!string.IsNullOrEmpty((string)student.Id))
                        {
                            var certExists = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT Id FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                                new { UserId = (string)student.Id, WaveId = waveId });

                            if (certExists != null)
                            {
                                await conn.ExecuteAsync(@"
                                    UPDATE dbo.UserWaveCertificates 
                                    SET CertificateCode = @CertCode
                                    WHERE UserId = @UserId AND WaveId = @WaveId",
                                    new { CertCode = code, UserId = (string)student.Id, WaveId = waveId });
                            }
                            else
                            {
                                await conn.ExecuteAsync(@"
                                    INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, Score, CreatedAt)
                                    VALUES (@UserId, @WaveId, @CertCode, NULL, @CreatedAt)",
                                    new { UserId = (string)student.Id, WaveId = waveId, CertCode = code, CreatedAt = DateTime.Now });
                            }
                        }
                    }

                    using var ms = new MemoryStream();
                    using (var document = new PdfSharpCore.Pdf.PdfDocument())
                    {
                        var page = document.AddPage();
                        page.Orientation = PdfSharpCore.PageOrientation.Landscape;
                        using (var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page))
                        {
                            using (var image = PdfSharpCore.Drawing.XImage.FromFile(templatePath))
                            {
                                page.Width = image.PointWidth;
                                page.Height = image.PointHeight;
                                gfx.DrawImage(image, 0, 0, page.Width, page.Height);
                            }

                            var font = new PdfSharpCore.Drawing.XFont("Great Vibes", 72, PdfSharpCore.Drawing.XFontStyle.Regular);
                            gfx.DrawString((string)student.StudentName, font, PdfSharpCore.Drawing.XBrushes.Navy,
                                new PdfSharpCore.Drawing.XRect(0, page.Height * 0.40, page.Width, 120),
                                PdfSharpCore.Drawing.XStringFormats.Center);

                            var codeFont = new PdfSharpCore.Drawing.XFont("Arial", 12, PdfSharpCore.Drawing.XFontStyle.Bold);
                            gfx.DrawString($"Verification ID: {code}", codeFont, PdfSharpCore.Drawing.XBrushes.DimGray, 
                                new PdfSharpCore.Drawing.XRect(60, page.Height - 100, page.Width - 120, 40), 
                                PdfSharpCore.Drawing.XStringFormats.BottomLeft);
                        }
                        document.Save(ms, false);
                    }

                    byte[] pdfBytes = ms.ToArray();
                    string waveName = student.WaveName ?? "";
                    string subject = $"تهانينا! شهادة إتمام الدورة التدريبية {waveName}";
                    string htmlBody = $@"
                    <div dir='rtl' style='font-family: Arial, sans-serif; padding: 20px; color: #333;'>
                        <p>عزيزي <strong>{student.StudentName}</strong>،</p>
                        <p>تهانينا الحارة! لقد حصلت على شهادة إتمام الدورة التدريبية لـ <strong>{waveName}</strong> بنجاح وتفوق.</p>
                        <p>مرفق مع هذه الرسالة شهادة التخرج الخاصة بك كملف PDF.</p>
                        <p>كود الشهادة المرجعي الخاص بك هو: <strong>{code}</strong></p>
                        <br>
                        <p>مع خالص تمنياتنا بدوام النجاح والتوفيق،</p>
                        <p>Walid Tarshoubi Training Academy</p>
                    </div>
                ";

                    await _emailSender.SendEmailWithAttachmentAsync((string)student.StudentEmail, subject, htmlBody, pdfBytes, $"Certificate_{((string)student.StudentName).Replace(" ", "_")}.pdf");
                    
                    count++;
                }
                return Json(new { success = true, message = $"تم إرسال {count} شهادة بنجاح!" });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = "خطأ في السيرفر: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadCertificateTemplate(int examId, IFormFile templateFile)
        {
            if (templateFile == null || templateFile.Length == 0) return BadRequest("No file uploaded");

            var fileName = $"cert_template_{examId}_{Guid.NewGuid().ToString("N").Substring(0, 6)}{Path.GetExtension(templateFile.FileName)}";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "certs", fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await templateFile.CopyToAsync(stream);
            }

            var relativePath = $"/uploads/certs/{fileName}";
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync("UPDATE Exams SET CertificateTemplatePath = @Path WHERE Id = @Id", new { Path = relativePath, Id = examId });
            }

            return Json(new { success = true, path = relativePath });
        }

        [HttpPost]
        public async Task<IActionResult> UploadCertificatesOnlyExcel(IFormFile excelFile, int? examId = null, int? waveId = null)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "Please upload an Excel file." });
            }

            try
            {
                using var memoryStream = new MemoryStream();
                await excelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                using var workbook = new XLWorkbook(memoryStream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return Json(new { success = false, message = "Excel file has no worksheets." });

                var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var headerRow = 1;

                // Try to find the header row in the first 3 rows
                for (int r = 1; r <= 3; r++)
                {
                    headers.Clear();
                    for (int col = 1; col <= 20; col++)
                    {
                        var val = worksheet.Cell(r, col).Value.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(val) && !headers.ContainsKey(val)) headers[val] = col;
                    }
                    if (headers.ContainsKey("Code") || headers.ContainsKey("UserCode") || headers.ContainsKey("User Code") || 
                        headers.ContainsKey("الرمز") || headers.ContainsKey("الكود") || headers.ContainsKey("كود") || 
                        headers.ContainsKey("Certificate") || headers.ContainsKey("CertificateCode") || headers.ContainsKey("الشهادة") || 
                        headers.ContainsKey("كود الشهادة"))
                    {
                        headerRow = r;
                        break;
                    }
                }

                int? GetCol(params string[] possibleNames)
                {
                    foreach (var name in possibleNames)
                    {
                        if (headers.TryGetValue(name.Trim(), out var colIndex)) return colIndex;
                        foreach (var kvp in headers)
                        {
                            if (kvp.Key.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase) || name.Trim().Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                return kvp.Value;
                            }
                        }
                    }
                    return null;
                }

                var colUserCode = GetCol("Code", "UserCode", "User Code", "الكود", "كود", "كود الطالب");
                var colCertificateCode = GetCol("Certificate", "CertificateCode", "Certificate Code", "الشهادة", "كود الشهادة", "رقم الشهادة");
                var colScore = GetCol("Score", "الدرجة", "النسبة", "النسبه", "النسبة المئوية", "الدرجة المئوية", "درجة", "نسبة", "نسبه", "Score %", "Percentage");
                var colWaveName = GetCol("Wave", "الويف", "الدورة", "المجموعة", "WaveName", "Wave Name");
                var colStudentName = GetCol("Name", "FullName", "StudentName", "Student Name", "الاسم", "اسم الطالب", "الاسم بالكامل", "الاسم ثلاثي");
                var colEmail = GetCol("Email", "Mail", "الايميل", "البريد الالكتروني", "البريد الإلكتروني", "الميل");
                var colBranchName = GetCol("Branch", "BranchName", "الفرع", "فرع", "الفرع/المنطقة");
                var colRole = GetCol("Role", "RoleName", "الدور", "الوظيفة", "الوظيفه");

                if (colUserCode == null)
                {
                    return Json(new { 
                        success = false, 
                        message = "الملف المرفوع يجب أن يحتوي على عمود كود المستخدم (UserCode) على الأقل." 
                    });
                }

                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;
                int updatedCount = 0;
                var skippedCodes = new List<string>();

                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                for (int row = headerRow + 1; row <= lastRow; row++)
                {
                    var rawUserCode = worksheet.Cell(row, colUserCode.Value).Value.ToString()?.Trim();
                    var rawCertCode = colCertificateCode != null ? worksheet.Cell(row, colCertificateCode.Value).Value.ToString()?.Trim() : null;
                    var rawWaveName = colWaveName != null ? worksheet.Cell(row, colWaveName.Value).Value.ToString()?.Trim() : null;
                    var rawScore = colScore != null ? worksheet.Cell(row, colScore.Value).Value.ToString()?.Trim() : null;
                    var rawStudentName = colStudentName != null ? worksheet.Cell(row, colStudentName.Value).Value.ToString()?.Trim() : null;
                    var rawEmail = colEmail != null ? worksheet.Cell(row, colEmail.Value).Value.ToString()?.Trim() : null;
                    var rawBranchName = colBranchName != null ? worksheet.Cell(row, colBranchName.Value).Value.ToString()?.Trim() : null;
                    var rawRole = colRole != null ? worksheet.Cell(row, colRole.Value).Value.ToString()?.Trim() : null;

                    if (string.IsNullOrWhiteSpace(rawUserCode) && string.IsNullOrWhiteSpace(rawEmail)) continue;

                    if (!string.IsNullOrWhiteSpace(rawUserCode) && double.TryParse(rawUserCode, out var codeDouble))
                    {
                        rawUserCode = ((long)codeDouble).ToString();
                    }

                    decimal? parsedScore = null;
                    if (!string.IsNullOrWhiteSpace(rawScore))
                    {
                        var cleanScoreStr = rawScore.Replace("%", "").Trim();
                        if (decimal.TryParse(cleanScoreStr, out var sVal))
                        {
                            parsedScore = sVal;
                        }
                    }

                    ApplicationUser user = null;
                    if (!string.IsNullOrWhiteSpace(rawUserCode))
                    {
                        user = await _userManager.Users.FirstOrDefaultAsync(x => x.UserCode == rawUserCode);
                    }
                    if (user == null && !string.IsNullOrWhiteSpace(rawEmail))
                    {
                        user = await _userManager.FindByEmailAsync(rawEmail);
                    }

                    if (user == null)
                    {
                        if (string.IsNullOrWhiteSpace(rawEmail))
                        {
                            rawEmail = $"{rawUserCode}@eltarshouby.com";
                        }
                        
                        var displayName = rawStudentName;
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            displayName = $"Trainee {rawUserCode}";
                        }

                        int? resolvedBranchId = null;
                        if (!string.IsNullOrWhiteSpace(rawBranchName))
                        {
                            var dbBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT Id FROM dbo.Branches WHERE BranchName = @BranchName",
                                new { BranchName = rawBranchName });

                            if (dbBranchId == null)
                            {
                                dbBranchId = await conn.QueryFirstOrDefaultAsync<int>(@"
                                    INSERT INTO dbo.Branches (BranchName, BranchCode, IsActive)
                                    VALUES (@BranchName, @BranchName, 1);
                                    SELECT CAST(SCOPE_IDENTITY() as int);",
                                    new { BranchName = rawBranchName });
                            }
                            resolvedBranchId = dbBranchId;
                        }
                        else
                        {
                            // Auto-lookup branch from HR Employees
                            var hrLocation = await conn.QueryFirstOrDefaultAsync<string>(@"
                                SELECT TOP 1 E.LocationCode 
                                FROM [HR].dbo.Employees E 
                                WHERE (E.[No.] = @Code OR (TRY_CAST(E.[No.] as bigint) = TRY_CAST(@Code as bigint) AND @Code IS NOT NULL))
                                   OR REPLACE(E.SearchName, ' ', '') = REPLACE(@Name, ' ', '')",
                                new { Code = rawUserCode, Name = displayName });

                            if (!string.IsNullOrWhiteSpace(hrLocation))
                            {
                                var dbBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                                    "SELECT Id FROM dbo.Branches WHERE BranchName = @BranchName",
                                    new { BranchName = hrLocation.Trim() });

                                if (dbBranchId == null)
                                {
                                    dbBranchId = await conn.QueryFirstOrDefaultAsync<int>(@"
                                        INSERT INTO dbo.Branches (BranchName, BranchCode, IsActive)
                                        VALUES (@BranchName, @BranchName, 1);
                                        SELECT CAST(SCOPE_IDENTITY() as int);",
                                        new { BranchName = hrLocation.Trim() });
                                }
                                resolvedBranchId = dbBranchId;
                            }
                        }

                        var newUser = new ApplicationUser
                        {
                            UserName = rawEmail,
                            Email = rawEmail,
                            FullName = displayName,
                            UserCode = rawUserCode,
                            BranchId = resolvedBranchId,
                            IsActive = true,
                        };

                        var createResult = await _userManager.CreateAsync(newUser, "Test@123");
                        if (createResult.Succeeded)
                        {
                            string resolvedRole = "Pharmacist";
                            if (!string.IsNullOrWhiteSpace(rawRole))
                            {
                                var lowerRole = rawRole.ToLower();
                                if (lowerRole.Contains("pharmacist") || lowerRole.Contains("صيدل") || lowerRole.Contains("doctor"))
                                {
                                    resolvedRole = "Pharmacist";
                                }
                                else if (lowerRole.Contains("assistant") || lowerRole.Contains("مساعد"))
                                {
                                    resolvedRole = "Assistant";
                                }
                                else if (lowerRole.Contains("admin"))
                                {
                                    resolvedRole = "Admin";
                                }
                                else if (lowerRole.Contains("hr"))
                                {
                                    resolvedRole = "HR";
                                }
                            }
                            await _userManager.AddToRoleAsync(newUser, resolvedRole);
                            user = newUser;
                        }
                        else
                        {
                            var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                            skippedCodes.Add($"{rawUserCode} (فشل الإنشاء: {errors})");
                            continue;
                        }
                    }
                    else
                    {
                        bool needsUpdate = false;
                        if (!string.IsNullOrWhiteSpace(rawStudentName) && user.FullName != rawStudentName)
                        {
                            user.FullName = rawStudentName;
                            needsUpdate = true;
                        }
                        if (!string.IsNullOrWhiteSpace(rawBranchName))
                        {
                            var dbBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT Id FROM dbo.Branches WHERE BranchName = @BranchName",
                                new { BranchName = rawBranchName });

                            if (dbBranchId == null)
                            {
                                dbBranchId = await conn.QueryFirstOrDefaultAsync<int>(@"
                                    INSERT INTO dbo.Branches (BranchName, BranchCode, IsActive)
                                    VALUES (@BranchName, @BranchName, 1);
                                    SELECT CAST(SCOPE_IDENTITY() as int);",
                                    new { BranchName = rawBranchName });
                            }
                            if (user.BranchId != dbBranchId)
                            {
                                user.BranchId = dbBranchId;
                                needsUpdate = true;
                            }
                        }
                        else if (!user.BranchId.HasValue)
                        {
                            // Auto-lookup branch from HR Employees if currently null
                            var hrLocation = await conn.QueryFirstOrDefaultAsync<string>(@"
                                SELECT TOP 1 E.LocationCode 
                                FROM [HR].dbo.Employees E 
                                WHERE (E.[No.] = @Code OR (TRY_CAST(E.[No.] as bigint) = TRY_CAST(@Code as bigint) AND @Code IS NOT NULL))
                                   OR REPLACE(E.SearchName, ' ', '') = REPLACE(@Name, ' ', '')",
                                new { Code = user.UserCode, Name = user.FullName });

                            if (!string.IsNullOrWhiteSpace(hrLocation))
                            {
                                var dbBranchId = await conn.QueryFirstOrDefaultAsync<int?>(
                                    "SELECT Id FROM dbo.Branches WHERE BranchName = @BranchName",
                                    new { BranchName = hrLocation.Trim() });

                                if (dbBranchId == null)
                                {
                                    dbBranchId = await conn.QueryFirstOrDefaultAsync<int>(@"
                                        INSERT INTO dbo.Branches (BranchName, BranchCode, IsActive)
                                        VALUES (@BranchName, @BranchName, 1);
                                        SELECT CAST(SCOPE_IDENTITY() as int);",
                                        new { BranchName = hrLocation.Trim() });
                                }
                                user.BranchId = dbBranchId;
                                needsUpdate = true;
                            }
                        }

                        if (needsUpdate)
                        {
                            await _userManager.UpdateAsync(user);
                        }
                    }

                    if (user != null)
                    {
                        int resolvedWaveId = 0;
                        if (!string.IsNullOrWhiteSpace(rawWaveName))
                        {
                            var dbWaveId = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT Id FROM dbo.TrainingWaves WHERE WaveName = @WaveName",
                                new { WaveName = rawWaveName });

                            if (dbWaveId == null)
                            {
                                dbWaveId = await conn.QueryFirstOrDefaultAsync<int>(@"
                                    INSERT INTO dbo.TrainingWaves (WaveName, StartDate) 
                                    VALUES (@WaveName, @StartDate);
                                    SELECT CAST(SCOPE_IDENTITY() as int);",
                                    new { WaveName = rawWaveName, StartDate = DateTime.Now });
                            }
                            resolvedWaveId = dbWaveId ?? 0;
                        }

                        if (resolvedWaveId <= 0)
                        {
                            resolvedWaveId = waveId ?? 0;
                        }

                        if (resolvedWaveId <= 0 && examId.HasValue && examId.Value > 0)
                        {
                            resolvedWaveId = await conn.QueryFirstOrDefaultAsync<int>(
                                "SELECT WaveId FROM Exams WHERE Id = @ExamId",
                                new { ExamId = examId.Value });
                        }

                        if (resolvedWaveId <= 0)
                        {
                            resolvedWaveId = await conn.QueryFirstOrDefaultAsync<int>(
                                "SELECT TOP 1 Id FROM TrainingWaves ORDER BY StartDate DESC");
                        }

                        if (resolvedWaveId > 0)
                        {
                            var userWaveExists = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT WaveId FROM dbo.UserWaves WHERE UserId = @UserId AND WaveId = @WaveId",
                                new { UserId = user.Id, WaveId = resolvedWaveId });

                            if (userWaveExists == null)
                            {
                                await conn.ExecuteAsync(@"
                                    INSERT INTO dbo.UserWaves (UserId, WaveId, JoinDate, IsActive)
                                    VALUES (@UserId, @WaveId, @JoinDate, 1)",
                                    new { UserId = user.Id, WaveId = resolvedWaveId, JoinDate = DateTime.Now });
                            }
                            else
                            {
                                await conn.ExecuteAsync(@"
                                    UPDATE dbo.UserWaves 
                                    SET IsActive = 1 
                                    WHERE UserId = @UserId AND WaveId = @WaveId",
                                    new { UserId = user.Id, WaveId = resolvedWaveId });
                            }

                            var certExists = await conn.QueryFirstOrDefaultAsync<int?>(
                                "SELECT Id FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                                new { UserId = user.Id, WaveId = resolvedWaveId });

                            string finalCertCode = rawCertCode;
                            if (string.IsNullOrWhiteSpace(finalCertCode))
                            {
                                var waveInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                                    SELECT WaveName, StartDate, ISNULL(IsOnline, 0) AS IsOnline, Mode 
                                    FROM TrainingWaves 
                                    WHERE Id = @WaveId", new { WaveId = resolvedWaveId });

                                if (waveInfo != null)
                                {
                                    string wName = (string)waveInfo.WaveName ?? "";
                                    DateTime wDate = waveInfo.StartDate ?? DateTime.Now;
                                    string yearStr = wDate.Year.ToString();
                                    string waveNumStr = "001";
                                    var digits = new string(wName.Where(char.IsDigit).ToArray());
                                    if (!string.IsNullOrEmpty(digits))
                                    {
                                        waveNumStr = digits.PadLeft(3, '0');
                                    }
                                    else
                                    {
                                        waveNumStr = resolvedWaveId.ToString().PadLeft(3, '0');
                                    }

                                    bool isWOnline = waveInfo.IsOnline != null && Convert.ToBoolean(waveInfo.IsOnline);
                                    string wModeProp = waveInfo.Mode != null ? (string)waveInfo.Mode : null;
                                    string mCode = ExtractModeForSerial(wModeProp, isWOnline || wName.ToLower().Contains("online") || wName.Contains("أونلاين"));
                                    string uCode = string.IsNullOrWhiteSpace(user.UserCode) ? "0000" : user.UserCode.Trim();
                                    finalCertCode = $"WTTA-{yearStr}-{waveNumStr}-PB-{mCode}-{uCode}";
                                }
                            }

                            if (certExists != null)
                            {
                                await conn.ExecuteAsync(@"
                                    UPDATE dbo.UserWaveCertificates 
                                    SET CertificateCode = @CertCode, Score = @Score
                                    WHERE UserId = @UserId AND WaveId = @WaveId",
                                    new { CertCode = finalCertCode, Score = parsedScore, UserId = user.Id, WaveId = resolvedWaveId });
                            }
                            else
                            {
                                await conn.ExecuteAsync(@"
                                    INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, Score, CreatedAt)
                                    VALUES (@UserId, @WaveId, @CertCode, @Score, @CreatedAt)",
                                    new { UserId = user.Id, WaveId = resolvedWaveId, CertCode = finalCertCode, Score = parsedScore, CreatedAt = DateTime.Now });
                            }
                        }

                        updatedCount++;
                    }
                }

                var msg = $"تم معالجة وتحديث البيانات بنجاح لعدد {updatedCount} مستخدم.";
                if (skippedCodes.Any())
                {
                    msg += $" تم مواجهة مشكلات/تخطي لبعض الأكواد: {string.Join(", ", skippedCodes.Take(10))}";
                    if (skippedCodes.Count > 10) msg += $" (وآخرين...)";
                }

                return Json(new { success = true, message = msg });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء قراءة الملف: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UploadCertificatesPdfs(List<IFormFile> pdfFiles, int? waveId = null)
        {
            if (pdfFiles == null || !pdfFiles.Any())
            {
                return Json(new { success = false, message = "يرجى اختيار ملفات PDF للرفع." });
            }

            if (!waveId.HasValue || waveId.Value <= 0)
            {
                return Json(new { success = false, message = "يرجى تحديد الـ Wave أولاً قبل رفع الشهادات." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var waveInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT Id, WaveName, StartDate, ISNULL(IsOnline, 0) AS IsOnline, Mode
                    FROM TrainingWaves 
                    WHERE Id = @WaveId", new { WaveId = waveId.Value });

                if (waveInfo == null)
                {
                    return Json(new { success = false, message = "الـ Wave المحدد غير موجود بالنظام." });
                }

                string waveName = (string)waveInfo.WaveName ?? "";
                DateTime waveDate = waveInfo.StartDate ?? DateTime.Now;
                string yearStr = waveDate.Year.ToString();

                string waveNumStr = "001";
                var digits = new string(waveName.Where(char.IsDigit).ToArray());
                if (!string.IsNullOrEmpty(digits))
                {
                    waveNumStr = digits.PadLeft(3, '0');
                }
                else
                {
                    waveNumStr = waveId.Value.ToString().PadLeft(3, '0');
                }

                bool isWaveOnline = waveInfo.IsOnline != null && Convert.ToBoolean(waveInfo.IsOnline);
                string waveModeProp = waveInfo.Mode != null ? (string)waveInfo.Mode : null;
                string modeCode = ExtractModeForSerial(waveModeProp, isWaveOnline || waveName.ToLower().Contains("online") || waveName.Contains("أونلاين") || waveName.Contains("اونلاين"));

                string waveUploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "certificates", waveId.Value.ToString());
                if (!Directory.Exists(waveUploadDir))
                {
                    Directory.CreateDirectory(waveUploadDir);
                }

                int successCount = 0;
                int emailSentCount = 0;
                var skippedCodes = new List<string>();

                foreach (var pdf in pdfFiles)
                {
                    if (pdf == null || pdf.Length == 0) continue;

                    string fileName = Path.GetFileNameWithoutExtension(pdf.FileName).Trim();
                    if (!double.TryParse(fileName, out var codeDouble) && !long.TryParse(fileName, out _))
                    {
                        var codeMatch = System.Text.RegularExpressions.Regex.Match(fileName, @"\d+");
                        if (codeMatch.Success)
                        {
                            fileName = codeMatch.Value;
                        }
                    }
                    else if (double.TryParse(fileName, out codeDouble))
                    {
                        fileName = ((long)codeDouble).ToString();
                    }

                    var student = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT U.Id, ISNULL(U.FullName, U.UserName) as StudentName, U.Email, U.UserCode, UWC.CertificateCode
                        FROM AspNetUsers U
                        LEFT JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = @WaveId
                        WHERE U.UserCode = @UserCode", new { UserCode = fileName, WaveId = waveId.Value });

                    if (student == null)
                    {
                        student = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                            SELECT U.Id, ISNULL(U.FullName, U.UserName) as StudentName, U.Email, U.UserCode, UWC.CertificateCode
                            FROM AspNetUsers U
                            LEFT JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = @WaveId
                            WHERE U.UserName = @UserCode OR U.Email LIKE @EmailPattern",
                            new { UserCode = fileName, EmailPattern = $"{fileName}@%", WaveId = waveId.Value });
                    }

                    if (student == null)
                    {
                        skippedCodes.Add(fileName);
                        continue;
                    }

                    string studentId = (string)student.Id;
                    string studentEmail = (string)student.Email;
                    string studentName = (string)student.StudentName;
                    string certCode = (string)student.CertificateCode;

                    if (string.IsNullOrWhiteSpace(certCode))
                    {
                        certCode = $"WTTA-{yearStr}-{waveNumStr}-PB-{modeCode}-{fileName}";
                    }

                    string targetPath = Path.Combine(waveUploadDir, $"{fileName}.pdf");
                    using (var stream = new FileStream(targetPath, FileMode.Create))
                    {
                        await pdf.CopyToAsync(stream);
                    }

                    bool isEmailSent = false;
                    if (!string.IsNullOrWhiteSpace(studentEmail))
                    {
                        try
                        {
                            byte[] pdfBytes;
                            using (var ms = new MemoryStream())
                            {
                                await pdf.CopyToAsync(ms);
                                pdfBytes = ms.ToArray();
                            }
                            if (pdfBytes == null || pdfBytes.Length == 0)
                            {
                                pdfBytes = await System.IO.File.ReadAllBytesAsync(targetPath);
                            }

                            string subject = $"شهادة التخرج الرسمية - أكاديمية الطرشوبي ({waveName})";
                            string htmlBody = $@"
                                <div dir='rtl' style='font-family: Arial, sans-serif; padding: 25px; color: #1e293b; background-color: #f8fafc; border-radius: 16px;'>
                                    <div style='max-width: 600px; margin: 0 auto; background: #ffffff; padding: 30px; border-radius: 20px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1); border: 1px solid #e2e8f0;'>
                                        <h2 style='color: #059669; font-size: 22px; margin-bottom: 15px;'>تهانينا لك! 🎓</h2>
                                        <p style='font-size: 15px; line-height: 1.6;'>عزيزي/عزيزتي <strong>{studentName}</strong>،</p>
                                        <p style='font-size: 14px; line-height: 1.6; color: #475569;'>نحيطكم علماً بأنه قد تم رفع شهادة التخرج الرسمية الخاصة بكم لدورة <strong>{waveName}</strong> بنجاح. تجدون نسخة الشهادة بصيغة (PDF) مرفقة مع هذا البريد.</p>
                                        
                                        <div style='margin: 20px 0; padding: 15px; background: #ecfdf5; border: 1px border #a7f3d0; border-radius: 12px;'>
                                            <p style='margin: 0; font-size: 12px; color: #047857; font-weight: bold;'>كود الشهادة التسلسلي (Serial Code):</p>
                                            <p style='margin: 5px 0 0 0; font-family: monospace; font-size: 16px; font-weight: bold; color: #065f46; letter-spacing: 1px;'>{certCode}</p>
                                        </div>

                                        <p style='font-size: 13px; color: #64748b; margin-top: 25px;'>مع أطيب التمنيات لكم بدوام التوفيق والنجاح،<br/><strong>أكاديمية الطرشوبي</strong></p>
                                    </div>
                                </div>";

                            await _emailSender.SendEmailWithAttachmentAsync(studentEmail, subject, htmlBody, pdfBytes, $"{fileName}_Certificate.pdf");
                            isEmailSent = true;
                            emailSentCount++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error sending email to {studentEmail}: {ex.Message}");
                        }
                    }

                    var userWaveExists = await conn.QueryFirstOrDefaultAsync<int?>(
                        "SELECT 1 FROM dbo.UserWaves WHERE UserId = @UserId AND WaveId = @WaveId",
                        new { UserId = studentId, WaveId = waveId.Value });

                    if (userWaveExists == null)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.UserWaves (UserId, WaveId, JoinDate, IsActive)
                            VALUES (@UserId, @WaveId, GETDATE(), 1)",
                            new { UserId = studentId, WaveId = waveId.Value });
                    }

                    var certExists = await conn.QueryFirstOrDefaultAsync<int?>(
                        "SELECT Id FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                        new { UserId = studentId, WaveId = waveId.Value });

                    if (certExists != null)
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.UserWaveCertificates 
                            SET CertificateCode = @CertCode, EmailSent = CASE WHEN @IsSent = 1 THEN 1 ELSE EmailSent END, CreatedAt = GETDATE()
                            WHERE UserId = @UserId AND WaveId = @WaveId",
                            new { CertCode = certCode, IsSent = isEmailSent ? 1 : 0, UserId = studentId, WaveId = waveId.Value });
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, EmailSent, Score, CreatedAt)
                            VALUES (@UserId, @WaveId, @CertCode, @EmailSent, NULL, GETDATE())",
                            new { UserId = studentId, WaveId = waveId.Value, CertCode = certCode, EmailSent = isEmailSent ? 1 : 0 });
                    }

                    successCount++;
                }

                string msg = $"تم حفظ الشهادات وتوليد الأكواد بنجاح لعدد {successCount} طالب (وتم إرسال {emailSentCount} بريد إلكتروني).";
                if (skippedCodes.Any())
                {
                    msg += $" تعذر العثور على المستخدمين للأكواد التالية: {string.Join(", ", skippedCodes.Take(10))}";
                    if (skippedCodes.Count > 10) msg += " (وآخرين...)";
                }

                return Json(new { 
                    success = true, 
                    message = msg,
                    successCount = successCount,
                    emailSentCount = emailSentCount,
                    skippedCount = skippedCodes.Count,
                    skippedCodes = skippedCodes
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء معالجة ملفات الشهادات: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ResendCertificateEmail(string userId, int waveId, IFormFile? pdfFile)
        {
            if (string.IsNullOrEmpty(userId) || waveId <= 0)
            {
                return Json(new { success = false, message = "بيانات غير صالحة." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var user = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT U.Id, ISNULL(U.FullName, U.UserName) as StudentName, U.Email, U.UserCode, UWC.CertificateCode, W.WaveName, W.StartDate, ISNULL(W.IsOnline, 0) AS IsOnline, W.Mode
                    FROM AspNetUsers U
                    LEFT JOIN dbo.UserWaveCertificates UWC ON U.Id = UWC.UserId AND UWC.WaveId = @WaveId
                    JOIN TrainingWaves W ON W.Id = @WaveId
                    WHERE U.Id = @UserId", new { UserId = userId, WaveId = waveId });

                if (user == null)
                {
                    return Json(new { success = false, message = "لم يتم العثور على بيانات المستخدم أو الـ Wave." });
                }

                string userEmail = user.Email;
                if (string.IsNullOrWhiteSpace(userEmail))
                {
                    return Json(new { success = false, message = "هذا المستخدم ليس لديه بريد إلكتروني مسجل." });
                }

                string userCodeStr = (string)user.UserCode ?? "";
                string certCode = (string)user.CertificateCode;
                string waveName = (string)user.WaveName ?? "";

                if (string.IsNullOrWhiteSpace(certCode))
                {
                    string waveNumStr = "001";
                    var digits = new string(waveName.Where(char.IsDigit).ToArray());
                    if (!string.IsNullOrEmpty(digits))
                    {
                        waveNumStr = digits.PadLeft(3, '0');
                    }
                    else
                    {
                        waveNumStr = waveId.ToString().PadLeft(3, '0');
                    }

                    DateTime waveDate = user.StartDate ?? DateTime.Now;
                    string yearStr = waveDate.Year.ToString();
                    bool isWaveOnline = user.IsOnline != null && Convert.ToBoolean(user.IsOnline);
                    string userModeProp = user.Mode != null ? (string)user.Mode : null;
                    string modeCode = ExtractModeForSerial(userModeProp, isWaveOnline || waveName.ToLower().Contains("online") || waveName.Contains("أونلاين") || waveName.Contains("اونلاين"));

                    certCode = $"WTTA-{yearStr}-{waveNumStr}-PB-{modeCode}-{userCodeStr}";
                }

                string waveUploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "certificates", waveId.ToString());
                if (!Directory.Exists(waveUploadDir))
                {
                    Directory.CreateDirectory(waveUploadDir);
                }

                string filePath = Path.Combine(waveUploadDir, $"{userCodeStr}.pdf");

                if (pdfFile != null && pdfFile.Length > 0)
                {
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await pdfFile.CopyToAsync(stream);
                    }
                }

                if (!System.IO.File.Exists(filePath))
                {
                    return Json(new { success = false, message = $"ملف الـ PDF غير موجود للمستخدم ({userCodeStr}). يرجى رفع ملف PDF لهذا الطالب أولاً أو اختياره من نافذة الإرسال." });
                }

                byte[] pdfBytes = await System.IO.File.ReadAllBytesAsync(filePath);
                string emailSubject = $"شهادة التخرج الرسمية - أكاديمية الطرشوبي ({waveName})";
                string emailBody = $@"
                    <div dir='rtl' style='font-family: Arial, sans-serif; padding: 25px; color: #1e293b; background-color: #f8fafc; border-radius: 16px;'>
                        <div style='max-width: 600px; margin: 0 auto; background: #ffffff; padding: 30px; border-radius: 20px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1); border: 1px solid #e2e8f0;'>
                            <h2 style='color: #059669; font-size: 22px; margin-bottom: 15px;'>تهانينا لك! 🎓</h2>
                            <p style='font-size: 15px; line-height: 1.6;'>عزيزي/عزيزتي <strong>{user.StudentName}</strong>،</p>
                            <p style='font-size: 14px; line-height: 1.6; color: #475569;'>نحيطكم علماً بأنه قد تم إرسال شهادة التخرج الرسمية الخاصة بكم لدورة <strong>{waveName}</strong> بنجاح. تجدون نسخة الشهادة بصيغة (PDF) مرفقة مع هذا البريد.</p>
                            
                            <div style='margin: 20px 0; padding: 15px; background: #ecfdf5; border: 1px border #a7f3d0; border-radius: 12px;'>
                                <p style='margin: 0; font-size: 12px; color: #047857; font-weight: bold;'>كود الشهادة التسلسلي (Serial Code):</p>
                                <p style='margin: 5px 0 0 0; font-family: monospace; font-size: 16px; font-weight: bold; color: #065f46; letter-spacing: 1px;'>{certCode}</p>
                            </div>

                            <p style='font-size: 13px; color: #64748b; margin-top: 25px;'>مع أطيب التمنيات لكم بدوام التوفيق والنجاح،<br/><strong>أكاديمية الطرشوبي</strong></p>
                        </div>
                    </div>";

                await _emailSender.SendEmailWithAttachmentAsync(userEmail, emailSubject, emailBody, pdfBytes, $"{userCodeStr}_Certificate.pdf");

                var uwExists = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT 1 FROM dbo.UserWaves WHERE UserId = @UserId AND WaveId = @WaveId",
                    new { UserId = userId, WaveId = waveId });

                if (uwExists == null)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.UserWaves (UserId, WaveId, JoinDate, IsActive)
                        VALUES (@UserId, @WaveId, GETDATE(), 1)",
                        new { UserId = userId, WaveId = waveId });
                }

                var certExists = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT Id FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                    new { UserId = userId, WaveId = waveId });

                if (certExists != null)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.UserWaveCertificates 
                        SET CertificateCode = @CertCode, EmailSent = 1, CreatedAt = GETDATE()
                        WHERE UserId = @UserId AND WaveId = @WaveId",
                        new { CertCode = certCode, UserId = userId, WaveId = waveId });
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, EmailSent, Score, CreatedAt)
                        VALUES (@UserId, @WaveId, @CertCode, 1, NULL, GETDATE())",
                        new { UserId = userId, WaveId = waveId, CertCode = certCode });
                }

                return Json(new { success = true, message = $"تم إرسال البريد الإلكتروني بنجاح لحساب {userEmail} وتوليد/حفظ كود الشهادة ({certCode}) في النظام." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "فشل إرسال البريد الإلكتروني: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> MoveUserToWave(string userId, int oldWaveId, int newWaveId)
        {
            if (string.IsNullOrEmpty(userId) || oldWaveId <= 0 || newWaveId <= 0)
            {
                return Json(new { success = false, message = "بيانات غير صالحة." });
            }

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                await conn.ExecuteAsync(@"
                    UPDATE dbo.UserWaves 
                    SET IsActive = 0, IsDeactivated = 1 
                    WHERE UserId = @UserId AND WaveId = @OldWaveId",
                    new { UserId = userId, OldWaveId = oldWaveId });

                var siteLink = "http://41.33.149.186:5208";
                await _examService.AssignUsersToWaveAsync(newWaveId, new List<string> { userId }, siteLink);

                return Json(new { success = true, message = "تم تحويل المستخدم للويف الجديدة بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء التحويل: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateCertificateCode(string userId, int waveId, string certificateCode)
        {
            if (string.IsNullOrEmpty(userId) || waveId <= 0)
            {
                return Json(new { success = false, message = "بيانات المستخدم أو الويف غير صحيحة." });
            }

            try
            {
                var cleanCode = (certificateCode ?? "").Trim();
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                var certExists = await conn.QueryFirstOrDefaultAsync<int?>(
                    "SELECT Id FROM dbo.UserWaveCertificates WHERE UserId = @UserId AND WaveId = @WaveId",
                    new { UserId = userId, WaveId = waveId });

                if (certExists != null)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.UserWaveCertificates 
                        SET CertificateCode = @CertCode
                        WHERE UserId = @UserId AND WaveId = @WaveId",
                        new { CertCode = cleanCode, UserId = userId, WaveId = waveId });
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.UserWaveCertificates (UserId, WaveId, CertificateCode, EmailSent, Score, CreatedAt)
                        VALUES (@UserId, @WaveId, @CertCode, 0, NULL, GETDATE())",
                        new { UserId = userId, WaveId = waveId, CertCode = cleanCode });
                }

                return Json(new { success = true, message = "تم تعديل وترحيل كود الشهادة (Serial Code) بنجاح.", newCode = cleanCode });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء تعديل كود الشهادة: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateWaveSerialFormat(int waveId, string mode, bool updateExistingCertificates = true)
        {
            if (waveId <= 0 || string.IsNullOrWhiteSpace(mode))
            {
                return Json(new { success = false, message = "بيانات الـ Wave أو الـ Mode غير صالحة." });
            }

            try
            {
                var cleanMode = mode.Trim();
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                await conn.ExecuteAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TrainingWaves') AND name = 'Mode')
                    BEGIN
                        ALTER TABLE dbo.TrainingWaves ADD Mode NVARCHAR(100) NULL;
                    END");

                bool isOnline = cleanMode.Equals("Online", StringComparison.OrdinalIgnoreCase);
                await conn.ExecuteAsync(@"
                    UPDATE dbo.TrainingWaves
                    SET Mode = @Mode, IsOnline = @IsOnline
                    WHERE Id = @WaveId",
                    new { Mode = cleanMode, IsOnline = isOnline ? 1 : 0, WaveId = waveId });

                int updatedCount = 0;
                if (updateExistingCertificates)
                {
                    var waveInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT WaveName, StartDate FROM dbo.TrainingWaves WHERE Id = @WaveId", new { WaveId = waveId });

                    if (waveInfo != null)
                    {
                        string waveName = (string)waveInfo.WaveName ?? "";
                        DateTime waveDate = waveInfo.StartDate ?? DateTime.Now;
                        string yearStr = waveDate.Year.ToString();

                        string waveNumStr = "001";
                        var digits = new string(waveName.Where(char.IsDigit).ToArray());
                        if (!string.IsNullOrEmpty(digits))
                        {
                            waveNumStr = digits.PadLeft(3, '0');
                        }
                        else
                        {
                            waveNumStr = waveId.ToString().PadLeft(3, '0');
                        }

                        var certUsers = await conn.QueryAsync<dynamic>(@"
                            SELECT UWC.UserId, U.UserCode
                            FROM dbo.UserWaveCertificates UWC
                            INNER JOIN dbo.AspNetUsers U ON UWC.UserId = U.Id
                            WHERE UWC.WaveId = @WaveId", new { WaveId = waveId });

                        string serialMode = ExtractModeForSerial(cleanMode, false);
                        foreach (var u in certUsers)
                        {
                            string uid = (string)u.UserId;
                            string userCodeStr = (string)u.UserCode ?? "0000";
                            string newCertCode = $"WTTA-{yearStr}-{waveNumStr}-PB-{serialMode}-{userCodeStr}";

                            await conn.ExecuteAsync(@"
                                UPDATE dbo.UserWaveCertificates
                                SET CertificateCode = @CertCode
                                WHERE UserId = @UserId AND WaveId = @WaveId",
                                new { CertCode = newCertCode, UserId = uid, WaveId = waveId });
                            updatedCount++;
                        }
                    }
                }

                return Json(new { success = true, message = $"تم تحديث نمط سيريال الويف إلى ({cleanMode}) وتطبيق التعديل على {updatedCount} شهادة بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء تحديث سيريال الويف: " + ex.Message });
            }
        }

        private static string ExtractModeForSerial(string? mode, bool isOnlineFallback = false)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return isOnlineFallback ? "ON" : "Off";
            }

            mode = mode.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(mode, @"\(([^)]+)\)");
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                return match.Groups[1].Value.Trim();
            }

            if (mode.Equals("Online", StringComparison.OrdinalIgnoreCase)) return "ON";
            if (mode.Equals("Offline", StringComparison.OrdinalIgnoreCase)) return "Off";
            if (mode.Equals("Onsite", StringComparison.OrdinalIgnoreCase)) return "ONS";
            if (mode.Equals("Hybrid", StringComparison.OrdinalIgnoreCase)) return "HYB";
            if (mode.Equals("VIP", StringComparison.OrdinalIgnoreCase)) return "VIP";

            return mode;
        }
    }
}
