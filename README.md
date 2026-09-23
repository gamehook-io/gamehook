# Gamehook

Gamehook is desktop software for reading emulator memory and inspecting game data through XML
mapper definitions. It includes a property explorer, raw memory viewer, property inspector, and
pinned watches.

## Repository guide

- [`src/`](src/README.md): application projects and tests.
- [`docs/architecture.md`](docs/architecture.md): boundaries and dependency direction.
- [`tools/`](tools): standalone developer tools, including WebSocket viewer.
- [mappers repository](https://github.com/gamehook-io/mappers): mapper definitions and mapper API
  documentation, intentionally versioned outside this application repository.

Project standards: [contributing](CONTRIBUTING.md), [security](SECURITY.md), and
[code of conduct](CODE_OF_CONDUCT.md).

## Build and run

Install the .NET 10 SDK. Windows x64 and Linux x64 are the release targets.

```sh
dotnet build src/Gamehook.slnx -c Release
```

Select a driver and a matching mapper in the Load screen. For the Save State driver, select a GB/GBC save-state file first. Live RetroArch reads require Network Commands to be enabled; the default endpoint is `127.0.0.1:55355`. SuperShuckie uses its Poke-A-Byte shared-memory protocol, negotiated over UDP on `127.0.0.1:55356` by default. For either network driver the Load screen shows the port prefilled with its default and remembers any change; over the API, pass it as `POST /driver` with `{ "value": "RetroArch", "port": 55400 }`.

To use your own mappers, place them in the `user-mappers` folder inside the Gamehook profile directory (`%LOCALAPPDATA%\Gamehook\user-mappers` on Windows, `~/.local/share/Gamehook/user-mappers` on Linux). Gamehook creates this folder on startup and never modifies it. `MapperDirectory` adds one more folder of custom mappers alongside it. Debug builds skip official mappers and the mapper updater entirely: `MapperDirectory` (`../mappers/dist/` in `appsettings.Development.json`) is their only mapper source, and everything in it is listed as a custom mapper. Custom mappers appear after the official ones in the Load screen, marked **USER MAPPER**. The API lists them with `custom: true` and keys prefixed `custom/`. Official mappers are downloaded to the separate `official-mappers` folder, which is replaced on every update, so don't put your own files there.

Mapper scripts execute locally. Use mapper definitions from sources you trust. Execution limits protect against common runaway scripts; they are not an operating-system sandbox.

## Configuration

The bundled `appsettings.json` is loaded relative to the application executable. Release builds also read an optional `appsettings.json` in the Gamehook profile directory. Profile configuration takes precedence over bundled settings; environment variables and command-line settings take precedence over both.

The default profile is `Gamehook` under the operating system's local application-data directory. Logs and remembered selections are stored there. The profile location cannot be changed. Logs rotate at 10 MiB with ten retained files.

| Setting | Behavior |
| --- | --- |
| `MapperDirectory` | Optional extra directory of custom mappers, listed alongside `user-mappers`. Defaults to unset; official mappers still update. |
| `Mapper:Commit` | Pins managed mappers to a full 40-character commit SHA. Release builds populate this automatically. |
| `Mapper:CommitDate` | Committer date (UTC) of `Mapper:Commit`, shown in the About window. Release builds populate this automatically. |
| `Mapper:Branch` | Tracks this branch when no commit is pinned; defaults to `main`. |
| `Mapper:Source` | `Direct` resolves the branch through GitHub; `Proxy` uses `Mapper:ProxyUrl`. |
| `AppUpdateEnabled` | Disables app update checks when set to `false`. |
| `AppUpdateFeedUrl` | Optional custom HTTPS Velopack feed. Without it, updates use this project's public GitHub Releases. |
| `ContinuousRead` | Defaults to `true`. When `false`, Gamehook stops its continuous driver read loop and refuses WebSocket connections and writes. The workspace stays usable and shows the last-read values; **Read** reads once. Property GETs return the last-read values; add `?read=true` (e.g. `GET /instance/properties?read=true`) to read the driver first. `?read=true` is ignored while continuous read mode is on. Toggle it with **Settings > Continuous Read Mode** or `POST /settings` with `{ "continuousRead": false }`; either change lasts for the current session only and is never written back to `appsettings.json`. |

App updates run only in installed Release builds. A downloaded update can be applied from **Help > About Gamehook > Restart to update**. `--recover` checks for a replacement before starting the UI; add `--feed https://example.com/releases` when using a custom recovery feed.

Managed mapper updates are staged before replacement. Failed installation attempts restore the previous directory; interrupted directory swaps are recovered on the next startup. User-configured mapper directories are never automatically replaced.

## License

See [LICENSE](LICENSE). The license is included in published application files.

## Notes

Some AI-assisted tooling was used during development, primarily for minor drafting and editing support.
