# PC Butler

A C# rewrite of [Downloads Sorter](https://github.com/hmodymac8-droid/downloads-sorter): a
Windows program that keeps your Downloads folder tidy, finds duplicates, cleans junk files,
trims startup apps and keeps private files in an encrypted vault.

**Status: work in progress.** The engine (`src/PCButler.Core`) is written and tested. The
interface is not built yet, so there is nothing to install. See [HANDOVER.md](HANDOVER.md) for
what exists, what is missing and how to continue.

## Build and test the engine

Requires the .NET 10 SDK on Windows.

```
dotnet build src/PCButler.Core
dotnet run --project tests/PCButler.Tests
```

## License

Copyright (c) 2026 hmodymac8-droid.

Released under the [GNU General Public License, version 3](LICENSE).
