// PC Time view: per-account daily screen-time allowance.
// Shares the dashboard's navigation, authentication (api()) and translations (t()).
// Policy changes are authorized by Windows (UAC) on the PC itself; see ScreenTime/.
async function screenApi(url, options) {
  try { return await api(url, options); }
  catch (error) {
    let message = error.message;
    try { message = JSON.parse(message).error || message; } catch (_) {}
    throw new Error(message);
  }
}

let screenData = null;
let screenDirty = false;
let screenLoading = false;
let screenPending = null;
let screenBusy = false;
// Set when the page was opened from a phone alert's "Add N min" button; used once the requests are loaded.
let screenPendingGrant = null;
// Set when the page was opened from a new-program alert's "Allow" button.
let screenPendingProgram = null;
const screenElement = id => document.getElementById('screen-' + id);

// On the child PC itself a change is approved with a Windows (UAC) prompt. From any other
// device there is no prompt to answer, so a PIN set on the PC authorizes it instead.
const screenIsLocal = ['localhost', '127.0.0.1', '[::1]', '::1'].includes(location.hostname);

// Returns why a remote change cannot be made right now, or '' when it can.
function screenRemoteBlock() {
  if (screenIsLocal || !screenData) return '';
  const remote = screenData.remote;
  if (!remote?.configured)
    return t('Remote changes are off. On the child PC, run GameHost.exe --set-remote-pin as administrator, or open this dashboard on that PC.');
  const until = remote.lockedUntilUtc ? new Date(remote.lockedUntilUtc) : null;
  if (until && until > new Date())
    return t('Remote changes are locked until {time} after too many wrong PIN attempts. You can still make changes on the child PC.', { time: until.toLocaleTimeString() });
  return '';
}

function screenDuration(seconds) {
  return Math.floor(seconds / 3600) + 'h ' + Math.floor(seconds % 3600 / 60) + 'm';
}

function screenSelectedProfile() {
  return screenData?.profiles.find(p => p.userSid === screenElement('account').value);
}

