using System;

namespace Exam.Models
{
    public class Material
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string FilePath { get; set; }
        public string FileType { get; set; } // "PDF", "Excel", "Image", "Other"
        public DateTime UploadedAt { get; set; }
        public string UploadedBy { get; set; }
        public long FileSizeBytes { get; set; }
        public string? PosterPath { get; set; }
    }
}
