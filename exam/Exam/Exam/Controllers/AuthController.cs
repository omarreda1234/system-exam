using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Exam.DTOs;
using Exam.Services;
using Microsoft.AspNetCore.Identity;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Exam.Controllers
{
    public class AuthController : Controller
    {
        private readonly IAuthService _authService;
        private readonly IExamService _examService;
        private readonly IEmailSender _emailSender;
        private readonly string _connectionString;

        public AuthController(IAuthService authService, IExamService examService, IEmailSender emailSender, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _authService = authService;
            _examService = examService;
            _emailSender = emailSender;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        [HttpGet]
        public IActionResult Login()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginDTO loginDto)
        {
            if (!ModelState.IsValid)
            {
                // return view with validation errors
                return View(loginDto);
            }

            var result = await _authService.LoginAsync(loginDto);
            if (result.Succeeded)
            {
                // set a TempData flag to show SweetAlert welcome on next page
                TempData["ShowWelcome"] = "true";
                TempData["WelcomeUser"] = loginDto.Email;
                return RedirectToAction("Index", "Home");
            }

            // Add exact error message to ModelState so the validation summary displays it
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Invalid login credentials.");
            return View(loginDto);
        }

        [HttpGet]
        public IActionResult AccessDenied()
        {
            return View("AccessDenied");
        }

        [HttpGet]
        public async Task<IActionResult> Register()
        {
            ViewBag.Branches = await _examService.GetAllBranchesAsync();
            return View(new RegisterDTO());
        }

        [HttpGet]
        public async Task<IActionResult> Apply()
        {
            ViewBag.Branches = await _examService.GetAllBranchesAsync();
            return View(new Models.RegistrationRequest());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Apply(Models.RegistrationRequest request)
        {
            if (string.IsNullOrEmpty(request.FullName) || string.IsNullOrEmpty(request.Email))
            {
                ViewBag.Branches = await _examService.GetAllBranchesAsync();
                ModelState.AddModelError("", "Please fill in all required fields.");
                return View(request);
            }

            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
            var sql = @"
                INSERT INTO RegistrationRequests (FullName, Email, Gmail, UserCode, PasswordHash, JobTitle, Shift, PhoneNumber, BranchId, Notes, Status)
                VALUES (@FullName, @Email, @Gmail, @UserCode, @Password, @JobTitle, @Shift, @PhoneNumber, @BranchId, @Notes, 'Pending')";
            
            await conn.ExecuteAsync(sql, request);

            ViewBag.ShowSuccess = true;
            ViewBag.Branches = await _examService.GetAllBranchesAsync();
            return View(new Models.RegistrationRequest());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterDTO registerDto)
        {
            //if (!ModelState.IsValid)
            //{
            //    ViewBag.Branches = await _examService.GetAllBranchesAsync();
            //    return View(registerDto);
            //}

            var result = await _authService.RegisterAsync(registerDto);
            if (result.Succeeded)
            {
                if (User.Identity.IsAuthenticated && User.IsInRole("Admin"))
                {
                    TempData["SuccessMessage"] = $"User '{registerDto.UserName}' created successfully.";
                    return RedirectToAction("Register"); // Keep admin on the same page to add more if needed
                }

                TempData["ShowWelcome"] = "true";
                TempData["WelcomeUser"] = registerDto.UserName;
                return RedirectToAction("Index", "Home");
            }

            ViewBag.Branches = await _examService.GetAllBranchesAsync();

            // add identity errors to model state so view shows them specifically
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(registerDto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _authService.SignOutAsync();
            return RedirectToAction("Index", "Home");
        }

        [HttpGet]
        public IActionResult ForgotPassword() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordDTO dto)
        {
            if (!ModelState.IsValid) return View(dto);

            var token = await _authService.GeneratePasswordResetTokenAsync(dto.Email);
            if (token != null)
            {
                var publicIp = "41.33.149.186:5208";
                var relativeUrl = Url.Action("ResetPassword", "Auth", new { token, email = dto.Email });
                var callbackUrl = $"{Request.Scheme}://{publicIp}{relativeUrl}";
                var subject = "Reset Your Password - Eltarshoubi Academy";
                var body = $@"
                    <div style='font-family: sans-serif; line-height: 1.5; color: #333;'>
                        <h2 style='color: #4f46e5;'>Password Reset Request</h2>
                        <p>We received a request to reset your password. Click the link below to proceed:</p>
                        <p style='margin: 30px 0;'>
                            <a href='{callbackUrl}' style='background: #4f46e5; color: white; padding: 12px 24px; text-decoration: none; border-radius: 8px; font-weight: bold;'>Reset Password</a>
                        </p>
                        <p>If you didn't request this, you can ignore this email.</p>
                        <hr style='border: none; border-top: 1px solid #eee;' />
                        <p style='font-size: 11px; color: #999;'>Eltarshoubi Academy LMS Portal</p>
                    </div>";
                
                _ = _emailSender.SendEmailAsync(dto.Email, subject, body);
                
                TempData["SuccessMessage"] = "If an account with that email exists, a password reset link has been sent.";
                return View();
            }
            
            ModelState.AddModelError(string.Empty, "No user found with this email address.");
            return View(dto);
        }

        [HttpGet]
        public IActionResult ResetPassword(string token, string email)
        {
            if (token == null || email == null) return RedirectToAction("Login");
            return View(new ResetPasswordDTO { Token = token, Email = email });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordDTO dto)
        {
            if (!ModelState.IsValid) return View(dto);

            var result = await _authService.ResetPasswordAsync(dto.Email, dto.Token, dto.NewPassword);
            if (result.Succeeded)
            {
                // Send confirmation email with the new password
                var subject = "Your Password Has Been Reset Successfully - Eltarshoubi Academy";
                var body = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 600px; margin: auto; border: 1px solid #eee; padding: 25px; border-radius: 12px; border-top: 4px solid #10b981;'>
                        <h2 style='color: #10b981; text-align: center;'>Password Reset Successful</h2>
                        <p>Hello,</p>
                        <p>Your password for the **El-Tarshoubi Training Academy Exam System** has been updated successfully.</p>
                        
                        <div style='background: #f0fdf4; padding: 20px; border-radius: 8px; border: 1px solid #dcfce7; margin: 25px 0;'>
                            <p style='margin: 8px 0;'><strong>Login Email:</strong> {dto.Email}</p>
                            <p style='margin: 8px 0;'><strong>New Password:</strong> <code style='background: #fff; padding: 2px 6px; border: 1px solid #cbd5e1; border-radius: 4px; color: #15803d;'>{dto.NewPassword}</code></p>
                        </div>

                        <p>You can now use this password to log in to your account.</p>
                        <hr style='border: none; border-top: 1px solid #eee; margin: 20px 0;' />
                        <p style='font-size: 11px; color: #94a3b8; text-align: center;'>© {DateTime.Now.Year} El-Tarshoubi Group. All rights reserved.</p>
                    </div>";

                _ = _emailSender.SendEmailAsync(dto.Email, subject, body);

                TempData["SuccessMessage"] = "Your password has been reset successfully. Please login with your new password.";
                return RedirectToAction("Login");
            }

            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(dto);
        }
    }
}
