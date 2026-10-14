# AGENTS.md

Operating guide for AI agents and automation driving **TinyWin2** — a layered Windows
image slimming tool with explicit inputs, outputs, and workspaces.

Everything here is Windows-only and requires an elevated shell (DISM, diskpart, VHDX
attach). Run `tinywin2 doctor --workspace <dir>` first; it checks elevation, tools,
and disk space.

## Command map

```
tinywin2 doctor --workspace <dir>   # environment readiness (exit 1 on failed required checks)
tinywin2 inspect --input <iso>  # list image indexes of an ISO / media folder / WIM / ESD
tinywin2 source validate --input <iso> [--kind image|media|iso]
tinywin2 plan list|show|validate
tinywin2 profile list|show|export|validate
tinywin2 build    --input --index --output --workspace --format wim|esd|vhdx
tinywin2 preview  --input --index --workspace        # what would each plan change?
tinywin2 package iso --input --install-image --output --workspace --oscdimg
tinywin2 layer list|diff|extract|capture --workspace ...
```

Typical workflow:

```
tinywin2 inspect --input win11.iso
tinywin2 preview --input win11.iso --index 1 --workspace ./ws \
  --profile profiles/laptop.json
tinywin2 build --input win11.iso --index 1 --output out/install.esd --workspace ./ws \
  --format esd --single-layer --profile profiles/laptop.json
tinywin2 package iso --input --install-image out/install.esd \
  --output out/win11-slim.iso --workspace ./pkg --oscdimg "C:/oscdimg/oscdimg.exe"
```

`profiles/laptop.json` is the single shipped profile: every plan in the catalog
enabled, except a keep-set (`enabled: false`) for laptop-functional components —
RDP + OpenSSH, SMB sharing core, Hyper-V/VirtualMachinePlatform/WSL/Containers,
WLAN + RAS chain + Bluetooth (incl. device association) + audio + location +
biometrics + hotspot + consumer drivers, and usability baselines (Client.CBS
shell package, SysMain, Notepad, WOW64, DirectX config, display broker). Windows
Update stays on by default (`registry.update-task-cache` kept) and the power
scheme stays Balanced. Printing and SMB1 are removed by default — flip the
corresponding plan to `enabled: false` in the profile to keep them.

A plan disabled by a profile cannot be re-enabled via `--plan`; edit the profile
selection instead. To build an extreme image (drop all keeps), duplicate the
profile and delete the `enabled: false` entries. Selection order in the profile
is execution order: keep the alphabetical main block, and keep the tail task
cleanups (`registry.update-task-cache`, `fs.sync-center`, `fs.dead-tasks`)
trailing the `service.*` block.

## Machine-readable surfaces

**Exit codes (stable):** `0` success · `1` failure · `130` cancelled (Ctrl+C) ·
`3` unsupported platform.

**Error codes (stable):** failures print to stderr. In JSON mode (`--json`,
`--json-events`, or `TINYWIN2_ERRORS=json`) one object per failure:

```json
{"error":{"code":"step-failed","message":"plan step 'x' failed at layer 007: ...","hint":"rerun the same build command with --resume ..."}}
```

Codes: `plan-validation-failed`, `plan-resolution-failed`, `parameter-binding-failed`,
`step-failed`, `environment-check-failed`, `native-tool-failed`, `operation-failed`,
`output-exists`, `workspace-conflict`, `not-found`, `io-failed`, `invalid-argument`,
`invalid-operation`, `canceled`, `unsupported-platform`, `internal`.
Set `TINYWIN2_DEBUG=1` for a stack trace.

**Build progress:** `--json-events` streams one JSON object per line on stdout
(schema: `schemas/build-events.schema.json`); the last line has `"phase":"result"`.
Tool logs go to `<workspace>/logs/*.log`, never stdout in this mode.

**Build manifest:** `<workspace>/tinywin2-manifest.json` after every build
(schema: `schemas/build-manifest.schema.json`). It contains the artifact xxh3 hash,
per-layer records, and per-operation applied/skipped outcomes.

