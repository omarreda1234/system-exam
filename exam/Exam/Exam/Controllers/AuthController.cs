using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Exam.DTOs;
using Exam.Services;
using Microsoft.AspNetCore.Identity;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;

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
                ModelState.AddModelError("", "رجاءً ملء جميع الحقول المطلوبة.");
                return View(request);
            }

            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);

            // Check if user already exists in AspNetUsers
            var existingUserCount = await conn.QueryFirstOrDefaultAsync<int>(@"
                SELECT COUNT(1) FROM AspNetUsers 
                WHERE (Email IS NOT NULL AND LOWER(Email) = LOWER(@Email)) 
                   OR (UserCode IS NOT NULL AND UserCode <> '' AND UserCode = @UserCode)", request);

            if (existingUserCount > 0)
            {
                ViewBag.Branches = await _examService.GetAllBranchesAsync();
                ModelState.AddModelError("", "هذا البريد الإلكتروني أو كود المستخدم (UserCode) مسجل بالفعل بالنظام. يمكنك تسجيل الدخول مباشرة بدلاً من إنشاء حساب جديد.");
                return View(request);
            }

            // Check if there is already a Pending request
            var existingReqCount = await conn.QueryFirstOrDefaultAsync<int>(@"
                SELECT COUNT(1) FROM RegistrationRequests 
                WHERE Status = 'Pending' AND ((Email IS NOT NULL AND LOWER(Email) = LOWER(@Email)) 
                   OR (UserCode IS NOT NULL AND UserCode <> '' AND UserCode = @UserCode))", request);

            if (existingReqCount > 0)
            {
                ViewBag.Branches = await _examService.GetAllBranchesAsync();
                ModelState.AddModelError("", "يوجد طلب تسجيل معلق بالفعل بنفس البريد الإلكتروني أو كود المستخدم تحت المراجعة.");
                return View(request);
            }

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
            if (string.IsNullOrEmpty(registerDto.Email))
            {
                ViewBag.Branches = await _examService.GetAllBranchesAsync();
                ModelState.AddModelError(string.Empty, "يرجى أدخال البريد الإلكتروني.");
                return View(registerDto);
            }

            using (var conn = new Microsoft.Data.SqlClient.SqlConnection(_connectionString))
            {
                var existingCount = await conn.QueryFirstOrDefaultAsync<int>(@"
                    SELECT COUNT(1) FROM AspNetUsers 
                    WHERE (Email IS NOT NULL AND LOWER(Email) = LOWER(@Email)) 
                       OR (UserCode IS NOT NULL AND UserCode <> '' AND UserCode = @UserCode)", registerDto);

                if (existingCount > 0)
                {
                    ViewBag.Branches = await _examService.GetAllBranchesAsync();
                    ModelState.AddModelError(string.Empty, "هذا الحساب أو البريد الإلكتروني أو كود المستخدم مسجل بالفعل.");
                    return View(registerDto);
                }
            }

            var result = await _authService.RegisterAsync(registerDto);
            if (result.Succeeded)
            {
                if (User.Identity.IsAuthenticated && User.IsInRole("Admin"))
                {
                    TempData["SuccessMessage"] = $"تم إنشاء الحساب '{registerDto.UserName}' بنجاح.";
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

            var cleanEmail = dto.Email?.Trim().ToLower();
            if (string.IsNullOrEmpty(cleanEmail))
            {
                ModelState.AddModelError(string.Empty, "يرجى إدخال البريد الإلكتروني.");
                return View(dto);
            }

            // 1. Hourly Rate Limiting check per email (Max 3 attempts per hour)
            using (var conn = new SqlConnection(_connectionString))
            {
                var recentRequestsCount = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) 
                    FROM EmailLogs 
                    WHERE LOWER(Recipient) = LOWER(@Recipient) 
                      AND SentAt > DATEADD(HOUR, -1, GETDATE())", 
                    new { Recipient = cleanEmail });

                const int maxHourlyAttemptsPerUser = 3;
                if (recentRequestsCount >= maxHourlyAttemptsPerUser)
                {
                    ModelState.AddModelError(string.Empty, "لقد تجاوزت الحد المسموح به لطلبات استعادة كلمة المرور (3 محاولات كحد أقصى خلال الساعة). يرجى الانتظار والمحاولة لاحقاً بعد مرور ساعة.");
                    return View(dto);
                }
            }

            var token = await _authService.GeneratePasswordResetTokenAsync(dto.Email);
            if (token != null)
            {
                var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                LogDebug($"ForgotPassword: Email={dto.Email}, Generated Raw Token Length={token.Length}, Encoded Token={encodedToken}");
                var publicIp = "41.33.149.186:8090";
                var relativeUrl = Url.Action("ResetPassword", "Auth", new { token = encodedToken, email = dto.Email });
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
                
                try
                {
                    await _emailSender.SendEmailAsync(dto.Email, subject, body);
                    TempData["SuccessMessage"] = "تم إرسال رابط استعادة كلمة المرور إلى بريدك الإلكتروني بنجاح. يرجى التحقق من صندوق الوارد (أو مجلد الرسائل غير المرغوب فيها Spam).";
                    return View();
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("LIMIT_REACHED") || ex.Message.Contains("Daily user sending limit exceeded"))
                    {
                        ModelState.AddModelError(string.Empty, "تم تجاوز الحد المسموح به لإرسال البريد الإلكتروني حالياً. يرجى المحاولة لاحقاً بعد قليل.");
                    }
                    else
                    {
                        ModelState.AddModelError(string.Empty, "تعذر إرسال البريد الإلكتروني في الوقت الحالي. يرجى مراجعة إدارة الأكاديمية أو المحاولة لاحقاً.");
                    }
                    return View(dto);
                }
            }
            
            ModelState.AddModelError(string.Empty, "لا يوجد حساب مسجل بهذا البريد الإلكتروني.");
            return View(dto);
        }

        [HttpGet]
        public IActionResult ResetPassword(string token, string email)
        {
            if (token == null || email == null) return RedirectToAction("Login");
            
            LogDebug($"ResetPassword GET: Incoming token={token}, email={email}");
            Console.WriteLine($"[ResetPassword GET] Incoming token: {token}");
            string decodedToken;
            try
            {
                decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
                LogDebug($"ResetPassword GET: Base64UrlDecode succeeded. Decoded token Length={decodedToken.Length}");
                Console.WriteLine($"[ResetPassword GET] Base64UrlDecode succeeded. Decoded token length: {decodedToken?.Length}");
            }
            catch (Exception ex)
            {
                LogDebug($"ResetPassword GET: Base64UrlDecode failed, exception={ex.Message}");
                Console.WriteLine($"[ResetPassword GET] Base64UrlDecode failed: {ex.Message}");
                // Fallback: replace spaces with pluses if it wasn't Base64UrlEncoded
                decodedToken = token.Replace(" ", "+");
            }

            ModelState.Clear();
            return View(new ResetPasswordDTO { Token = decodedToken, Email = email });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordDTO dto)
        {
            if (!ModelState.IsValid) return View(dto);

            LogDebug($"ResetPassword POST: dto.Email={dto.Email}, dto.Token Length={dto.Token?.Length}");
            Console.WriteLine($"[ResetPassword POST] Incoming dto.Token: {dto.Token}");
            Console.WriteLine($"[ResetPassword POST] Email: {dto.Email}");

            var token = dto.Token;
            if (token != null)
            {
                token = token.Replace(" ", "+");
            }

            var result = await _authService.ResetPasswordAsync(dto.Email, token, dto.NewPassword);
            if (!result.Succeeded)
            {
                var errMsgs = string.Join(", ", result.Errors.Select(e => e.Description));
                LogDebug($"ResetPassword POST Failed: Email={dto.Email}, Errors={errMsgs}");
                Console.WriteLine($"[ResetPassword POST] Failed! Errors: {errMsgs}");
            }
            if (result.Succeeded)
            {
                try
                {
                    using var conn = new SqlConnection(_connectionString);
                    var targetUser = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT Id FROM AspNetUsers WHERE LOWER(Email) = LOWER(@Email)", new { Email = dto.Email });
                    if (targetUser != null && targetUser.Id != null)
                    {
                        string targetUserId = (string)targetUser.Id;
                        await conn.ExecuteAsync(@"
                            IF EXISTS (SELECT 1 FROM UserSavedPasswords WHERE UserId = @UserId)
                            BEGIN
                                UPDATE UserSavedPasswords 
                                SET PlainPassword = @PlainPassword, UpdatedAt = GETDATE(), UpdatedBy = 'EmailReset' 
                                WHERE UserId = @UserId;
                            END
                            ELSE
                            BEGIN
                                INSERT INTO UserSavedPasswords (UserId, PlainPassword, UpdatedAt, UpdatedBy) 
                                VALUES (@UserId, @PlainPassword, GETDATE(), 'EmailReset');
                            END", new { UserId = targetUserId, PlainPassword = dto.NewPassword });
                    }
                }
                catch { }

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

                TempData["ResetEmail"] = dto.Email;
                TempData["ResetPassword"] = dto.NewPassword;
                return RedirectToAction("ResetSuccess");
            }

            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(dto);
        }

        [HttpGet]
        public IActionResult ResetSuccess()
        {
            var email = TempData["ResetEmail"] as string;
            var password = TempData["ResetPassword"] as string;
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                return RedirectToAction("Login");
            }

            ViewBag.Email = email;
            ViewBag.Password = password;
            return View();
        }

        private void LogDebug(string message)
        {
            try
            {
                var logPath = @"c:\exam final\exam\Exam\Exam\wwwroot\reset_log.txt";
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch {}
        }
    }
}
