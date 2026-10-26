namespace Gamehook.Presentation;

public static class PropertySearch
{
    /// Turns what a user typed into a property-path query: spaces stand in for the dots of a
    /// dotted path, so "player team" finds "player.team.0.level".
    public static string NormalizeQuery(string searchText) => string.Join(
        '.',
        searchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
