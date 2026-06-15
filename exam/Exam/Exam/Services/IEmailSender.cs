using System.Threading.Tasks;

namespace Exam.Services
{
    public interface IEmailSender
    {
        Task SendEmailAsync(string to, string subject, string htmlMessage);
        Task SendEmailWithAttachmentAsync(string to, string subject, string htmlMessage, byte[] attachmentBytes, string attachmentName);
    }
}
