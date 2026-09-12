namespace Gamehook.Domain.Interface;

public interface IMapperFactory
{
    IMapper Create(string mapperPath, string driverName, string? driverSourcePath = null);
}
