using DocumentFormat.OpenXml.Office2010.Excel;
using DocumentFormat.OpenXml.Office2013.Excel;
using Exam.DTOs;
using Exam.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.DotNet.Scaffolding.Shared.Messaging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Exam.Controllers
{
    [Authorize]
    public class ExamsController : Controller
    {
        private readonly IExamService _examService;

        public ExamsController(IExamService examService)
        {
            _examService = examService;
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public async Task<IActionResult> Create(string type = null)
        {
            var model = new ExamCreateDTO
            {
                StartTime = System.DateTime.Now,
                EndTime = System.DateTime.Now.AddHours(1),
                Duration = 60,
                PassPercentage = 50,
                IsActive = true
            };

            ViewBag.ExamTypes = await _examService.GetAllExamTypesAsync();
            ViewBag.Categories = await _examService.GetAllCategoriesAsync();
            ViewBag.DefaultType = type;
            return View(model);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ExamCreateDTO model)
        {
            var m = model ?? new ExamCreateDTO();
            if (!m.IsGraded)
            {
                m.PassPercentage = 0;
            }

            ViewBag.ExamTypes = await _examService.GetAllExamTypesAsync();
            ViewBag.Categories = await _examService.GetAllCategoriesAsync();

            // Custom validation: check choices for any manually added questions
            if (m.Questions != null && m.Questions.Count > 0)
            {
                for (int qi = 0; qi < m.Questions.Count; qi++)
                {
                    var q = m.Questions[qi];
                    if (q == null) continue;



                    if (q.Choices == null || q.Choices.Count == 0)
                    {
                        ModelState.AddModelError(string.Empty, $"Question #{qi + 1} must have at least one answer choice.");
                        continue;
                    }

                    var hasCorrect = q.Choices.Any(c => c != null && c.IsCorrect);
                    if (!hasCorrect)
                    {
                        ModelState.AddModelError(string.Empty, $"Question #{qi + 1} must have at least one correct answer selected.");
                    }
                }
            }

            var examTypes = ViewBag.ExamTypes as IEnumerable<ExamTypeDto>;
            var selectedType = examTypes?.FirstOrDefault(t => t.Id == m.ExamTypeId);
            bool isWavey = selectedType != null && selectedType.TypeName.ToLower().Contains("wave");

            // Wavey exam requires WaveId
            if (isWavey && m.WaveId == null)
            {
                ModelState.AddModelError(string.Empty, "Please select a wave for wavey exams.");
            }
            else if (!isWavey)
            {
                m.WaveId = null;
            }

            if (!ModelState.IsValid)
            {
                // collect model state errors and surface them via TempData so layout can show a SweetAlert
                var errors = string.Join("\n", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Exception?.Message : e.ErrorMessage));
                TempData["ErrorMessage"] = errors;
                return View(m);
            }

            var examId = await _examService.CreateExamWithQuestionsAsync(m);

            TempData["SuccessMessage"] = "Exam created successfully";
            return RedirectToAction("Index", "Admin");
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> CreateDraft([FromBody] ExamCreateDTO model)
        {
            if (model == null)
                return Json(new { success = false, message = "Could not parse exam data. Please check required fields." });

            if (string.IsNullOrWhiteSpace(model.Title))
                return Json(new { success = false, message = "Exam title is required for drafting." });

            if (!ModelState.IsValid)
            {
                var errors = string.Join(", ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                return Json(new { success = false, message = "Validation failed: " + errors });
            }

            var allTypes = await _examService.GetAllExamTypesAsync();
            var dType = allTypes.FirstOrDefault(t => t.Id == model.ExamTypeId);
            if (dType != null && !dType.TypeName.ToLower().Contains("wave"))
            {
                model.WaveId = null;
            }

            try
            {
                int examId;
                if (model.Id > 0)
                {
                    // Existing draft - Update it as an adminExamDto
                    var updated = new adminExamDto
                    {
                        Id = model.Id,
                        Title = model.Title,
                        Description = model.Description,
                        StartTime = model.StartTime ?? DateTime.Now,
                        EndTime = model.EndTime ?? DateTime.Now.AddHours(1),
                        DurationInMinutes = model.Duration ?? 0,
                        PassPercentage = model.PassPercentage ?? 0,
                        IsActive = model.IsActive,
                        IsGraded = model.IsGraded,
                        WaveId = model.WaveId,
                        ExamTypeId = model.ExamTypeId,
                        TotalQuestionsToShow = model.TotalQuestionsToShow,
                        GenerationRules = model.GenerationRules
                    };
                    await _examService.UpdateExamAsync(updated);
                    examId = model.Id;
                }
                else
                {
                    // New draft
                    examId = await _examService.AddExamAsync(model);
                }
                return Json(new { success = true, examId = examId });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var exam = await _examService.GetExamByIdAsync(id);
            if (exam == null)
                return NotFound();
            
            ViewBag.ExamTypes = await _examService.GetAllExamTypesAsync();
            ViewBag.Waves = await _examService.GetAllWavesAsync();
            ViewBag.Categories = await _examService.GetAllCategoriesAsync(exam.ExamTypeId);
            return View(exam);
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public async Task<IActionResult> GetDetails(int id)
        {
            try
            {
                var details = await _examService.GetExamDetailsAsync(id);
                if (details == null)
                    return Content("<div class='p-10 text-center text-rose-500 font-bold'>Exam not found.</div>");
                
                ViewBag.Categories = await _examService.GetAllCategoriesAsync(details.ExamTypeId);
                return PartialView("_EditQuestions", details);
            }
            catch (System.Exception ex)
            {
                return Content($"<div class='p-10 text-center text-rose-500 font-bold'>Error loading library: {ex.Message}</div>");
            }
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Start(int id)
        {
            // 1. جلب معرف المستخدم الحالي
            var userId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Challenge();

            // 2. جلب بيانات الامتحان الأساسية (الاسم، الوقت، إلخ)
            var examInfo = await _examService.GetExamByIdAsync(id);
            if (examInfo == null) return NotFound();

            // 3. التحقق من المواعيد (التحقق البروفيشنال الجديد)
            var assignment = await _examService.GetStudentAssignmentAsync(id, userId);
            var latestAttempt = await _examService.GetExistingAttemptAsync(id, userId);
            var shift = await _examService.GetUserShiftAsync(userId);
            
            // تحديد النطاق الزمني المسموح به
            // الأولوية للموعد المخصص في الـ Assignment، وإلا نستخدم موعد الامتحان العام
            DateTime allowedStart = examInfo.StartTime;
            DateTime allowedEnd = examInfo.EndTime;
            
            if (assignment != null && assignment.ScheduledStartTime.HasValue)
            {
                allowedStart = assignment.ScheduledStartTime.Value;
                allowedEnd = assignment.ScheduledEndTime ?? allowedEnd;
            }

            var now = DateTime.Now;
            
            // التحقق من تاريخ الامتحان
            if (now < allowedStart || now > allowedEnd)
            {
                TempData["ErrorMessage"] = $"عذراً، هذا الامتحان غير متاح حالياً. الموعد المسموح: من {allowedStart:yyyy/MM/dd HH:mm} إلى {allowedEnd:yyyy/MM/dd HH:mm}";
                return RedirectToAction("Index", "Home");
            }

            // التحقق من الشيفت
            // نتجاهل الشيفت لو الوقتين متساويين (00:00 — 00:00 يعني مش محدد)
            if (shift != null && shift.StartTime != shift.EndTime)
            {
                var timeNow = now.TimeOfDay;
                bool inShift;
                if (shift.StartTime <= shift.EndTime)
                {
                    inShift = (timeNow >= shift.StartTime && timeNow <= shift.EndTime);
                }
                else
                {
                    // Midnight cross (e.g., 22:00 to 06:00)
                    inShift = (timeNow >= shift.StartTime || timeNow <= shift.EndTime);
                }

                if (!inShift)
                {
                    TempData["ErrorMessage"] = $"عذراً، يمكنك أداء الامتحان فقط خلال فترة عملك من {shift.StartTime:hh\\:mm} إلى {shift.EndTime:hh\\:mm} (الوقت الحالي: {timeNow:hh\\:mm})";
                    return RedirectToAction("Index", "Home");
                }
            }

            int attemptId;
            DateTime examStartTime;

            if (latestAttempt != null)
            {
                if (latestAttempt.Status != "InProgress")
                {
                    int assignmentCount = assignment != null ? await _examService.GetAssignmentCountAsync(id, userId) : 0;
                    // Check if this finished attempt belongs to an old assignment
                    if (assignment != null && 
                        ((assignment.ScheduledStartTime.HasValue && latestAttempt.AttemptDate < assignment.ScheduledStartTime.Value) ||
                         (assignmentCount > latestAttempt.AttemptNumber)))
                    {
                        attemptId = await _examService.CreateStudentAttemptAsync(id, userId);
                        examStartTime = DateTime.Now;
                    }
                    else
                    {
                        TempData["InfoMessage"] = "لقد أنهيت هذا امتحان بالفعل.";
                        return RedirectToAction("StudentExams", "Home");
                    }
                }
                else
                {
                    attemptId = latestAttempt.Id;
                    examStartTime = latestAttempt.StartTime ?? DateTime.Now;
                }
            }
            else
            {
                attemptId = await _examService.CreateStudentAttemptAsync(id, userId);
                examStartTime = DateTime.Now;
            }

            ViewData["AttemptId"] = attemptId;
            ViewData["Duration"] = examInfo.DurationInMinutes;
            ViewData["StartTime"] = examStartTime;
            ViewData["ServerTime"] = DateTime.Now;

            ExamDetailsDto? exam;

            // Get student's role from claims
            var userRole = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value ?? "All";
            
            // Fetch questions using the Smart engine (Dynamic matching based on Role)
            exam = await _examService.GetSmartQuestionsByRoleAsync(id, userId, attemptId, userRole); 
            
            if (exam == null || !exam.Questions.Any())
            {
                exam = await _examService.GetRandomExamWithChoicesAsync(id, userId, attemptId);
            }

            if (exam == null || exam.Questions == null || !exam.Questions.Any())
            {
                return NotFound("No questions available for this exam based on your role.");
            }

            // Record assigned questions in UserSeenQuestions to fix scoring and prevent refresh shuffle
            await _examService.RecordSeenQuestionsAsync(attemptId, userId, exam.Questions.Select(q => q.QuestionId));

            // نقل البيانات الأساسية للـ DTO النهائي لضمان عرضها في الـ View
            exam.Title = examInfo.Title;
            exam.PassPercentage = examInfo.PassPercentage;
            exam.ShowQuestionOverview = examInfo.ShowQuestionOverview;

            return View(exam);
        }
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> SaveSingleAnswer(int attemptId, int questionId, int selectedChoiceId)
        {
            try
            {
                await _examService.SaveStudentAnswerAsync(attemptId, questionId, selectedChoiceId);
                return Json(new { success = true });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> SubmitAnswers(int attemptId, string status, [FromBody] List<AnswerModel> answers)
        {
            try
            {
                // 0. Safety check: If already completed, don't re-run logic
                var currentAttempt = await _examService.GetAttemptByIdAsync(attemptId);
                if (currentAttempt != null && (currentAttempt.Status == "Completed" || currentAttempt.Status.StartsWith("Fail_")))
                {
                    return Ok(new { success = true, attemptId });
                }

                string finalStatus = string.IsNullOrEmpty(status) ? "Completed" : status;

                // 1. Save all answers in a single transaction batch to improve performance
                if (answers != null && answers.Any())
                {
                    await _examService.BatchSaveStudentAnswersAsync(attemptId, answers);
                }

                // 2. Finalize the exam
                await _examService.SubmitFinalAsync(attemptId, finalStatus);

                return Ok(new { success = true, attemptId });
            }
            catch (System.Exception ex)
            {
                // Log error if needed (System.Diagnostics.Debug for now)
                System.Diagnostics.Debug.WriteLine($"Submission Error: {ex.Message}");
                return StatusCode(500, new { success = false, message = "Server-side error during submission: " + ex.Message });
            }
        }

        [HttpGet]
        [Authorize]
        public IActionResult Result(int attemptId)
        {
            ViewData["AttemptId"] = attemptId;
            return View();
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateQuestion(int examId, int questionId, string questionText, int points, int difficulty, int categoryId, int? topicId = null)
        {
            await _examService.UpdateQuestionAsync(examId, questionId, questionText, points, difficulty, categoryId, topicId);
            return Ok(new { success = true });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateChoice(int choiceId, int questionId, string choiceText, bool isCorrect)
        {
            await _examService.UpdateChoiceAsync(choiceId, questionId, choiceText, isCorrect);
            return Ok(new { success = true });
        }

        // Admin edit: add/delete questions and choices on existing exams (used by _EditQuestions.cshtml)
        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddQuestion(int examId, string questionText, int points, int QuestionTypeId, int difficulty = 1, int? topicId = null)
        {
            var id = await _examService.AddQuestionForExistingExamAsync(examId, questionText, points, QuestionTypeId, difficulty, topicId);
            return Ok(new { success = true, questionId = id });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddChoice(int questionId, string choiceText, bool isCorrect)
        {
            if (questionId <= 0) return Json(new { success = false, message = "Invalid Question ID. Please commit the question again." });
            if (string.IsNullOrWhiteSpace(choiceText)) return Json(new { success = false, message = "Choice text cannot be empty." });

            var choiceId = await _examService.AddChoiceForExistingQuestionAsync(questionId, choiceText, isCorrect);
            return Ok(new { success = true, choiceId = choiceId });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteQuestion(int questionId)
        {
            await _examService.DeleteQuestionAsync(questionId);
            return Ok(new { success = true });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllQuestions(int examId)
        {
            await _examService.DeleteAllQuestionsForExamAsync(examId);
            return Ok(new { success = true });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> DeleteExam(int examid)
        {
            try
            {
                await _examService.DeleteExamAsync(examid);
                return Json(new { success = true, Message = "delete exam sucess" }); 
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: This exam might have linked student records." });
            }
        }


        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteChoice(int choiceId)
        {
            await _examService.DeleteChoiceAsync(choiceId);
            return Ok(new { success = true });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(adminExamDto model)
        {
            if (model != null && !model.IsGraded)
            {
                model.PassPercentage = 0;
            }
            ModelState.Remove("ExamType");
            ModelState.Remove("WaveName");

            if (!ModelState.IsValid)
            {
                var errors = string.Join("\n", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Exception?.Message : e.ErrorMessage));
                return Json(new { success = false, message = errors });
            }

            try
            {
                await _examService.UpdateExamAsync(model);
                return Json(new { success = true, message = "Exam updated successfully" });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}
