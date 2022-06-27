using GameHook.Domain.DTOs;

namespace GameHook.Domain.Interfaces
{
    public interface IClientNotifier
    {
        Task SendGameHookError(ProblemDetailsForClientDTO details);
        Task SendMapperLoading();
        Task SendMapperLoaded();
        Task SendDriverError(ProblemDetailsForClientDTO details);
        Task SendPropertyChanged(string key, object? value, byte[]? bytes, bool frozen);
        Task SendPropertyFrozen(string key);
        Task SendPropertyUnfrozen(string key);
    }
}
