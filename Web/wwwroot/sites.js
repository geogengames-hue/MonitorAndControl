// Website limits (Limits page): block a site or give it a daily allowance.
// The SYSTEM service turns the blocked sites into browser policy; this page only edits the rules.
const siteEl = id => document.getElementById('site-' + id);
// The rule loaded with Edit; if the site or account is changed before saving, the old rule is replaced.
let siteEditing = null;

function siteDeleteUrl(rule) {
  return rule.userSid
    ? '/api/user-site-limits/' + encodeURIComponent(rule.host) + '/' + encodeURIComponent(rule.userSid)
    : '/api/site-limits/' + encodeURIComponent(rule.host);
}

function siteMessage(text) {
  const box = siteEl('message');
  box.textContent = text || '';
  box.classList.toggle('hidden', !text);
}

function siteDuration(seconds) {
  const minutes = Math.floor(seconds / 60);
  return minutes >= 60 ? Math.floor(minutes / 60) + 'h ' + (minutes % 60) + 'm' : minutes + 'm';
}

async function loadSiteLimits() {
  const holder = document.getElementById('site-limits-table');
  if (!holder) return;
  try {
    // Rules for everyone and rules for one Windows account share one table.
    const [shared, own] = await Promise.all([api('/api/site-limits'), api('/api/user-site-limits'), loadAccountChoices()]);
    fillAccountSelect(siteEl('account'));
    const rules = [...shared.map(r => ({ ...r, userSid: '' })), ...own];
    holder.replaceChildren();
    if (!rules.length) {
      const p = document.createElement('p');
      p.style.cssText = 'color:#666;text-align:center;padding:20px';
      p.textContent = t('No website limits yet.');
      holder.appendChild(p);
      return;
    }
    const table = document.createElement('table');
    const head = table.createTHead().insertRow();
    ['Website', 'Account', 'Rule', 'Used today', 'Status', ''].forEach(label => {
      const th = document.createElement('th');
      th.textContent = label ? t(label) : '';
      head.appendChild(th);
    });
    const body = table.createTBody();
    rules.forEach(rule => {
      const row = body.insertRow();
      row.insertCell().textContent = rule.host;
      row.insertCell().textContent = accountLabel(rule.userSid);
      row.insertCell().textContent = rule.dailyMaxMinutes === 0 ? t('Always blocked') : rule.dailyMaxMinutes < 0 ? t('Always allowed') : t('{n} min/day', { n: rule.dailyMaxMinutes });
      row.insertCell().textContent = siteDuration(rule.usedSeconds);
      row.insertCell().textContent = !rule.enabled ? t('Off') : rule.blocked ? t('Blocked') : t('Allowed');
      const actions = row.insertCell();
      const edit = document.createElement('button');
      edit.className = 'btn-secondary';
      edit.textContent = t('Edit');
      edit.title = t('Load this rule into the form below.');
      edit.addEventListener('click', () => {
        fillAccountSelect(siteEl('account'), rule.userSid);
        siteEditing = { host: rule.host, userSid: rule.userSid };
        siteEl('host').value = rule.host;
        siteEl('mode').value = rule.dailyMaxMinutes === 0 ? 'block' : rule.dailyMaxMinutes < 0 ? 'allow' : 'limit';
        if (rule.dailyMaxMinutes > 0) siteEl('minutes').value = rule.dailyMaxMinutes;
        siteModeChanged();
        siteEl('host').focus();
      });
      const remove = document.createElement('button');
      remove.className = 'btn-danger';
      remove.textContent = t('Remove');
      remove.title = t('Delete this website rule. The site is unblocked within a minute.');
      remove.addEventListener('click', async () => {
        remove.disabled = true;
        try {
          await api(siteDeleteUrl(rule), { method: 'DELETE' });
          await loadSiteLimits();
        }
        catch (error) { siteMessage(error.message); remove.disabled = false; }
      });
      actions.append(edit, ' ', remove);
    });
    holder.appendChild(table);
  } catch (error) { /* shown by the page's other loaders */ }
}

