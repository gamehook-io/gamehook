using GameHook.Domain;
using GameHook.Domain.DTOs;
using GameHook.Domain.Interfaces;
using GameHook.WebAPI.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GameHook.WebAPI.ClientNotifiers
{
    public class WebSocketClientNotifier : IClientNotifier
    {
        private readonly IHubContext<UpdateHub> _hubContext;
        public WebSocketClientNotifier(IHubContext<UpdateHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public Task SendGameHookError(ProblemDetailsForClientDTO problemDetails) =>
            _hubContext.Clients.All.SendAsync("GameHookError", problemDetails);

        public Task SendMapperLoading() =>
            _hubContext.Clients.All.SendAsync("MapperLoading");

        public Task SendMapperLoaded() =>
            _hubContext.Clients.All.SendAsync("MapperLoaded");

        public Task SendDriverError(ProblemDetailsForClientDTO problemDetails) =>
            _hubContext.Clients.All.SendAsync("DriverError", problemDetails);

        public Task SendPropertyChanged(string key, uint? address, object? value, byte[]? bytes, bool frozen, string[] fieldsChanged) =>
            _hubContext.Clients.All.SendAsync("PropertyChanged", key, address, value, bytes?.ToIntegerArray(), frozen, fieldsChanged);
    }
}
