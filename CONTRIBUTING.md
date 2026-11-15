# Contributing

Branch from `next` and open pull requests against `next`. What changed in each version goes in [CHANGELOG.md](CHANGELOG.md). Do not change `Version` in `everest.yaml`.

## Running the tests

The unit tests need only the .NET 8 SDK. They compile the game-free sources directly, so they need no Celeste, Everest or Speedrun Tool.

```
cd Tests
dotnet test
```

`SRS_LIVE_SHEET=1 dotnet test` runs the suite against the live reference sheet instead of the snapshots in `Tests/Fixtures/`. Refreshing those snapshots is how a rename on the sheet shows up.

A source file belongs to one of two sides. A file with no use of the game goes in the test project's list, the `<Compile Include="../Source/...">` lines of `Tests/srs.Tests.csproj`. A file that touches the game goes in the `GameCoupled` list of `Tests/SourceListTests.cs`. That test fails when a file under `Source/` is on neither list, or on both. A file on the first list must not use the game, or the test project stops compiling.

New logic that does not need the game should be testable there. When you add a regression test, break the line it guards once and check that it goes red.

## Building the mod

The build needs a Celeste install with Everest, because it compiles against the game's assemblies. The project reads these from the Celeste folder:

- `Celeste.dll`, `FNA.dll`, `MMHOOK_Celeste.dll`, `MonoMod.Utils.dll` and `MonoMod.RuntimeDetour.dll`;
- `Mods/SpeedrunTool.zip`, from which the build extracts `SpeedrunTool.dll` to compile against. Without the zip it falls back to a `SpeedrunTool.dll` placed in the Celeste folder.

Point the build at that folder:

```
dotnet build -p:CelestePrefix=/path/to/Celeste
```

When the repository sits in `<Celeste>/Mods/<name>/`, the folder is found without the option. The build restores `CelesteMod.Publicizer` from NuGet.

The output is in `build/`: `bin/` holds the DLL and PDB, next to `everest.yaml` and `Dialog/`. Copy that folder into `Celeste/Mods/` as a folder named `srs`. A copy of the DLL is also left in `bin/` at the root of the repository, which is the path `everest.yaml` names. Keep the build at zero warnings.

Do not remove `<Compile Remove="Tests/**" />` from `srs.csproj`, or the tests are compiled into `srs.dll`.

## Frame order

`SrsModule` owns the only `Level.Update` hook. Each frame it runs, in this order:

1. `RunWatcher.Update`, which calls the game's own update first and polls the hotkeys right after it;
2. `TierComparison.Update`, which reads that poll;
3. `ExportMenu.Update`, which reads it too.

To change the order, edit that one method. Moving the hotkey poll away from the front of the frame changes what the other two see.

## Startup

`SrsModule.Load` starts by probing Speedrun Tool (`SpeedrunToolMatch.Adopt`). When what srs reads from it does not match, `Load` installs nothing, and the Mod Options title says so in red. Everything else in `Load` runs only after that check passes, and `Unload` does nothing in the other case.

## Dialog text

Every string the player sees is a key in `Dialog/English.txt` and `Dialog/French.txt`. Add each key to both files. Do not use `{0}` placeholders in them: Everest strips the braces, so compose the text in code.

## Network and credentials

No network call runs on the game thread, each has a timeout, and an error is logged and never thrown. The export web app URL is the credential for the user's sheet: keep it out of logs and messages. It is stored in `Saves/srs/export-url.txt`, not in the settings file, which players share.

## Seeing why a run did or did not count

With Everest's Debug Mode on, type `setloglevel srs verbose` in the console. The tracker then writes a `trace` line for each row it opens, holds, closes or drops, with the reason. [README.md](README.md) says how to keep it on.

## Copied folders

`CelesteHotkeys/src/` and `Source/MenuTools/` are copies of code kept elsewhere. Do not edit them here: a change made in the copy is lost the next time the copy is refreshed. For `Source/MenuTools/`, `menutools.txt` lists the files and the upstream commit they came from, and the licence that comes with them is `Source/MenuTools/LICENSE`. If one needs a fix, open an issue or say so in your pull request.
