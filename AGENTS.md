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
tinywin2 package iso --input --install-image --output --workspace --oscdimg \
  [--unattend auto|<file>|none] [--firmware bios|uefi] [--layout standard|minimal] [--kiosk-user --kiosk-password]
tinywin2 unattend generate --output <file> [--firmware bios|uefi] [--user --password --image-index]
tinywin2 unattend validate --file <autounattend.xml>
tinywin2 layer list|diff|extract|capture --workspace ...
```

Typical workflow:

```
tinywin2 inspect --input win11.iso
tinywin2 preview --input win11.iso --index 1 --workspace ./ws \
  --profile profiles/vm-init.json
tinywin2 build --input win11.iso --index 1 --output out/install.esd --workspace ./ws \
  --format esd --single-layer --profile profiles/vm-init.json
tinywin2 package iso --input --install-image out/install.esd \
  --output out/win11-slim.iso --workspace ./pkg --oscdimg "C:/oscdimg/oscdimg.exe"
```

`package iso` burns a generated `Autounattend.xml` into the media root by default
(`--unattend auto`). It wipes disk 0, installs the selected index, skips both OOBE
phases, creates a local administrator and signs it in; `--firmware bios` (default)
writes an MBR layout and `--firmware uefi` a GPT layout. The generated file — and any
`--unattend <file>` overlay — is validated before it is copied. `--unattend none`
ships the media untouched. `--layout minimal` keeps the same partitions but removes the
setup-created recovery partition: the media carries
`sources\$OEM$\$$\Setup\Scripts\SetupComplete.cmd`, which runs as SYSTEM before OOBE and
moves WinRE to `C:\Recovery\WindowsRE` before deleting the partition. UEFI cannot drop
the MSR — without it Setup fails at the disk page
(`DiskLayoutMakeReserved: Couldn't create MSR partition`). FirstLogonCommands cannot do
this work: they run with the user's filtered token, so `reagentc` and `diskpart` fail
with access denied.

`profiles/vm-init.json` is the shipped THS-only profile, not a general laptop keep-set. It keeps WOW64 for 32-bit THS, Client.CBS for the Windows shell/Chinese text input, Chinese fonts, WinRE, and the system-managed pagefile; `registry.hibernate-disabled` only removes hibernation/fast startup. The default power scheme is left unchanged.
It disables the RDP service chain and removes RDP client/management tools, OpenSSH client/server components, and SMB service roles; dockur's VNC console on port 8006 is independent. RDP core files remain (`fs.remote-desktop-residuals` is off). `fs.kiosk-shell` remains off until the real THS executable path and startup are tested; `appx.desktop-app-installer` stays kept until the THS installer route is known.
`appx.ai-residuals` stays off while the dockur/windows 6.x install regression is unresolved. `appx.security-health-ui` is selected but the v15 guest still registered the package, so do not count it as saved space until a fresh build verifies the hook. `fs.searchhost` v2 removes the whole search UI surface (SearchHost/FESearchHost/SearchUx/Cortana.UI) and `fs.cbs-manifest-strip` strips the CortanaUI/CortanaUIFullTrustApp/FESearchUI activation entries from the CBS manifest — the v15 DCOM 10001 errors came from activating removed entries, which the manifest strip eliminates. Keeping SearchHost was not viable: 2026-10-16 smoke (WER dumps) showed it crash-looping 6× per boot in twinapi.appcore.dll (stowed exception 0xC000027B). The profile disables SysMain via its final suppressor; this also forgoes the memory-compression/page-combining behavior documented by `service.compressed-memory`. That tradeoff is unmeasured with THS, so compare under actual load before changing it. `registry.update-task-cache` removes update/maintenance task trees but preserves SoftwareProtectionPlatform licensing tasks. `fs.cross-device-resume` removes the CrossDeviceResume payload; deleting only the payload files left the `CrossDeviceResumeApp` activation entry behind and the guest showed a "Windows 找不到文件 …\CrossDeviceResume.exe" popup on shell use. `fs.path` apply takes ownership when overwriting TrustedInstaller-owned files; the CBS manifest asset overwrite in `fs.cbs-manifest-strip` relies on it. `fs.dead-tasks` v1.2 removes the `Device Information` tasks (their DeviceCensus.exe crashed with a stack-buffer-overflow popup on the telemetry-stripped image); the specialize pass may re-provision them — verify on deploy. `registry.update-policy-strip` is DISABLED after triple verification (2026-10-17): deleting the UpdatePolicy CLSIDs breaks OOBE itself — msoobe loads wuapi, wuapi CoCreates the UpdatePolicy CLSIDs at init, and with them gone msoobe/wuapi fastfails into the "Windows 无法完成安装" loop (v22 offline-apply AND Gen1 real-setup flow both reproduce; v20b/v21 with CLSIDs intact all reach desktop). The DllHost surrogate crash was a SIDE EFFECT of wuapi's CoCreate when the CLSIDs exist; with the strip disabled it returns as a silent crash (no popup — `registry.crash-diagnostics-silent` WER handles it with a dump). Real fix requires validating wuapi's tolerance for the missing CLSIDs or keeping wuauserv usable — do not delete the registrations. `registry.crash-diagnostics-silent` (profile tail) + `service.kiosk-suppress` WerSvc=manual give the guest silent crash forensics: any process crash writes a minidump to C:\CrashDumps and Application 1000 (faulting module), WER UI and hard-error popups are suppressed (DontShowUI=1, ErrorMode=2); zero idle cost since WerSvc runs only on demand. CRITICAL deploy note: a first boot WITH a NIC attached drives online OOBE into the stripped update stack (msoobe/wuapi fastfail, "Windows 无法完成安装" loop) — apply the bypass-oobe registry block (clear Setup state) offline at deploy time or live via PowerShell Direct; see hyperv/tools/README. The taskbar search glyph needs `SearchboxTaskbarMode=0` **and** `SearchboxTaskbarModeCache=0` written by the *live* user: the offline default-user write is impossible (the Search key cannot be created offline), and an HKLM RunOnce hook races OOBE (the defaultuser0 logon consumes it). `kiosk-firstlogon.cmd` is therefore registered through Active Setup (`HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components\{7F2C1B44-6A9D-4E0C-9E2B-3A5D8C1F0B77}`), which runs once per profile before the shell starts, so no explorer restart is needed and the batch asset must stay CRLF. `fs.cbs-consumer-apps` removes the CBS-embedded Windows 备份 (WindowsBackupClient/WindowsBackup.dll/WindowsBackup\), 入门/发现中心 (DiscoveryHubApp/DiscoveryHub/DiscoveryHubWidget) and 单击以执行 (CoreAI SystemApps package) apps — v16's appx.getstarted-backup skipped them because CBS-embedded components never appear as provisioned/inbox; their manifest entries are stripped by `fs.cbs-manifest-strip` v4.

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