**Other `--json` outputs:** `doctor`, `inspect`, `source validate`, `plan list`,
`plan validate`, `preview`, `layer list`, `layer diff`.

## Workspace invariants

- A build workspace must be an empty directory (a `logs` child is tolerated), or a
  previous workspace resumed with `--resume`.
- Existing output files require `--overwrite`.
- `--resume` reuses committed checkpoint layers whose plan step fingerprint matches;
  everything above the match is truncated and rebuilt. Fingerprints cover plan content,
  parameters, and any referenced asset file.
- `--single-layer` trades per-step atomicity for speed; progress is checkpointed and
  replayed from the source on resume.
- Post-mortem: `layer list`, then `layer diff --from N --to N+1` shows exactly what a
  step changed; `layer capture --layer N` exports that state as WIM/ESD. These are
  read-only against the chain.

## Authoring plans

One plan = one JSON file in `plans/`, validated by `schemas/plan-v5.schema.json`.
Each carries one or more atomic operations with the three-verb vocabulary:

- `"action": "apply"` — make the target present/in the desired state
  (registry values, service start modes, fs.path asset copy, …)
- `"action": "remove"` — make the target absent
- `"action": "cleanup"` — run-once maintenance (component-store cleanup)

Catalog organization: one plan per *decision*, not per mechanism. Same-intent
mechanisms (files + registry + policy for one feature) live in one plan
(e.g. `fs.onedrive`, `registry.responsiveness-tuning`); per-entity plans stay
split where the choice is individual (one service per `service.*` plan, one
appx per `appx.*` plan, security toggles like UAC/VBS/firewall never bundled).
When merging, keep the merged id's alphabetical position stable: the base
profile lists selections alphabetically, and selection order is execution
order — post-servicing cleanup plans (`registry.control-panel-orphans`,
`registry.update-task-cache`, `fs.sync-center`, `fs.dead-tasks`) deliberately
trail the `service.*` block.

Parameters are referenced inside specs as `{"$parameter":"name"}` and translated via
`{"$map":{"parameter":"n","cases":{...},"default":...}}`. Bundled files live in
`plans/assets/<planId>/` and are referenced by fs.path apply operations; asset content
participates in the step fingerprint.

**Always self-check** after writing or editing a plan:

```
tinywin2 plan validate --file plans/my.plan.json --json
```

It runs the full production pipeline (schema, dependencies, parameter and operation
binding) and reports errors as a structured list.

Profiles (schema v3) snapshot selections + parameters with `profile export`; a plan
disabled by a profile cannot be re-enabled via `--plan` — remove the exclusion in the
profile instead.

## Operation reference

Resources and their specs are declared per plan; `tinywin2 plan show <id>` prints the
complete definition of any catalog entry, including parameters and risk levels.

## Field notes: ISO rebuild pitfalls (Setup/dockur deploy flows, 2026-10)

Lessons from a full `build` → `package iso` → boot-under-QEMU (dockur/windows)
cycle against a zh-cn 26300 client ISO:

- `dism /Export-Image` **appends** to an existing destination WIM instead of
  replacing it. Delete the destination first, then assert `/Get-ImageInfo`
  reports exactly one index — otherwise a stale file yields two same-name
  images and Setup stops at the edition-selection page.
- Setup's Downlevel phase extracts `\Windows\System32\Recovery\winre.wim`
  from the install image. Images built with `fs.recovery-environment` applied
  fail with 0x80070003 ("Windows 11 安装失败"). DISM-apply flows do not need
  WinRE; **Setup flows do** — keep that plan disabled for ISO outputs that
  will be installed through Setup.
- Non-ASCII image names (e.g. `Windows 11 专业版`) break downstream template
  matching: dockur derives `win11x64-<transliterated>.xml` from the name and
  misses. Export with `/DestinationName:"Windows 11 Pro"` when the consumer
  selects images by name.
- `registry.privacy-hardening` fails deterministically when writing
  `Policies\Microsoft\Windows\Windows Feeds\EnableFeeds` (access denied even
  after the `RegistryAcl` ownership rescue). Use the shipped
  `registry.privacy-hardening-nofeeds` variant for container/layered ISO
  builds; the dropped policy is cosmetic when the Widgets appx is removed.
