# Monitor & Control v1.12.0

## Highlights

- **New programs need your OK.** Under PC Time > New programs press **Ask me before new
  programs**. Programs already on the PC keep working. A program installed, downloaded or
  copied later is closed when it opens, the child is told you have been asked, and you get an
  alert with **Allow** (asks for your approval: Windows prompt on the PC, remote PIN from
  another device) and **Keep blocked** (one tap, no sign-in). The list in PC Time shows what
  is waiting, blocked and allowed. The list is kept by the SYSTEM service, so the child cannot
  approve anything; turning it off needs your approval. Your administrator accounts, Windows,
  its built-in apps and DeviceMon are never held up.
- **Ask to open a blocked site.** When the child is on a blocked website (blocked, daily time
  used up, allow-list or homework mode), DeviceMon asks whether to ask you. Your alert has
  **Allow** (opens Limits with the website form filled in for that child; press Save) and
  **Not now**. Requests also appear on the Limits page. The child is told the answer.
- **Always-allowed apps.** Under Limits > Always allowed, list apps that are never closed: not
  by the schedule, app limits, groups, homework mode, Block now or the new-program check. For
  school apps, a calculator or a music tutor. Part of **Protect limits**. Bedtime, PC time
  running out and Lock now still sign the account out or lock it.

## Fixes and hardening (found by code review)

- Parents' administrator accounts are recognised by the account itself, so a UAC-filtered
  sign-in is never treated as a child's.
- Programs are recognised by the original file name built into them: renaming a game to an
  allowed name (for example chrome.exe) does not get it past the check.
- An account that signs in for the first time after you turned the check on does not record
  program files created after that moment as "already there".
- Windows' own Store apps (Notepad, Calculator, Photos, Terminal and others) are not treated
  as new programs.
- A website request is marked allowed only when the site really opens for that child. If
  another rule still blocks it, the Limits page says so and the request stays open.
- Lapsed website requests (older than a day) no longer stop the child asking again.
- Request alerts that cannot be delivered are retried every 15 minutes with the same link,
  instead of every minute.
- A program still waiting for an answer is never announced twice.
- "Ask to open this site" also works with website time turned off (the address is checked,
  not recorded).
- The new-program list is read again only when it changes.

## Upgrade notes

- Everything new is off or empty until you set it, except the "ask to open this site"
  question, which appears only when a site you blocked is opened.
- Turn on the new-program check, then look at the list after a day: a program that was
  already there but whose built-in name differs from its file name may be asked about once.
- Limits of the new-program check: a program without a built-in name can still be renamed to
  an allowed one, and a program is checked when it comes to the front (a program without a
  window is not closed).
- Limits sealed by an older version still match; there is no need to protect them again.
- Request alerts follow the same switch as time requests.
- Not yet verified on a real install: the new-program check, website requests and
  always-allowed apps. Test with a spare account first.
