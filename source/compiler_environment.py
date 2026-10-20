"""Resolve a local .NET SDK and installed KSP references before output writes."""
from pathlib import Path
from datetime import datetime
from zoneinfo import ZoneInfo
import os, re, shutil, subprocess
HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
LEGACY = ROOT / 'tools/dotnet'

def options(parser):
    parser.add_argument('--dotnet-root', type=Path, help='SDK root containing dotnet; overrides discovery')
    parser.add_argument('--sdk-version', help='Exact installed SDK version, e.g. 8.0.425')
    parser.add_argument('--game-root', type=Path, help='KSP installation; default PROJECT_PATHS.json')

def fail(message):
    raise SystemExit('KSP build: ' + message)

def compiler(args):
    explicit = args.dotnet_root or os.environ.get('DENIZ_KSP_DOTNET_ROOT')
    if explicit:
        candidates = [Path(explicit).expanduser() / 'dotnet']
    else:
        candidates = [HERE / 'dotnet/dotnet', ROOT / 'tools/dotnet/dotnet']
        found = shutil.which('dotnet')
        if found: candidates.append(Path(found))
        candidates.append(LEGACY / 'dotnet')
    errors = []
    for host in candidates:
        if not host.is_file():
            errors.append(str(host) + ' missing'); continue
        try:
            found = subprocess.run([str(host), '--list-sdks'], capture_output=True, text=True, timeout=15)
        except (OSError, subprocess.TimeoutExpired) as exc:
            errors.append(str(host) + ': ' + str(exc)); continue
        rows = []
        for line in found.stdout.splitlines():
            match = re.fullmatch(r'(\d+\.\d+\.\d+) \[(.+)\]', line.strip())
            if match:
                version, parent = match.groups()
                csc = Path(parent) / version / 'Roslyn/bincore/csc.dll'
                if csc.is_file(): rows.append((tuple(map(int, version.split('.'))), version, csc))
        if args.sdk_version: rows = [row for row in rows if row[1] == args.sdk_version]
        if rows:
            _, version, csc = max(rows)
            print('Compiler:', host, 'SDK', version, flush=True)
            return host, csc, version
        errors.append(str(host) + ': requested stable SDK/compiler not installed')
    fail('install a .NET SDK (a runtime alone is insufficient), then set --dotnet-root or DENIZ_KSP_DOTNET_ROOT. Tested SDK: 8.0.425. ' + '; '.join(errors))

def game_root(args):
    import json
    game = args.game_root or Path(os.environ.get('KSP_ROOT', Path.home() / 'Library/Application Support/Steam/steamapps/common/Kerbal Space Program'))
    game = game.expanduser().resolve()
    if not (game / 'KSP.app/Contents/Resources/Data/Managed').is_dir():
        fail('KSP managed assemblies missing under ' + str(game) + '; use --game-root pointing to Steam KSP 1.12.5')
    return game

def references(game, development=False, separate=False):
    names = ['mscorlib.dll', 'System.dll', 'System.Core.dll', 'Assembly-CSharp.dll',
             'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll',
             'UnityEngine.AnimationModule.dll', 'UnityEngine.ScreenCaptureModule.dll']
    if development:
        names += ['Assembly-CSharp-firstpass.dll', 'UnityEngine.UI.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.TextRenderingModule.dll']
    refs = [game / 'KSP.app/Contents/Resources/Data/Managed' / name for name in names]
    refs += [game / 'GameData/StarshipExpansionProject/Plugin/StarshipExpansionProject.dll', game / 'GameData/StarshipLaunchExpansion/Plugin/StarshipLaunchExpansion.dll']
    if development:
        refs += [game / 'GameData/MechJeb2/Plugins/MechJeb2.dll', game / 'GameData/Trajectories/Plugin/Trajectories.dll', game / 'GameData/FMRS/Plugins/FMRSContinued.dll']
    if separate: refs.append(game / 'GameData/LusiadosStarshipMissions/Plugins/DenizStarship.dll')
    missing = [str(ref) for ref in refs if not ref.is_file()]
    if missing: fail('required installed assemblies missing: ' + ', '.join(missing))
    return refs

def compile_sources(args, out, sources, development=False, separate=False):
    host, csc, version = compiler(args)
    refs = references(game_root(args), development, separate)
    missing = [str(src) for src in sources if not src.is_file()]
    if missing: fail('C# sources missing: ' + ', '.join(missing))
    out = out.expanduser().resolve()
    if out.exists():
        now = datetime.now(ZoneInfo('Europe/Brussels'))
        bk = ROOT / 'Backups' / now.strftime('%Y-%m-%d-codex-compiler-output-%H%M%S-%f')
        bk.mkdir(parents=True, exist_ok=False); shutil.copy2(out, bk / out.name)
        print('Prior output backup:', bk / out.name, flush=True)
    out.parent.mkdir(parents=True, exist_ok=True)
    cmd = [str(host), str(csc), '/nologo', '/target:library', '/nostdlib', '/langversion:7.3', '/out:' + str(out)]
    cmd += ['/reference:' + str(ref) for ref in refs] + [str(src) for src in sources]
    run = subprocess.run(cmd)
    if run.returncode: fail('C# compiler failed (exit ' + str(run.returncode) + '); output is not accepted or installed')
    print(out, flush=True)
    return out