siteEl('save').addEventListener('click', async () => {
  siteMessage('');
  const host = siteEl('host').value.trim();
  const mode = siteEl('mode').value;
  const minutes = mode === 'block' ? 0 : mode === 'allow' ? -1 : Number(siteEl('minutes').value);
  if (!host) { siteMessage(t('Enter a site name such as youtube.com.')); return; }
  if (mode === 'limit' && (!Number.isInteger(minutes) || minutes < 1 || minutes > 1440)) { siteMessage(t('Minutes per day must be between 1 and 1440.')); return; }
  siteEl('save').disabled = true;
  try {
    const userSid = siteEl('account').value;
    const saved = await api(userSid ? '/api/user-site-limits' : '/api/site-limits', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ host, dailyMaxMinutes: minutes, enabled: true, userSid })
    });
    // Saved under another site name or account than the rule being edited: remove the old one.
    const original = siteEditing;
    siteEditing = null;
    if (original && (original.host !== saved?.host || original.userSid !== userSid))
      await api(siteDeleteUrl(original), { method: 'DELETE' }).catch(() => {});
    siteEl('host').value = '';
    await loadSiteLimits();
  } catch (error) {
    let text = error.message;
    try { text = JSON.parse(text).error || text; } catch { /* plain text */ }
    siteMessage(t(text));
  } finally { siteEl('save').disabled = false; }
});

function siteModeChanged() { siteEl('minutes').classList.toggle('hidden', siteEl('mode').value !== 'limit'); }
siteEl('mode').addEventListener('change', siteModeChanged);
siteModeChanged();

async function loadSiteSettings() {
  try {
    const s = await api('/api/site-settings');
    siteEl('safe').checked = !!s.safeBrowsing;
    siteEl('allow-only').checked = !!s.allowOnly;
  } catch { /* shown by the page's other loaders */ }
}
async function saveSiteSettings() {
  siteMessage('');
  try {
    await api('/api/site-settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ safeBrowsing: siteEl('safe').checked, allowOnly: siteEl('allow-only').checked })
    });
  } catch (error) { siteMessage(error.message); await loadSiteSettings(); }
}
siteEl('safe').addEventListener('change', saveSiteSettings);
siteEl('allow-only').addEventListener('change', () => {
  if (siteEl('allow-only').checked && !confirm(t('Only the sites in the list below will open, and everything else is blocked. Add the sites the child needs first, as Always allowed. Turn it on?'))) {
    siteEl('allow-only').checked = false;
    return;
  }
  saveSiteSettings();
});

document.querySelectorAll('[data-view="limits"]').forEach(a => a.addEventListener('click', () => { loadSiteLimits(); loadSiteSettings(); }));
setInterval(() => {
  if (document.getElementById('view-limits')?.classList.contains('active')) loadSiteLimits();
}, 15000);

// ---- homework mode: the apps closed while it is on
const hwEl = id => document.getElementById('homework-' + id);

function hwMessage(text) {
  const box = hwEl('message');
  box.textContent = text || '';
  box.classList.toggle('hidden', !text);
}

async function saveHomeworkApps(apps) {
  await api('/api/homework/apps', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ apps })
  });
}

async function loadHomeworkApps() {
  const holder = hwEl('apps');
  if (!holder) return;
  try {
    const [hw, known] = await Promise.all([api('/api/homework'), api('/api/apps')]);
    const list = hwEl('app-list');
    list.replaceChildren();
    [...new Set(Object.values(known))].sort().forEach(name => { const o = document.createElement('option'); o.value = name; list.appendChild(o); });
    holder.replaceChildren();
    if (!hw.apps.length) {
      const p = document.createElement('p');
      p.style.cssText = 'color:#666;text-align:center;padding:20px';
      p.textContent = t('No apps are set to close during homework mode yet.');
      holder.appendChild(p);
      return;
    }
    hw.apps.forEach(name => {
      const row = document.createElement('div');
      row.className = 'other-pc';
      const label = document.createElement('span');
      label.textContent = name;
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'btn-danger';
      remove.textContent = t('Remove');
      remove.title = t('Stop closing this app during homework mode.');
      remove.addEventListener('click', async () => {
        remove.disabled = true;
        try {
          await api('/api/homework/apps/' + encodeURIComponent(name), { method: 'DELETE' });
          await loadHomeworkApps();
        }
        catch (error) { hwMessage(error.message); remove.disabled = false; }
      });
      row.append(label, remove);
      holder.appendChild(row);
    });
  } catch { /* shown by the page's other loaders */ }
}

hwEl('add')?.addEventListener('click', async () => {
  hwMessage('');
  const name = hwEl('app').value.trim();
  if (!name) { hwMessage(t('Enter the name of an app.')); return; }
  try {
    await api('/api/homework/apps/add', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ app: name })
    });
    hwEl('app').value = '';
    await loadHomeworkApps();
  } catch (error) {
    let text = error.message;
    try { text = JSON.parse(text).error || text; } catch { /* plain text */ }
    hwMessage(t(text));
  }
});
document.querySelectorAll('[data-view="limits"]').forEach(a => a.addEventListener('click', loadHomeworkApps));

// ---- app limits for one Windows account
const ulEl = id => document.getElementById('ul-' + id);
let ulAccounts = [];

