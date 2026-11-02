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
      (profile.bedtimeStart ? ' · ' + t('bedtime {from}-{to}', { from: profile.bedtimeStart, to: profile.bedtimeEnd }) : '') + (profile.bonusMinutes ? ' ' + t('+ {n} min today', { n: profile.bonusMinutes }) : '')
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
  screenElement('unlock').classList.toggle('hidden', !isLocked);
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
  screenElement('lock-status').textContent = isLocked
    ? t('Locked until {time}.', { time: lockedUntil.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
    : t('Not locked.');
  screenElement('enabled').disabled = !!account && !account.eligible;
  if (populate) {
    screenElement('minutes').value = profile?.dailyMinutes ?? 180;
    screenElement('weekend').value = profile?.weekendMinutes ?? 0;
    screenElement('bedtime').value = profile?.bedtimeStart || '';
    screenElement('wake').value = profile?.bedtimeEnd || '';
    screenElement('weekend-bedtime').value = profile?.weekendBedtimeStart || '';
    screenElement('bonus').value = profile?.bonusMinutes ?? 0;
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
    enabled: profile?.enabled ?? false,
    lockMinutes: minutes
  };
  screenOpenAuth(minutes > 0
    ? t('Lock {name} for {n} minutes. Its programs keep running; it returns to the sign-in screen.', { name: account.name, n: minutes })
    : t('Unlock {name} now.', { name: account.name }));
}

function screenAskLimits(operation, minutes, summary) {
  screenPending = { userSid: '', dailyMinutes: 180, enabled: false, bonusMinutes: 0, operation, limitsMinutes: minutes };
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
