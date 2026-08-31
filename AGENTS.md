# AGENTS.md

Operating guide for AI agents and automation driving **TinyWin2** — a layered Windows
image slimming tool with explicit inputs, outputs, and workspaces.

Everything here is Windows-only and requires an elevated shell (DISM, diskpart, VHDX
attach). Run `tinywin2 doctor --workspace <dir>` first; it checks elevation, tools,
and disk space.

## Command map

```
tinywin2 doctor                 # environment readiness (exit 1 on failed required checks)
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
  --profile profiles/base-full.json
tinywin2 build --input win11.iso --index 1 --output out/install.esd --workspace ./ws \
  --format esd --single-layer --profile profiles/base-full.json \
  --profile profiles/developer-overlay.json
tinywin2 package iso --input win11.iso --install-image out/install.esd \
  --output out/win11-slim.iso --workspace ./pkg --oscdimg "C:/oscdimg/oscdimg.exe"
```

Profiles layer in `--profile` order (later overrides earlier): the base
`profiles/base-full.json` (the whole catalog, every removal enabled except the Client.CBS/SysMain baseline); scenario overlays
(e.g. `profiles/developer-overlay.json`) carry only their deltas — keeps
(`enabled: false`) and parameter overrides. A plan disabled by any layer cannot
be re-enabled by a later one. New plans: add an enabled selection to the base
profile (one file), put scenario-specific overrides in the overlay.

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
Patterns on `appx.provisioned`, `appx.system`, `registry.service`, and `fs.path` are
PowerShell-like wildcards (`*`, `?`); `dism.package` patterns are .NET regular
expressions.
