using System.Xml.Linq;
using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Infrastructure;

namespace Gamehook.Tests.Infrastructure;

// Smoke coverage over every shipped mapper file, discovered from disk rather than named one by
// one - a new mapper under _mappers is covered automatically, and a renamed/moved one can't leave
// a stale hardcoded path behind. This proves each file parses, its macros/conditions/companion
// script all compile, and one full read cycle runs without throwing; it is not a correctness test
// (see e.g. PokemonRedBlueHpIvTests for per-property assertions against a specific mapper, which
// is the case where naming one file by hand is the right call - and where SaveStateDriverTests'
// Mapper_reads_pokemon_red_blue_xml uses the one real save state we have, because it's the mapper
// that state was actually captured from).
//
// Deliberately zero-filled memory, not a real save state, even for the one mapper (pokemon_red_blue)
// a matching state exists for: a save state is captured from one specific game, and every other
// mapper here would be decoding bytes that belong to a different game's memory layout entirely.
// That's not hypothetical - an earlier version of this sweep fed the Pokemon Blue save state to
// every same-platform mapper "opportunistically" and pokemon_yellow.xml's money field promptly
// failed to decode, because Blue's save data isn't valid BCD at Yellow's money address. Zero bytes
// are memory no mapper is entitled to assume real values for either, but at least they're not
// silently wrong data borrowed from an unrelated game.
public class RealMapperSmokeTests : BaseTest
{
    private sealed class ZeroFillDriver : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request)
        {
            var segments = request.Segments
                .Select(s => new IDriver.MemorySegmentSnapshot(s.RegionId, s.StartingAddress, new byte[s.Length]))
                .ToArray();
            return Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow, segments));
        }
    }

    private static readonly HashSet<string> SupportedPlatforms = GameSystem.All.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

    // _mappers/nds/* declares platform="NDS", which GameSystem.All has no entry for yet - that's
    // a real, pre-existing gap (Mapper's constructor rejects the platform outright), not something
    // this sweep should paper over or fail on. Excluded here until NDS support exists; every other
    // shipped mapper's platform is already supported and gets exercised for real.
    private static IEnumerable<TestCaseData> MapperFiles() =>
        AllMapperFiles
            .Where(path => SupportedPlatforms.Contains((string?)XDocument.Load(path).Root?.Attribute("platform") ?? string.Empty))
            .Select(path => new TestCaseData(path).SetArgDisplayNames(Path.GetFileName(path)));

    [TestCaseSource(nameof(MapperFiles))]
    public async Task Mapper_loads_and_completes_a_read_cycle(string mapperPath)
    {
        var mapper = new Mapper(mapperPath, new ZeroFillDriver());

        Assert.That(await mapper.ReadAsync(), Is.True);
    }
}
