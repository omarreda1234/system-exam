using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Exam.Controllers
{
    public partial class AdminController
    {
        [HttpGet]
        public async Task<IActionResult> NominationHistory(
            string? search, 
            string? actionType, 
            string? entityType, 
            string? branch, 
            DateTime? date, 
            DateTime? fromDate, 
            DateTime? toDate,
            int page = 1,
            int pageSize = 30)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1. Overall stats
            var stats = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    COUNT(1) AS TotalCount,
                    SUM(CASE WHEN Action = 'Approved' THEN 1 ELSE 0 END) AS ApprovedCount,
                    SUM(CASE WHEN Action = 'Rejected' THEN 1 ELSE 0 END) AS RejectedCount,
                    SUM(CASE WHEN Action = 'Replaced' THEN 1 ELSE 0 END) AS ReplacedCount,
                    SUM(CASE WHEN Action IN ('Pending', 'Nominated') THEN 1 ELSE 0 END) AS PendingCount
                FROM dbo.NominationAuditLogs");

            // 2. Branches list for filtering
            var branches = (await conn.QueryAsync<string>(@"
                SELECT DISTINCT BranchName 
                FROM dbo.NominationAuditLogs 
                WHERE BranchName IS NOT NULL AND BranchName <> '' 
                ORDER BY BranchName")).ToList();

            // 3. Build dynamic query for audit logs
            var conditions = new List<string>();
            var p = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(TraineeName LIKE @Search OR TraineeCode LIKE @Search OR EntityName LIKE @Search OR CompanyName LIKE @Search OR ActionByUserName LIKE @Search OR ReplacementTraineeName LIKE @Search)");
                p.Add("Search", $"%{search.Trim()}%");
            }

            if (!string.IsNullOrWhiteSpace(actionType) && actionType != "All")
            {
                if (actionType == "Pending")
                {
                    conditions.Add("Action IN ('Pending', 'Nominated')");
                }
                else
                {
                    conditions.Add("Action = @ActionType");
                    p.Add("ActionType", actionType);
                }
            }

            if (!string.IsNullOrWhiteSpace(entityType) && entityType != "All")
            {
                conditions.Add("EntityType = @EntityType");
                p.Add("EntityType", entityType);
            }

            if (!string.IsNullOrWhiteSpace(branch) && branch != "All")
            {
                conditions.Add("BranchName = @Branch");
                p.Add("Branch", branch);
            }

            // Filter by specific day ("يوم كذا")
            if (date.HasValue)
            {
                conditions.Add("CAST(ActionDate AS DATE) = @SpecificDate");
                p.Add("SpecificDate", date.Value.Date);
            }
            else
            {
                if (fromDate.HasValue)
                {
                    conditions.Add("CAST(ActionDate AS DATE) >= @FromDate");
                    p.Add("FromDate", fromDate.Value.Date);
                }
                if (toDate.HasValue)
                {
                    conditions.Add("CAST(ActionDate AS DATE) <= @ToDate");
                    p.Add("ToDate", toDate.Value.Date);
                }
            }

            string whereClause = conditions.Any() ? "WHERE " + string.Join(" AND ", conditions) : "";

            // Total filtered count
            string countSql = $"SELECT COUNT(1) FROM dbo.NominationAuditLogs {whereClause}";
            int totalFiltered = await conn.ExecuteScalarAsync<int>(countSql, p);

            // Paged results
            int offset = Math.Max(0, (page - 1) * pageSize);
            p.Add("Offset", offset);
            p.Add("PageSize", pageSize);

            string querySql = $@"
                SELECT 
                    Id, EntityType, EntityId, EntityName, CompanyName,
                    TraineeId, TraineeName, TraineeCode, TraineeRole,
                    BranchId, BranchName, Action, ActionReason,
                    ReplacementTraineeId, ReplacementTraineeName,
                    ActionByUserId, ActionByUserName, ActionByUserRole, ActionDate
                FROM dbo.NominationAuditLogs
                {whereClause}
                ORDER BY ActionDate DESC, Id DESC
                OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

            var items = (await conn.QueryAsync<dynamic>(querySql, p)).ToList();

            ViewBag.Stats = stats;
            ViewBag.Branches = branches;
            ViewBag.Search = search;
            ViewBag.ActionType = actionType ?? "All";
            ViewBag.EntityType = entityType ?? "All";
            ViewBag.Branch = branch ?? "All";
            ViewBag.Date = date?.ToString("yyyy-MM-dd");
            ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");
            ViewBag.Page = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalCount = totalFiltered;
            ViewBag.TotalPages = (int)Math.Ceiling((double)totalFiltered / pageSize);

            return View(items);
        }

        [HttpGet]
        public async Task<IActionResult> ExportNominationHistoryExcel(
            string? search, 
            string? actionType, 
            string? entityType, 
            string? branch, 
            DateTime? date, 
            DateTime? fromDate, 
            DateTime? toDate)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var conditions = new List<string>();
            var p = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(TraineeName LIKE @Search OR TraineeCode LIKE @Search OR EntityName LIKE @Search OR CompanyName LIKE @Search OR ActionByUserName LIKE @Search OR ReplacementTraineeName LIKE @Search)");
                p.Add("Search", $"%{search.Trim()}%");
            }

            if (!string.IsNullOrWhiteSpace(actionType) && actionType != "All")
            {
                if (actionType == "Pending")
                    conditions.Add("Action IN ('Pending', 'Nominated')");
                else
                {
                    conditions.Add("Action = @ActionType");
                    p.Add("ActionType", actionType);
                }
            }

            if (!string.IsNullOrWhiteSpace(entityType) && entityType != "All")
            {
                conditions.Add("EntityType = @EntityType");
                p.Add("EntityType", entityType);
            }

            if (!string.IsNullOrWhiteSpace(branch) && branch != "All")
            {
                conditions.Add("BranchName = @Branch");
                p.Add("Branch", branch);
            }

            if (date.HasValue)
            {
                conditions.Add("CAST(ActionDate AS DATE) = @SpecificDate");
                p.Add("SpecificDate", date.Value.Date);
            }
            else
            {
                if (fromDate.HasValue)
                {
                    conditions.Add("CAST(ActionDate AS DATE) >= @FromDate");
                    p.Add("FromDate", fromDate.Value.Date);
                }
                if (toDate.HasValue)
                {
                    conditions.Add("CAST(ActionDate AS DATE) <= @ToDate");
                    p.Add("ToDate", toDate.Value.Date);
                }
            }

            string whereClause = conditions.Any() ? "WHERE " + string.Join(" AND ", conditions) : "";

            string sql = $@"
                SELECT TOP 5000
                    Id, EntityType, EntityName, CompanyName,
                    TraineeName, TraineeCode, TraineeRole,
                    BranchName, Action, ActionReason,
                    ReplacementTraineeName, ActionByUserName, ActionByUserRole, ActionDate
                FROM dbo.NominationAuditLogs
                {whereClause}
                ORDER BY ActionDate DESC, Id DESC";

            var rows = (await conn.QueryAsync<dynamic>(sql, p)).ToList();

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("سجل الترشيحات والاعتمادات");
            ws.RightToLeft = true;

            // Headers
            string[] headers = new[]
            {
                "#", "نوع التدريب", "الدورة / الجلسة", "الشركة",
                "اسم المتدرب", "الكود", "الوظيفة", "الفرع",
                "القرار / الإجراء", "السبب / ملاحظات", "المتدرب البديل",
                "متخذ الإجراء", "الصفة / الدور", "تاريخ وتوقيت العملية"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#01443c");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int rowIdx = 2;
            foreach (var r in rows)
            {
                string entityDisplay = (string)r.EntityType == "Wave" ? "دورة ويف (Wave)" : "جلسة شركة (Company Session)";
                string actionDisplay = (string)r.Action switch
                {
                    "Approved" => "تم الاعتماد ✅",
                    "Rejected" => "تم الرفض / اعتذار ❌",
                    "Replaced" => "تم الاستبدال 🔄",
                    "Pending" => "بانتظار الاعتماد ⏳",
                    "Nominated" => "تم الترشيح 📋",
                    _ => (string)r.Action
                };

                ws.Cell(rowIdx, 1).SetValue(r.Id);
                ws.Cell(rowIdx, 2).SetValue(entityDisplay);
                ws.Cell(rowIdx, 3).SetValue((string)(r.EntityName ?? "--"));
                ws.Cell(rowIdx, 4).SetValue((string)(r.CompanyName ?? "--"));
                ws.Cell(rowIdx, 5).SetValue((string)(r.TraineeName ?? "--"));
                ws.Cell(rowIdx, 6).SetValue((string)(r.TraineeCode ?? "--"));
                ws.Cell(rowIdx, 7).SetValue((string)(r.TraineeRole ?? "--"));
                ws.Cell(rowIdx, 8).SetValue((string)(r.BranchName ?? "--"));
                ws.Cell(rowIdx, 9).SetValue(actionDisplay);
                ws.Cell(rowIdx, 10).SetValue((string)(r.ActionReason ?? "--"));
                ws.Cell(rowIdx, 11).SetValue((string)(r.ReplacementTraineeName ?? "--"));
                ws.Cell(rowIdx, 12).SetValue((string)(r.ActionByUserName ?? "النظام"));
                ws.Cell(rowIdx, 13).SetValue((string)(r.ActionByUserRole ?? "Branch Manager"));
                ws.Cell(rowIdx, 14).SetValue(((DateTime)r.ActionDate).ToString("yyyy-MM-dd HH:mm:ss"));

                // Highlighting based on action
                if ((string)r.Action == "Approved")
                    ws.Cell(rowIdx, 9).Style.Font.FontColor = XLColor.FromHtml("#16a34a");
                else if ((string)r.Action == "Rejected")
                    ws.Cell(rowIdx, 9).Style.Font.FontColor = XLColor.FromHtml("#dc2626");
                else if ((string)r.Action == "Replaced")
                    ws.Cell(rowIdx, 9).Style.Font.FontColor = XLColor.FromHtml("#4f46e5");

                rowIdx++;
            }

            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var content = stream.ToArray();
            string fileName = $"Nomination_Audit_History_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";

            return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
    }
}
