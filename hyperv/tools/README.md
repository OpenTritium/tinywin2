# Hyper-V smoke-test tooling

Host-side scripts for driving the smoke-test VM without a GUI console. All paths
reference `F:\tinywin2\` (vm/, iso/, out/, probe/) — adjust to your layout.

## Canonical build

The smoke image is Server 2025 **Datacenter with Desktop Experience** — source
index **4** (index 1/2 are Standard, 3 is Datacenter without desktop experience;
the wrong index yields a deceptively healthy but half-sized ESD).

```
tinywin2 build --input <26100 ISO> --index 4 \
  --profile profiles/base-full.json --profile profiles/developer-overlay.json \
  --output F:/tinywin2/out/dev-server.esd --workspace F:/tinywin2/ws-dev-server \
  --format esd --single-layer --overwrite
```

## Fast iteration loop (chained builds)

Each build's ESD is itself a valid `--input` (WIM/ESD are accepted sources).
To test a new plan or a small delta, chain from the previous output instead of
the original ISO — the ~283 already-applied steps are then already converged
and finish as near-instant skips:

```
tinywin2 inspect --input F:/tinywin2/out/dev-server.esd        # 1 index
tinywin2 build --input F:/tinywin2/out/dev-server.esd --index 1 \
  --profile profiles/delta-overlay.json \
  --output F:/tinywin2/out/dev-server-next.esd --workspace F:/tinywin2/ws-delta \
  --format esd --single-layer --overwrite
tinywin2 package iso --input <26100 ISO> --install-image F:/tinywin2/out/dev-server-next.esd \
  --output F:/tinywin2/iso/tinywin2-next.iso --workspace F:/tinywin2/pkg --oscdimg <...> --overwrite
```

Rules of the chain:

- `package iso` always pairs the **original ISO** as `--input` (boot.wim/setup
  files come from the media, never from the slim base).
- A chained base can only go **further**, not back: plans whose payload a
  previous round removed (OpenSSH Server capability, FoD metadata under
  /ResetBase, vendor RAID drivers) cannot be restored — restart from the
  original ISO for that.
- Keep `profiles/base-full.json` + full chain as the canonical release path;
  treat chained builds as the dev loop for plan authoring.

## Pipeline

1. `vm-recreate.ps1` — recreate the Gen2 VM (TPM, nested virtualization, 80GB vhdx).
2. `offline-deploy.ps1` — apply `F:\tinywin2\out\dev-server.esd` to a fresh vhdx,
   `bcdboot`, stage `Unattend-oobe.xml` into `Panther` + `Sysprep`, point
   `HKLM\SYSTEM\Setup!UnattendFile` at it, boot. The unattend creates the
   `TinyWin` account (password `TinyWin2026Temp`) with AutoLogon.
3. `bypass-oobe.ps1` — only needed if msoobe still shows UI: clears the forced-setup
   registry state (SetupType/SystemSetupInProgress/CmdLine) and forces
   Winlogon AutoAdminLogon, then reboots.
4. `snap.ps1 <vmName> <out.png>` — one-shot guest screenshot via
   `GetVirtualSystemThumbnailImage` (768x1024, 16bpp, `ImageData` is already a
   decoded byte[] — do not base64-decode). Black frames mean the display idled;
   wake the guest with a Shift scancode via `Msvm_Keyboard.TypeScancodes`.
5. `verify-final.ps1` — PowerShell Direct suite verifying the applied plan set
   (service start types, SearchHost removal, perf registry values, power scheme,
   process/service counts).

## Physical deployment runbook (optional, workload-specific)

Not baked into the image — apply per machine when the workload calls for it:

- **BBR2 congestion control** (cross-region / lossy WAN throughput; LAN gains nothing):

  ```powershell
  Set-NetTCPSetting -SettingName Internet -CongestionProvider BBR2
  ```

- **Timer resolution**: ships governed by `registry.timer-resolution` (default honors
  process `timeBeginPeriod` requests; override with
  `--set registry.timer-resolution.mode=ignore` on battery-sensitive builds).
- **HAGS** is on by default for supported GPUs on Server 2025 — no plan needed.
- **SSH server**: the image removes the OpenSSH Server capability, and `/ResetBase`
  strips the FoD metadata payload, so `Add-WindowsCapability` cannot resolve the name
  even with wuauserv/DoSvc running. Restore in one command with the bundled script
  (Microsoft's official Win32-OpenSSH build from GitHub):

  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\install-openssh-server.ps1 [-PublicKey "<ssh-ed25519 ...>"]
  ```
