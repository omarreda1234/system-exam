using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Exam.Middlewares
{
    public class ExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionMiddleware> _logger;

        public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception has occurred.");
                
                if (context.Request.Headers["X-Requested-With"] == "XMLHttpRequest" || context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { success = false, message = "An internal server error occurred.", details = ex.Message });
                }
                else if (!context.Response.HasStarted)
                {
                    var errorMessage = Uri.EscapeDataString(ex.Message);
                    var stackTrace = Uri.EscapeDataString(ex.StackTrace ?? string.Empty);
                    context.Response.Redirect($"/Home/Error?message={errorMessage}&trace={stackTrace}");
                }
            }
        }
    }
}
