// Website limits (Limits page): block a site or give it a daily allowance.
// The SYSTEM service turns the blocked sites into browser policy; this page only edits the rules.
const siteEl = id => document.getElementById('site-' + id);

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
    const rules = await api('/api/site-limits');
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
    ['Website', 'Rule', 'Used today', 'Status', ''].forEach(label => {
      const th = document.createElement('th');
      th.textContent = label ? t(label) : '';
      head.appendChild(th);
    });
    const body = table.createTBody();
    rules.forEach(rule => {
      const row = body.insertRow();
      row.insertCell().textContent = rule.host;
      row.insertCell().textContent = rule.dailyMaxMinutes === 0 ? t('Always blocked') : t('{n} min/day', { n: rule.dailyMaxMinutes });
      row.insertCell().textContent = siteDuration(rule.usedSeconds);
      row.insertCell().textContent = !rule.enabled ? t('Off') : rule.blocked ? t('Blocked') : t('Allowed');
      const actions = row.insertCell();
      const edit = document.createElement('button');
      edit.className = 'btn-secondary';
      edit.textContent = t('Edit');
      edit.title = t('Load this rule into the form below.');
      edit.addEventListener('click', () => {
        siteEl('host').value = rule.host;
        siteEl('minutes').value = rule.dailyMaxMinutes;
        siteEl('host').focus();
      });
      const remove = document.createElement('button');
      remove.className = 'btn-danger';
      remove.textContent = t('Remove');
      remove.title = t('Delete this website rule. The site is unblocked within a minute.');
      remove.addEventListener('click', async () => {
        remove.disabled = true;
        try { await api('/api/site-limits/' + encodeURIComponent(rule.host), { method: 'DELETE' }); await loadSiteLimits(); }
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
  const minutes = Number(siteEl('minutes').value);
  if (!host) { siteMessage(t('Enter a site name such as youtube.com.')); return; }
  if (!Number.isInteger(minutes) || minutes < 0 || minutes > 1440) { siteMessage(t('Minutes per day must be between 0 (block) and 1440.')); return; }
  siteEl('save').disabled = true;
  try {
    await api('/api/site-limits', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ host, dailyMaxMinutes: minutes, enabled: true })
    });
    siteEl('host').value = '';
    await loadSiteLimits();
  } catch (error) {
    let text = error.message;
    try { text = JSON.parse(text).error || text; } catch { /* plain text */ }
    siteMessage(text);
  } finally { siteEl('save').disabled = false; }
});

document.querySelectorAll('[data-view="limits"]').forEach(a => a.addEventListener('click', loadSiteLimits));
setInterval(() => {
  if (document.getElementById('view-limits')?.classList.contains('active')) loadSiteLimits();
}, 15000);
