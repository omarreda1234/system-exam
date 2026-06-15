using DocumentFormat.OpenXml.InkML;
using Microsoft.AspNetCore.SignalR;

namespace Exam.Hubs
{
    public class ImportHub : Hub
    {
        
        public override async Task OnConnectedAsync()
        {
            string connectionId = Context.ConnectionId;
            await base.OnConnectedAsync();
        }
    }
}
