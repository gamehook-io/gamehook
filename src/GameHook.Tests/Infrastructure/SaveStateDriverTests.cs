using GameHook.Domain;
using GameHook.Domain.Interface;
using GameHook.Infrastructure.Drivers;

namespace GameHook.Tests.Infrastructure;

public class Tests : BaseTest
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task Basic_Pokemon_Blue_STATE()
    {
        var statePath = GetSaveStateFilePath("Pokemon Blue-00.state");
        var driver = new SaveStateDriver(statePath);

        var response = await driver.Read(new IDriver.Request(GameSystem.GB, [
            new IDriver.MemorySegmentRequest("WRAM", 0, 4),
            new IDriver.MemorySegmentRequest("VRAM", 0, 4),
            new IDriver.MemorySegmentRequest("SRAM", 0, 4),
            new IDriver.MemorySegmentRequest("OAM", 0, 4),
            new IDriver.MemorySegmentRequest("IO", 0, 4),
            new IDriver.MemorySegmentRequest("HRAM", 0, 4),
            new IDriver.MemorySegmentRequest("IE", 0, 1),
            new IDriver.MemorySegmentRequest("Wave RAM", 0, 4)
        ]));

        Assert.Multiple(() =>
        {
            Assert.That(response.Segments, Has.Count.EqualTo(8));
            Assert.That(response.Segments[0].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x00, 0x90, 0x00, 0x00 }));
            Assert.That(response.Segments[1].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x07, 0x07, 0x08, 0x0F }));
            Assert.That(response.Segments[2].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x00 }));
            Assert.That(response.Segments[3].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x4C, 0x48, 0x00, 0x00 }));
            Assert.That(response.Segments[4].Bytes.ToArray(), Is.EqualTo(new byte[] { 0xFF, 0x02, 0xFE, 0xFF }));
            Assert.That(response.Segments[5].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x3E, 0xC3, 0xE0, 0x46 }));
            Assert.That(response.Segments[6].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x0D }));
            Assert.That(response.Segments[7].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x02, 0x46, 0x8A, 0xCE }));
        });
    }

    [Test]
    public async Task Read_returns_SRAM_from_raw_save_file()
    {
        var savePath = Path.GetTempFileName();

        try
        {
            await File.WriteAllBytesAsync(savePath, [0x00, 0x01, 0x02, 0x03]);
            var driver = new SaveStateDriver(savePath);

            var response = await driver.Read(new IDriver.Request(GameSystem.GB, [
                new IDriver.MemorySegmentRequest("SRAM", 1, 2)
            ]));

            Assert.That(response.Segments[0].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02 }));
        }
        finally
        {
            File.Delete(savePath);
        }
    }

    [Test]
    public void Read_rejects_systems_not_supported_by_driver()
    {
        var driver = new SaveStateDriver("unused.state");

        Assert.ThrowsAsync<NotSupportedException>(async () => await driver.Read(new IDriver.Request(
            GameSystem.SNES,
            [new IDriver.MemorySegmentRequest("SRAM", 0, 1)])));
    }

    [Test]
    public void Read_rejects_regions_not_contained_by_state()
    {
        var statePath = GetSaveStateFilePath("Pokemon Blue-00.state");
        var driver = new SaveStateDriver(statePath);

        Assert.ThrowsAsync<NotSupportedException>(async () => await driver.Read(new IDriver.Request(
            GameSystem.GB,
            [new IDriver.MemorySegmentRequest("RDRAM", 0, 1)])));
    }

    [Test]
    public async Task Mapper_reads_pokemon_red_blue_xml()
    {
        var mapper = await CreateSaveStateMapper("pokemon_red_blue.xml", "Pokemon Blue-00.state");
        
        Assert.Multiple(() =>
        {
            Assert.That(mapper.Properties["meta.game_name"].Value, Is.EqualTo("Red and Blue"));
            Assert.That(mapper.Properties["player.team_count"].Value, Is.EqualTo(6));
            Assert.That(mapper.Properties["player.team_count"].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x06 }));

            // macro-expanded (pokemon) property inside a var:address-parameterized class (party_pokemon)
            Assert.That(mapper.Properties.ContainsKey("player.team.0.species"), Is.True);
            Assert.That(mapper.Properties["player.team.0.level"].Value, Is.Not.EqualTo(0));
            // nested class-in-class (moves) with its own var:moveAddress
            Assert.That(mapper.Properties.ContainsKey("player.team.0.moves.0.move"), Is.True);
        });
    }
}
