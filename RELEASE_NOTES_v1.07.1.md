# Monitor & Control v1.07.1

## Highlights

- **Change PC Time rules from another device.** Until now rules could only be edited
  on the child PC (UAC). You can now set a **remote PIN** on the child PC
  (`GameHost.exe --set-remote-pin`, Administrator prompt) and approve changes from
  your phone or another computer with it. The PIN is stored only as a salted
  PBKDF2 hash in a folder the child account cannot read. Wrong guesses lock remote
  changes for 15 minutes, doubling up to 24 hours, and every lockout sends you an
  alert. Leave the PIN unset to keep PC Time editable only at the PC.
- **Health & diagnostics page.** The dashboard shows install protection, config
  source, hotkey registration (never the combination itself), update channel and
  last update result, PC Time service state and data sizes, with a **Copy report**
  button for support. Problems are marked OK / warning / problem.
- **Tooltips and "?" help mode.** Controls in the dashboard now explain
  themselves on hover. A "?" button turns on help mode for touch screens: tap
  any item to read what it does without triggering it. Translated into
  German, Spanish, French and Russian.
- **`release.ps1`** builds, tests and packages a release (matched `DeviceMon.zip`
  and versioned zip with `.sha256` files) and can publish it to GitHub.

## Security hardening

- The service refuses remote changes unless the PIN folder is owned by
  SYSTEM/Administrators and grants nothing to other accounts; the PIN tool refuses to
  store a PIN in an unsafe folder.
- Remote-PIN lockouts are counted by the service itself and alerted by DeviceMon,
  even if general tamper alerts are off.
- Remote reads and writes of PC Time need the dashboard admin login and a
  same-origin request; the service checks only the PIN, so treat it as the key.
- The in-dashboard update button no longer offers an update that a protected install
  cannot apply without the system update channel.

## Fixes

- Update status was ignored when its field names differed in letter case.
- Hotkey "registered" status now clears when it is unregistered.
- Wrong-PIN alerts are throttled safely under concurrent requests.

## Upgrade notes

- Standard update path works as before (quiet SYSTEM update).
- The remote PIN is **off** until you set it. To enable, on the child PC:
  `GameHost.exe --set-remote-pin` in an Administrator prompt.
