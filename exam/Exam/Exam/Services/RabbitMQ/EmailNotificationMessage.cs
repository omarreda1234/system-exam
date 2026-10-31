using System;

namespace Exam.Services.RabbitMQ
{
    public class EmailNotificationMessage
    {
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
        public string To { get; set; } = "";
        public string Subject { get; set; } = "";
        public string BodyHtml { get; set; } = "";
        
        // Metadata for tracing and custom formatting
        public string? CandidateName { get; set; }
        public string? UserCode { get; set; }
        public string? BranchName { get; set; }
        public string? SessionOrWaveName { get; set; }
        public string? SessionDate { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? CompanyName { get; set; }
        public string? EventType { get; set; } // DirectApproval, ExcelImport, FairRotationInjection, BranchManagerApproval, Replacement, WaveEnrollment
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Optional attachment
        public byte[]? AttachmentBytes { get; set; }
        public string? AttachmentName { get; set; }
    }
}
