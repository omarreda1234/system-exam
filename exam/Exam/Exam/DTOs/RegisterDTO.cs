using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Exam.DTOs
{
    [Keyless]
    public class RegisterDTO
    {
        [Required(ErrorMessage = "User name is required")]
        [StringLength(256, ErrorMessage = "User name must be at most {1} characters")]
        public string UserName { get; set; }
        
        public string? FullName { get; set; }

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least {2} characters long.")]
        public string Password { get; set; }

        [Required(ErrorMessage = "Phone number is required")]
        public string Phone { get; set; }

        [Required(ErrorMessage = "Branch is required")]
        public int? BranchId { get; set; }

        [Required(ErrorMessage = "Shift is required")]
        public int? ShiftId { get; set; }

        [Required(ErrorMessage = "Role is required")]
        public string RoleName { get; set; }

        [Required(ErrorMessage = "User code is required")]
        public string UserCode { get; set; }

        public string? CertificateCode { get; set; }
    }
}
