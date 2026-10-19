"""Compile only the optional development bridge; never install or change the clean DLL.

Usage: python3 -B work/starship-pack/build_validation_bridge.py --out <file.dll>
Install deliberately only for the dedicated test save, with its outside-GameData
flag. Archive this separate DLL and flag, then restart KSP before normal play.
"""
from pathlib import Path
import argparse
from compiler_environment import HERE, options, compile_sources


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    options(parser)
    args = parser.parse_args()
    compile_sources(args, args.out, [HERE / 'TestBridge.cs'], development=True, separate=True)


if __name__ == '__main__':
    main()
