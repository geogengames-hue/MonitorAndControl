# Monitor & Control v1.13.0

## Highlights

- **Break reminders.** In PC Time set **Break after** (15 to 240 minutes of use in a row) and
  **Break length** (5 to 120 minutes), for example 45 and 10. The child is warned 5 minutes and
  1 minute before; then the account goes to the Windows sign-in screen until the break is over
  (programs keep running). Being away from the PC for a whole break length also counts as a
  break. PC Time shows "On a break until ...", and **Unlock now** ends a break early. Breaks are
  part of the PC Time rule, so changing them needs your approval (Windows prompt on the PC,
  remote PIN from another device).
- **App categories.** A shared group can cover a whole category: choose **+ all Games** (or
  Social apps, Video apps, Browsers, School apps) in the group form. "Games 1 h" then also
  counts games installed later, and once the time is used up a newly opened game is closed at
  once. DeviceMon guesses each app's category from its name and install folder (Steam, Epic,
  Riot, Xbox, GOG, Ubisoft, EA, Roblox, Battle.net, Minecraft). **Limits > App categories**
  lists the guesses; change any from its dropdown. An app ticked by name in another group stays
  in that group. Part of **Protect limits**.
- **Always-allowed apps don't use up PC time.** While an always-allowed app is in front, that
  time is not counted against the account's PC Time allowance. If DeviceMon is closed, the time
  is counted again. A program renamed to an always-allowed name is recognised by the name built
  into it. Break reminders still count this time.
- **Complete backups.** Configuration export/import now also covers account limits, website
  rules (for everyone and for one account), homework apps, always-allowed apps, category
  choices and group categories. Rules for Windows accounts that are not on the PC you import
  into are left out, and the import says how many.

## Fixes and hardening (found by code review)

- Turning breaks off or pressing **Unlock now** ends a break that has already started; a
  shorter break length shortens it.
- Making the time between breaks shorter than the child has already used gives a 1-minute
  warning first instead of locking at once.
- A group that is turned off no longer takes its app out of the category group.
- A game whose program file cannot be read at first (elevated or anti-cheat) is looked at
  again every 30 seconds instead of never getting a category.
- A used-up category group is known right after DeviceMon starts and right after a group is
  edited or deleted.
- Apps built on Electron (VS Code and others) count as genuine always-allowed apps.
- Two quick category changes no longer lose one of them.
- The always-allowed time report does not make the service list accounts or read the rules.

## Upgrade notes

- Everything new is off or empty until you set it. Existing groups keep working as before
  ("Only the apps ticked below").
- Backups made by older versions still import; the rule types they do not contain are left as
  they are.
- Limits sealed by an older version still match; there is no need to protect them again.
- Limits: a program without a built-in name can still be renamed; category guesses cover
  common apps only (choose the rest in the dropdown); the always-allowed exemption applies only
  while the app is in front.
- Not yet verified on a real install: breaks, categories and the always-allowed exemption.
  Test with a spare account first.
