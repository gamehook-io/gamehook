namespace Gamehook.Domain.Interface;

public interface IDriverFactory
{
    IDriver Create(string name, string? sourcePath = null);
}
