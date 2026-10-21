using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace Exam.Hubs
{
    public class NotificationHub : Hub
    {
        public async Task JoinBranchGroup(string branchId)
        {
            if (!string.IsNullOrEmpty(branchId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"Branch_{branchId}");
            }
        }

        public async Task LeaveBranchGroup(string branchId)
        {
            if (!string.IsNullOrEmpty(branchId))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Branch_{branchId}");
            }
        }

        public async Task JoinRoleGroup(string roleName)
        {
            if (!string.IsNullOrEmpty(roleName))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"Role_{roleName}");
            }
        }

        public async Task SendNotification(string user, string message)
        {
            await Clients.User(user).SendAsync("ReceiveNotification", message);
        }
    }
}
