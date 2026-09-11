namespace Gamehook.Infrastructure.MapperUpdate;

// Written into the managed mapper directory after every successful update so the next launch
// knows what's already on disk without re-resolving/re-downloading to compare. Mappers always
// come from GitHub regardless of MapperUpdateSource, so comparison keys off the repository, not
// the resolve source. Reference is the branch name when tracking one, or "pinned" when Commit was
// configured explicitly - purely informational, CommitSha is what update comparisons key off.
public sealed record MapperVersionManifest(
    string Repository,
    string Reference,
    string CommitSha,
    DateTimeOffset UpdatedAtUtc);
