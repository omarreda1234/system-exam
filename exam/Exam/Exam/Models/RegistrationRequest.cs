using System;
using System.ComponentModel.DataAnnotations;

namespace Exam.Models
{
    public class RegistrationRequest
    {
        public int Id { get; set; }
        
        [Required]
        [Display(Name = "الاسم خماسي بالعربي")]
        public string FullName { get; set; }
        
        [Required]
        [EmailAddress]
        public string Email { get; set; }
        
        [Display(Name = "Gmail (اختياري)")]
        public string Gmail { get; set; }
        
        [Required]
        [Display(Name = "كود الموظف")]
        public string UserCode { get; set; }
        
        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; }
        
        [Display(Name = "الوظيفة")]
        public string JobTitle { get; set; }
        
        [Display(Name = "الشيفت")]
        public string Shift { get; set; }
        
        [Required]
        [Display(Name = "رقم الموبايل")]
        public string PhoneNumber { get; set; }
        
        [Display(Name = "الفرع")]
        public int? BranchId { get; set; }
        
        public string Notes { get; set; }
        
        public string Status { get; set; } = "Pending";
        public DateTime RequestDate { get; set; } = DateTime.Now;
        public DateTime? ProcessedDate { get; set; }
        public string ProcessedBy { get; set; }
    }
}
