namespace Exam.Services
{
    public class EmailSettings
    {
        public string SmtpServer { get; set; }
        public int Port { get; set; }
        public bool UseSsl { get; set; }
        public string From { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int HourlyLimit { get; set; }
    }
}
