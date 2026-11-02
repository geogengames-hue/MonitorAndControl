# Monitor & Control v1.08.1

A review of 1.8.0 found one serious problem and several smaller ones. All are fixed here.
Updating is recommended.

## Security

- **Only DeviceMon can use the service's no-approval commands.** `set-blocked-sites`,
  `limits-seal`, `limits-close` and `dismiss-request` were reachable by any program the
  child ran, so an empty block list or a forged sealed copy was one message away. The
  GameHost service now accepts them only from the installed `DeviceMon.exe` and logs any
  refusal with the caller's path.
- **Private windows no longer avoid website limits.** Time in Incognito and InPrivate
  windows is now counted against the site's allowance. (`(private window)` is only used
  when the address bar cannot be read.)
- A sealed copy with missing fields can no longer crash the service; the size limit for
  a sealed copy is raised to 512 KB.
- Remote opening or removal of **Protect limits** now sends its own alert instead of a
  misleading "PC Time was changed" message.

## Fixes

- **History > Top Websites** moved up next to Top Apps; it used to sit far down the page.
- One DeviceMon restart is now counted once, even when its alert is retried, so the
  restart count and the repeated-close alert are accurate.
- Website time is no longer under-counted by rounding.
- "Ask for more time" alerts are retried at most every 15 minutes when no email or
  webhook is configured, instead of every minute.
- A weekend bedtime after midnight (such as 00:30) is refused with a clear message; it
  must be an evening time after the wake-up time, like the normal bedtime.
- The address-bar reader is safe against overlapping reads when a browser stalls.

## Upgrade notes

- If website blocking or Protect limits stop working after updating, check the GameHost
  log for "Refused ... only DeviceMon may send it" and send it to me; it names the path
  the service saw.
- Still to verify on a real install: Lock now, bedtime sign-out, browser blocking and the
  sealing flow.
