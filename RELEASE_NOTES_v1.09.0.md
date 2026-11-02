# Monitor & Control v1.09.0

## Highlights

- **Safe browsing.** One switch on the Limits page forces **SafeSearch** in Google and Bing
  and **YouTube Restricted Mode (strict)** in Chrome, Edge and Brave. The child cannot turn
  it off. (Firefox has no such policy.)
- **Allow-list mode.** Only the websites you add as **Always allow** (or with a daily
  allowance and time left) can be opened in Chrome, Edge, Brave and Firefox; everything
  else is blocked. The dashboard on the PC and the browsers' own pages keep working. Test
  with a spare account first: sites often need login or other domains to work.
- **Activity timeline.** History shows, hour by hour for any day in the last 90 days,
  which apps were in the foreground and the top websites.
- **Phone notifications.** Get tamper alerts, time requests, lockouts and limit events as a
  push notification through **ntfy** (free app, no account) or a **Telegram** bot,
  alongside email and the webhook. Nothing is installed on the child's PC.
- **Other PCs.** Add shortcuts to other PCs' dashboards in the sidebar. No passwords are
  stored: this PC belongs to the child, so it must not hold the keys to another one.
- Website rules now come in three kinds: **Block completely**, **Limit minutes per day**
  and **Always allow**. Safe browsing and allow-list mode are part of **Protect limits**,
  so once sealed they cannot be switched off without your approval.

## Fixes and hardening (found by code review)

- Turning safe browsing off now restores the values that were there before instead of
  deleting them, and a failed change is retried rather than forgotten.
- Push notifications refuse redirects, so the checked server address cannot be sidestepped,
  and are sent side by side with the webhook so a slow service cannot delay it.
- `[::1]` and the PC's own name stay reachable in allow-list mode.
- Malformed requests to the new settings endpoints get a clear error instead of a crash.
- Timeline tooltips escape app names properly.

## Upgrade notes

- Everything new is off or empty until you set it.
- The ntfy topic or Telegram token is stored on the child's PC. Treat it as low-risk (it can
  only send you messages) and use a long random topic.
- Safe browsing and allow-list mode use browser policy, so they need the GameHost service
  and do not cover Opera, Vivaldi or portable browsers. A child with an administrator
  account can undo browser policy.
- Not yet verified on a real install: safe browsing, allow-list mode and phone delivery
  (use **Send test**). Website blocking itself is confirmed working.
