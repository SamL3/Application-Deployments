using Microsoft.AspNetCore.SignalR;

namespace ApplicationDeployment.Hubs
{
    public class CopyHub : Hub
    {
        public async Task SendProgress(string userId, int progress)
        {
            await Clients.User(userId).SendAsync("ReceiveProgress", progress);
        }

        public async Task SendMessage(string userId, string message)
        {
            await Clients.User(userId).SendAsync("ReceiveMessage", message);
        }
    }
}