function ulMessage(text) {
  const box = ulEl('message');
  box.textContent = text || '';
  box.classList.toggle('hidden', !text);
}

async function loadUserLimits(refreshAccounts = true) {
  const holder = document.getElementById('user-limits-table');
  if (!holder) return;
  try {
    // The account list comes from the service; ask for it when the page opens, and keep the last list if it is briefly unavailable.
    if (refreshAccounts) {
      try {
        const st = await api('/api/screen-time');
        ulAccounts = (st.accounts || []).filter(a => a.eligible);
      } catch { /* keep the previous list */ }
    }
    const select = ulEl('account');
    const chosen = select.value;
    select.replaceChildren();
    if (!ulAccounts.length) {
      const o = document.createElement('option');
      o.value = '';
      o.textContent = t('No standard Windows account is available (needs the GameHost service).');
      select.appendChild(o);
    }
    ulAccounts.forEach(a => { const o = document.createElement('option'); o.value = a.sid; o.textContent = a.name; select.appendChild(o); });
    if (ulAccounts.some(a => a.sid === chosen)) select.value = chosen;

    const [rules, known] = await Promise.all([api('/api/user-limits'), api('/api/apps')]);
    const list = ulEl('app-list');
    list.replaceChildren();
    [...new Set(Object.values(known))].sort().forEach(name => { const o = document.createElement('option'); o.value = name; list.appendChild(o); });

    holder.replaceChildren();
    if (!rules.length) {
      const p = document.createElement('p');
      p.style.cssText = 'color:#666;text-align:center;padding:20px';
      p.textContent = t('No account limits yet.');
      holder.appendChild(p);
      return;
    }
    const nameOf = sid => ulAccounts.find(a => a.sid === sid)?.name || t('Unknown account');
    const table = document.createElement('table');
    const head = table.createTHead().insertRow();
    ['Account', 'App', 'Limit', 'Used today', ''].forEach(label => {
      const th = document.createElement('th');
      th.textContent = label ? t(label) : '';
      head.appendChild(th);
    });
    const body = table.createTBody();
    rules.forEach(rule => {
      const row = body.insertRow();
      row.insertCell().textContent = nameOf(rule.userSid);
      row.insertCell().textContent = rule.appName;
      row.insertCell().textContent = t('{n} min/day', { n: rule.dailyMaxMinutes });
      row.insertCell().textContent = siteDuration(rule.usedSeconds);
      const actions = row.insertCell();
      const edit = document.createElement('button');
      edit.className = 'btn-secondary';
      edit.textContent = t('Edit');
      edit.title = t('Load this rule into the form below.');
      edit.addEventListener('click', () => {
        ulEl('account').value = rule.userSid;
        ulEl('app').value = rule.appName;
        ulEl('minutes').value = rule.dailyMaxMinutes;
        ulEl('app').focus();
      });
      const remove = document.createElement('button');
      remove.className = 'btn-danger';
      remove.textContent = t('Remove');
      remove.title = t('Delete this account limit.');
      remove.addEventListener('click', async () => {
        remove.disabled = true;
        try {
          await api('/api/user-limits/' + encodeURIComponent(rule.appName) + '/' + encodeURIComponent(rule.userSid), { method: 'DELETE' });
          await loadUserLimits();
        } catch (error) { ulMessage(error.message); remove.disabled = false; }
      });
      actions.append(edit, ' ', remove);
    });
    holder.appendChild(table);
  } catch { /* shown by the page's other loaders */ }
}

ulEl('save')?.addEventListener('click', async () => {
  ulMessage('');
  const userSid = ulEl('account').value;
  const appName = ulEl('app').value.trim();
  const minutes = Number(ulEl('minutes').value);
  if (!userSid) { ulMessage(t('Choose a Windows account.')); return; }
  if (!appName) { ulMessage(t('Enter the name of an app.')); return; }
  if (!Number.isInteger(minutes) || minutes < 1 || minutes > 1440) { ulMessage(t('Minutes per day must be between 1 and 1440.')); return; }
  ulEl('save').disabled = true;
  try {
    await api('/api/user-limits', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ appName, userSid, dailyMaxMinutes: minutes, enabled: true })
    });
    ulEl('app').value = '';
    await loadUserLimits();
  } catch (error) {
    let text = error.message;
    try { text = JSON.parse(text).error || text; } catch { /* plain text */ }
    ulMessage(t(text));
  } finally { ulEl('save').disabled = false; }
});
document.querySelectorAll('[data-view="limits"]').forEach(a => a.addEventListener('click', () => loadUserLimits(true)));
setInterval(() => {
  if (document.getElementById('view-limits')?.classList.contains('active')) loadUserLimits(false);
}, 15000);
