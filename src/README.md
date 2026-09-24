# Source layout

`Gamehook.slnx` is the only solution entry point.

| Project | Responsibility | May reference |
| --- | --- | --- |
| `Gamehook.Domain` | Core models, contracts, session/router behavior, the mapper runtime (read/write cycle, script bridge), property decoding, and the address/expression grammar. No file or network IO. | External libraries only. |
| `Gamehook.Infrastructure` | Drivers, mapper file loading (`MapperCompiler`: XML + companion .js into a `MapperDefinition`), filesystem, logging, updates, and composition helpers. | `Gamehook.Domain` |
| `Gamehook.UI` | Avalonia app, views, controls, and view models; in-process HTTP/WebSocket boundary (`RestApi/`); executable composition root. | All application projects |
| `Gamehook.Tests` | Automated behavior tests and fixtures. | Application projects as needed |

`Gamehook.Domain/Interface` contains application contracts; native processor contracts stay beside
their implementations. Immutable domain values belong in
`Gamehook.Domain/Models`, and mapper-specific native processing belongs in
`Gamehook.Domain/NativeProcessors`. Domain orchestration helpers belong in `Gamehook.Domain/Logic`.
The mapper runtime (`Mapper`, `MapperDefinition`) lives in `Gamehook.Domain/Mapping`.
Keep dependencies flowing inward. New emulator integrations belong in
`Gamehook.Infrastructure/Drivers`; UI code does not belong in Domain or Infrastructure.
Mapper definitions live in the separate [mappers repository](https://github.com/gamehook-io/mappers).
