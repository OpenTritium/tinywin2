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

- dockur/windows auto-install hard prerequisites (verified 2026-10-15 against
  a zh-cn 26300 client ISO):
  - **Source index must be Pro** (`--index 4` = `Windows 11 专业版`). Home
    indexes ship `termsrv.dll` with an SKU check, so inbound RDP can never
    work there, and dockur's generic answer file `win11x64.xml` carries the
    Pro generic key `VK7JG-NPHTM-C97JM-9MPGT-3V66T` (mismatch fails Setup).
  - **Rename the index to the English name** before packaging, because dockur
    derives `win11x64-<transliterated>.xml` from the WIM name and misses on
    `Windows 11 家庭版` (falls back to interactive Setup). DISM cannot rename:
    `/Export-Image` **rejects `/Name`** with
    `Error: 87 — "The /name option is not recognized in this context."`
    (verified 2026-10-15). Use wimlib's positional name instead; delete the
    destination first (exports append to an existing WIM):
    `wimlib-imagex export SRC.wim 1 DST.esd "Windows 11 Home" "Windows 11 Home" --compress=LZMS`
    then assert exactly one index via `wimlib-imagex info`.
  - **RDP stays OFF by default** (deliberate policy: saves memory — while
    `fDenyTSConnections=1` (the Windows default we keep) nothing ever starts
    TermService/SessionEnv/UmRdpService, so they cost zero resident memory).
    Enter via the VNC console (port 8006) and enable RDP in-guest only when
    actually needed. The container's 3389 is plain passt forwarding
    (`-t 3389 -u 3389`) to the guest's own RDP; dockur injects the 3389
    firewall rule only in its automatic (answer-file) flow. Note Home editions
    can never serve RDP (termsrv SKU check) regardless of these toggles — Pro
    images are mandatory for any RDP use at all.
  - **Do not `taskkill` a running build and reuse its workspace**: the killed
    DISM leaves `base.vhdx` attached ("used by another process"), the tool then
    refuses with "workspace is not empty", and `--overwrite` does not clear
    that check. Use a fresh workspace name instead.

- **Empty DriverStore shells break offline Setup** (found 2026-10-15): the `driver.*`
  cleanup plans (`driver.legacy-printing`, `driver.legacy-modem`,
  `driver.non-consumer-extra`, ...) emptied `DriverStore\FileRepository\<pkg>` but
  could not delete the directory ("parent denies DELETE_CHILD"), leaving ~164 empty
  shells in a built image. Offline Setup walks FileRepository when staging its
  `$OEM$` drivers, fails each copy with 0x80070002, and then crashes the deployment
  engine (`WINDEPLOY error code is 0xC0000409` => "Windows 无法完成安装"). Stock
  ISOs have no shells, so only slim images fail. Fix: `DeletePackageFilesAsync`
  must go through `ImageFs.DeleteWithRescueAsync` (which grants the parent
  DELETE_CHILD and retries) and warn loudly if a shell survives.

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

## Field notes: kiosk appliance trimming (2026-10-15, THS-targeted vm-init)

Measured against the *deployed* vm-init build (dockur/windows podman container,
2 GB / 4 vCPU guest) by mounting `data.img` read-only (qemu-nbd + ntfs-3g) and by
driving the live guest over Win+R/QEMU-monitor:

- **WOF hides 3x of the disk.** The guest uses 7.5 GiB of a 32 GB sparse data.img,
  but plain `du` on the ntfs-3g mount reports only ~2.9 GiB: every inbox binary is a
  WOF/CompactOS reparse point and its bytes live in the `:WofCompressedData` ADS,
  which `du` never sees. Mount with `streams_interface=windows` and lstat
  `file:WofCompressedData` (see `tw2-out/wof-size.py`) or every number is wrong.
- **Idle RAM (1.3 GB / 2 GB) is the Win11 shell.** tasklist working sets: explorer.exe
  289 MB, StartMenuExperienceHost 91, TextInputHost 73, ctfmon 58, ApplicationFrameHost
  43, sihost 41, ShellHost 32, CrossDeviceResume 31 → ≈650 MB of the shell stack
  alone; svchost 1196 (netsvcs: Appinfo/Schedule/Themes/UserManager/Winmgmt/UsoSvc/
  wuauserv/LanmanServer) adds 298 MB with TiWorker.exe at 95 MB while Windows Update
  churns. dwm 120 MB and Registry 82 MB are irreducible. => the only big lever is not
  starting explorer at all (`fs.kiosk-shell`), second is stopping the WU/servicing
  stack (`service.kiosk-suppress` + `registry.update-task-cache`).
- **`feature.extra-fonts` is a Server-SKU no-op on client**: it removes
  `ServerCoreFonts-NonCritical-Fonts-*`, which do not exist on client SKUs, so
  Windows\Fonts stayed at 209.6 MiB. Client-side font cuts need real `fs.path`
  removes (`fs.fonts-strip`, ≈134 MiB: Noto SC, Deng/等线, SimSun-ExtB, 楷体/仿宋,
  StaticCache.dat, …).
- **Profile selection order is execution order, so a post-servicing suppressor must
  sit last.** `service.kiosk-suppress` was appended mid-block, and its
  `LanmanServer`/`UsoSvc`/`TrustedInstaller` writes were overwritten by the later
  `service.server` / `service.workstation` / `service.update-orchestrator` /
  `service.windows-update` plans (same for `service.compressed-memory` → SysMain, which
  the suppressor disables again). It now trails the selection list, and its v1.1.0
  description says so.
