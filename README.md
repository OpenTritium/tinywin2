# TinyWin2

Layered Windows image slimming with explicit inputs, outputs, and workspaces.

TinyWin2 applies declarative, atomic plan operations (registry, DISM features,
capabilities, packages, appx, drivers, files) onto a Windows install image staged in a
VHDX differencing chain, then captures the result as WIM, ESD, or VHDX. Every step is
checkpointed, resumable, diffable, and reversible.

## For agents and automation

See [AGENTS.md](AGENTS.md) for the command map, exit-code and error-code contracts,
JSON output schemas (`schemas/`), workspace invariants, and plan-authoring rules.

## Build & test

```
dotnet build TinyWin2.slnx
dotnet test --project tests/TinyWin2.Core.Tests
```

Windows only; builds are NativeAOT (`win-x64`). Functional checks require elevation
and real images: run `tinywin2 doctor --workspace <dir>` first.
