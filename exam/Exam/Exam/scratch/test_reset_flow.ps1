$connString = "Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"

# We can run a small C# inline script to test the identity UserManager token validation
$csharpCode = @"
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Exam.Models;
using Exam.Services;

namespace TestReset
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationContext>(options =>
                options.UseSqlServer("Server=192.168.1.111;Database=Eltarshouby-Exam;User Id=sa;Password=sa@123456;MultipleActiveResultSets=true;TrustServerCertificate=True"));

            services.AddIdentity<User, IdentityRole>(options => {
                options.Password.RequireDigit = false;
                options.Password.RequiredLength = 6;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
            })
            .AddEntityFrameworkStores<ApplicationContext>()
            .AddDefaultTokenProviders();

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            
            // Find a test user or admin
            var user = await userManager.FindByEmailAsync("admin@eltarshouby.com") ?? await userManager.FindByEmailAsync("omaraladeeb45@gmail.com");
            if (user == null)
            {
                Console.WriteLine("User not found!");
                return;
            }

            Console.WriteLine($"Found user: {user.Email}");

            // 1. Generate Token
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            Console.WriteLine($"Generated Raw Token: {token}");

            // 2. Base64Url Encode (simulating Controller generation)
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            Console.WriteLine($"Encoded Token: {encodedToken}");

            // 3. Base64Url Decode (simulating GET ResetPassword)
            var decodedBytes = WebEncoders.Base64UrlDecode(encodedToken);
            var decodedToken = Encoding.UTF8.GetString(decodedBytes);
            Console.WriteLine($"Decoded Token: {decodedToken}");

            // Verify they are identical
            if (token == decodedToken)
            {
                Console.WriteLine("SUCCESS: Raw and Decoded tokens are identical.");
            }
            else
            {
                Console.WriteLine("ERROR: Tokens mismatch!");
                return;
            }

            // 4. Validate ResetPassword (simulating POST ResetPassword)
            var result = await userManager.ResetPasswordAsync(user, decodedToken, "NewPass123!");
            if (result.Succeeded)
            {
                Console.WriteLine("SUCCESS: Password reset successfully using decoded token!");
            }
            else
            {
                Console.WriteLine("ERROR: Password reset failed!");
                foreach (var err in result.Errors)
                {
                    Console.WriteLine($" - {err.Description}");
                }
            }
        }
    }
}
"@

# Let's save this C# code to a temporary file
$csharpFile = "C:\exam final\exam\Exam\Exam\scratch\TestReset.cs"
Set-Content -Path $csharpFile -Value $csharpCode

# Let's compile and run it using dotnet-script or by creating a temp project
# Or we can just run a quick dotnet run in a temporary console app inside scratch