- **Prefer NVMe (or AHCI) for the test disk — never virtio-blk without a driver**
  (found 2026-10-21): WinPE and Windows ship the inbox `stornvme` and AHCI drivers, but
  not virtio-blk, so a qemu disk attached with `-device virtio-blk-pci` is invisible to
  Setup (`diskpart` reports "没有可以显示的固定磁盘") and unattended provisioning fails
  with `VDS_E_OBJECT_NOT_FOUND` (0x80042405) before touching the disk. Use
  `-drive file=disk.qcow2,if=none,id=d0,format=qcow2 -device nvme,drive=d0,serial=...`
  (qemu NVMe; matches Hyper-V's modern disk path) or `if=ide` on q35. A disk
  pre-partitioned offline masks the failure: the partitions exist, but Setup still
  cannot see them.

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
- **Profile selections execute in order; `service.kiosk-suppress` must be last among
  `service.*` plans.** It had been appended mid-block, so later `service.server` /
  `service.workstation` / `service.update-orchestrator` / `service.windows-update`
  writes (and `service.compressed-memory` → SysMain) overwrote its values. The current
  profile places it after every service plan; only registry/filesystem/maintenance
  cleanups follow, and they do not change service start modes.
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

## ROOT CAUSE #3 (2026-10-16): dockur/windows 6.x aborts at "Windows 无法完成安装" — pin 5.16

- **Symptom:** every install from a `:latest` (6.0x) container dies right after the
  `Pre OOBE` phase succeeds: `WINDEPLOY error code is 0xC0000409` (WebDeploy crash →
  the "Windows 无法完成安装" dialog / black screen). Just before it, the WinPE log shows
  12× `CApplyDrivers::CopyToDriverStore ... oem0..11.inf ... 0x80070002` — those are
  dockur's *own* injected `$OEM$` driver paths ("Adding drivers to image..." +
  "Creating/Writing overlay image..." in `podman logs`), not anything from tinywin2.
