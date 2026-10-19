# Source

| File | Purpose |
|---|---|
| `DenizStarship.cs` | The plugin: cargo doors and locks, arm, payload release guards, legs, flaps, engine modes, RCS modes, tower mount staging, tower catch. |
| `TestBridge.cs` | Development-only automation harness used for the recorded test flights. Never ship it in GameData. |
| `build_plugin.py` | `python3 -B build_plugin.py --clean` → `DenizStarship.dll`. Copy it to `GameData/DenizStarshipMissions/Plugins/`. |
| `build_validation_bridge.py` | Builds the separate TestBridge DLL (development only). |
| `compiler_environment.py` | Finds the .NET SDK and the KSP/SEP/SLE assemblies to reference. |
| `build_pack.py`, `config.py`, `geometry.py` | Regenerate the craft files and `MissionParts.cfg`. They write into your KSP install, so back it up first, or use `--dry-run --out <folder>`. |

Requirements: Python 3.9+, a .NET SDK (tested 8.0.425, `--dotnet-root` or `DENIZ_KSP_DOTNET_ROOT`),
and KSP 1.12.5 with the required mods installed (set `KSP_ROOT` if it isn't in the default macOS Steam location).

`geometry.py` imports `mu.py`, the Unity .mu model reader from
[taniwha/io_object_mu](https://github.com/taniwha/io_object_mu) (GPL). Download it into this
folder if you need to rebuild craft. It isn't included because of its license. NumPy is also needed.
