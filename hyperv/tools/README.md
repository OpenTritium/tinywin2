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
- Deliberately NOT provided: Spectre/Meltdown mitigation toggles (security-hostile),
  standby-list purge tools (self-defeating), DisablePagingExecutive (RAM cost with no
  server-side benefit).

## Known behaviors

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
- Detach the smoke-test VM's DVD before `package iso --overwrite`: oscdimg fails
  with "file in use" while the VM holds the ISO mounted.
