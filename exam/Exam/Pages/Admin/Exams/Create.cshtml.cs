using System;
using System.Threading.Tasks;
using Exam.DTOs;
using Exam.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Exam.Pages.Admin.Exams
{
    public class CreateModel : PageModel
    {
        private readonly IExamService _examService;

        public CreateModel(IExamService examService)
        {
            _examService = examService;
        }

        [BindProperty]
        public ExamCreateDTO Input { get; set; }

        public void OnGet()
        {
            Input = new ExamCreateDTO
            {
                StartTime = DateTime.Now,
                EndTime = DateTime.Now.AddHours(1),
                Duration = 60,
                PassPercentage = 50,
                IsActive = true
            };
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            var examId = await _examService.AddExamAsync(Input);

            if (Input.Questions != null)
            {
                foreach (var q in Input.Questions)
                {
                    var questionId = await _examService.AddQuestionAsync(examId, q);
                    if (q.Choices != null)
                    {
                        foreach (var c in q.Choices)
                        {
                            await _examService.AddChoiceAsync(questionId, c);
                        }
                    }
                }
            }

            TempData["SuccessMessage"] = "Exam created successfully";
            return RedirectToPage("/Index");
        }
    }
}
