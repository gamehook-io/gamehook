# Gamehook

Gamehook is a desktop tool for reading emulator memory and inspecting game data through XML mapper definitions. It includes a property explorer, raw memory viewer, property inspector, and pinned watches.

## Build and run

Install the .NET 10 SDK. Windows x64 and Linux x64 are the release targets.

```sh
dotnet build src/Gamehook.slnx -c Release
./dev.sh
```

Select a driver and a matching mapper in the Load screen. For the Save State driver, select a GB/GBC save-state file first. Live RetroArch reads require Network Commands to be enabled; the default endpoint is `127.0.0.1:55355`. SuperShuckie uses its Poke-A-Byte shared-memory protocol.

Mapper scripts execute locally. Use mapper definitions from sources you trust. Execution limits protect against common runaway scripts; they are not an operating-system sandbox.

## Configuration

The bundled `appsettings.json` is loaded relative to the application executable. Release builds also read an optional `appsettings.json` in the Gamehook profile directory. Profile configuration takes precedence over bundled settings, environment variables, and command-line settings.

The default profile is `Gamehook` under the operating system's local application-data directory. Logs and remembered selections are stored there. `GamehookProfileDirectory` overrides the profile location. Logs rotate at 10 MiB with ten retained files.

| Setting | Behavior |
| --- | --- |
| `MapperDirectory` | Uses a local mapper directory and disables automatic mapper replacement. |
| `Mapper:Commit` | Pins managed mappers to a full 40-character commit SHA. Release builds populate this automatically. |
| `Mapper:Branch` | Tracks this branch when no commit is pinned; defaults to `main`. |
| `Mapper:Source` | `Direct` resolves the branch through GitHub; `Proxy` uses `Mapper:ProxyUrl`. |
| `AppUpdateEnabled` | Disables app update checks when set to `false`. |
| `AppUpdateFeedUrl` | Optional custom HTTPS Velopack feed. Without it, updates use this project's public GitHub Releases. |

App updates run only in installed Release builds. A downloaded update can be applied from **Help > About Gamehook > Restart to update**. `--recover` checks for a replacement before starting the UI; add `--feed https://example.com/releases` when using a custom recovery feed.

Managed mapper updates are staged before replacement. Failed installation attempts restore the previous directory; interrupted directory swaps are recovered on the next startup. User-configured mapper directories are never automatically replaced.

## Tests

Clone the mapper repository beside this repository:

```text
parent/
  gamehook/
  mappers/
    dist/
```

Alternatively, set `GAMEHOOK_TEST_MAPPERS` to the mapper `dist` directory. Run:

```sh
dotnet test src/Gamehook.slnx -c Release
dotnet test src/Gamehook.slnx -c Debug
```

Tests use local UDP fixtures and the bundled Pokémon Blue save state. Shared-memory protocol tests run on Linux and will not overwrite an existing emulator shared-memory file. Performance benchmarks are explicit tests and do not run in the normal regression suite.

The timing harness accepts a mapper path:

```sh
dotnet run --project src/Gamehook.TimingMetrics -- ../mappers/dist/gbc/pokemon_red_blue.xml --samples 30
```

## Release

The Release workflow accepts a `yy.mm.N` tag (for example, `26.09.1`). Manual runs accept an exact version, a month (`26.09`), or no input (the current UTC month). For a month, the next number is selected from existing GitHub releases (including drafts) and tags, starting at 1 for each year/month: `26.09.1`, `26.09.2`, then `26.10.1`. Release workflows are serialized to prevent number collisions. Years represent 2000–2099. GitHub and About show the calendar version; .NET and Velopack use the three-part numeric version (`2026.9.1`) internally for update ordering without leading zeros. Use increasing versions. About shows the mapper reference and short commit SHA, without a date. The workflow resolves and tests an exact mapper commit, builds self-contained binaries, checks startup, and packages Windows and Linux with Velopack. Linux packaging requires `squashfs-tools`; Linux UI checks also require Xvfb and xdotool.

Both platforms must finish successfully before their assets are uploaded to a draft release. The release becomes public only after both uploads complete. Published releases cannot be overwritten by rerunning the workflow. Calendar releases are stable releases, not prereleases.

Commit mapper changes in the mapper repository before releasing Gamehook, then supply that commit as `mapper_commit`. A local mapper change does not automatically become part of a pinned remote release.

Before public distribution, validate install, update, recovery, and uninstall on native Windows and Linux machines. Keep the generated Velopack feed files and packages with their release assets. See [release review](RELEASE_REVIEW.md) for verification evidence and remaining environment-dependent checks.

## License

See [LICENSE](LICENSE). The license is included in published application files.

## Notes

Some AI-assisted tooling was used during development, primarily for minor drafting and editing support.
