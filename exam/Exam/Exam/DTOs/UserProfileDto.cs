using System;
using System.Collections.Generic;

namespace Exam.DTOs
{
    public class UserProfileViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string UserCode { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public string ShiftName { get; set; } = string.Empty;
        public TimeSpan? ShiftStartTime { get; set; }
        public TimeSpan? ShiftEndTime { get; set; }
        public decimal? CertificateScore { get; set; }
        public string CertificateCode { get; set; } = string.Empty;

        // Mandatory Wave Google Form Link requested by client
        public string WaveFormUrl { get; set; } = "https://docs.google.com/forms/d/e/1FAIpQLScPL0Iwra9knXtHgtQiQyRPo4d6x6apb8Tyr6oZ9ijLWOOJWw/viewform";

        // Certificates list
        public List<UserCertificateItemViewModel> Certificates { get; set; } = new();

        // Trainee Hub Navigation Sections
        public IEnumerable<ExamDto> Exams { get; set; } = new List<ExamDto>();
        public UserShiftDto? UserShift { get; set; }
        public IEnumerable<dynamic> Assignments { get; set; } = new List<dynamic>();
        public string? ActiveWaveName { get; set; }
        public bool HasProgramDashAccess { get; set; }
        public List<UserWaveItemViewModel> MyWaves { get; set; } = new();
        public List<UserCompanySessionItemViewModel> MyCompanies { get; set; } = new();
    }

    public class UserWaveItemViewModel
    {
        public int WaveId { get; set; }
        public string WaveName { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? Mode { get; set; }
        public bool IsOnline { get; set; }
        public string ApprovalStatus { get; set; } = "Approved";
        public DateTime? JoinDate { get; set; }
    }

    public class UserCompanySessionItemViewModel
    {
        public int SessionId { get; set; }
        public string SessionName { get; set; } = string.Empty;
        public int? CompanyId { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public DateTime? SessionDate { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public string ApprovalStatus { get; set; } = "Approved";
    }

    public class UserCertificateItemViewModel
    {
        public int? WaveId { get; set; }
        public string WaveName { get; set; } = string.Empty;
        public string CertificateCode { get; set; } = string.Empty;
        public decimal? Score { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string FileExtension { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public bool HasFileOnDisk { get; set; }
        public string SourceFolder { get; set; } = string.Empty;
    }
}