- Deliberately NOT provided: Spectre/Meltdown mitigation toggles (security-hostile),
  standby-list purge tools (self-defeating), DisablePagingExecutive (RAM cost with no
  server-side benefit).

## Known behaviors:

- **Client images with a NIC attached brick at first boot**: online OOBE calls the
  stripped update stack and msoobe/wuapi fastfails (0xC0000409) into a
  "Windows 无法完成安装" boot loop (2026-10-17 vm-init v19/v20 smoke). Offline
  boots limp through to the desktop. Fix: apply the `bypass-oobe.ps1` registry
  block (clear Setup state + Setup\OOBE displayed flags + Winlogon autologon)
  offline after `/Apply-Image`, or live via PowerShell Direct while the loop
  runs — winlogon then stops launching msoobe and first logon goes straight to
  the shell. `registry.update-policy-strip` (deleting the UpdatePolicy CLSIDs)
  is NOT a fix: it moves the same fastfail into msoobe and bricks OOBE harder.
- **OOBE bypass execution gotchas (2026-10-17 vm-init v22/v23 smoke)**:
  1. Writing the bypass regs then `Stop-VM -TurnOff` LOSES them — the hard power
     cut drops the unflushed registry. After writing the regs, flush with a
     **clean guest restart** (`Restart-Computer -Force`).
  2. A guest-initiated restart can drop the disk from the Gen2 firmware boot
     order (falls to PXE, "no boot devices"). Recover host-side: stop the VM,
     `Set-VMFirmware -FirstBootDevice (Get-VMHardDiskDrive ...)`, start.
  3. Never leave a `Mount-VHD` / WSL `drvfs` mount on the VM's VHD volumes —
     leaked handles block `Start-VM` with "另一个程序正在使用此文件" and survive
     vmms/vmcompute restarts. `wsl --shutdown` + explicit Dismount release them.
  The validated end-to-end sequence lives in `hyperv/tools/bypass-oobe-live.ps1`
  (bypass regs → clean guest restart → host-side boot-order fix).
- **SecHealthUI removal (validated live)**: the Start entry can be removed with
  the reference flow (`script/RemoveSecHealthApp.ps1`) run elevated via
  PowerShell Direct — the critical bit is **EndOfLife markers for EVERY user
  SID** (S-1-5-18 + all S-1-5-21-*); missing the calling user's SID yields
  0x70032 "part of Windows". Validated copy: `hyperv/tools/remove-sechealth-live.ps1`.
  The staged package payload itself stays in WindowsApps (WindowsApps ACL wall
  blocks offline deletion) — cosmetic orphan, no functions behind it. For real
  setup.exe deployments the staged `SetupComplete.cmd` covers removal; the
  offline-apply smoke flow never runs SetupComplete, so use the live script.
- After killing an interrupted build, its `workspace\base.vhdx` stays attached at the
  Virtual Disk level: `Dismount-VHD -Path <workspace>\base.vhdx` **before** deleting the
  workspace, or the delete fails with "Device or resource busy" and the next build
  rejects the leftover as a non-empty workspace.
- The `maintenance.component-cleanup` / `maintenance.component-resetbase` plans strip the
  payload of every *disabled* optional feature (that is most of the size win). Features
  that must be installable later — e.g. Hyper-V management tools, whose payload is not on
  the Server media at all — need either their feature enabled in the image or a `/Source`
  restore at deploy time; `offline-deploy.ps1` restores Hyper-V management from the staged
  original WIM automatically.
- Windows Setup specialize payload-scrubs *disabled* optional features on first boot.
  Anything that must survive has to be enabled offline **before** the first boot
  (`offline-deploy.ps1` does this for the Hyper-V core).
- Windows Setup specialize re-provisions inbox scheduled tasks and may reset
  `W32Time` to manual for OOBE time sync; FirstLogonCommands re-delete/re-disable
  after logon. The dev overlay pins `service.time-sync` to `startMode: disabled`
  so both plans agree on W32Time.
- CBS "reginf repair": any servicing transaction (`/StartComponentCleanup`,
  `/ResetBase`, feature enable) re-applies the registry registrations of still-
  present components and re-creates *missing* keys with a TrustedInstaller-only
  security descriptor that not even SYSTEM can delete. Deleting component-owned
  registry keys must therefore be the **last** step after every CBS operation —
  `registry.control-panel-orphans` is selected at the tail of `developer-overlay`
  and `requires maintenance.component-resetbase` for exactly this reason.
- Detach the smoke-test VM's DVD before `package iso --overwrite`: oscdimg fails
  with "file in use" while the VM holds the ISO mounted.