function screenRenderSelection(populate) {
  const sid = screenElement('account').value;
  const account = screenData?.accounts.find(a => a.sid === sid);
  const profile = screenSelectedProfile();
  screenElement('account-name').textContent = account?.name || t('Select an account');
  screenElement('account-sid').textContent = account?.sid || '-';
  screenElement('account-help').textContent = account && !account.eligible
    ? t('{reason}. An old rule can be turned off, but not turned on.', { reason: t(account.exclusionReason) })
    : t('Each account has its own allowance; other accounts are unchanged.');
  screenElement('used').textContent = profile ? screenDuration(profile.usedSeconds) : '0h 0m';
  screenElement('remaining').textContent = profile?.enabled ? screenDuration(profile.remainingSeconds) : t('Unlimited');
  screenElement('allowance').textContent = profile
    ? t('{n} min/day', { n: profile.dailyMinutes }) + (profile.weekendMinutes ? ' · ' + t('{n} min at weekends', { n: profile.weekendMinutes }) : '') +
      (profile.bedtimeStart ? ' · ' + t('bedtime {from}-{to}', { from: profile.bedtimeStart, to: profile.bedtimeEnd }) : '') +
      (profile.breakAfterMinutes ? ' · ' + t('{n}-min break every {m} min', { n: profile.breakMinutes, m: profile.breakAfterMinutes }) : '') + (profile.bonusMinutes ? ' ' + t('+ {n} min today', { n: profile.bonusMinutes }) : '')
    : t('No rule applied');
  screenElement('state').textContent = profile?.enabled
    ? (profile.remainingSeconds <= 0 ? t('Time is up') : t('Active'))
    : t('Not controlled');
  const editable = !!account && (account.eligible || !!profile);
  screenElement('save').disabled = !editable || !!screenData?.error || !!screenRemoteBlock();
  const lockedUntil = profile?.lockedUntilUtc ? new Date(profile.lockedUntilUtc) : null;
  const isLocked = !!lockedUntil && lockedUntil > new Date();
  const canLock = !!account?.eligible && !screenData?.error && !screenRemoteBlock();
  screenElement('lock').disabled = !canLock;
  screenElement('unlock').disabled = !canLock;
  const onBreak = !!profile?.breakUntilUtc && new Date(profile.breakUntilUtc) > new Date();
  screenElement('unlock').classList.toggle('hidden', !isLocked && !onBreak);
  const protection = screenData?.limitsProtection;
  const phase = protection?.phase || 'off';
  const canProtect = !!screenData && !screenData.error && !screenRemoteBlock();
  screenElement('limits-protect').classList.toggle('hidden', phase !== 'off');
  screenElement('limits-edit').classList.toggle('hidden', phase !== 'enforce');
  screenElement('limits-remove').classList.toggle('hidden', phase !== 'enforce');
  screenElement('limits-finish').classList.toggle('hidden', phase !== 'editing');
  ['protect', 'edit', 'remove', 'finish'].forEach(name => { screenElement('limits-' + name).disabled = !canProtect; });
  screenElement('limits-status').textContent = phase === 'enforce' ? t('Protected.')
    : phase === 'editing' ? t('Editing is open until {time}.', { time: new Date(protection.editOpenUntilUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
    : phase === 'sealnow' ? t('Saving the new limits...') : t('Not protected.');
  const breakUntil = profile?.breakUntilUtc ? new Date(profile.breakUntilUtc) : null;
  screenElement('lock-status').textContent = isLocked
    ? t('Locked until {time}.', { time: lockedUntil.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
    : breakUntil && breakUntil > new Date()
      ? t('On a break until {time}.', { time: breakUntil.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
      : t('Not locked.');
  screenElement('enabled').disabled = !!account && !account.eligible;
  if (populate) {
    screenElement('minutes').value = profile?.dailyMinutes ?? 180;
    screenElement('weekend').value = profile?.weekendMinutes ?? 0;
    screenElement('bedtime').value = profile?.bedtimeStart || '';
    screenElement('wake').value = profile?.bedtimeEnd || '';
    screenElement('weekend-bedtime').value = profile?.weekendBedtimeStart || '';
    screenElement('bonus').value = profile?.bonusMinutes ?? 0;
    screenElement('break-after').value = profile?.breakAfterMinutes ?? 0;
    screenElement('break-length').value = profile?.breakMinutes || 10;
    screenElement('enabled').checked = profile?.enabled ?? false;
    if (account && !account.eligible) screenElement('enabled').checked = false;
    screenDirty = false;
  }
}

// A child's "ask for more time" requests. Approving fills in the extra minutes; the parent still
// presses Save and authorizes it (UAC on the PC, PIN from another device) like any other change.
function screenRenderRequests() {
  const box = screenElement('requests');
  const requests = screenData?.requests || [];
  box.replaceChildren();
  box.classList.toggle('hidden', !requests.length);
  requests.forEach(request => {
    const row = document.createElement('div');
    row.className = 'screen-request';
    const label = document.createElement('span');
    label.textContent = t('{name} asked for more PC time.', { name: request.name }) + ' ';
    row.appendChild(label);
    [15, 30, 60].forEach(minutes => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'btn-secondary';
      button.textContent = t('Add {n} min', { n: minutes });
      button.title = t('Fills in {n} extra minutes for today. Press Save settings to apply them.', { n: minutes });
      button.addEventListener('click', () => {
        const select = screenElement('account');
        select.value = request.userSid;
        screenRenderSelection(true);
        const current = Number(screenElement('bonus').value) || 0;
        screenElement('bonus').value = current + minutes;
        screenElement('enabled').checked = true;
        screenDirty = true;
        screenElement('save').focus();
      });
      row.appendChild(button);
    });
    const decline = document.createElement('button');
    decline.type = 'button';
    decline.className = 'btn-secondary';
    decline.textContent = t('Not now');
    decline.title = t('Decline this request. The child is told that no time was added.');
    decline.addEventListener('click', async () => {
      decline.disabled = true;
      try {
        await screenApi('/api/screen-time/dismiss-request', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ userSid: request.userSid })
        });
        await loadScreenTime();
      } catch (error) {
        screenElement('message').textContent = error.message;
        screenElement('message').classList.add('screen-error');
        decline.disabled = false;
      }
    });
    row.appendChild(decline);
    box.appendChild(row);
  });
}

// Opened from "Add 15 min" in a phone alert: fill in the extra time and ask for approval straight away.
function screenApplyPendingGrant() {
  const grant = screenPendingGrant;
  if (!grant || !screenData) return;
  screenPendingGrant = null;
  history.replaceState(null, '', location.pathname + '#screen-time');
  const request = (screenData.requests || []).find(r => r.id === grant.request);
  const account = request && screenData.accounts.find(a => a.sid === request.userSid && a.eligible);
  if (!account) {
    screenElement('message').textContent = t('This request was already answered.');
    return;
  }
  const minutes = Math.min(Math.max(grant.minutes, 1), 240);
  screenElement('account').value = account.sid;
  screenRenderSelection(true);
  const profile = screenSelectedProfile();
  screenPending = {
    userSid: account.sid,
    dailyMinutes: profile?.dailyMinutes ?? 180,
    weekendMinutes: profile?.weekendMinutes ?? 0,
    bedtimeStart: profile?.bedtimeStart || '',
    bedtimeEnd: profile?.bedtimeEnd || '',
    weekendBedtimeStart: profile?.weekendBedtimeStart || '',
    bonusMinutes: (profile?.bonusMinutes ?? 0) + minutes,
    breakAfterMinutes: profile?.breakAfterMinutes ?? 0,
    breakMinutes: profile?.breakMinutes ?? 0,
    enabled: true
  };
  screenOpenAuth(t('Add {n} minutes of PC time for {name} today.', { n: minutes, name: account.name }));
}

// "New programs need my OK": the list is kept by the service. Allowing a program and turning the check off
// need the same approval as other PC Time changes; keeping one blocked does not.
function screenProgramLabel(entry) {
  return entry.display && entry.display.toLowerCase() !== entry.name ? `${entry.display} (${entry.name})` : entry.name;
}

function screenAllowProgram(entry) {
  screenAskLimits('program-allow', 0, t('Allow {name} on this PC.', { name: screenProgramLabel(entry) }), { programName: entry.name });
}

function screenRenderPrograms() {
  const programs = screenData?.programs;
  const panel = screenElement('programs-panel');
  if (!programs) { panel.classList.add('hidden'); return; }
  panel.classList.remove('hidden');
  const blocked = !!screenData.error || !!screenRemoteBlock();
  const waiting = programs.list.filter(e => e.state === 'waiting').length;
  screenElement('programs-status').textContent = !programs.enabled
    ? t('Off: new programs open without asking.')
    : waiting ? t('On. {n} program(s) waiting for your answer.', { n: waiting })
    : t('On: a new program is closed until you allow it.');
  screenElement('programs-on').classList.toggle('hidden', programs.enabled);
  screenElement('programs-off').classList.toggle('hidden', !programs.enabled);
  screenElement('programs-off').disabled = blocked;
  const list = screenElement('programs-list');
  list.replaceChildren();
  programs.list.forEach(entry => {
    const row = document.createElement('div');
    row.className = 'screen-request';
    const label = document.createElement('span');
    const who = entry.account ? ' - ' + entry.account : '';
    const state = entry.state === 'waiting' ? t('waiting') : entry.state === 'blocked' ? t('blocked') : t('allowed');
    label.textContent = `${screenProgramLabel(entry)}${who}: ${state} `;
    row.appendChild(label);
    if (entry.state !== 'allowed') {
      const allow = document.createElement('button');
      allow.type = 'button';
      allow.className = 'btn-secondary';
      allow.textContent = t('Allow');
      allow.title = t('Let this program open on this PC. Needs your approval.');
      allow.disabled = blocked;
      allow.addEventListener('click', () => screenAllowProgram(entry));
      row.appendChild(allow);
    }
    if (entry.state !== 'blocked') {
      const keep = document.createElement('button');
      keep.type = 'button';
      keep.className = 'btn-secondary';
      keep.textContent = t('Keep blocked');
      keep.title = t('The program stays closed. The child is told.');
      keep.addEventListener('click', async () => {
        keep.disabled = true;
        try {
          await screenApi('/api/programs/block', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: entry.name })
          });
          await loadScreenTime(true);
        } catch (error) {
          screenElement('message').textContent = error.message;
          screenElement('message').classList.add('screen-error');
          keep.disabled = false;
        }
      });
      row.appendChild(keep);
    }
    list.appendChild(row);
  });
  // Opened from a new-program alert's "Allow" button: ask for approval straight away.
  const pending = screenPendingProgram;
  if (pending) {
    screenPendingProgram = null;
    history.replaceState(null, '', location.pathname + '#screen-time');
    const entry = programs.list.find(e => e.id === pending);
    if (!entry || entry.state === 'allowed') screenElement('message').textContent = t('This request was already answered.');
    else if (!blocked) screenAllowProgram(entry);
  }
}

