namespace Gamehook.Tests.Tests;

public sealed class PokemonRedBlue : BaseTest
{
    private static readonly string[] States =
    [
        "Pokemon Blue.state0",
        "Pokemon Blue.state1",
        "Pokemon Blue.state2",
    ];

    [TestCaseSource(nameof(States))]
    public async Task Reads_common_player_data(string state)
    {
        var mapper = await CreateSaveStateMapper("pokemon_red_blue.xml", state);

        Assert.Multiple(() =>
        {
            Assert.That(mapper.Properties["meta.game_name"].Value, Is.EqualTo("Red and Blue"));
            Assert.That(mapper.Properties["meta.generation"].Value, Is.EqualTo(1));
            Assert.That(mapper.Properties["player.name"].Value, Is.Not.Null.And.Not.EqualTo(""));
            Assert.That(mapper.Properties["player.player_id"].Value, Is.Not.Null);
            Assert.That(mapper.Properties["player.starter_pokemon"].Value, Is.Not.Null);
            Assert.That(mapper.Properties["bag.money"].Value, Is.Not.Null);
            Assert.That(mapper.Properties["bag.coins"].Value, Is.Not.Null);
            Assert.That(mapper.Properties["bag.item_count"].Value, Is.EqualTo(0x12));
            Assert.That(mapper.Properties["player.pokedex_seen"].Bytes.Length, Is.EqualTo(19));
            Assert.That(mapper.Properties["player.pokedex_caught"].Bytes.Length, Is.EqualTo(19));
        });
    }

    [TestCase("Pokemon Blue.state0", 1)]
    [TestCase("Pokemon Blue.state1", 6)]
    [TestCase("Pokemon Blue.state2", 6)]
    public async Task Reads_party_and_badges_from_each_save(string state, int expectedTeamCount)
    {
        var mapper = await CreateSaveStateMapper("pokemon_red_blue.xml", state);

        Assert.That(mapper.Properties["player.team_count"].Value, Is.EqualTo(expectedTeamCount));

        for (var slot = 0; slot < expectedTeamCount; slot++)
        {
            var prefix = $"player.team.{slot}";
            Assert.Multiple(() =>
            {
                Assert.That(mapper.Properties[$"{prefix}.species"].Value, Is.Not.Null);
                Assert.That(mapper.Properties[$"{prefix}.nickname"].Value, Is.Not.Null);
                Assert.That(mapper.Properties[$"{prefix}.level"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.exp"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.type_1"].Value, Is.Not.Null);
                Assert.That(mapper.Properties[$"{prefix}.type_2"].Value, Is.Not.Null);
                Assert.That(mapper.Properties[$"{prefix}.ot_id"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.catch_rate"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.status_condition"].Value, Is.Not.Null);
                Assert.That(mapper.Properties[$"{prefix}.stats.hp_max"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.stats.attack"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.stats.defense"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.stats.speed"].Value, Is.GreaterThan(0));
                Assert.That(mapper.Properties[$"{prefix}.stats.special"].Value, Is.GreaterThan(0));
            });

            for (var move = 0; move < 4; move++)
            {
                var movePrefix = $"{prefix}.moves.{move}";
                Assert.That(mapper.Properties[$"{movePrefix}.move"].Value, Is.Not.Null);
                Assert.That(mapper.Properties[$"{movePrefix}.pp"].Value, Is.InRange(0, 63));
                Assert.That(mapper.Properties[$"{movePrefix}.pp_up"].Value, Is.InRange(0, 3));
            }

            foreach (var stat in new[] { "attack", "defense", "speed", "special" })
                Assert.That(mapper.Properties[$"{prefix}.ivs.{stat}"].Value, Is.InRange(0, 15));

            foreach (var stat in new[] { "hp", "attack", "defense", "speed", "special" })
                Assert.That(mapper.Properties[$"{prefix}.evs.{stat}"].Value, Is.GreaterThanOrEqualTo(0));
        }

        for (var badge = 0; badge < 8; badge++)
            Assert.That(mapper.Properties[$"player.badges.{badge}"].Value, Is.True, $"badge {badge}");
    }

    [Test]
    public async Task Reads_wild_battle_and_both_active_pokemon_from_state2()
    {
        var mapper = await CreateSaveStateMapper("pokemon_red_blue.xml", "Pokemon Blue.state2");

        Assert.Multiple(() =>
        {
            Assert.That(mapper.Properties["battle.mode"].Value, Is.EqualTo("Wild"));
            Assert.That(mapper.Properties["battle.type"].Value, Is.EqualTo("Normal"));
            Assert.That(mapper.Properties["battle.opponent.active_pokemon.species"].Value, Is.EqualTo("Machoke"));
            Assert.That(mapper.Properties["battle.opponent.active_pokemon.level"].Value, Is.EqualTo(42));
            Assert.That(mapper.Properties["player.active_pokemon.species"].Value, Is.EqualTo("Mew"));
            Assert.That(mapper.Properties["player.active_pokemon.level"].Value, Is.EqualTo(70));
            Assert.That(mapper.Properties["battle.player.active_pokemon.stats.hp"].Value, Is.EqualTo(260));
            Assert.That(mapper.Properties["battle.player.active_pokemon.stats.hp_max"].Value, Is.EqualTo(260));
            Assert.That(mapper.Properties["battle.opponent.active_pokemon.stats.hp"].Value, Is.GreaterThan(0));
            Assert.That(mapper.Properties["battle.opponent.active_pokemon.stats.hp_max"].Value, Is.GreaterThan(0));
        });
    }

    [TestCase("Pokemon Blue.state0", "To Battle")]
    [TestCase("Pokemon Blue.state1", "Battle")]
    [TestCase("Pokemon Blue.state2", "Battle")]
    public async Task Derived_state_matches_save_state_screenshot(string state, string expectedState)
    {
        var mapper = await CreateSaveStateMapper("pokemon_red_blue.xml", state);

        Assert.Multiple(() =>
        {
            Assert.That(mapper.Properties["meta.state"].Value, Is.EqualTo(expectedState));
            Assert.That(mapper.Properties["battle.outcome"].Value, Is.Null);
            Assert.That(mapper.Properties["overworld.map_name"].Value, Is.Not.Null);
            Assert.That(mapper.Properties["overworld.x"].Value, Is.GreaterThan(0));
            Assert.That(mapper.Properties["overworld.y"].Value, Is.GreaterThan(0));
            Assert.That(mapper.Properties["player.active_pokemon.level"].Value, Is.GreaterThan(0));
        });

        var activeSlot = Convert.ToInt32(mapper.Properties["player.party_position"].Value);
        Assert.That(mapper.Properties["player.active_pokemon.level"].Value,
            Is.EqualTo(mapper.Properties[$"player.team.{activeSlot}.level"].Value));
    }
}