- Layerless (`--single-layer`) `--resume` validates the full selection
  fingerprint: any profile change between runs makes resume refuse to start
  ("checkpoint belongs to a different source, index, or plan selection").
  Treat resume as same-config-only; a config change means a clean build.
- `dotnet publish` of the CLI fails while a built `tinywin2.exe` is still
  running (the publish output is file-locked). Kill running instances before
  republishing.
- `service.*` plans are not uniformly "disable the service": some are tuning
  (e.g. `service.server` sets LanmanServer to delayed-auto and *keeps* SMB
  sharing). Run `plan show <id>` before assuming a plan removes a capability.

- `service.*` plans are not uniformly "disable the service": some are tuning
  (e.g. `service.server` sets LanmanServer to delayed-auto and *keeps* SMB
  sharing). Run `plan show <id>` before assuming a plan removes a capability.

## Field notes: deploy-loop postmortem (dockur + WSL2 podman, 2026-10-13)

A full day of "VM powers off seconds after every boot" that was NOT one bug but a
chain. Reproduce-order matters when debugging; each fix below was necessary.

- **WSL2 kills containers when idle.** `wsl.exe` shuts the whole VM down after
  `vmIdleTimeout` (default 60 s) with no attached client — podman/QEMU get
  SIGTERM'd mid-write. Container exit code 143, dockur logs "Received SIGTERM
  signal, sending ACPI shutdown...". Fix: `[wsl2] vmIdleTimeout=86400000` in
  `.wslconfig` (needs `wsl --shutdown`), or keep a client attached.
- **A guest reset kills QEMU when `-no-reboot` is set, and a reset *is* how
  Setup ends its apply/finalize phases.** "Requesting reboot of system..." in
  `setupact.log` followed by a clean container exit 0 ("Shutdown completed!")
  is *expected* — restart the container and the install continues. Don't
  confuse this with a failure.
- ** Killing QEMU while the kernel flushes hives tears them.** A torn
  `SYSTEM` hive = every later boot bugchecks `CONFIG_INITIALIZATION_FAILED`
  (0x67) seconds after bootmgr, before any disk write, looping forever under
  `-no-reboot` as "exit 0 after 10-15 s". chntpw still opens torn hives fine —
  a parseable hive is not proof of health. Recovery: re-apply the five hives
  (SAM/SECURITY/SOFTWARE/SYSTEM/DEFAULT) from the source WIM.
- **Ship the debug insurance in the image.** `registry.crash-debug`
  (AutoReboot=0 + CrashDumpEnabled=7) turns the invisible loop into a parked
  BSOD you can read plus an on-disk dump. Debug with it; cut the production
  ISO with `registry.crash-dump-disabled` instead.
- **The edition must match dockur's answer file.** dockur's
  `win11x64.xml` embeds the generic **Pro** key `VK7JG-NPHTM-C97JM-9MPGT-3V66T`;
  a Home WIM (zh-cn consumer ISO **index 1** is 家庭版, **index 4** is 专业版)
  makes Setup fall back to the interactive product-key page and the rest of
  unattend automation degrades. Match index→edition→embedded key, or override
  the answer file.
