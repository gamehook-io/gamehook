using GameHook.Domain.DTOs;
using GameHook.Domain.Interfaces;

namespace GameHook.WebAPI.ClientNotifiers
{
    public class FilesystemClientNotifier : IClientNotifier
    {
        public Task SendGameHookError(ProblemDetailsForClientDTO _) => Task.CompletedTask;

        public async Task SendMapperLoading()
        {
            if (Directory.Exists(BuildEnvironment.OutputPropertiesDirectory))
            {
                Directory.Delete(BuildEnvironment.OutputPropertiesDirectory, true);
            }

            Directory.CreateDirectory(BuildEnvironment.OutputPropertiesDirectory);

            await Task.CompletedTask;
        }

        public Task SendMapperLoaded() => Task.CompletedTask;

        public Task SendDriverError(ProblemDetailsForClientDTO _) => Task.CompletedTask;

        public async Task SendPropertyChanged(string key, object? value, byte[]? bytes, bool frozen)
        {
            await File.WriteAllTextAsync(Path.Combine(BuildEnvironment.OutputPropertiesDirectory, $"{key}.txt"), value?.ToString());
        }

        public Task SendPropertyFrozen(string _) => Task.CompletedTask;

        public Task SendPropertyUnfrozen(string _) => Task.CompletedTask;
    }
}
