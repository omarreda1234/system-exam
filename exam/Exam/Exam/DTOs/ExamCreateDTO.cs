using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Exam.DTOs
{
    public class ExamCreateDTO
    {
        public int Id { get; set; }
        [Required]
        [StringLength(200)]
        public string Title { get; set; }

        public string? Description { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public int? Duration { get; set; }

        public decimal? PassPercentage { get; set; }

        public int? ExamTypeId { get; set; }

        // Optional: only used when the selected ExamType is "wavey"
        public int? WaveId { get; set; }

        public bool IsActive { get; set; }
        public bool IsGraded { get; set; } = true;
        public int? TotalQuestionsToShow { get; set; }
        public bool ShowQuestionOverview { get; set; } = true;
        public bool IsFinalExam { get; set; }

        public List<ExamGenerationRuleDto> GenerationRules { get; set; } = new();
        public List<QuestionCreateDTO> Questions { get; set; } = new();
    }

    public class ExamGenerationRuleDto
    {
        public int Id { get; set; }
        public int ExamId { get; set; }
        public int CategoryId { get; set; }
        public string TargetRole { get; set; } = "All"; // 'Pharmacist', 'Assistant', 'All'
        public int EasyCount { get; set; }
        public int MediumCount { get; set; }
        public int HardCount { get; set; }
        public int? TopicId { get; set; }
        
        // Navigation properties for UI
        public string? CategoryName { get; set; }
        public string? TopicName { get; set; }
    }

    public class StudentDto
    {
        public string Id { get; set; }
        public string UserName { get; set; }
        public string Email { get; set; }
        public bool EmailConfirmed { get; set; }
        public decimal? TotalScore { get; set; }
        public bool? IsPassed { get; set; }
        public string Status { get; set; }
        public string RoleName { get; set; }
    }

    public class ExamDto
    {
        public int ExamId { get; set; }
        public string ExamTitle { get; set; }
        public string ExamDescription { get; set; }
        public DateTime ExamDate { get; set; }
        public DateTime EndTime { get; set; }
        public int DurationInMinutes { get; set; }
        public decimal PassPercentage { get; set; }
        public bool IsActive { get; set; }
        public bool IsGraded { get; set; } = true;
        public bool ShowQuestionOverview { get; set; } = true;
        public bool IsFinalExam { get; set; }
    }

    public class adminExamDto
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int DurationInMinutes { get; set; }
        public decimal PassPercentage { get; set; }
        public bool IsActive { get; set; }
        public bool IsGraded { get; set; } = true;
        public int? TotalQuestionsToShow { get; set; }
        public bool ShowQuestionOverview { get; set; }
        public bool IsFinalExam { get; set; }

        // Added to map dbo.sp_GetAllExamsWithDetails aliases
        public string ExamType { get; set; }
        public int? ExamTypeId { get; set; }
        public string WaveName { get; set; }
        public int? WaveId { get; set; }
        public string? CertificateTemplatePath { get; set; }

        // Blueprint Support
        public List<ExamGenerationRuleDto> GenerationRules { get; set; } = new();
        public int TotalQuestionsAvailable { get; set; }
        
        // Stats Summary
        public ExamQuestionStatsDto? Stats { get; set; }
    }

    public class ResultDetailDto
    {
        public string QuestionText { get; set; }
        public string StudentChoice { get; set; }
        public string CorrectChoice { get; set; }
        public bool IsCorrect { get; set; }
    }

    // Details for editing exam questions and choices
    public class ExamDetailsDto
    {
        public int ExamId { get; set; }
        public string Title { get; set; }
        public int? ExamTypeId { get; set; }
        public decimal PassPercentage { get; set; }
        public decimal TotalPoints { get; set; }
        public bool ShowQuestionOverview { get; set; } = true;
        public List<QuestionDetailDto> Questions { get; set; } = new();
    }

    public class QuestionDetailDto
    {
        public int QuestionId { get; set; }
        public string QuestionText { get; set; }
        public int CategoryId { get; set; }
        public string QuestionType { get; set; }
        public int Points { get; set; }
        public int Difficulty { get; set; }
        public int? TopicId { get; set; }
        public string? CategoryName { get; set; }
        public string? TopicName { get; set; }
        public int? SelectedChoiceId { get; set; }
        public List<ChoiceDetailDto> Choices { get; set; } = new();
    }

    public class ChoiceDetailDto
    {
        public int ChoiceId { get; set; }
        public string ChoiceText { get; set; }
        public bool IsCorrect { get; set; }
    }

    public class QuestionCreateDTO
    {
        [Required(ErrorMessage = "Question text is required")]
        public string QuestionText { get; set; }

        public int Points { get; set; } = 1;

        public int Difficulty { get; set; }
        public int? CategoryId { get; set; }
        public int? TopicId { get; set; }
        public int? QuestionMonth { get; set; }
        public int? QuestionYear { get; set; }

        public List<ChoiceCreateDTO> Choices { get; set; } = new();
    }

    public class ChoiceCreateDTO
    {
        [Required]
        public string ChoiceText { get; set; }
        public bool IsCorrect { get; set; }
    }

    // For admin dropdown of active exams
    public class ExamDropdownDto
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string TypeName { get; set; }
        public DateTime StartTime { get; set; }
        public string? WaveName { get; set; }
    }

    // For admin per-exam results table
    public class ExamResultRowDto
    {
        public string ExamName { get; set; }
        public string Id { get; set; }
        public int? AttemptId { get; set; }
        public string StudentName { get; set; }
        public string StudentEmail { get; set; }
        public string Status { get; set; }          // e.g. Not Started / InProgress / Completed
        public decimal Score { get; set; }          // Percentage
        public decimal FinalScore { get; set; }     // Points
        public int DurationInMinutes { get; set; }
        public bool? IsPassed { get; set; }
        public string CertificateCode { get; set; }
        public string CompletionDate { get; set; }
        public string WaveName { get; set; }
        public string ExamType { get; set; }
        public string CertificateTemplatePath { get; set; }
        public string BranchName { get; set; }
        public bool EmailSent { get; set; }
        public int AttemptNumber { get; set; }
        public decimal TotalScoreAvailable { get; set; } // Points
        public DateTime? ActualStartTime { get; set; }
        public DateTime? ActualEndTime { get; set; }
        public string UserCode { get; set; }
        public string RoleName { get; set; }
    }

    public class StudentExamReviewRowDto
    {
        public int QuestionId { get; set; }
        public string QuestionText { get; set; }
        public string QuestionType { get; set; }
        public int Points { get; set; }
        public int Difficulty { get; set; }
        public int ChoiceId { get; set; }
        public string ChoiceText { get; set; }
        public bool IsRightAnswer { get; set; }
        public bool IsStudentSelection { get; set; }
    }

    public class ExamTypeDto
    {
        public int Id { get; set; }
        public string TypeName { get; set; }
    }

    public class QuestionTypeDto
    {
        public int Id { get; set; }
        public string TypeName { get; set; }
    }

    // For Categories dropdown in question creation (sp_GetAllCategories)
    public class CategoryDto
    {
        public int Id { get; set; }
        public string? CategoryName { get; set; }
        public int? ExamTypeId { get; set; }
        public string? ExamTypeName { get; set; }
    }

    public class TopicDto
    {
        public int Id { get; set; }
        public string TopicName { get; set; }
        public int CategoryId { get; set; }
        public DateTime? CreatedAt { get; set; }
    }

    public class UserWithRoleDto
    {
        public string Id { get; set; }
        public string UserName { get; set; }
        public string? FullName { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string Code { get; set; }
        public int? BranchId { get; set; }
        public string BranchName { get; set; }
        public string CertificateCode { get; set; }
        public string CustomRole { get; set; }
        public string RoleName { get; set; }
        public int? ShiftId { get; set; }
        public string ShiftName { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public bool? IsActive { get; set; }
        public string WaveName { get; set; }
    }

    public class RoleDto
    {
        public string RoleId { get; set; }
        public string RoleName { get; set; }
    }

    public class ShiftDto
    {
        public int Id { get; set; }
        public string ShiftName { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
    }

    // Used for admin dropdown: waves list
    public class WaveDto
    {
        public int Id { get; set; }
        public string WaveName { get; set; }
        public DateTime? StartDate { get; set; }
    }

    public class UserShiftDto
    {
        public int ShiftId { get; set; }
        public string ShiftName { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
    }

    public class ExamAssignmentDto
    {
        public int Id { get; set; }
        public int ExamId { get; set; }
        public string StudentId { get; set; }
        public DateTime? ScheduledStartTime { get; set; }
        public DateTime? ScheduledEndTime { get; set; }
    }

    public class UserAttemptSummaryDto
    {
        public int Id { get; set; }
        public string Status { get; set; }
        public bool? IsPassed { get; set; }
        public DateTime AttemptDate { get; set; }
        public DateTime? StartTime { get; set; }
    }

    public class ExamQuestionStatsDto
    {
        public int TotalQuestions { get; set; }
        public List<CategoryStatDto> Categories { get; set; } = new();
    }

    public class CategoryStatDto
    {
        public string CategoryName { get; set; }
        public int Count { get; set; }
        public List<TopicStatDto> Topics { get; set; } = new();
    }

    public class TopicStatDto
    {
        public string TopicName { get; set; }
        public int Count { get; set; }
    }

    public class AnswerModel
    {
        public int QuestionId { get; set; }
        public int SelectedChoiceId { get; set; }
    }
}
