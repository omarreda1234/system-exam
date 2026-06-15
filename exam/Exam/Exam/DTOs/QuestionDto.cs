namespace Exam.DTOs
{
    public class QuestionDto
    {
        public int Id { get; set; }
        public string QuestionText { get; set; } = string.Empty;
        public int Points { get; set; }

        // النوع كـ ID للفلترة (1 أو 2)
        public int QuestionTypeId { get; set; }

        // النوع كـ String للعرض (مثلاً 'Medicines' أو 'Cosmetics')
        public string? QuestionType { get; set; }

        // قائمة الاختيارات التابعة للسؤال
        public List<ChoiceDto> Choices { get; set; } = new List<ChoiceDto>();
    }

    public class ChoiceDto
    {
        public int Id { get; set; }
        public string ChoiceText { get; set; } = string.Empty;
        public bool IsCorrect { get; set; }
    }
}
