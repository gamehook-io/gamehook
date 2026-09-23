using Gamehook.Infrastructure;

namespace Gamehook.Tests.Tests;

public sealed class MapperValidationTests : BaseTest
{
    [Test]
    public void Validate_returns_compiled_mapper_metadata()
    {
        var path = GetMapperFilePath("pokemon_red_blue.xml");

        var metadata = MapperValidation.Validate(path);

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Path, Is.EqualTo(Path.GetFullPath(path)));
            Assert.That(metadata.Id, Is.EqualTo("e1396b30-2dc2-11ee-be56-0242ac120002"));
            Assert.That(metadata.Name, Is.EqualTo("Pokemon Red and Blue"));
            Assert.That(metadata.Platform, Is.EqualTo("GB"));
            Assert.That(metadata.PropertyCount, Is.GreaterThan(0));
            Assert.That(metadata.ReferenceTableCount, Is.GreaterThan(0));
            Assert.That(metadata.HasScript, Is.True);
        });
    }
}