- **Controls (all with the same zh-cn 26300 ISO):** our slim ISOs crash; the **stock
  source ISO crashes**; dockur's **default flow with no `custom.xml` crashes** ⇒ no
  slimming plan and no answer file is implicated. `ghcr.io/dockur/windows:5.16` (last
  5.x) with the *stock* ISO installs past the crash point ("正在安装 95%", `Users\Docker`
  created, CBS live) ⇒ the v6.00 install-path refactor (its notes: wholesale
  install/ESD/download refactor + VirtIO bump) is the regression.
- **Workaround:** run the install container on `ghcr.io/dockur/windows:5.16`
  (env/ports/mounts unchanged); 6.x can still be used for viewing the finished guest.
- **Consequent exonerations (do not re-litigate without new evidence):**
  `appx.ai-residuals` (its isolated probe3 reproduction was confounded by this bug and
  is currently left disabled in vm-init as a conservative default), the security-strip
  set (v11 blacklist), the Home `--index 1` + `YTMG3` key pairing (self-consistent), and
  the empty-DriverStore-shell fix (kept; v11/v12/v13 build logs show zero
  `EMPTY-SHELL-RETAINED` warnings).
- **Reading the guest's fate offline:** stop the container first (`data.img` is the live
  qemu disk), then `qemu-nbd -c /dev/nbd0 --read-only <volume>/_data/data.img`, mount
  `/dev/nbd0p3`, and grep `Windows/Panther/setupact.log` for
  `WINDEPLOY error code is 0xC0000409`; the last lines of `setuperr.log` +
  `setupact.log` give the phase that died.

## ROOT CAUSE #4 (2026-10-16): Task Scheduler dead since install — TimeBrokerSvc never runs (TriggerInfo wipe)

- **Symptom set:** `sc start Schedule` → START_PENDING then Stopped; the spawned svchost
  (`svchost -k netsvcs -p -s Schedule`) lives ~60 ms, loads every DLL including
  `schedsvc.dll` + `TimeBrokerClient.dll`, then unloads cleanly and exits 0; System and
  Application logs show nothing. `schtasks /query` HANGS against the dead service (always
  wrap it in a bounded job; bare calls wedge a probe for minutes). Security-SPP logs
  `0x80041315` (SCHED_E_SERVICE_NOT_RUNNING) every 5 min. The
  `Microsoft-Windows-TaskScheduler/Operational` channel is empty — but it is disabled by
  default on healthy hosts too, so emptiness proves nothing.
