using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Exam.DTOs;

namespace Exam.Services
{
    public interface IAuthService
    {
        Task<IdentityResult> RegisterAsync(RegisterDTO registerDto);
        Task<(bool Succeeded, string ErrorMessage)> LoginAsync(LoginDTO loginDto);
        Task SignOutAsync();
        Task<string> GeneratePasswordResetTokenAsync(string email);
        Task<IdentityResult> ResetPasswordAsync(string email, string token, string newPassword);
    }
}
