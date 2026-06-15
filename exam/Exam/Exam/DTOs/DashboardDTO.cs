using System.Collections.Generic;

namespace Exam.DTOs
{
    public class DashboardDto
    {
        public int TotalPharmacists { get; set; }
        public int TotalAssistants { get; set; }
        public int ActiveExamsCount { get; set; }
        public int AssignedAssistantsCount { get; set; }
        public int TotalWavesCount { get; set; }
        public double OverallPassRate { get; set; }
        public List<BranchStatsDto> PharmacistsPerBranch { get; set; } = new();
        public List<WaveEnrollmentDto> WaveEnrollmentTrend { get; set; } = new();
        public List<TopPharmacistDto> TopPerformingPharmacists { get; set; } = new();
        public int PassedAttempts { get; set; }
        public int FailedAttempts { get; set; }

        // Question Bank Stats
        public int TotalQuestionsCount { get; set; }
        public List<CategoryStatDto> QuestionsPerCategory { get; set; } = new();
        public List<QuestionAnomaliesDto> MismatchedQuestions { get; set; } = new();
    }

    public class QuestionAnomaliesDto
    {
        public int QuestionId { get; set; }
        public string QuestionText { get; set; }
        public string CategoryName { get; set; }
        public string TopicName { get; set; }
        public string ExpectedCategory { get; set; }
    }

    public class BranchStatsDto
    {
        public string BranchName { get; set; }
        public int UserCount { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
    }

    public class WaveEnrollmentDto
    {
        public string Month { get; set; }
        public int EnrollmentCount { get; set; }
    }

    public class TopPharmacistDto
    {
        public string Name { get; set; }
        public string UserCode { get; set; }
        public string ExamTitle { get; set; }
        public decimal Score { get; set; }
    }
}
