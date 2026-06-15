using Exam.Models;
using Exam.Middlewares;
using Exam.MyContext;
using Exam.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
PdfSharpCore.Fonts.GlobalFontSettings.FontResolver = new Exam.Services.FileFontResolver();
builder.Services.AddControllersWithViews();
var connection = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<ApplicationContext>(option =>
{
   option.UseSqlServer(connection, sqlOptions =>
       sqlOptions.EnableRetryOnFailure(
           maxRetryCount: 5,
           maxRetryDelay: TimeSpan.FromSeconds(10),
           errorNumbersToAdd: null));
});
// إضافة الهوية مع الإعدادات الخاصة بك
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // إعدادات الباسورد (Password) - هنا لغينا الرقم تماماً
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 4;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequiredUniqueChars = 1;

    // إعدادات المستخدم (User) - دعم العربي والمسافات
    options.User.AllowedUserNameCharacters = null;
     

    options.User.RequireUniqueEmail = true;

    // إعدادات القفل (Lockout)
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;
})
.AddEntityFrameworkStores<ApplicationContext>() // ربط بقاعدة البيانات
.AddDefaultTokenProviders(); // مهم جداً لاستعادة الباسورد والتوكنز
builder.Services.AddTransient<IAuthService, AuthService>();
// email sender
builder.Services.AddSingleton<Exam.Services.IEmailSender, Exam.Services.SmtpEmailSender>();
// exam service
builder.Services.AddScoped<IExamService, ExamService>();
builder.Services.AddScoped<GoogleSheetSyncService>();
// SignalR
builder.Services.AddSignalR();

// configuration cookies 
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "ExamSystemCookie"; 
    options.ExpireTimeSpan = TimeSpan.FromDays(20); 
    options.SlidingExpiration = true; 
    options.LoginPath = "/Auth/Login";
    options.AccessDeniedPath = "/Auth/AccessDenied";    
});

var app = builder.Build();

// Run database schema updates cleanly through ExamService
using (var scope = app.Services.CreateScope())
{
    var examService = scope.ServiceProvider.GetRequiredService<IExamService>();
    await examService.EnsureDatabaseSchemaUpdatedAsync(scope.ServiceProvider);
}

// Global exception middleware
app.UseMiddleware<ExceptionMiddleware>();

if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// map SignalR hubs
app.MapHub<Exam.Hubs.ImportHub>("/importHub");
//app.MapHub<Exam.Hubs.NotificationHub>("/notificationHub");
//app.MapHub<Exam.Hubs.ImportHub>("/ImportHub");
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
