using System.Collections.Generic;
using System.Threading.Tasks;

namespace Exam.Services.RabbitMQ
{
    public interface IRabbitMqEmailProducer
    {
        Task<bool> EnqueueEmailAsync(EmailNotificationMessage message);
        Task<int> EnqueueEmailsAsync(IEnumerable<EmailNotificationMessage> messages);
    }
}
