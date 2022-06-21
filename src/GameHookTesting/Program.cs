using GameHook.Application;
using GameHook.Domain.Drivers;
using GameHook.Domain.Infrastructure;
using GameHook.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((_, services) =>
        services
            .AddSingleton<DriverOptions>()
            .AddSingleton<IMapperFilesystemProvider, MapperFilesystemProvider>()
            .AddSingleton<IGameHookDriver, RetroArchUdpPollingDriver>()
            .AddSingleton<GameHookInstance>()
    )
    .Build();

var mapperFilesystemProvider = host.Services.GetRequiredService<IMapperFilesystemProvider>();
var mapper = mapperFilesystemProvider.MapperFiles.Single(x => x.Id == "ff4d0e23c73b21068ef1f5deffb6b6ea");

var driver = host.Services.GetRequiredService<IGameHookDriver>();
var instance = host.Services.GetRequiredService<GameHookInstance>();
instance.Load(driver, mapper.Id);

System.Console.WriteLine("Done.");