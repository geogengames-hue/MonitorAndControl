// Website limits (Limits page): block a site or give it a daily allowance.
// The SYSTEM service turns the blocked sites into browser policy; this page only edits the rules.
const siteEl = id => document.getElementById('site-' + id);
// The rule loaded with Edit; if the site or account is changed before saving, the old rule is replaced.
let siteEditing = null;
// Set when the page was opened from a website request alert's "Allow" button.
let sitePendingRequest = null;

// Fill in the form to allow a requested site for the child who asked (the parent still presses Save).
function siteFillFromRequest(request) {
  siteEditing = null;
  fillAccountSelect(siteEl('account'), request.userSid);
  siteEl('host').value = request.host;
  siteEl('mode').value = 'allow';
  siteModeChanged();
  siteMessage(t('Check the rule and press Save to allow {host}. You can also give it a daily limit instead.', { host: request.host }));
  siteEl('host').scrollIntoView({ block: 'center' });
  siteEl('save').focus();
}

async function loadSiteRequests() {
  const box = document.getElementById('site-requests');
  if (!box) return;
  try {
    const requests = await api('/api/site-requests');
    box.replaceChildren();
    box.classList.toggle('hidden', !requests.length);
    requests.forEach(request => {
      const row = document.createElement('div');
      row.className = 'screen-request';
      const label = document.createElement('span');
      label.textContent = t('{name} asked to open {host}.', { name: request.account || accountLabel(request.userSid), host: request.host }) + ' ';
      const allow = document.createElement('button');
      allow.type = 'button';
      allow.className = 'btn-secondary';
      allow.textContent = t('Allow');
      allow.title = t('Fills in the form below for this site and account. Press Save to allow it.');
      allow.addEventListener('click', () => siteFillFromRequest(request));
      const decline = document.createElement('button');
      decline.type = 'button';
      decline.className = 'btn-secondary';
      decline.textContent = t('Not now');
      decline.title = t('Decline this request. The child is told.');
      decline.addEventListener('click', async () => {
        decline.disabled = true;
        try { await api('/api/site-requests/' + encodeURIComponent(request.id) + '/decline', { method: 'POST' }); await loadSiteRequests(); }
        catch (error) { siteMessage(error.message); decline.disabled = false; }
      });
      row.append(label, allow, ' ', decline);
      box.appendChild(row);
    });
    const pending = sitePendingRequest;
    if (pending) {
      sitePendingRequest = null;
      history.replaceState(null, '', location.pathname + '#limits');
      const request = requests.find(r => r.id === pending);
      if (request) siteFillFromRequest(request);
      else siteMessage(t('This request was already answered.'));
    }
  } catch { /* shown by the page's other loaders */ }
}

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
    siteMessage(saved?.stillBlocked
      ? t('Saved, but {host} is still blocked for the child who asked (another rule blocks it, or its daily time is used up).', { host: saved.host })
      : '');
    await Promise.all([loadSiteLimits(), loadSiteRequests()]);
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

document.querySelectorAll('[data-view="limits"]').forEach(a => a.addEventListener('click', () => { loadSiteLimits(); loadSiteSettings(); loadSiteRequests(); }));
setInterval(() => {
  if (document.getElementById('view-limits')?.classList.contains('active')) { loadSiteLimits(); loadSiteRequests(); }
}, 15000);

// ---- app lists on the Limits page: apps closed during homework mode, and apps that are never closed
function appListEditor({ prefix, getUrl, addUrl, deleteUrl, empty, removeTitle }) {
  const el = id => document.getElementById(prefix + '-' + id);
  const message = text => {
    const box = el('message');
    box.textContent = text || '';
    box.classList.toggle('hidden', !text);
  };

  async function load() {
    const holder = el('apps');
    if (!holder) return;
    try {
      const [data, known] = await Promise.all([api(getUrl), api('/api/apps')]);
      const list = el('app-list');
      list.replaceChildren();
      [...new Set(Object.values(known))].sort().forEach(name => { const o = document.createElement('option'); o.value = name; list.appendChild(o); });
      holder.replaceChildren();
      if (!data.apps.length) {
        const p = document.createElement('p');
        p.style.cssText = 'color:#666;text-align:center;padding:20px';
        p.textContent = t(empty);
        holder.appendChild(p);
        return;
      }
      data.apps.forEach(name => {
        const row = document.createElement('div');
        row.className = 'other-pc';
        const label = document.createElement('span');
        label.textContent = name;
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn-danger';
        remove.textContent = t('Remove');
        remove.title = t(removeTitle);
        remove.addEventListener('click', async () => {
          remove.disabled = true;
          try {
            await api(deleteUrl + encodeURIComponent(name), { method: 'DELETE' });
            await load();
          }
          catch (error) { message(error.message); remove.disabled = false; }
        });
        row.append(label, remove);
        holder.appendChild(row);
      });
    } catch { /* shown by the page's other loaders */ }
  }

  el('add')?.addEventListener('click', async () => {
    message('');
    const name = el('app').value.trim();
    if (!name) { message(t('Enter the name of an app.')); return; }
    try {
      await api(addUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ app: name })
      });
      el('app').value = '';
      await load();
    } catch (error) {
      let text = error.message;
      try { text = JSON.parse(text).error || text; } catch { /* plain text */ }
      message(t(text));
    }
  });
  document.querySelectorAll('[data-view="limits"]').forEach(a => a.addEventListener('click', load));
  return load;
}

appListEditor({
  prefix: 'homework', getUrl: '/api/homework', addUrl: '/api/homework/apps/add', deleteUrl: '/api/homework/apps/',
  empty: 'No apps are set to close during homework mode yet.', removeTitle: 'Stop closing this app during homework mode.'
});
appListEditor({
  prefix: 'always', getUrl: '/api/always-allowed', addUrl: '/api/always-allowed/add', deleteUrl: '/api/always-allowed/',
  empty: 'No apps are always allowed yet.', removeTitle: 'This app can be closed again by the schedule, limits and homework mode.'
});

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
