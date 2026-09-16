using System;
using Microsoft.AspNetCore.Http;

namespace Exam.DTOs
{
    public class ChangeRequestDto
    {
        public int Id { get; set; }
        public string TicketCode { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Priority { get; set; } = "Medium";
        public string? Module { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? ExpectedResult { get; set; }
        public string? AttachmentPath { get; set; }
        public string? AttachmentName { get; set; }
        public string Status { get; set; } = "Open";
        public string? DevNotes { get; set; }
        public string CreatedByUserId { get; set; } = string.Empty;
        public string CreatedByName { get; set; } = string.Empty;
        public string CreatedByEmail { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }

    public class CreateChangeRequestDto
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Priority { get; set; } = "Medium";
        public string? Module { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? ExpectedResult { get; set; }
        public IFormFile? Attachment { get; set; }
    }

    public class UpdateChangeRequestStatusDto
    {
        public int Id { get; set; }
        public string Status { get; set; } = "In Progress";
        public string? DevNotes { get; set; }
    }

    public class ChangeRequestsSummaryDto
    {
        public int TotalCount { get; set; }
        public int OpenCount { get; set; }
        public int InProgressCount { get; set; }
        public int ResolvedCount { get; set; }
    }
}
