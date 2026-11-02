# Monitor & Control v1.07.2

## Highlights

- **Lock now (pause this PC).** On PC Time, lock a child's account immediately for a
  set number of minutes (homework, dinner). Its programs keep running; it returns to
  the sign-in screen and is locked again if it signs in before the time is up. Locked
  time does not count against the allowance. Unlock early at any time. Uses the same
  approval as other PC Time changes (UAC on the PC, remote PIN from another device).
- **Bedtime.** Set a bedtime and wake-up time per account (with an optional later
  weekend bedtime for Friday and Saturday nights). The account is signed out overnight,
  with warnings 10, 5 and 1 minutes before.
- **Ask for more time.** At the 5-minute warning the child can answer Yes to "Ask your
  parent for more time?". You get an alert, and **Add 15 / 30 / 60 min** on PC Time
  fills in the extra minutes for you to save.
- **Weekend limit.** A separate daily allowance for Saturday and Sunday.
- **Weekly trend report.** History shows this week vs last week per day and per app,
  and the summary email ends with the same comparison. The email also reports how
  many times DeviceMon was restarted and each account's PC Time usage.
- **Repeated-close alert.** If DeviceMon is closed 3 times within 30 minutes you get an
  extra alert on top of the normal restart alert.
- **Limits tamper guard.** If the app limits or schedule in the database are changed
  by another program while DeviceMon is running, the previous values are restored and
  you are alerted.

## Upgrade notes

- Existing PC Time rules keep working; the new options are off until you set them.
- The remote PIN from 1.7.1 is unchanged and also approves lock, bedtime and
  weekend-limit changes.
- Time requests alert you even when tamper alerts are off. Turn that off with the
  database setting `TimeRequestAlertsEnabled=false`.
- The tamper guard covers app limits and the schedule (not groups) and only works
  while DeviceMon is running.