- `driver.store` removals leave the empty `FileRepository` shell behind ("parent denies
  directory deletion") — 671 empty dirs in the deployed image, but the payload is gone.
- **Driving the live guest for measurements**: `podman exec -i windows11 python3 - <
  guest-drive.py` types into Win+R through `/run/shm/monitor.sock` (`sendkey`), have the
  command redirect output to `C:\diag1.txt`, then remount the ntfs-3g guest mount to
  read it (ntfs-3g caches dentries, new files are invisible until remount). The zh-CN
  IME silently converts injected ASCII into pinyin/Hanzi — press a bare `shift`
  (`sendkey shift`) to switch to English mode and check the tray shows 英 before typing.
  A screendump (`screendump /storage/x.ppm`, readable on the host through the volume
  dir) is the cheapest way to verify guest state; PPM → PNG needs a tiny stdlib
  converter (`tw2-out/ppm2png.py`).
- **`fs.path` patterns must be built from full relative paths, never basenames.** v1.0.0
  of `fs.systemresources-strip` asked for `SystemResources\StudioEffects*` and
  `*Sample.mkv` and matched *nothing*: those files live under
  `Windows.UI.ControlCenter\Assets\Images\` and
  `Windows.UI.SettingsAppThreshold\SystemSettings\Assets\`. The ad-hoc WOF inventory
  scripts printed basenames only, which hid the real paths. Verify a path plan actually
  hit its targets from the build log (`removing image path: …` lines, one per target) or
  `wimlib-imagex dir install-vN.wim 1 --path=<dir>`; `wimlib-imagex info` also reports the
  image Total Bytes, so `vN` vs `vN-1` deltas prove what a trim really removed (v6 →
  v7 = −290 MiB uncompressed).

## ROOT CAUSE #2 (2026-10-15): plan-side driver `Start` edits kill first boot (0x7B)

- **Never edit the `Start` value of a service whose *stock* value is 0 (Boot) or 1
  (System).** `service.kiosk-suppress` v1.2 set the container/dev filter drivers
  (`wcifs/CimFS/FileCrypt/luafv/bfs/storqosflt/bindflt`) to manual and 13 more
  (`vwififlt/NetBIOS/Dfsc/EhStorClass/EhStorTcgDrv/bttflt/rdyboost/nvmedisk/storufs/
  pmem/nvdimm/mmcss/scmbus`) to disabled. First boot died with
  `INACCESSIBLE_BOOT_DEVICE (0x7B)` in *both* targets (Hyper-V gen-2 via storvsc and the
  dockur QEMU guest), while the same v8 WIM with those 49 plan-touched Starts backfilled
  to stock boots fine (uptime 40 s → 80 s monotonic, heartbeat `OkApplicationsUnknown`,
  demand ~430–500 MB on a 1 GB static VM — vs 1085→1986 MB for stock).
  Stock table measured from the source ISO: Start=0 = `nvmedisk storufs bttflt
  EhStorClass EhStorTcgDrv rdyboost pmem nvdimm scmbus`; Start=1 = `CimFS FileCrypt
  Dfsc NetBIOS vwififlt`. Disabling a demand driver saves ~0 RAM anyway (it never loads
  when its hardware is absent), so v1.3.0 deletes the whole driver block and the plan
  only touches user-mode services.
- **Image-vs-recipe discriminator (~15 min, do this before bisecting plans):** deploy the
  *stock* `install.wim` through the exact same VM/script/profile. Stock boots and stays
  up under 1 GB static (demand peaks ~2 GB, i.e. starved but functional). If your WIM
  loops on the same recipe, the bug is image-side — no need to question the deployment.
- **Cheapest boot-fix test is an offline Start-backfill, not a rebuild.** Apply your WIM
  to the target VHDX, `reg load` BOTH the target SYSTEM hive and a stock one, copy every
  plan-touched service's stock `Start` into the target, dismount, boot (one 15-min round
  trip instead of a 45-min rebuild). The verdict is `uptime` monotonicity + heartbeat.
  Tooling traps: (a) `reg unload` answers "access denied" when the PowerShell registry
  provider (`Get-ChildItem`/`Get-ItemProperty`) touched the hive in the same process —
  unload from a *fresh* `pwsh` child process; (b) a status partial result ("0 lines")
  from `reglookup` is usually "file gone", not "key absent": WSL `/tmp` is wiped on
  distro restart, so keep probe artifacts on the Windows side and do
  extract→dump→check inside ONE shell.
- **Diagnosing the deployed VM:** `Get-VM <name> | Select Uptime,Heartbeat,MemoryDemand` is the
  whole verdict: `uptime` must grow monotonically across 40 s samples; a guest that
  resets ~40 s after start and reports `NoContact`/`demandMB=0` never reached the kernel.
- **dockur (podman) container reinstall semantics:** the container mounts
  `-v <iso>:/custom.iso` and stores the baseline name+size in
  `<volume>/windows.base` (`windows.<bytes>.iso`). Replacing the ISO *changes its byte
  size*, which makes dockur reinstall from scratch — delete `data.img` and recreate the
  container with the same env (`VERSION=11 RAM_SIZE=2G CPU_CORES=4 DISK_SIZE=32G`).
  WSL cannot see Windows-mounted ISOs under `/mnt/<letter>`; `dism /Mount-Image` on the
  host is the reliable way to read a WIM from WSL-hostile paths.

