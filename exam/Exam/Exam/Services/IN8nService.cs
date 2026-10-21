using System.Threading.Tasks;

namespace Exam.Services
{
    public interface IN8nService
    {
        Task<bool> TriggerCompanySessionNotificationAsync(object payload);
        Task<bool> TriggerWaveNominationNotificationAsync(object payload);
    }
}
