"""Compile the custom plugin locally; --clean is required for normal play."""
import argparse
from pathlib import Path
from compiler_environment import HERE, options, compile_sources

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--clean', action='store_true', help='Exclude all TestBridge development code')
    parser.add_argument('--out', type=Path, default=HERE / 'DenizStarship.dll')
    options(parser)
    args = parser.parse_args()
    if args.out.name != 'DenizStarship.dll': parser.error('--out filename must be DenizStarship.dll to preserve assembly identity')
    sources = [HERE / 'DenizStarship.cs']
    if not args.clean: sources.append(HERE / 'TestBridge.cs')
    compile_sources(args, args.out, sources, development=not args.clean)

if __name__ == '__main__': main()
