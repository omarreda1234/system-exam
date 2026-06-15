using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Data.SqlClient;
using Dapper;

namespace Exam.Services
{
    public class SmtpEmailSender : IEmailSender
    {
        private readonly EmailSettings _settings;
        private readonly string _connectionString;

        public SmtpEmailSender(IConfiguration configuration)
        {
            _settings = new EmailSettings();
            var section = configuration.GetSection("EmailSettings");
            section.Bind(_settings);
            
            _connectionString = configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(_settings.SmtpServer))
                throw new Exception("SmtpServer not found in EmailSettings. Check appsettings.json.");
            if (_settings.Port == 0) _settings.Port = 587;
        }

        private async Task CheckLimitAndLogAsync(string recipient)
        {
            if (_settings.HourlyLimit <= 0) return;

            using var conn = new SqlConnection(_connectionString);
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM EmailLogs WHERE SentAt > DATEADD(HOUR, -1, GETDATE())");

            if (count >= _settings.HourlyLimit)
            {
                throw new Exception("LIMIT_REACHED: لقد تم تجاوز الحد المسموح للإرسال حالياً لحماية البريد الإلكتروني. يرجى المحاولة لاحقاً بعد ساعة.");
            }

            await conn.ExecuteAsync("INSERT INTO EmailLogs (Recipient) VALUES (@Recipient)", new { Recipient = recipient });
        }

        public async Task SendEmailAsync(string to, string subject, string htmlMessage)
        {
            await CheckLimitAndLogAsync(to);

            var email = new MimeMessage();
            email.From.Add(new MailboxAddress("Eltarshoubi Academy", _settings.From));
            email.To.Add(MailboxAddress.Parse(to));
            email.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = htmlMessage };
            email.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(_settings.SmtpServer, _settings.Port, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_settings.Username, _settings.Password);
                await client.SendAsync(email);
                await client.DisconnectAsync(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MailKit Error: {ex.Message}");
                throw;
            }
        }

        public async Task SendEmailWithAttachmentAsync(string to, string subject, string htmlMessage, byte[] attachmentBytes, string attachmentName)
        {
            await CheckLimitAndLogAsync(to);

            var email = new MimeMessage();
            email.From.Add(new MailboxAddress("Eltarshoubi Academy", _settings.From));
            email.To.Add(MailboxAddress.Parse(to));
            email.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = htmlMessage };
            if (attachmentBytes != null && attachmentBytes.Length > 0)
            {
                builder.Attachments.Add(attachmentName, attachmentBytes, ContentType.Parse("application/pdf"));
            }
            email.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(_settings.SmtpServer, _settings.Port, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_settings.Username, _settings.Password);
                await client.SendAsync(email);
                await client.DisconnectAsync(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MailKit Error: {ex.Message}");
                throw;
            }
        }
    }
}
