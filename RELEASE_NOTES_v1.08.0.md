# Monitor & Control v1.08.0

## Highlights

- **Website limits and blocking.** On the Limits page, block a site completely (0 minutes)
  or give it a daily allowance (for example `youtube.com`, 30 minutes). Subdomains are
  included. Chrome, Edge, Brave and Firefox show their own "blocked by your
  administrator" page, also in private windows. The GameHost service writes the block
  list into the browsers' Windows policy, which a standard user cannot change.
- **Website time, without a browser extension.** DeviceMon reads the address bar of the
  foreground browser through Windows UI Automation and adds up time per site. Only the
  site name is stored, never the page, search text or title; private windows are one
  anonymous entry. See **History > Top Websites** and the summary email. Turn it off in
  Settings.
- **Protect limits.** Under PC Time, seal your app limits, schedule, groups and website
  rules so they can only be changed after you approve it (Windows prompt on the PC,
  remote PIN from another device). The service keeps the sealed copy, so edits to the
  database are undone even after DeviceMon is closed and restarted.
- **Lock now.** Lock a child's account for a set time (homework, dinner) from the
  dashboard or your phone. Its programs keep running; it returns to the sign-in screen.
- **Bedtime.** Per-account bedtime and wake-up time, with an optional later weekend
  bedtime (Friday and Saturday nights) and warnings before sign-out.
- **Ask for more time.** At the 5-minute warning the child can ask for more time. You get
  an alert and approve with **Add 15 / 30 / 60 min**, or decline with **Not now**.
- **Weekend allowance** for PC Time (Saturday and Sunday).
- **This week vs last week.** A trend panel on History and in the summary email, per day
  and per app (foreground time only, so overlapping apps are not added up).
- **Repeated-close alert** when DeviceMon is closed 3 times within 30 minutes, and a
  guard that restores app limits changed behind DeviceMon's back.

## Also changed

- The summary email now lists how many times DeviceMon was restarted, each account's
  PC Time usage, the week-on-week comparison and the top websites.
- The Health data flag `dashboardUpdateAvailable` is now `canUpdateFromDashboard`.
- Better touch targets for Lock now, requests and the weekly chart on phones.

## Upgrade notes

- Everything new is off or empty until you set it, except **website time tracking**,
  which is on by default (Settings > Track time on websites). It stores site names only.
- Website blocking and Lock now need the GameHost service. Other browsers (Opera,
  Vivaldi, portable ones) are not covered by blocking, and an administrator account can
  undo browser policy.
- Time-request alerts are sent even when tamper alerts are off. Turn them off with the
  database setting `TimeRequestAlertsEnabled=false`.
- Requires the .NET 8 Desktop Runtime, as before (a Windows UI Automation reference is
  added for reading the address bar).
- Not yet verified on a real install: Lock now, bedtime sign-out, browser blocking and
  the sealing flow. Test them on a spare account first.
