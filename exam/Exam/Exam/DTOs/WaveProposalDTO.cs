using System;
using System.Collections.Generic;

namespace Exam.DTOs
{
    public class WaveCandidateDto
    {
        public string UserId { get; set; } = "";
        public string StudentName { get; set; } = "";
        public string UserName { get; set; } = "";
        public string UserCode { get; set; } = "--";
        public string Email { get; set; } = "--";
        public string PhoneNumber { get; set; } = "--";
        public DateTime? CreatedAt { get; set; }
        public int? BranchId { get; set; }
        public string BranchName { get; set; } = "--";
        public string ShiftName { get; set; } = "--";
        public string RoleName { get; set; } = "User";
        public string CandidateType { get; set; } = "New"; // "New" or "Resitter"
        public int PreviousWaveCount { get; set; } = 0;
        public decimal? LastExamScore { get; set; }
        public bool IsSelected { get; set; } = true;
        public string JoinedWaveNames { get; set; } = "";
    }


    public class WaveProposalFilterRequest
    {
        public int? Days { get; set; } = 60; // 30, 60, 90, or null
        public DateTime? DateFrom { get; set; }
        public DateTime? DateTo { get; set; }
        public List<string> Roles { get; set; } = new();
        public List<int> BranchIds { get; set; } = new();
        public bool ActiveOnly { get; set; } = true;
        public int Capacity { get; set; } = 50;
        public int ResitterPercentage { get; set; } = 20; // 0% - 50%
        public string ProposedWaveName { get; set; } = "";
        public DateTime? ProposedStartDate { get; set; }
        public Dictionary<int, int>? BranchQuotas { get; set; } = new();
        public bool BalanceAcrossBranches { get; set; } = false;
    }

    public class WaveProposalResultDto
    {
        public int TotalCandidates { get; set; }
        public int FreshCount { get; set; }
        public int ResitterCount { get; set; }
        public int BranchesCount { get; set; }
        public List<WaveCandidateDto> Candidates { get; set; } = new();
    }

    public class WaveProposalExportRequest
    {
        public List<string> SelectedUserIds { get; set; } = new();
        public string ProposedWaveName { get; set; } = "Proposed Training Batch";
        public DateTime? ProposedStartDate { get; set; }
        public string TargetRolesDescription { get; set; } = "Pharmacists & Assistants";
        public string Notes { get; set; } = "";
        public WaveProposalFilterRequest? FilterSnapshot { get; set; }
    }

    public class WaveEnrollmentRequest
    {
        public List<string> UserIds { get; set; } = new();
        public int? ExistingWaveId { get; set; }
        public string? NewWaveName { get; set; }
        public DateTime? NewStartDate { get; set; }
        public DateTime? NewEndDate { get; set; }
        public string? Mode { get; set; } = "Offline in Main Branch";
    }
}
