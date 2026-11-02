// Phone notifications (ntfy / Telegram) and shortcuts to other PCs' dashboards.
const pushEl = id => document.getElementById('push-' + id);

function pushMessage(text, bad) {
  const box = pushEl('result');
  box.textContent = text || '';
  box.style.color = bad ? '#e07a6a' : '';
}

function pushProviderChanged() {
  const provider = pushEl('provider').value;
  pushEl('ntfy').classList.toggle('hidden', provider !== 'ntfy');
  pushEl('telegram').classList.toggle('hidden', provider !== 'telegram');
}

async function loadPushSettings() {
  if (!pushEl('provider')) return;
  try {
    const s = await api('/api/settings/push');
    pushEl('provider').value = s.provider || '';
    pushEl('ntfy-server').value = s.ntfyServer || 'https://ntfy.sh';
    pushEl('ntfy-topic').value = s.ntfyTopic || '';
    pushEl('telegram-chat').value = s.telegramChatId || '';
    pushEl('dashboard-url').value = s.dashboardUrl || '';
    pushEl('new-apps').checked = s.newAppAlerts !== false;
    pushEl('telegram-token').value = '';
    pushEl('telegram-token').placeholder = s.telegramTokenSet ? t('(saved - leave empty to keep it)') : '123456789:AA...';
    pushProviderChanged();
  } catch { /* the settings page reports its own errors */ }
}

function errorText(error) {
  try { return JSON.parse(error.message).error || error.message; } catch { return error.message; }
}

pushEl('provider')?.addEventListener('change', pushProviderChanged);
pushEl('save')?.addEventListener('click', async () => {
  pushMessage('');
  try {
    await api('/api/settings/push', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        provider: pushEl('provider').value,
        ntfyServer: pushEl('ntfy-server').value,
        ntfyTopic: pushEl('ntfy-topic').value,
        telegramToken: pushEl('telegram-token').value,
        telegramChatId: pushEl('telegram-chat').value,
        dashboardUrl: pushEl('dashboard-url').value,
        newAppAlerts: pushEl('new-apps').checked
      })
    });
    pushMessage(t('Saved.'));
    await loadPushSettings();
  } catch (error) { pushMessage(errorText(error), true); }
});
pushEl('test')?.addEventListener('click', async () => {
  pushMessage(t('Sending...'));
  try { await api('/api/settings/push-test', { method: 'POST' }); pushMessage(t('Test sent. Check your phone.')); }
  catch (error) { pushMessage(errorText(error), true); }
});

// ---- other PCs
async function loadOtherPcs() {
  const nav = document.getElementById('other-pcs');
  if (!nav) return;
  try {
    const pcs = await api('/api/other-pcs');
    nav.replaceChildren();
    nav.classList.toggle('hidden', !pcs.length);
    if (pcs.length) {
      const heading = document.createElement('small');
      heading.textContent = t('Other PCs');
      nav.appendChild(heading);
    }
    pcs.forEach(pc => {
      const row = document.createElement('div');
      row.className = 'other-pc';
      const link = document.createElement('a');
      link.href = pc.url;
      link.target = '_blank';
      link.rel = 'noopener';
      link.textContent = pc.name;
      link.title = t('Open this PC\'s own dashboard in a new tab.');
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'btn-secondary';
      remove.textContent = 'x';
      remove.title = t('Remove this shortcut.');
      remove.addEventListener('click', async () => {
        try { await api('/api/other-pcs/' + encodeURIComponent(pc.name), { method: 'DELETE' }); await loadOtherPcs(); } catch { /* ignored */ }
      });
      row.append(link, remove);
      nav.appendChild(row);
    });
  } catch { /* optional */ }
}
document.getElementById('otherpc-add')?.addEventListener('click', async () => {
  const result = document.getElementById('otherpc-result');
  result.textContent = '';
  try {
    await api('/api/other-pcs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: document.getElementById('otherpc-name').value, url: document.getElementById('otherpc-url').value })
    });
    document.getElementById('otherpc-name').value = '';
    document.getElementById('otherpc-url').value = '';
    await loadOtherPcs();
  } catch (error) { result.textContent = errorText(error); }
});

document.querySelectorAll('[data-view="settings"]').forEach(a => a.addEventListener('click', loadPushSettings));
loadOtherPcs();

// Alerts link straight to a page (for example .../index.html#screen-time): open that view on load.
window.addEventListener('load', () => {
  const view = (location.hash || '').slice(1);
  if (/^[a-z-]{1,20}$/.test(view)) document.querySelector(`.sidebar nav a[data-view="${view}"]`)?.click();
});

// ---- "show time left" switch for the child's taskbar icon
async function loadChildView() {
  const box = document.getElementById('child-show-time-left');
  if (!box) return;
  try { box.checked = !!(await api('/api/child-view')).showTimeLeft; } catch { /* shown by the page's other loaders */ }
}
document.getElementById('child-show-time-left')?.addEventListener('change', async event => {
  try {
    await api('/api/child-view', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ showTimeLeft: event.target.checked })
    });
  } catch { event.target.checked = !event.target.checked; }
});
document.querySelectorAll('[data-view="settings"]').forEach(a => a.addEventListener('click', loadChildView));
