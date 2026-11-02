# Monitor & Control v1.11.0

## Highlights

- **"PC went quiet" alert.** A switched-off or unplugged PC cannot send an alert itself, so
  this uses a free outside check. At healthchecks.io create a check (Period 10 minutes,
  Grace 10 minutes, or a schedule for the hours the PC is used), connect it to ntfy,
  Telegram or email, and paste its ping address into Settings > Phone notifications >
  **"PC went quiet" check-in address**. The GameHost service calls it every 5 minutes, also
  while nobody is signed in; when the calls stop you are alerted. The page shows the last
  successful check-in. Only https addresses on the internet are accepted.
- **Answer a time request from the phone.** A "more time" alert has three buttons:
  **Add 15 min** / **Add 30 min** open PC Time with the request filled in and ask for the
  remote PIN as usual. **Not now** declines at once (one tap in ntfy; in Telegram it opens a
  small page with a Not now button). It uses a single-use link that can only decline that
  one request and expires with it after 12 hours.
- **Time zone can no longer shift the rules.** Any Windows account may change the time zone,
  which used to move bedtime and start a new day early. The service now pins the time zone,
  and bedtime, schedules, daily limits and every "today" counter keep using it. A change is
  reported to you, and PC Time shows **Use the new time zone**, which needs your approval.
  Daylight-saving changes are handled automatically.
- **Groups, schedule and website rules per account.** Choose an account in the
  **Everyone** box of the group, schedule and website forms. An account's group counts only
  that account's time (an app can be in one group for Anna and another for Ben); its website
  allowance counts only its own browsing. Website rules for an account are in force while
  that account is at the screen (within about 5 seconds of switching user). All of it is part
  of **Protect limits**.

## Fixes and hardening (found by code review)

- After a restart, a group for one account no longer blocks apps for another account; app
  start alerts also ignore other accounts' groups.
- **Not now** on an older alert declines only that request, not a newer one from the same
  child. The link is used up only once the answer is recorded, so a failed try can be
  repeated, and a re-sent alert keeps the same link.
- Accepting a new time zone pins exactly the zone you were shown; if Windows was switched
  again in the meantime, the approval is refused.
- The check-in address cannot reach the home network or the PC itself, including through
  IPv4-mapped IPv6 addresses or a name that resolves to a local address.
- With two accounts signed in at once (for example remote desktop), both accounts' website
  rules apply.
- Editing a website rule and changing its site or account replaces the rule instead of
  leaving a duplicate.
- Extra app time from email or the dashboard is bounded only by the schedule rules for
  everyone, not by another account's schedule.
- Protect limits now holds up to 2000 website rules (shared and per account together).

## Upgrade notes

- Everything new is off or empty until you set it. Existing groups, schedule and website
  rules stay "Everyone".
- The time zone in effect when 1.11.0 first starts is the one that gets pinned.
- Limits sealed by an older version still match; there is no need to protect them again.
- The phone buttons need your phone to reach the dashboard (home network or VPN). Telegram
  may refuse buttons for a bare PC name; the alert is then sent without them.
- Account website rules and account limits are not part of the config export yet; homework
  mode, safe browsing and allow-list mode still apply to everyone.
- Not yet verified on a real install: the check-in alert, the phone buttons, the pinned time
  zone and per-account groups, schedule and websites (needs two Windows accounts). Test with
  a spare account first.
