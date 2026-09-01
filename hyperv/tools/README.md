# Hyper-V smoke-test tooling

Host-side scripts for driving the smoke-test VM without a GUI console. All paths
reference `F:\tinywin2\` (vm/, iso/, out/, probe/) — adjust to your layout.

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

## Known behaviors

- Windows Setup specialize re-provisions inbox scheduled tasks and may reset
  `W32Time` to manual for OOBE time sync; FirstLogonCommands re-delete/re-disable
  after logon. The dev overlay pins `service.time-sync` to `startMode: disabled`
  so both plans agree on W32Time.
- Detach the smoke-test VM's DVD before `package iso --overwrite`: oscdimg fails
  with "file in use" while the VM holds the ISO mounted.
