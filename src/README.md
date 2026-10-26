# Source layout

`Gamehook.slnx` is the only solution entry point.

| Project | Responsibility | May reference |
| --- | --- | --- |
| `Gamehook.Domain` | Core models, contracts, session/router behavior, the mapper runtime (read/write cycle, script bridge), property decoding, and the address/expression grammar. No file or network IO. | External libraries only. |
| `Gamehook.Infrastructure` | Drivers, mapper file loading (`MapperCompiler`: XML + companion .js into a `MapperDefinition`), filesystem, logging, updates, and composition helpers. | `Gamehook.Domain` |
| `Gamehook.Presentation` | UI-framework-free view models shared by the desktop and Android UIs (the property tree node, value formatting, search query rules). | `Gamehook.Domain` |
| `Gamehook.RestApi` | The local Web API (Kestrel minimal APIs, WebSocket, OpenAPI/Scalar) and its hosted service. Shared by the desktop and Android hosts. | `Gamehook.Domain`, `Gamehook.Infrastructure` |
| `Gamehook.Android` | Android app: runs the engine and Web API in a foreground service on the device, with a phone UI (load a mapper, browse and edit properties, pins, memory viewer). Build the APK with `scripts/build-android.sh` in a container. | `Gamehook.Domain`, `Gamehook.Infrastructure`, `Gamehook.Presentation`, `Gamehook.RestApi` |
| `Gamehook.UI` | Avalonia desktop app, views, controls, and view models; executable composition root. | All application projects |
| `Gamehook.Tests` | Automated behavior tests and fixtures. | Application projects as needed |

`Gamehook.Domain/Interface` contains application contracts; native processor contracts stay beside
their implementations. Immutable domain values belong in
`Gamehook.Domain/Models`, and mapper-specific native processing belongs in
`Gamehook.Domain/NativeProcessors`. Domain orchestration helpers belong in `Gamehook.Domain/Logic`.
The mapper runtime (`Mapper`, `MapperDefinition`) lives in `Gamehook.Domain/Mapping`.
Keep dependencies flowing inward. New emulator integrations belong in
`Gamehook.Infrastructure/Drivers`; UI code does not belong in Domain or Infrastructure.
Mapper definitions live in the separate [mappers repository](https://github.com/gamehook-io/mappers).
