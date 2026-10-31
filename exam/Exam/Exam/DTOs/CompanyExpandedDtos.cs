using System;
using System.Collections.Generic;

namespace Exam.DTOs
{
    public class CompanyProductDto
    {
        public int Id { get; set; }
        public int CompanyId { get; set; }
        public string ProductName { get; set; } = "";
        public string? GenericName { get; set; }
        public string? Category { get; set; } // Medicine, Cosmetics, Supplement, Medical Device, etc.
        public string? DosageForm { get; set; } // Tablet, Syrup, Cream, Serum, Injection, etc.
        public decimal? Price { get; set; }
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class DepartmentQuotaItem
    {
        public int DepartmentId { get; set; }
        public string DepartmentName { get; set; } = "";
        public int Quota { get; set; }
    }

    public class CompanyInjectionRuleDto
    {
        public int Id { get; set; }
        public int? CompanyId { get; set; }
        public string RuleName { get; set; } = "";
        public string DepartmentQuotasJson { get; set; } = "[]";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    public class InjectionRulePreviewRequest
    {
        public int SessionId { get; set; }
        public int? CompanyId { get; set; }
        public List<DepartmentQuotaItem>? DepartmentQuotas { get; set; } = new();

        public string? Mode { get; set; } // "branchStaff", "departments", or "combined"
        public int PharmacistQuota { get; set; } = 0;
        public int AssistantQuota { get; set; } = 0;
        public List<int>? BranchIds { get; set; }
        public List<int>? ShiftIds { get; set; }
    }

    public class InjectionCandidatePreviewDto
    {
        public string CandidateKey { get; set; } = ""; // e.g. U_{UserId}
        public string UserId { get; set; } = "";
        public string FullName { get; set; } = "";
        public string UserCode { get; set; } = "--";
        public string JobTitle { get; set; } = "--";
        public string BranchName { get; set; } = "بدون فرع";
        public string ShiftName { get; set; } = "غير محدد";
        public int DepartmentId { get; set; }
        public string DepartmentName { get; set; } = "--";
        public int PreviousAttendedCount { get; set; } = 0;
        public DateTime? LastAttendedDate { get; set; }
        public bool IsSelected { get; set; } = true;
    }

    public class InjectionRuleExecuteRequest
    {
        public int SessionId { get; set; }
        public int? CompanyId { get; set; }
        public List<DepartmentQuotaItem>? DepartmentQuotas { get; set; }
        public List<string>? ConfirmedCandidateKeys { get; set; } // Specific candidate keys from preview (e.g. U_...)
        public List<string>? UserIds { get; set; }
        public bool BypassApproval { get; set; } = true;
        public bool SendNotification { get; set; } = true;
        public bool SendN8nWebhook { get; set; } = true;
        public bool SendEmail { get; set; } = true;

        public string? Mode { get; set; }
        public int PharmacistQuota { get; set; } = 0;
        public int AssistantQuota { get; set; } = 0;
        public List<int>? BranchIds { get; set; }
        public List<int>? ShiftIds { get; set; }
    }

    public class BranchStaffFilterCountRequest
    {
        public int SessionId { get; set; }
        public List<int>? BranchIds { get; set; }
        public List<int>? ShiftIds { get; set; }
    }

    public class ExportInjectionPreviewRequest
    {
        public int SessionId { get; set; }
        public List<InjectionCandidatePreviewDto>? Candidates { get; set; }
    }
}