screenElement('programs-on').addEventListener('click', async () => {
  if (!confirm(t('Programs already on this PC keep working. A program installed or copied later is closed until you allow it, and you get an alert. Turn it on?'))) return;
  screenElement('programs-on').disabled = true;
  try {
    await screenApi('/api/programs/on', { method: 'POST' });
    await loadScreenTime(true);
  } catch (error) {
    screenElement('message').textContent = error.message;
    screenElement('message').classList.add('screen-error');
  } finally { screenElement('programs-on').disabled = false; }
});
screenElement('programs-off').addEventListener('click', () =>
  screenAskLimits('programs-off', 0, t('New programs open without asking you first.')));

// The rules use the time zone the service pinned; a change in Windows waits for the parent.
function screenRenderTimeZone() {
  const zone = screenData?.timeZone;
  const panel = screenElement('timezone-panel');
  if (!zone) { panel.classList.add('hidden'); return; }
  const changed = zone.system && zone.system !== zone.pinned;
  panel.classList.remove('hidden');
  screenElement('timezone-status').textContent = changed
    ? t('Windows was switched to {now}. The rules still use {pinned}.', { now: zone.system, pinned: zone.pinned })
    : t('Rules use {zone}.', { zone: zone.pinned });
  panel.classList.toggle('screen-error', changed);
  const accept = screenElement('timezone-accept');
  accept.classList.toggle('hidden', !changed);
  accept.disabled = !!screenData.error || !!screenRemoteBlock();
}
screenElement('timezone-accept').addEventListener('click', () => {
  const zone = screenData?.timeZone;
  // The zone shown is sent along, so the approval cannot pin a zone the parent never saw.
  if (zone) screenAskLimits('timezone-accept', 0, t('Use the time zone {zone} for bedtime, schedules and daily limits from now on.', { zone: zone.system }),
    { timeZoneId: zone.system });
});

