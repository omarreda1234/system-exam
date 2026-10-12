using Microsoft.AspNetCore.Identity;

namespace Exam.Models
{
    public class ApplicationUser:IdentityUser
    {
        public string? FullName { get; set; }
        public string? UserCode { get; set; }
        public string? CertificateCode { get; set; }
        public decimal? CertificateScore { get; set; }
        public int? BranchId { get; set; }
        public int? ShiftId { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? CreationSource { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }
}
