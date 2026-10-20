# TinyWin2

Layered Windows image slimming with explicit inputs, outputs, and workspaces.

TinyWin2 applies declarative, atomic plan operations (registry, DISM features,
capabilities, packages, appx, drivers, files) onto a Windows install image staged in a
VHDX differencing chain, then captures the result as WIM, ESD, or VHDX. Every step is
checkpointed, resumable, diffable, and reversible.

## For agents and automation

See [AGENTS.md](AGENTS.md) for the command map, exit-code and error-code contracts,
JSON output schemas (`schemas/`), workspace invariants, and plan-authoring rules.

## Install

Windows 10/11 x64 only. Every command must run elevated: DISM, diskpart and VHDX attach need it.

**Prebuilt binary** — download `tinywin2-win-x64.zip` from the
[latest release](https://github.com/OpenTritium/tinywin2/releases), extract it, and add the
folder to `PATH`.

**.NET tool** — requires the .NET 10 runtime:

```
dotnet tool install --global TinyWin2.Cli
```

Until the package is published to NuGet.org, install the `.nupkg` attached to a release from
the folder that contains it:

```
dotnet tool install --global TinyWin2.Cli --add-source <folder-with-nupkg> --version 2.0.0-alpha1
```

## Unattended VM installs

`tinywin2 package iso` burns an `Autounattend.xml` into the ISO root by default (`--unattend auto`).
It wipes disk 0, installs the requested image index, skips both OOBE phases, creates a local
administrator and signs it in, so a fresh VM reaches the desktop without interaction. The
partition layout follows the VM firmware:

```
tinywin2 package iso ... --firmware bios   # MBR: System Reserved + Windows (default)
tinywin2 package iso ... --firmware uefi   # GPT: EFI + MSR + Windows
```

Pass `--unattend <file>` to overlay your own answer file (it is validated before being copied)
or `--unattend none` to ship the media unchanged. `tinywin2 unattend generate` writes the file
standalone and `tinywin2 unattend validate --file <file>` checks one.



```
dotnet build TinyWin2.slnx
dotnet test --project tests/TinyWin2.Core.Tests
```

Windows only; builds are NativeAOT (`win-x64`). Functional checks require elevation
and real images: run `tinywin2 doctor --workspace <dir>` first.