function screenRenderTable() {
  const body = screenElement('profiles');
  body.replaceChildren();
  (screenData?.profiles || []).forEach(profile => {
    const row = document.createElement('tr');
    [
      profile.name,
      t('{n} min', { n: profile.dailyMinutes }) + (profile.weekendMinutes ? ' / ' + t('{n} min', { n: profile.weekendMinutes }) : ''),
      screenDuration(profile.usedSeconds),
      profile.enabled ? screenDuration(profile.remainingSeconds) : '-',
      profile.enabled ? t('Active') : t('Disabled')
    ].forEach(value => {
      const cell = document.createElement('td');
      cell.textContent = value;
      row.appendChild(cell);
    });
    body.appendChild(row);
  });
  if (!body.children.length) {
    const row = document.createElement('tr'), cell = document.createElement('td');
    cell.colSpan = 5;
    cell.textContent = t('No accounts have a PC time rule. Select an account above to start.');
    row.appendChild(cell);
    body.appendChild(row);
  }
}

async function loadScreenTime(force = false) {
  if (screenLoading || screenBusy) return;
  screenLoading = true;
  screenElement('refresh').disabled = true;
  try {
    const data = await screenApi('/api/screen-time');
    const previousSid = screenElement('account').value;
    screenData = data;
    const select = screenElement('account');
    const eligibleAccounts = data.accounts.filter(account => account.eligible);
    select.replaceChildren();
    eligibleAccounts.forEach(account => {
      const option = document.createElement('option');
      option.value = account.sid;
      option.textContent = account.name;
      select.appendChild(option);
    });
    if (eligibleAccounts.some(a => a.sid === previousSid)) select.value = previousSid;
    else select.value = eligibleAccounts[0]?.sid || '';
    if (!eligibleAccounts.length) {
      const option = document.createElement('option');
      option.value = '';
      option.textContent = t('No enabled standard account found');
      select.appendChild(option);
    }
    const remoteBlock = screenRemoteBlock();
    screenElement('message').textContent = data.error || remoteBlock || t('Accounts refreshed. Choose who to control and save the limit.');
    screenElement('message').classList.toggle('screen-error', !!data.error || !!remoteBlock);
    screenRenderSelection(force || !screenDirty || select.value !== previousSid);
    screenRenderRequests();
    screenRenderTimeZone();
    screenRenderPrograms();
    screenApplyPendingGrant();
    screenLoadHomework();
    screenRenderTable();
  } catch (error) {
    screenElement('message').textContent = error.message;
    screenElement('message').classList.add('screen-error');
    screenElement('save').disabled = true;
  } finally {
    screenLoading = false;
    screenElement('refresh').disabled = false;
  }
}

