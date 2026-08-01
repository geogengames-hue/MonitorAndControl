# Monitor & Control v1.06.2

Patch release. See v1.06 for the full feature set.

## Fixes

- **Updates no longer reset `appsettings.json`.** Settings that live only in
  `appsettings.json` - notably `EnableRemoteDashboard` and `DashboardBindAddress` -
  were overwritten with the packaged defaults on every update and reinstall. The
  updater and `install.ps1` now preserve an existing `appsettings.json` and only
  write it on a fresh install.

## Upgrade notes

- After installing this build, set `appsettings.json` once (as Administrator, in
  the install folder) and your dashboard/remote settings will survive future
  updates.
- New configuration keys added in later versions fall back to built-in defaults,
  since your existing `appsettings.json` is kept as-is.

## Verification

- Release build completed successfully for all four executables.
- Full regression suite passed (29 tests).
- appsettings.json preservation verified for both the updater and the installer.
