# Monitor & Control v1.07.0

## Highlights

- **PC Time: per-account daily screen-time limits.** Give a standard Windows account
  a daily allowance that applies across all apps. Time counts while the account's
  session is active and unlocked; when it runs out, the GameHost service signs the
  account out (and again on each new login until the next day). Warnings at 10, 5 and
  1 minutes. Supports a one-day bonus and independent counters per account (tracked
  by Windows SID). Administrators are never controlled.
- **Administrator-approved changes.** Rules are stored and enforced by the SYSTEM
  service in a protected folder. Changing them requires Windows (UAC) authorization on
  the PC itself, via single-use, expiring approval tickets over a local named pipe.
  Rules cannot be edited from a remote browser.

## Credits

The screen-time module is based on a contribution by Marlon (Pensarei) in pull
request #1. It was integrated into the existing install/update flow, translated to
English with de/es/fr/ru dictionary entries, and hardened (see below).

## Hardening on top of the original contribution

- Pending approval tickets are capped per caller, so a standard user cannot fill the
  approval queue and block the administrator.
- Usage counters and status are checkpointed every few seconds (and immediately
  before a sign-out) instead of rewriting files every second.

## Upgrade notes

- Install/upgrade as usual (`install.ps1`, or the remote update). The PC Time tab
  appears in the dashboard; no configuration is needed until you add a rule.
- The feature needs the GameHost service running; without it the tab reports that
  screen-time control is unavailable.

## Verification

- Release build completed for all four executables.
- Full regression suite passed (39 tests, including account isolation, day rollover and
  clock rollback, protocol bounds, approval expiry/replay/ownership, per-caller ticket
  cap, and the local HTTP boundary).
- Not yet exercised on a clean machine: the real UAC approval flow and the actual
  sign-out at quota exhaustion (these need the installed SYSTEM service).
