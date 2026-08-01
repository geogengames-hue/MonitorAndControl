# Monitor & Control v1.06.1

Patch release. Fixes update stability; see v1.06 for the full feature set.

## Fixes

- **Failed updates no longer destabilize the machine.** The updater now downloads
  and verifies the package **before** stopping GameHost or closing DeviceMon, and
  restarts GameHost if a later step fails. Previously, a bad or hash-mismatched
  download (e.g. a `DeviceMon.zip` that didn't match its published `.sha256`) left
  the GameHost service stopped and DeviceMon unable to restart it, causing
  `watchdog_unavailable` tamper alerts and repeated restarts.

## Upgrade notes

- Install/upgrade with `install.ps1` as Administrator. Existing installs pick this
  up on the next successful update.
- When publishing releases, make sure `DeviceMon.zip` and `DeviceMon.zip.sha256`
  are a matched pair (generated together); a mismatch is (correctly) rejected by
  the SYSTEM updater.

## Verification

- Release build completed successfully for all four executables.
- Full regression suite passed (29 tests).
