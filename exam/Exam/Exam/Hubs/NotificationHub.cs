using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace Exam.Hubs
{
    public class NotificationHub : Hub
    {
        public async Task SendNotification(string user, string message)
        {
            await Clients.User(user).SendAsync("ReceiveNotification", message);
        }
    }
}
