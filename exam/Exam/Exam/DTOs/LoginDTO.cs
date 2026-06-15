using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Exam.DTOs
{
    [Keyless]
    public class LoginDTO
    {
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [DataType(DataType.Password)]
        public string Password { get; set; }

        public bool RemmberMe { get; set; }
    }
}