- **QEMU monitor is the debugger.** `podman exec <c> python3 -c "...connect
  /run/shm/monitor.sock... sendkey ..."` drives Setup pages blind
  (`sendkey alt-n` hits access keys; plain `ret`/`spc` often don't).
  `podman exec` **needs `-i`** for heredoc stdin or nothing arrives.
- Host-side shell quoting through `wsl.exe -- bash -c '...'` silently eats
  `$var` / `$(...)` expansions and re-quotes args — never use variables or
  command substitution in the payload; write a script file and run it.
- Windows 11 applies inbox binaries as WOF/CompactOS reparse points
  (tag `0x80000017`); Linux ntfs drivers show them as unreadable symlinks.
  That is normal and not a boot failure cause.

Patterns on `appx.provisioned`, `appx.system`, `registry.service`, and `fs.path` are
PowerShell-like wildcards (`*`, `?`); `dism.package` patterns are .NET regular
expressions.

## Field notes: host-update kills offline hive ops (2026-10-14)

The host (Insider 26300.9457 → 9711 after an overnight LCU) lost **all offline registry
capability**: `RegLoadKey` returns ERROR_INVALID_PARAMETER for *any* hive (even a copy
of the host's own SOFTWARE), `reg.exe load` misreports it as "文件名或扩展名太长"
(ERROR 206), and every DISM offline image session dies in init with
`get_OSVersion HRESULT=0x80070057` → exit 87 on `/Cleanup-Image /ScanHealth`. wusa
refuses to uninstall flight LCUs (exit 87); `dism /Online /Remove-Package
/PackageName:Package_for_RollupFix~...~<lcu-version>` works (exit 3010 = reboot
pending) and restores the previous flight.

- When tinywin2 builds suddenly fail at CBS scan / reg.exe 1, **check what Windows
  Update installed overnight first** — the build machinery is fine, the host kernel's
  hive loader is not. Diagnostic ladder: P/Invoke `RegLoadKey` (true error code),
  `reg load` of the host's own hive copy (fails ⇒ host bug, not image bug).
- The base CBS `ScanHealth` in `BuildEngine` is NOT optional decoration: its repair
  pass re-applies component registrations that later `appx.system` reg operations
  expect. Skipping it (TINYWIN2_SKIP_CBS_SCAN=1) makes step 002 die with
  `reg.exe exited with code 1` (delete/load of keys that only exist after repair).
  Fix the host instead of skipping the scan.
- `tinywin2 build` refuses a profile with zero enabled plans ("no plans selected") —
  a pipeline control build must be done with plain DISM instead.
- Plan selection is dependency-graphed: disabling a plan that an enabled plan
  `requires` fails resolution (`service.compressed-memory` → `registry.prefetch-disabled`).
  Generate bisect profiles dependency-aware: force-enable the required chain.
- Profiles/plans must be read with `-Encoding UTF8` in Windows PowerShell 5 (Chinese
  descriptions otherwise garble and break ConvertFrom-Json); `$pid` is reserved.

## ROOT CAUSE (2026-10-14): offline writes to CurrentControlSet bugcheck 0x67

The week-long "slimmed images die at first boot with CONFIG_INITIALIZATION_FAILED
(0x67)" mystery is fully solved:

1. **Never write `CurrentControlSet\...` in an offline SYSTEM hive.** Offline the
   key is a dead stub left over from the source image's last boot; the boot-time
   early readers (winload/CmInit) read the literal `CurrentControlSet` path and
   the stub's values poison them. `registry.crash-dump-disabled` wrote
   `CurrentControlSet\Control\CrashControl\CrashDumpEnabled=0` → 0x67 at ~75 s
   on every boot. Fix: plans must target `ControlSet001\...` (crash-dump-disabled,
   crash-debug, hibernate-disabled were all converted). Engine-level translation
   (rewrite the prefix from Select\Current) is the proper long-term guard.
2. **Independent hazard:** `reg.exe unload` intermittently leaves the backing
   hive file a few bytes past a 4 KiB boundary (observed +51) — winload rejects
   such files with the same 0x67, while chntpw/reg.exe read them fine. A
   `NormalizeHiveFile` trim (truncate to 4 KiB multiple) was added to
   RegistryHiveCache.UnloadAllAsync, but B7's build showed zero trim lines while
   producing a clean hive — the no-layers flow may not pass through
   UnloadAllAsync; verify the wiring before relying on it.
3. Diagnosis machinery that cracked it: build-config bisection with boot-test
   timing verdicts (guest reset <120 s = fail, >240 s = pass), qemu-nbd +
   ntfs-3g for offline hive surgery, reglookup on the applied VHDX to compare
   ControlSet001 vs CurrentControlSet. Bisection took 6 rounds over 272
   selections (B1 pass → B2/B3/B4 fail → B5 pass → B6 fail with the plan
   re-added = verdict, each round ≈ 45 min).
