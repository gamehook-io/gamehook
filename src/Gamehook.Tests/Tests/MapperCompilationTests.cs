using Gamehook.Infrastructure;
using Gamehook.Infrastructure.Drivers;

namespace Gamehook.Tests.Tests;

public sealed class MapperCompilationTests : BaseTest
{
    [Test]
    public void Every_shipped_mapper_compiles()
    {
        Assert.That(AllMapperFiles, Is.Not.Empty);

        foreach (var mapperPath in AllMapperFiles)
        {
            var driver = new SaveStateDriver(GetSaveStateFilePath("Pokemon Blue.state0"));
            Assert.DoesNotThrow(() =>
            {
                using var mapper = new Mapper(mapperPath, driver);
            }, mapperPath);
        }
    }
}
