using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Exam.DTOs;
using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.Extensions.Configuration;
using Exam.Models;

namespace Exam.Services
{
    public class AuthService : IAuthService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IEmailSender _emailSender;
        private readonly string _connectionString;

        public AuthService(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            IEmailSender emailSender,
            IConfiguration configuration)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _emailSender = emailSender;
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<IdentityResult> RegisterAsync(RegisterDTO registerDto)
        {
            var user = new ApplicationUser
            {
                UserName = registerDto.UserName,
                FullName = registerDto.FullName,
                Email = registerDto.Email,
                PhoneNumber = registerDto.Phone,
                BranchId = registerDto.BranchId,
                UserCode = registerDto.UserCode,
                ShiftId = registerDto.ShiftId,
                CertificateCode = registerDto.CertificateCode,
                IsActive = true
            };

            var result = await _userManager.CreateAsync(user, registerDto.Password);

            if (result.Succeeded)
            {
                // Assign specified role
                var targetRole = string.IsNullOrWhiteSpace(registerDto.RoleName) ? "User" : registerDto.RoleName;
                
                if (!await _roleManager.RoleExistsAsync(targetRole))
                {
                    await _roleManager.CreateAsync(new IdentityRole(targetRole));
                }

                await _userManager.AddToRoleAsync(user, targetRole);
            }

            return result;
        }
        public async Task<(bool Succeeded, string ErrorMessage)> LoginAsync(LoginDTO loginDto)
        {
            var user = await _userManager.FindByEmailAsync(loginDto.Email);

            if (user == null)
            {
                return (false, "Incorrect Email/Password combination.");
            }

            if (!user.IsActive)
            {
                return (false, "Your account has been deactivated by the administrator.");
            }

            var isPasswordValid = await _userManager.CheckPasswordAsync(user, loginDto.Password);

            if (!isPasswordValid)
            {
                return (false, "Incorrect Email/Password combination.");
            }

            await _signInManager.SignInAsync(user, loginDto.RemmberMe);

            return (true, null);
        }

        public async Task SignOutAsync()
        {
            await _signInManager.SignOutAsync();
        }

        public async Task<string> GeneratePasswordResetTokenAsync(string email)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null) return null;
            return await _userManager.GeneratePasswordResetTokenAsync(user);
        }

        public async Task<IdentityResult> ResetPasswordAsync(string email, string token, string newPassword)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null) return IdentityResult.Failed(new IdentityError { Description = "User not found." });
            return await _userManager.ResetPasswordAsync(user, token, newPassword);
        }
    }
}
