using Exam.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Threading.Tasks;
using Exam.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Exam.DTOs;
using System.Linq;

namespace Exam.Controllers
{
    public class HomeController : Controller
    {
        private readonly IExamService _examService;

        public HomeController(IExamService examService)
        {
            _examService = examService;
        }

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetNotifications()
        {
            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { count = 0 });
            }

            var exams = await _examService.GetStudentExamsByStudentIdAsync(userId);
            int pendingCount = exams?.Count() ?? 0;
            return Json(new { count = pendingCount });
        }

        [Authorize]
        public async Task<IActionResult> StudentExams()
        {
            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Challenge();
            }

            var shift = await _examService.GetUserShiftAsync(userId);
            ViewBag.UserShift = shift;

            // Store shift info in cookies (best-effort) so views/JS can read if needed
            try
            {
                if (shift != null && shift.ShiftId > 0)
                {
                    Response.Cookies.Append("ShiftName", shift.ShiftName ?? "");
                    Response.Cookies.Append("ShiftStart", shift.StartTime.ToString());
                    Response.Cookies.Append("ShiftEnd", shift.EndTime.ToString());
                }
            }
            catch { }

            var exams = await _examService.GetStudentExamsByStudentIdAsync(userId);
            return View(exams);
        }

        [HttpGet]
        public async Task<IActionResult> GetInstructions()
        {
            var instructions = await _examService.GetInstructionsAsync();
            return Json(new { instructions });
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error(string message = null, string trace = null)
        {
            return View(new ErrorViewModel 
            { 
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
                ErrorMessage = message,
                StackTrace = trace
            });
        }
    }
}