screenElement('account').addEventListener('change', () => screenRenderSelection(true));
screenElement('form').addEventListener('input', () => { screenDirty = true; });
screenElement('refresh').addEventListener('click', () => loadScreenTime());

// "Lock now" re-sends the account's current rule unchanged, plus the lock; the service applies
// it with the same approval (UAC on the PC, PIN from another device) as any other change.
function screenAskLock(minutes) {
  const account = screenData?.accounts.find(a => a.sid === screenElement('account').value);
  if (!account?.eligible) return;
  const profile = screenSelectedProfile();
  screenPending = {
    userSid: account.sid,
    dailyMinutes: profile?.dailyMinutes ?? 180,
    weekendMinutes: profile?.weekendMinutes ?? 0,
    bedtimeStart: profile?.bedtimeStart || '',
    bedtimeEnd: profile?.bedtimeEnd || '',
    weekendBedtimeStart: profile?.weekendBedtimeStart || '',
    bonusMinutes: profile?.bonusMinutes ?? 0,
    breakAfterMinutes: profile?.breakAfterMinutes ?? 0,
    breakMinutes: profile?.breakMinutes ?? 0,
    enabled: profile?.enabled ?? false,
    lockMinutes: minutes
  };
  screenOpenAuth(minutes > 0
    ? t('Lock {name} for {n} minutes. Its programs keep running; it returns to the sign-in screen.', { name: account.name, n: minutes })
    : t('Unlock {name} now.', { name: account.name }));
}

