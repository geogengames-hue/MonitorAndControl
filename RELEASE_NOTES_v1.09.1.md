# Monitor & Control v1.09.1

## Fix

- **Settings page layout.** The Settings page showed as one long flat list of labels and
  inputs, with stray divider lines, instead of tidy panels. Its grouping script picked the
  first settings form on the page, which has been the PC Time form since v1.7.0, so the real
  Settings form was never grouped. It now targets the Settings form, so you get the panels
  again (General & Security, Other PCs, Phone notifications, Email, Reports, Backup,
  Updates, Health), including on phones. **Other PCs** now has its own panel.

## Upgrade notes

- No settings or data change; this is a display fix only.
- Everything from 1.9.0 applies (safe browsing, allow-list mode, activity timeline, phone
  notifications, other PCs).
