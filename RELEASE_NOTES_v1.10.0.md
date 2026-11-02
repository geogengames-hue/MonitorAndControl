# Monitor & Control v1.10.0

## Highlights

- **Homework mode.** Under PC Time press **Start 60 min** or **Start 120 min**. The apps you
  listed under Limits > Homework mode are closed (again if reopened), and only the websites
  you allow (Always allow, or with time left on a daily allowance) can be opened. The child
  sees a message when it starts. It ends by itself; **ending it early needs your approval**.
  The end time is kept by the SYSTEM service, so closing DeviceMon does not cancel it. The
  app list is part of **Protect limits**.
- **Limits for one account.** App limits that count **one Windows account's own time**, so two
  children sharing a PC can each have their own allowance. Each signed-in account's DeviceMon
  records its own time and closes the app only in its own session. They apply on top of the
  shared limits (the stricter wins) and are part of Protect limits. Only app limits are per
  account for now.
- **Time left for the child.** An optional taskbar icon in the child's own session (off by
  default): hover for a short summary, click for the details of PC time, bedtime, lock,
  homework mode and every app allowance.
- **Phone alerts open the right page.** ntfy alerts get an **Open dashboard** button and
  tap-to-open link, Telegram alerts end with the link. A time request opens PC Time, a new
  program opens Limits. Set **Dashboard address for links** if your phone reaches the PC at
  a different address.
- **New-program alerts.** Every few hours DeviceMon looks for newly installed programs and
  games and tells you by email and phone, so you hear about them before they are used.
- **Time requests** now have a **Not now** button (from 1.8) and homework closures are no
  longer reported as "schedule blocks".

## Fixes and hardening (found by code review)

- Starting a shorter homework session can no longer cut a running one short.
- Another account's copy of an app is no longer counted as this account's (running-app
  checks look at this account's own session only).
- Raising, removing or giving bonus time to a limit in the dashboard is now noticed by every
  account's DeviceMon, not only the one serving the page.
- Removing a tracked app also removes its account limits and that account's recorded time.
- Account SIDs of any real form are accepted (including Microsoft and Entra accounts).
- Phone-alert links keep their target page through the sign-in screen.
- App names are case-insensitive for account limits and usage.
- Adding and removing a homework app happens on the server in one step, so two open
  dashboards cannot overwrite each other.
- New-program alerts: each account is scanned in its own turn, a program is announced once,
  and an empty first scan is not treated as a baseline.
- The Settings page again groups into panels (fixed in 1.9.1), and the ntfy / Telegram labels
  now sit above their boxes.

## Upgrade notes

- Everything new is off or empty until you set it. The taskbar icon is off by default.
- Time-request emails and alerts are labelled **Request**, and new-program alerts
  **Notice**, instead of "Tamper Alert". If you filter mail on the old subject, update the
  rule.
- Time recorded before this version counts only toward shared limits, not account limits.
- Account limits are not part of the config export yet; groups, the schedule, website
  rules and homework mode still apply to everyone.
- Homework mode and bedtime use the PC clock: Windows lets only administrators change the
  system time by default, but a changed time zone can shift anything based on local time.
- Not yet verified on a real install: homework mode, account limits (needs two Windows
  accounts signed in), the taskbar icon and the phone link buttons. Test with a spare
  account first.
