# D Starship: instructions for AI agents (Claude Code, Codex, etc.)

This repo is the complete, portable home of the D Starship KSP mod. The old local
project folders and evidence archives were deleted on 2026-10-19. Everything needed
to continue is here.

## Layout
- `GameData/DenizStarshipMissions/`: installed mod (MissionParts.cfg + Plugins/DenizStarship.dll).
- `Ships/VAB/`: the supported fleet (16 Tower Launch craft + Booster Practice + Drone Ship).
- `source/DenizStarship.cs`: the whole normal-play plugin. `TestBridge.cs` is a development-only test harness.
- `source/build_plugin.py --clean`: compiles DenizStarship.dll (needs a .NET SDK; tested 8.0.425).
  KSP is found via `--game-root`, `$KSP_ROOT`, or the default macOS Steam path.
- `source/build_pack.py`: regenerates craft + MissionParts.cfg (needs `mu.py`, see source/README.md).
- `docs/dev/PROJECT_STATUS.md`: current engineering status. `docs/dev/PROJECT_LOG.md`: full history.
- `docs/MANUAL.md`: the player-facing beginner manual. Keep it in sync with control changes.

## Rules
- Target KSP 1.12.5 and SEP 3.1.0. Don't silently upgrade dependencies; part names change between SEP versions.
- Never commit third-party models/textures (Tundra, SEP, SLE, SpaceDust...). Configs may reference them.
- Keep game behaviour claims honest: distinguish "tested in a real flight" from "compiles/loads".
- Use the clean plugin build (`--clean`) for anything shipped in GameData.
- Back up the user's Steam GameData/Ships before overwriting them.
- Record each work session in `docs/dev/PROJECT_LOG.md` (newest last) and update PROJECT_STATUS.md.
- Ship releases as a zip of `GameData/Deniz*` + `Ships/VAB` attached to a GitHub Release.

Historical paths inside docs/dev (e.g. `/Users/.../work/starship-pack/evidence/...`) point to
evidence that no longer exists; treat them as history only.