// Homework mode: starting only restricts, so it needs no approval; ending it early does.
async function screenLoadHomework() {
  try {
    const hw = await screenApi('/api/homework');
    const until = hw.active ? new Date(hw.untilUtc) : null;
    screenElement('homework-status').textContent = hw.active
      ? t('Homework mode is on until {time}.', { time: until.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
      : t('Homework mode is off.');
    ['60', '120'].forEach(n => screenElement('homework-' + n).classList.toggle('hidden', hw.active));
    screenElement('homework-stop').classList.toggle('hidden', !hw.active);
    const hint = screenElement('homework-hint');
    const text = !hw.apps.length ? t('No apps are set to close yet. Add them on the Limits page.')
      : hw.allowedSites === 0 ? t('No websites are allowed (Always allow or a daily allowance), so every website will be blocked during homework mode.') : '';
    hint.textContent = text;
    hint.classList.toggle('hidden', !text || hw.active);
    const blocked = !!screenData?.error;
    ['60', '120', 'stop'].forEach(n => { screenElement('homework-' + n).disabled = blocked || (n === 'stop' && !!screenRemoteBlock()); });
  } catch { /* the page reports its own errors */ }
}

async function screenStartHomework(minutes) {
  if (!confirm(t('Start homework mode for {n} minutes? The listed apps are closed and only your allowed websites will open.', { n: minutes }))) return;
  try {
    await screenApi('/api/homework/start', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ minutes })
    });
    await screenLoadHomework();
  } catch (error) {
    screenElement('message').textContent = error.message;
    screenElement('message').classList.add('screen-error');
  }
}
screenElement('homework-60').addEventListener('click', () => screenStartHomework(60));
screenElement('homework-120').addEventListener('click', () => screenStartHomework(120));
screenElement('homework-stop').addEventListener('click', () => screenAskLimits('homework-stop', 0, t('End homework mode now.')));

function screenAskLimits(operation, minutes, summary, extra = {}) {
  screenPending = { userSid: '', dailyMinutes: 180, enabled: false, bonusMinutes: 0, operation, limitsMinutes: minutes, ...extra };
  screenOpenAuth(summary);
}

screenElement('limits-protect').addEventListener('click', () => screenAskLimits('limits-open', 1,
  t('Protect the current app limits, schedule and groups. They can then only be changed after you approve it.')));
screenElement('limits-edit').addEventListener('click', () => screenAskLimits('limits-open', 10,
  t('Allow changes to the app limits for 10 minutes.')));
screenElement('limits-remove').addEventListener('click', () => screenAskLimits('limits-clear', 0,
  t('Turn off the protection of the app limits.')));
screenElement('limits-finish').addEventListener('click', async () => {
  screenElement('limits-finish').disabled = true;
  try {
    await screenApi('/api/limits/finish-editing', { method: 'POST' });
    await loadScreenTime(true);
  } catch (error) {
    screenElement('message').textContent = error.message;
    screenElement('message').classList.add('screen-error');
  } finally { screenElement('limits-finish').disabled = false; }
});

