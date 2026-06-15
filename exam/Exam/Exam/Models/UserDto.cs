namespace Exam.Models
{
    public class UserDto
    {
        public string Id { get; set; }
        public string UserName { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string UserCode { get; set; }
        public string BranchName { get; set; }
        public string CertificateCode { get; set; }
        public string RoleName { get; set; }
        public bool IsActive { get; set; }
        public string WaveName { get; set; }
        public string LastExamStatus { get; set; }
        public int IsAlreadyAssigned { get; set; }
        public DateTime? JoinDate { get; set; }
        public int AbsenceCount { get; set; }
    }
}