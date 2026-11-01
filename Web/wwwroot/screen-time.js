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
    ? t('{n} min/day', { n: profile.dailyMinutes }) + (profile.bonusMinutes ? ' ' + t('+ {n} min today', { n: profile.bonusMinutes }) : '')
    : t('No rule applied');
  screenElement('state').textContent = profile?.enabled
    ? (profile.remainingSeconds <= 0 ? t('Time is up') : t('Active'))
    : t('Not controlled');
  const editable = !!account && (account.eligible || !!profile);
  screenElement('save').disabled = !editable || !!screenData?.error || !!screenRemoteBlock();
  screenElement('enabled').disabled = !!account && !account.eligible;
  if (populate) {
    screenElement('minutes').value = profile?.dailyMinutes ?? 180;
    screenElement('bonus').value = profile?.bonusMinutes ?? 0;
    screenElement('enabled').checked = profile?.enabled ?? false;
    if (account && !account.eligible) screenElement('enabled').checked = false;
    screenDirty = false;
  }
}

function screenRenderTable() {
  const body = screenElement('profiles');
  body.replaceChildren();
  (screenData?.profiles || []).forEach(profile => {
    const row = document.createElement('tr');
    [
      profile.name,
      t('{n} min', { n: profile.dailyMinutes }),
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

screenElement('form').addEventListener('submit', event => {
  event.preventDefault();
  const account = screenData?.accounts.find(a => a.sid === screenElement('account').value);
  if (!account?.eligible) return;
  screenPending = {
    userSid: account.sid,
    dailyMinutes: Number(screenElement('minutes').value),
    bonusMinutes: Number(screenElement('bonus').value),
    enabled: screenElement('enabled').checked
  };
  screenElement('auth-summary').textContent = screenPending.enabled
    ? t('Apply {n} minutes per day to {name}. Other accounts are preserved.', { n: screenPending.dailyMinutes, name: account.name })
    : t('Turn off control for {name}. Other accounts are preserved.', { name: account.name });
  screenElement('auth-error').textContent = '';
  // Local: Windows asks for approval. Remote: ask for the PIN instead.
  screenElement('auth-uac').classList.toggle('hidden', !screenIsLocal);
  screenElement('auth-pin-row').classList.toggle('hidden', screenIsLocal);
  screenElement('auth-pin').value = '';
  screenElement('auth').showModal();
  screenElement('auth-submit').focus();
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