// A notice on the Limits and Schedule pages, so changes that will be undone are not a surprise.
async function loadLimitsProtection() {
  try {
    const response = await fetch('/api/limits/protection');
    if (!response.ok) return;
    const data = await response.json();
    const text = data.phase === 'editing'
      ? t('Editing is open until {time}. Press Finish editing in PC Time when you are done.', { time: new Date(data.editOpenUntilUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
      : data.protectedLimits ? t('App limits are protected. Changes made here are undone unless you open editing first (PC Time > Protect limits).') : '';
    ['limits', 'schedule'].forEach(name => {
      const banner = document.getElementById(name + '-protect-banner');
      if (!banner) return;
      banner.textContent = text;
      banner.classList.toggle('hidden', !text);
    });
  } catch { /* the notice is optional */ }
}
document.querySelectorAll('[data-view="limits"], [data-view="schedule"]').forEach(a => a.addEventListener('click', loadLimitsProtection));
setInterval(() => {
  if (['view-limits', 'view-schedule'].some(id => document.getElementById(id)?.classList.contains('active'))) loadLimitsProtection();
}, 10000);

function screenOpenAuth(summary) {
  screenElement('auth-summary').textContent = summary;
  screenElement('auth-error').textContent = '';
  // Local: Windows asks for approval. Remote: ask for the PIN instead.
  screenElement('auth-uac').classList.toggle('hidden', !screenIsLocal);
  screenElement('auth-pin-row').classList.toggle('hidden', screenIsLocal);
  screenElement('auth-pin').value = '';
  screenElement('auth').showModal();
  screenElement('auth-submit').focus();
}

screenElement('lock').addEventListener('click', () => {
  const minutes = Math.floor(Number(screenElement('lock-minutes').value));
  if (!(minutes >= 1 && minutes <= 720)) {
    screenElement('message').textContent = t('Enter a lock time between 1 and 720 minutes.');
    screenElement('message').classList.add('screen-error');
    return;
  }
  screenAskLock(minutes);
});
screenElement('unlock').addEventListener('click', () => screenAskLock(-1));

screenElement('form').addEventListener('submit', event => {
  event.preventDefault();
  const account = screenData?.accounts.find(a => a.sid === screenElement('account').value);
  if (!account?.eligible) return;
  if (screenElement('bedtime').value && (!screenElement('wake').value || screenElement('wake').value === screenElement('bedtime').value)) {
    screenElement('message').textContent = t('Choose a wake-up time that differs from the bedtime.');
    screenElement('message').classList.add('screen-error');
    return;
  }
  const bed = screenElement('bedtime').value, wake = screenElement('wake').value, weekendBed = screenElement('weekend-bedtime').value;
  if (bed && weekendBed && (bed > wake) !== (weekendBed > wake)) {
    screenElement('message').textContent = t('The weekend bedtime must be an evening time after the wake-up time, like the normal bedtime (for example 23:00).');
    screenElement('message').classList.add('screen-error');
    return;
  }
  screenPending = {
    userSid: account.sid,
    dailyMinutes: Number(screenElement('minutes').value),
    weekendMinutes: Number(screenElement('weekend').value) || 0,
    bedtimeStart: screenElement('bedtime').value,
    bedtimeEnd: screenElement('bedtime').value ? screenElement('wake').value : '',
    weekendBedtimeStart: screenElement('bedtime').value ? screenElement('weekend-bedtime').value : '',
    bonusMinutes: Number(screenElement('bonus').value),
    breakAfterMinutes: Number(screenElement('break-after').value) || 0,
    breakMinutes: Number(screenElement('break-after').value) > 0 ? Number(screenElement('break-length').value) : 0,
    enabled: screenElement('enabled').checked
  };
  screenOpenAuth(screenPending.enabled
    ? t('Apply {n} minutes per day to {name}. Other accounts are preserved.', { n: screenPending.dailyMinutes, name: account.name })
    : t('Turn off control for {name}. Other accounts are preserved.', { name: account.name }));
});

screenElement('auth-cancel').addEventListener('click', () => { if (!screenBusy) screenElement('auth').close(); });
screenElement('auth').addEventListener('cancel', event => { if (screenBusy) event.preventDefault(); });
screenElement('auth').addEventListener('close', () => { screenPending = null; screenElement('auth-pin').value = ''; });

screenElement('auth-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (!screenPending || screenBusy) return;
  if (!screenIsLocal && !screenElement('auth-pin').value) {
    screenElement('auth-error').textContent = t('Enter the remote PIN.');
    return;
  }
  screenBusy = true;
  screenElement('auth-submit').disabled = true;
  screenElement('auth-cancel').disabled = true;
  screenElement('auth-error').textContent = '';
  try {
    await screenApi(screenIsLocal ? '/api/screen-time/policy' : '/api/screen-time/remote-policy', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(screenIsLocal ? { ...screenPending } : { ...screenPending, pin: screenElement('auth-pin').value })
    });
    screenElement('auth').close();
    screenDirty = false;
  } catch (error) {
    screenElement('auth-error').textContent = error.message;
  } finally {
    screenElement('auth-submit').disabled = false;
    screenElement('auth-cancel').disabled = false;
    screenBusy = false;
  }
  if (!screenElement('auth').open) {
    await loadScreenTime(true);
    if (!screenData?.error) screenElement('message').textContent = t('Settings saved for the selected account.');
  }
});

setInterval(() => {
  if (document.getElementById('view-screen-time').classList.contains('active') && !screenElement('auth').open) loadScreenTime();
}, 5000);