- **Causal chain:** `service.kiosk-suppress` (v1.3.0) disabled `TimeBrokerSvc`, and the
  tool's `registry.service` executer also rewrote `TriggerInfo` for every service a plan
  touched — with the plan's trigger list, which is empty for any non-`trigger` start mode.
  Net effect: the whole `Services\*\TriggerInfo` subtree vanished (stock SYSTEM hive: 1443
  entries; v13's: 0) and `TimeBrokerSvc` sat at Start=4 with no triggers. `schedsvc`'s
  init hard-depends on the Time Broker: with nothing to connect to it exits cleanly, so
  the Task Scheduler is dead from the first boot — and the ~200 missing task XML files
  are a *symptom* (the service materializes them only while it runs), not a cause.
- **Discriminator that cracked it (reusable):** the CKCL circular ETW buffer holds only
  the last ~11 s, so `wpr -stop` immediately after `sc start` captures the child's entire
  life: the ordered `Image,Load` list + `Process,End` (which carries the command line).
  A complete load sequence followed by a clean reverse unload = "init failed and exited",
  not a crash — while `Image,UnLoad` before all dependencies loaded would mean a load
  failure. Pair with `logman query -ets` + `sc qtriggerinfo` before/after comparisons
  against the healthy host.
- **Fix pair (both required):** (a) tool — `RegistryServiceExecuter` only manages
  `TriggerInfo` for `start: "trigger"`; a blank trigger list must never be read as "clear
  the triggers"; (b) profile/plans — keep `TimeBrokerSvc` out of every disable list and
  pin it `auto` via `service.time-broker`, because in a kiosk shell no user-mode activity
  fires its RPC-interface triggers and stock `manual` is not reliable here.
- **Same-diagnosis service reverts:** `CDPSvc`/`WpnService`/`WpnUserService` back to
  `auto`, `wlidsvc`/`TokenBroker` back to `manual`.
- **Verification recipe (proven):** registry-edit `TimeBrokerSvc\Start` to 2 (SCM caches
  service config per boot; `sc config` may answer access-denied, the registry always
  works), GRACEFUL `Stop-VM` → `Start-VM` (a `Restart-VM -Force` is a hard reset and
  loses unflushed registry writes), then `sc start Schedule` → Running and a bounded
  `schtasks /query` returning task rows.

## Field notes: `vm-init` resource audit (2026-10-16)

- **Storage is not the same as reclaimable space.** `tw2-out/comp-cleanup.log` is from the v15 Hyper-V test guest: the 31.8 GB C: volume had 18.0 GB free before `DISM /StartComponentCleanup /ResetBase` and 17.9 GB after; the counted `WinSxS` tree stayed at 9,825 MB. `AnalyzeComponentStore` reported 10.18 GB actual component-store size, of which 6.31 GB is shared with Windows and 3.86 GB is backups/disabled features; cache was 0 bytes and two packages were reclaimable. Do not treat the shared bytes or all 3.86 GB as safely removable, and do not claim a meaningful cleanup saving from this run.
- **The shell is the measured memory lever.** `tw2-out/boot-diagnostics.log` sampled v15 after 508 s with 4,095 MB total / 2,512 MB free and 77 processes; working sets included Explorer 254 MB, StartMenuExperienceHost 117, ShellHost 94, DWM 90, TextInputHost 83, ShellExperienceHost 71, ctfmon 44, plus Task Manager 129 MB. This is not a THS-loaded 2 GB measurement. The earlier dockur v6 idle snapshot above was about 1.3 GB used of 2 GB. Keep `fs.kiosk-shell` disabled until THS is installed and its actual executable path and startup behavior are verified; it remains the largest plausible memory saving. Do not lower guest RAM below 2 GB before measuring THS under load.
- **Preserve virtual memory and recovery.** `registry.hibernate-disabled` only disables hibernation/fast startup and explicitly leaves the system-managed pagefile alone. `fs.recovery-environment` is disabled in `vm-init` (kept), as is WOW64 for 32-bit THS. Never count pagefile or WinRE as trim targets.
- **v15 audit findings are not proof of the current profile.** That guest still had `TermService` Running/Manual, W32Time Running/Manual, `wisvc` Manual, `BcastDVRUserService_<suffix>` Manual, SysMain Disabled, and `Microsoft.SecHealthUI` registered; its `SearchHost.exe` was absent. The profile now disables the RDP service chain, removes unused SSH/RDP client components, keeps `fs.searchhost` off, preserves the SoftwareProtectionPlatform task tree, and adds `wisvc`/BcastDVR service suppression. Plan/profile validation passes and preview resolves 286 steps. The default shell token is non-admin; Windows `sudo.exe` is available in forced new-window mode for elevation. No full image build or rebuilt guest has verified the final settings.
- **Do not claim the Security Health UI saving.** The v15 guest still registered `Microsoft.SecHealthUI` and had no `sec-health-setup.log`; the selected `appx.security-health-ui` plan therefore remains an unverified removal, not a measured 23 MiB saving. Verify its setup hook on a fresh elevated build before counting it.
- **Visual-effects settings are a small cosmetic cut, not a RAM measure.** `registry.winui-effects-min` writes `VisualFXSetting=2`, `TaskbarAnimations=0`, and `DisallowAnimations=1`, but the v15 audit found `TaskbarAnimations=1` in the active user hive; taskbar animation suppression remains unverified until a fresh build/new-user check.
- **Keep the conservative cut line.** `appx.client-cbs` and `feature.wow64` remain kept for the shell/Chinese text input and 32-bit THS; the default installer path is unknown, so keep `appx.desktop-app-installer`. RDP service disablement is enough for VNC; leave high-risk `fs.remote-desktop-residuals` off rather than deleting `termsrv.dll` and RDP core files without a boot test.


## ROOT CAUSE #5 (2026-10-18): OOBE UI fast-fail breaks every post-v22 first boot — offline autologon plan is the fix

- **Symptom chain:** fresh offline-apply deploy → boot → autologon fails with「指定的域不存在，或无法联系」(no user was ever created); real-setup (Gen1 + autounattend) →「无法完成安装」restart loop that never reaches desktop. `Panther\setuperr.log`: `[oobeldr.exe] Failed to end OOBE transition UX; hr = 0x80070002` + `OOBE UI failed with exit code [0xc0000409]` after msoobe's mandatory tasks all report success.
- **Root cause:** `Windows\SystemApps\MicrosoftWindows.CloudExperienceHost_cw5n1h2txyewy\AppxManifest.xml` is missing in **every** build since at least v22 (present in the pristine base media). Without its manifest the CXH-based OOBE UI host cannot launch; oobeldr treats the UX failure as terminal. Unattend `UserAccounts` is processed inside the OOBE UI phase, so a UI crash means **no user is ever created** — autologon then fails with the misleading "domain does not exist" error. `appx.*` plan patterns do NOT match CXH (verified: no `appx.system` pattern hits it); the deletion happens elsewhere in the build and its precise origin is still open.
- **windeploy self-cleanup trap:** after the terminal OOBE failure windeploy marks setup complete (`SetupType 1→2`, `SystemSetupInProgress→0`), saves `MainQueueOnline0.que`, and **deletes `System32\oobe` binaries** (windeploy.exe/msoobe.exe/oobeldr.exe/UserOOBE.dll…). A later re-arm cannot rerun setup until the oobe directory is restored (robocopy `/B` from a known-good image works; the files are TrustedInstaller-ACL'd).
- **Deploy-script trap:** `Setup\CmdLine` (`oobe\windeploy.exe`) is REQUIRED for the specialize→OOBE continuation of an applied install.wim. deploy-v24b.ps1's `reg delete Setup /v CmdLine` silently disabled the whole continuation (no UnattendGC logs at all). Only `SetupType`/`SystemSetupInProgress`/`OOBEInProgress` may be touched.
- **Amplifier:** `CDPSvc`/`WpnService` (and per-user `CDPUserSvc`) disabled at first boot make the OOBE UI fail earlier/faster (see `service.token-broker` notes — the tested trio TokenBroker/CDPSvc/WpnService must all be on for interactive OOBE).
- **Fix (proven live 2026-10-18):** `registry.autologon-user` plan — a one-shot `CreateUser` service (auto-start, `ImagePath=cmd.exe /c net user … & net localgroup … & reg delete …`) creates TinyWin as SYSTEM seconds after boot regardless of OOBE state, plus the five Winlogon autologon values. v25 disk: PS Direct connected with `user=TinyWin, explorer=1`; Start entries 入门/单击以执行/安全中心 = 0; notepad kept; wuauserv/DPS disabled. Enabled in `vm-init` only — never ship plaintext-password images.
- **Diagnostics playbook (all proven this session):**
  - Setup state machine = three values under `HKLM\SYSTEM\Setup`: `SetupType` (1=specialize+OOBE pending, 2=continuation seen, 0=done), `SystemSetupInProgress`, `CmdLine`. Cross-check `Panther\setupact.log` LastWriteTime against boot times: a boot that writes nothing to Panther did NOT run windeploy.
  - PS Direct dies at logon screens with no interactive session ("尝试连接到目标虚拟机时发生故障" after ~80 s) — it is not a network or credential problem. Keyboard injection via `Msvm_Keyboard` works at the logon UI; `TypeText` + Enter unlocks when the tile matches `DefaultUserName`.
  - Hyper-V thumbnail screenshots return a solid ~5 KB black PNG for console sessions with no visible UX — do not trust them as "boot failed"; verify with PS Direct or the guest's own event log.
  - `reg load` of a hive from a hard-killed VM can leave the key loaded forever (`reg unload` → 拒绝访问) and locks the files against WSL/chntpw too. Only a host reboot clears it. Always `Dismount-VHD` + `reg unload` in a `finally`.
  - Partition-letter flakes on offline mounts make `Get-ChildItem "<letter>:\..."` return 0 silently — recheck `Test-Path` before believing a "directory is empty" result (this produced a false "all fonts deleted" alarm).
  - `robocopy /B` (backup mode) is the only reliable way to copy into TrustedInstaller-owned offline payload; `Copy-Item` dies on the first `Access denied`.
  - DWORD plan data is parsed as HEX (`NumberStyles.HexNumber`): `Type=16` must be written `"10"`, `Start=2` as `"2"`.
- **SecHealthUI status update:** the baked `SetupComplete.cmd` + `remove-sechealth.ps1` recipe is now verified on a REAL first boot: per-user EndOfLife for S-1-5-18 + all S-1-5-21-* SIDs, `dism /set-nonremovableapppolicy /nonremovable:0`, then `Remove-AppxPackage -AllUsers` — start entry gone; a staged remnant may still list under `Get-AppxPackage -AllUsers` (entry count is the user-visible truth). Note `-ErrorAction Continue` on Remove-AppxPackage can swallow the failure — rerun with `-ErrorAction Stop` when auditing.
- **Known remaining noise:** `svchost.exe_StorSvc` crashes at boot with VSS disabled (`0x80070422` from Volume Shadow Copy) — cosmetic for the kiosk appliance, revisit only if THS misbehaves.

## CORRECTION to ROOT CAUSE #5 + dockur field notes (2026-10-19, dockur 6.x/5.16 real-setup loop)

- **"CXH content missing from built images" was a self-inflicted false alarm.** The 26300
  WIM ships the package as `Windows\SystemApps\Microsoft.Windows.CloudExperienceHost_...`
  (WITH dot); the installed/enumerated SystemApps dir = `MicrosoftWindows.CloudExperienceHost_...`
  (NO dot). Diagnostics that probed the no-dot path against a DISM-applied tree reported
  "missing" forever. ntfs-3g additionally hides WOF-compressed reparse files from listings
  (they appear as 34-byte symlinks, tag 0x80000017) - 52k "husks" on a failed guest were
  NORMAL WOF compression, not data loss. Lesson: verify file presence through Windows
  (fsutil/Mount-DiskImage), never through ntfs-3g listings, and grep the WIM listing for the
  exact name variant before declaring content lost.
- **dockur/windows + custom.iso: dockur ALWAYS uses its own generated answer file** (Docker
  user, en-US template win11x64.xml) and IGNORES any autounattend.xml placed in the custom
  ISO. Evidence: guest `Panther\unattend.xml` = dockur's template (Docker x3) and
  `[Shell Unattend] UserAccounts: created account 'Docker'` in UnattendGC. Clicking "我没有
  产品密钥" manually is expected - dockur's template has no key for foreign editions.
- **dockur version matrix:** 6.x (latest) processes the custom ISO through the new setup and
  reaches OOBE; 5.16 boots the legacy purple setup which ignores the custom ISO's answer
  file entirely and stops at the product-key page. Neither version fixes the OOBE crash
  below.
- **Remaining real defect:** real-setup installation (dockur 6.x, QEMU) of v23/v24/v25
  trimmed images crashes at the OOBE UI hand-off: `[oobeldr.exe] Failed to end OOBE
  transition UX; hr = 0x80070002` + `OOBE UI failed with exit code [0xc0000409]` right
  after msoobe's mandatory tasks (through ZDP) complete. Restore of
  TokenBroker/CDPSvc/WpnService (registry.oobe-critical-services) did NOT change it.
  Suspected: one or more of the ~292 trim plans removes a component the OOBE UI stack
  requires. Isolation = plan bisection campaign (build half-profile -> install in dockur ->
  observe; ~1.5 h per round). Offline-apply + registry.autologon-user deploys bypass OOBE
  entirely and are the proven production path today.
- **WOF/deploy note:** after a failed OOBE hand-off windeploy's failure cleanup DELETES
  System32\oobe binaries and the CXH directory - post-mortems of a crashed install show
  them "missing" and it is a red herring; read `Panther\UnattendGC\setuperr.log` +
  `setupact.log` (timestamps are guest-clock skewed under QEMU) BEFORE concluding.
- **Pipeline:** `OutputBuilder.ReconcileChunkedFilesAsync` now diffs the applied tree
  against the staged WIM via 7z (dual size columns, backslash entry separators, tolerates
  the index prefix) right after apply and materializes anything absent; gated on the
  CloudExperienceHost sentinel. 7-Zip is a hard build dependency for 24H2+ chunked media.
  Build flag reminders: `--single-layer` needs 30 GB free; DWORD plan data is parsed as HEX
  and must be JSON numbers.

## HARD WORKFLOW GATE (user directive, 2026-10-19): smoke before dockur, fast smoke format

- **Every image MUST pass a full smoke in Hyper-V/QEMU (boot -> apply -> OOBE -> desktop)
  BEFORE it may enter the dockur flow.** No exceptions, no "dockur will tell us".
- **Smoke artifacts use the fast format**: `--format wim --compression none --fast
  --single-layer --no-verify --skip-evidence`. NO ESD/LZMS/max compression on smoke
  builds - compression is a final-delivery step only, after the smoke gate passes.
- Never let the smoke workspace/dead artifacts eat the C: drive: check `df -h /mnt/c`
  (need 30+ GB) before every build, and delete superseded WIMs/VHDs/ISOs immediately.
- The dockur custom.iso flow: dockur IGNORES autounattend.xml inside custom ISOs (it
  generates its own Docker-user answer file); manual clicks through the product-key
  page are part of the flow. Never remove the /custom.iso mount "to test" - dockur
  then downloads stock Windows from the internet.
