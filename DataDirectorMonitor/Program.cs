var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();

// Configure CORS for local WhatsApp bot
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

Console.WriteLine("==================================================");
Console.WriteLine("🚀 LS Data Director Standalone Monitoring Service Started!");
Console.WriteLine("📡 Listening on: http://localhost:9500");
Console.WriteLine("==================================================");

app.Run("http://localhost:9500");
