// State
let todayChart = null;
let historyChart = null;
let editingLimit = null;
let currentDays = 7;
let historyRaw = [];
let historyFiltered = [];
let historyRange = { mode: 'days', days: 7, from: '', to: '' };
let alertEventSource = null;
let alertReconnectTimer = null;

// Discover
document.getElementById('discover-scan').addEventListener('click', loadDiscover);

async function loadDiscover() {
  const el = document.getElementById('discover-results');
  el.innerHTML = '<p style="color:#888;text-align:center;padding:20px">Scanning...</p>';
  try {
    const [apps, processes] = await Promise.all([
      api('/api/discover'),
      api('/api/processes')
    ]);

    if (apps.length === 0 && processes.length === 0) {
      el.innerHTML = '<p style="color:#888;text-align:center;padding:20px">No new apps found. Everything is already tracked.</p>';
      return;
    }

    let html = '<h3 style="margin:10px 0">Installed Games & Apps</h3>';
    html += '<table><thead><tr><th>App</th><th>Process</th><th>Source</th><th>Actions</th></tr></thead><tbody>';
    apps.forEach(a => {
      const displayName = a.displayName || a.processName.replace('.exe','');
      html += `<tr>
        <td><strong>${esc(displayName)}</strong></td>
        <td style="font-size:12px;color:#888">${esc(a.processName)}</td>
        <td style="font-size:12px">${esc(a.source)}</td>
        <td class="actions">
          <button onclick="addDiscoveredApp('${escAttr(a.processName)}','${escAttr(displayName)}')">Add & Set Limit</button>
        </td>
      </tr>`;
    });
    html += '</tbody></table>';

    if (processes.length > 0) {
      html += '<h3 style="margin:15px 0 10px">Currently Running (untracked)</h3>';
      html += '<table><thead><tr><th>Process</th><th>Window Title</th><th>Actions</th></tr></thead><tbody>';
      processes.forEach(p => {
        // Use process name (not window title) as the app name - avoids storing
        // junk like "Loading..." or "War Thunder (DirectX 12, 64bit)" as the app name
        const appName = p.name.replace(/\.exe$/i, '');
        html += `<tr>
          <td style="font-size:12px;color:#888">${esc(p.name)}</td>
          <td>${esc(p.title)}</td>
          <td class="actions">
            <button onclick="addDiscoveredApp('${escAttr(p.name)}','${escAttr(appName)}')">Track</button>
          </td>
        </tr>`;
      });
      html += '</tbody></table>';
    }

    el.innerHTML = html;
  } catch (e) {
    log(e);
    el.innerHTML = '<p style="color:#ff4444;text-align:center;padding:20px">Scan failed. Try again.</p>';
  }
}

async function addDiscoveredApp(procName, displayName) {
  try {
    // Register process -> name mapping so tracker recognizes it
    await api('/api/apps', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ processName: procName, appName: displayName })
    });
    // Add default limit for this app
    await api('/api/limits', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ appName: displayName, dailyMaxMinutes: 120, enabled: true })
    });
    alert(`"${displayName}" is now tracked with 120 min daily limit.`);
    loadDiscover();
    loadLimits();
  } catch (e) {
    log(e);
    alert('Failed to add app. Try again.');
  }
}

function esc(s) { return s.replace(/[&<>"']/g, function(m) { return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":"&#39;"}[m]; }); }
function escAttr(s) { return s.replace(/'/g, "\\'").replace(/"/g, "&quot;"); }
function log(e) { console.error(e); document.getElementById('error-bar').classList.remove('hidden'); document.getElementById('error-bar').textContent = e.message || e; setTimeout(() => document.getElementById('error-bar').classList.add('hidden'), 8000); }

// Navigation
document.querySelectorAll('.sidebar nav a').forEach(a => {
  a.addEventListener('click', e => {
    e.preventDefault();
    document.querySelectorAll('.sidebar nav a').forEach(x => x.classList.remove('active'));
    a.classList.add('active');
    document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
    document.getElementById('view-' + a.dataset.view).classList.add('active');
    if (a.dataset.view === 'today') loadToday();
    if (a.dataset.view === 'history') loadHistory(currentDays);
    if (a.dataset.view === 'limits') loadLimits();
    if (a.dataset.view === 'discover') loadDiscover();
    if (a.dataset.view === 'schedule') loadSchedule();
    if (a.dataset.view === 'logs') loadLogs();
    if (a.dataset.view === 'settings') loadSettings();
  });
});

let liveTimer = null;
let todayTimer = null;

// SSE alerts
function connectSSE() {
  if (alertEventSource) {
    alertEventSource.close();
    alertEventSource = null;
  }
  if (alertReconnectTimer) {
    clearTimeout(alertReconnectTimer);
    alertReconnectTimer = null;
  }
  const token = sessionStorage.getItem('dashboardAdminToken') || '';
  const streamUrl = token ? `/api/alerts/stream?token=${encodeURIComponent(token)}` : '/api/alerts/stream';
  const evtSource = new EventSource(streamUrl);
  alertEventSource = evtSource;
  evtSource.onmessage = e => {
    try { handleAlert(JSON.parse(e.data)); } catch (e) { log(e); }
  };
  evtSource.addEventListener('breach', e => {
    try { handleAlert(JSON.parse(e.data)); } catch (e) { log(e); }
  });
  evtSource.addEventListener('countdown', e => {
    try { handleAlert(JSON.parse(e.data)); } catch (e) { log(e); }
  });
  evtSource.addEventListener('killed', e => {
    try { handleAlert(JSON.parse(e.data)); } catch (e) { log(e); }
  });
  evtSource.addEventListener('schedule_kill', e => {
    try { handleAlert(JSON.parse(e.data)); } catch (e) { log(e); }
  });
  evtSource.onerror = () => {
    evtSource.close();
    if (alertEventSource === evtSource) alertEventSource = null;
    if (!alertReconnectTimer)
      alertReconnectTimer = setTimeout(connectSSE, 5000);
  };
}

function handleAlert(data) {
  const bar = document.getElementById('alert-bar');
  bar.classList.remove('hidden', 'countdown', 'success');
  if (data.type === 'breach') {
    bar.className = 'alert-bar countdown';
    bar.textContent = `${data.appName}: limit reached! Closing in ${data.value}s`;
  } else if (data.type === 'countdown') {
    bar.textContent = `${bar.textContent.split(':')[0]}: ${data.value}s remaining`;
  } else if (data.type === 'killed') {
    bar.className = 'alert-bar success';
    bar.textContent = `${data.appName} was closed.`;
    setTimeout(() => bar.classList.add('hidden'), 5000);
  } else if (data.type === 'schedule_kill') {
    bar.className = 'alert-bar countdown';
    bar.textContent = `${data.appName} closed by schedule rule.`;
    setTimeout(() => bar.classList.add('hidden'), 5000);
  }
}

async function api(url, opts) {
  opts = opts || {};
  opts.headers = opts.headers || {};
  const token = sessionStorage.getItem('dashboardAdminToken');
  if (token) opts.headers['X-Admin-Token'] = token;
  let r = await fetch(url, opts);
  if (r.status === 401) {
    const password = prompt('Admin password required');
    if (password) {
      const login = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ password })
      });
      if (login.ok) {
        const auth = await login.json();
        sessionStorage.setItem('dashboardAdminToken', auth.token);
        opts.headers['X-Admin-Token'] = auth.token;
      }
      r = await fetch(url, opts);
    }
  }
  if (!r.ok) throw new Error(await r.text());
  return r.headers.get('content-type')?.includes('json') ? r.json() : r;
}

async function ensureAuthenticatedOnOpen() {
  const status = await fetch('/api/auth/status');
  if (!status.ok) return false;
  const authStatus = await status.json();
  if (!authStatus.passwordSet) return true;
  if (sessionStorage.getItem('dashboardAdminToken')) return true;

  while (true) {
    const password = prompt('Admin password required');
    if (!password) return false;
    const login = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password })
    });
    if (login.ok) {
      const auth = await login.json();
      sessionStorage.setItem('dashboardAdminToken', auth.token);
      return true;
    }
    alert('Invalid admin password.');
  }
}

async function startApp() {
  const ok = await ensureAuthenticatedOnOpen();
  if (!ok) {
    document.getElementById('error-bar').classList.remove('hidden');
    document.getElementById('error-bar').textContent = 'Admin password required.';
    return;
  }
  if (!liveTimer) liveTimer = setInterval(loadLive, 2000);
  if (!todayTimer) todayTimer = setInterval(loadLiveToday, 5000);
  loadLive();
  loadLiveToday();
  connectSSE();
  loadToday();
}

// Live
async function loadLive() {
  try {
    const data = await api('/api/live');
    document.getElementById('live-app').textContent = data.currentApp || '(idle)';
    document.getElementById('live-proc').textContent = data.currentProcess || '';
    const status = document.getElementById('enforcement-status');
    if (data.enforcementPaused) {
      const until = data.pausedUntil ? new Date(data.pausedUntil).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
      status.textContent = until ? `Enforcement paused until ${until}` : 'Enforcement paused';
      status.classList.add('paused');
    } else {
      status.textContent = 'Enforcement active';
      status.classList.remove('paused');
    }
  } catch (e) { log(e); }
}

async function runQuickAction(action, payload, confirmText) {
  if (confirmText && !confirm(confirmText)) return;
  try {
    await api(`/api/actions/${action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: payload ? JSON.stringify(payload) : undefined
    });
    loadLive();
    loadLiveToday();
    loadToday();
  } catch (e) {
    log(e);
    alert('Action failed.');
  }
}

document.getElementById('action-pause-15').addEventListener('click', () =>
  runQuickAction('pause', { minutes: 15 }));
document.getElementById('action-pause-30').addEventListener('click', () =>
  runQuickAction('pause', { minutes: 30 }));
document.getElementById('action-resume').addEventListener('click', () =>
  runQuickAction('resume'));
document.getElementById('action-reset-today').addEventListener('click', () =>
  runQuickAction('reset-today', null, "Reset today's usage for all apps?"));
document.getElementById('action-block-all').addEventListener('click', () =>
  runQuickAction('block-all', null, 'Close all running tracked apps now?'));

document.getElementById('quick-extend-15').addEventListener('click', () => quickExtend(15));
document.getElementById('quick-extend-30').addEventListener('click', () => quickExtend(30));
document.getElementById('quick-extend-bedtime').addEventListener('click', quickExtendUntilBedtime);

async function quickExtend(minutes) {
  const appName = document.getElementById('quick-extend-app').value;
  if (!appName) {
    alert('Choose an app first.');
    return;
  }
  await grantBonus(appName, minutes);
}

async function quickExtendUntilBedtime() {
  const appName = document.getElementById('quick-extend-app').value;
  if (!appName) {
    alert('Choose an app first.');
    return;
  }
  try {
    await api('/api/bonus/until-bedtime', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ appName })
    });
    loadLimits();
    loadLiveToday();
    loadToday();
  } catch (e) {
    log(e);
    alert('Failed to extend until bedtime.');
  }
}

async function loadLiveToday() {
  try {
    const usage = await api('/api/usage/today');
    const limits = await api('/api/limits');
    const bonuses = await api('/api/bonus/today');
    const list = document.getElementById('live-today-list');
    list.innerHTML = '';
    if (usage.length === 0) {
      list.innerHTML = '<div class="live-app-item"><span class="name" style="color:#666">No activity yet today</span></div>';
      syncQuickExtendApps(limits, usage);
      return;
    }
    syncQuickExtendApps(limits, usage);
    usage.forEach(u => {
      const limit = limits.find(l => l.appName === u.appName);
      const bonus = bonuses.find(b => b.appName === u.appName)?.bonusMinutes || 0;
      const maxSecs = limit ? (limit.dailyMaxMinutes + bonus) * 60 : 0;
      const remaining = maxSecs > 0 ? maxSecs - u.totalSeconds : 0;
      const cls = remaining <= 0 && maxSecs > 0 ? 'exceeded' :
                  remaining < 300 && maxSecs > 0 ? 'warning' : '';
      const item = document.createElement('div');
      item.className = `live-app-item ${cls}`;
      item.innerHTML = `
        <div>
          <div class="name">${esc(u.appName)}</div>
          ${limit ? `<div class="remaining">${remaining > 0 ? Math.floor(remaining / 60) + 'm remaining' : 'Limit exceeded'}${bonus ? ` (+${bonus}m bonus)` : ''}</div>` : ''}
        </div>
        <div class="time">${esc(u.durationFormatted)}</div>`;
      list.appendChild(item);
    });
  } catch (e) { log(e); }
}

function syncQuickExtendApps(limits, usage) {
  const select = document.getElementById('quick-extend-app');
  if (!select) return;
  const current = select.value;
  const apps = [...new Set([
    ...limits.map(l => l.appName),
    ...usage.map(u => u.appName)
  ])].filter(Boolean).sort();
  select.innerHTML = '<option value="">Choose app</option>' +
    apps.map(app => `<option value="${escAttr(app)}">${esc(app)}</option>`).join('');
  if (apps.includes(current)) select.value = current;
}

// Today
async function loadToday() {
  try {
    const usage = await api('/api/usage/today');
    renderTodayChart(usage);
    renderTable('today-table', usage, ['appName', 'durationFormatted'], ['App', 'Time']);
  } catch (e) { log(e); }
}

function renderTodayChart(usage) {
  const ctx = document.getElementById('today-chart').getContext('2d');
  if (todayChart) todayChart.destroy();
  const labels = usage.map(u => u.appName);
  const data = usage.map(u => +(u.totalSeconds / 60).toFixed(1));
  const colors = ['#7c7cff', '#ff7c7c', '#7cff7c', '#ffcc7c', '#7cffcc', '#cc7cff', '#ff7ccc', '#7cccff'];
  todayChart = new Chart(ctx, {
    type: 'bar',
    data: {
      labels,
      datasets: [{
        label: 'Minutes',
        data,
        backgroundColor: labels.map((_, i) => colors[i % colors.length]),
        borderRadius: 6
      }]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      plugins: { legend: { display: false } },
      scales: {
        y: { beginAtZero: true, ticks: { color: '#888' }, grid: { color: '#2a2a3e' } },
        x: { ticks: { color: '#888' }, grid: { display: false } }
      }
    }
  });
}

// History
document.querySelectorAll('.history-range button').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('.history-range button').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    currentDays = parseInt(btn.dataset.days);
    historyRange = { mode: 'days', days: currentDays, from: '', to: '' };
    loadHistory(currentDays);
  });
});

document.getElementById('history-apply').addEventListener('click', () => {
  const from = document.getElementById('history-from').value;
  const to = document.getElementById('history-to').value;
  if (!from || !to) {
    alert('Pick both start and end dates.');
    return;
  }
  document.querySelectorAll('.history-range button').forEach(b => b.classList.remove('active'));
  historyRange = { mode: 'custom', days: currentDays, from, to };
  loadHistory(currentDays);
});

document.getElementById('history-app-filter').addEventListener('change', renderHistoryDashboard);
document.getElementById('history-day-filter').addEventListener('change', renderHistoryDashboard);
document.getElementById('history-chart-mode').addEventListener('change', renderHistoryDashboard);

document.getElementById('history-clear').addEventListener('click', async () => {
  if (!confirm('Clear all usage history? Limits, schedules, app mappings, and settings will stay.')) return;
  try {
    await api('/api/usage/history', { method: 'DELETE' });
    loadHistory(currentDays);
    loadLiveToday();
  } catch (e) {
    log(e);
    alert('Failed to clear usage history.');
  }
});

async function loadHistory(days) {
  try {
    const url = historyRange.mode === 'custom'
      ? `/api/usage/history?from=${encodeURIComponent(historyRange.from)}&to=${encodeURIComponent(historyRange.to)}`
      : `/api/usage/history?days=${days}`;
    historyRaw = (await api(url))
      .map(u => ({ ...u, date: formatDateOnly(u.date) }));
    syncHistoryFilters();
    renderHistoryDashboard();
  } catch (e) { log(e); }
}

function formatDateOnly(value) {
  if (!value) return '-';
  return String(value).split('T')[0];
}

function syncHistoryFilters() {
  const appSelect = document.getElementById('history-app-filter');
  const daySelect = document.getElementById('history-day-filter');
  const currentApp = appSelect.value;
  const currentDay = daySelect.value;
  const apps = [...new Set(historyRaw.map(u => u.appName))].sort();
  const days = [...new Set(historyRaw.map(u => u.date))].sort().reverse();

  appSelect.innerHTML = '<option value="">All apps</option>' +
    apps.map(app => `<option value="${escAttr(app)}">${esc(app)}</option>`).join('');
  daySelect.innerHTML = '<option value="">All days</option>' +
    days.map(day => `<option value="${escAttr(day)}">${esc(day)}</option>`).join('');

  if (apps.includes(currentApp)) appSelect.value = currentApp;
  if (days.includes(currentDay)) daySelect.value = currentDay;
}

function renderHistoryDashboard() {
  const app = document.getElementById('history-app-filter').value;
  const day = document.getElementById('history-day-filter').value;
  historyFiltered = historyRaw.filter(u =>
    (!app || u.appName === app) &&
    (!day || u.date === day));

  renderHistoryStats(historyFiltered);
  renderHistoryTopApps(historyFiltered);
  renderHistoryDayDetail(historyRaw, day);
  renderHistoryChart(historyFiltered);
  renderTable('history-table', historyFiltered, ['date', 'appName', 'durationFormatted'], ['Date', 'App', 'Time']);
}

function renderHistoryStats(usage) {
  const totalSeconds = usage.reduce((sum, u) => sum + u.totalSeconds, 0);
  const activeDays = new Set(usage.map(u => u.date)).size;
  const apps = new Set(usage.map(u => u.appName)).size;
  const byApp = groupSeconds(usage, 'appName');
  const top = Object.entries(byApp).sort((a, b) => b[1] - a[1])[0];
  const dailyAvg = activeDays ? Math.round(totalSeconds / activeDays) : 0;
  document.getElementById('history-stats').innerHTML = [
    statCard('Total Time', formatDuration(totalSeconds), `${usage.length} records`),
    statCard('Daily Avg', formatDuration(dailyAvg), `${activeDays || 0} active days`),
    statCard('Top App', top ? esc(top[0]) : '-', top ? formatDuration(top[1]) : 'No usage'),
    statCard('Apps', apps.toString(), 'in current filter')
  ].join('');
}

function statCard(label, value, sub) {
  return `<div class="stat-card"><div class="label">${esc(label)}</div><div class="value">${value}</div><div class="sub">${esc(sub)}</div></div>`;
}

function renderHistoryTopApps(usage) {
  const byApp = groupSeconds(usage, 'appName');
  const rows = Object.entries(byApp).sort((a, b) => b[1] - a[1]).slice(0, 8);
  const max = rows[0]?.[1] || 1;
  document.getElementById('history-top-apps').innerHTML = rows.length
    ? rows.map(([name, seconds]) => historyBarRow(name, seconds, max)).join('')
    : '<p>No usage for this filter.</p>';
}

function renderHistoryDayDetail(allUsage, selectedDay) {
  const day = selectedDay || [...new Set(allUsage.map(u => u.date))].sort().reverse()[0];
  const rows = allUsage
    .filter(u => u.date === day)
    .sort((a, b) => b.totalSeconds - a.totalSeconds);
  const max = rows[0]?.totalSeconds || 1;
  document.getElementById('history-day-detail').innerHTML = rows.length
    ? `<p style="margin-top:0;color:#888">${esc(day)}</p>` + rows.slice(0, 8).map(u => historyBarRow(u.appName, u.totalSeconds, max)).join('')
    : '<p>No day selected.</p>';
}

function historyBarRow(name, seconds, max) {
  const pct = Math.max(2, Math.round((seconds / max) * 100));
  return `<div class="history-row">
    <div class="name">${esc(name)}</div>
    <div class="time">${formatDuration(seconds)}</div>
    <div class="bar-track"><div class="bar-fill" style="width:${pct}%"></div></div>
  </div>`;
}

function groupSeconds(usage, key) {
  return usage.reduce((acc, item) => {
    acc[item[key]] = (acc[item[key]] || 0) + item.totalSeconds;
    return acc;
  }, {});
}

function formatDuration(totalSeconds) {
  const seconds = Math.max(0, Math.round(totalSeconds || 0));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

function renderHistoryChart(usage) {
  const ctx = document.getElementById('history-chart').getContext('2d');
  if (historyChart) historyChart.destroy();
  const mode = document.getElementById('history-chart-mode').value;
  const byDate = {};
  usage.forEach(u => {
    if (!byDate[u.date]) byDate[u.date] = {};
    byDate[u.date][u.appName] = (byDate[u.date][u.appName] || 0) + u.totalSeconds / 60;
  });
  const dates = Object.keys(byDate).sort();
  const apps = [...new Set(usage.map(u => u.appName))];
  const colors = ['#7c7cff', '#ff7c7c', '#7cff7c', '#ffcc7c', '#7cffcc', '#cc7cff', '#ff7ccc', '#7cccff'];
  const datasets = mode === 'total'
    ? [{
        label: 'Total minutes',
        data: dates.map(d => Object.values(byDate[d]).reduce((sum, v) => sum + v, 0)),
        backgroundColor: '#7c7cff',
        borderRadius: 4
      }]
    : apps.map((app, i) => ({
    label: app,
    data: dates.map(d => byDate[d][app] || 0),
    backgroundColor: colors[i % colors.length],
    borderRadius: 3
  }));
  historyChart = new Chart(ctx, {
    type: 'bar',
    data: { labels: dates, datasets },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      plugins: { legend: { labels: { color: '#888' } } },
      scales: {
        x: { stacked: mode !== 'total', ticks: { color: '#888' }, grid: { display: false } },
        y: { stacked: mode !== 'total', beginAtZero: true, ticks: { color: '#888' }, grid: { color: '#2a2a3e' }, title: { display: true, text: 'Minutes', color: '#888' } }
      }
    }
  });
}

// Limits
async function loadLimits() {
  try {
    const [limits, known, today, mappings, bonuses] = await Promise.all([
      api('/api/limits'),
      api('/api/apps'),
      api('/api/usage/today'),
      api('/api/mappings'),
      api('/api/bonus/today')
    ]);
    const allApps = [...new Set([...Object.keys(known).map(k => known[k]), ...limits.map(l => l.appName)])].sort();

    const table = document.getElementById('limits-table');
    if (!Array.isArray(mappings)) {
      table.innerHTML = '<p style="color:#ff4444;text-align:center">Failed to load app mappings</p>';
      return;
    }
    const reverseMap = {};
    mappings.forEach(m => reverseMap[m.appName] = m.processName);

    let html = '<table><thead><tr><th>App</th><th>Process</th><th>Daily Max</th><th>Today Used</th><th>Status</th><th>Actions</th></tr></thead><tbody>';
    for (const app of allApps) {
      const limit = limits.find(l => l.appName === app);
      const usage = today.find(u => u.appName === app);
      const bonus = bonuses.find(b => b.appName === app)?.bonusMinutes || 0;
      const used = usage ? usage.totalSeconds : 0;
      const max = limit ? (limit.dailyMaxMinutes + bonus) * 60 : 0;
      const pct = max > 0 ? Math.round((used / max) * 100) : 0;
      const exceeded = max > 0 && used >= max;
      const enabled = limit ? limit.enabled : false;
      const procName = reverseMap[app] || Object.keys(known).find(k => known[k] === app) || '';
      html += `<tr>
        <td><strong>${esc(app)}</strong></td>
        <td style="font-size:11px;color:#666;max-width:250px;word-break:break-all">${esc(procName) || '-'}</td>
        <td>${limit ? limit.dailyMaxMinutes + ' min' + (bonus ? ` <span style="color:#ffcc66">(+${bonus})</span>` : '') : '<span style="color:#666">No limit</span>'}</td>
        <td>${usage ? esc(usage.durationFormatted) : '0m'}</td>
        <td>${enabled ? (exceeded ? '<span style="color:#ff4444">Exceeded</span>' : (pct > 80 ? '<span style="color:#ff8800">' + pct + '%</span>' : '<span style="color:#44bb44">' + pct + '%</span>')) : '<span style="color:#666">Disabled</span>'}</td>
        <td class="actions">
          <button onclick="editLimit('${escAttr(app)}')">Edit</button>
          ${limit ? `<button class="btn-secondary" onclick="grantBonus('${escAttr(app)}',15)">+15m</button><button class="btn-secondary" onclick="grantBonus('${escAttr(app)}',30)">+30m</button>` : ''}
          ${limit ? `<button class="btn-danger" onclick="deleteLimit('${escAttr(app)}')">Remove</button>` : `<button onclick="addLimit('${escAttr(app)}')">Add</button>`}
          ${procName ? `<button class="btn-danger" onclick="forgetApp('${escAttr(app)}','${escAttr(procName)}')" title="Delete mapping &amp; all data" style="background:#882222">&times; Forget</button>` : ''}
        </td>
      </tr>`;
    }
    html += '</tbody></table>';
    table.innerHTML = html;
  } catch (e) { log(e); }
}

async function grantBonus(appName, minutes) {
  try {
    await api('/api/bonus', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ appName, minutes })
    });
    loadLimits();
    loadLiveToday();
    loadToday();
  } catch (e) {
    log(e);
    alert('Failed to grant bonus time.');
  }
}

function editLimit(appName) {
  document.getElementById('limit-app').value = appName;
  document.getElementById('limit-cancel').classList.remove('hidden');
  editingLimit = appName;
  api('/api/limits').then(limits => {
    const limit = limits.find(l => l.appName === appName);
    if (limit) document.getElementById('limit-minutes').value = limit.dailyMaxMinutes;
  });
  // Populate process name from the app mapping
  api('/api/mappings').then(mappings => {
    const m = mappings.find(x => x.appName === appName);
    document.getElementById('limit-process').value = m ? m.processName : '';
  }).catch(() => {});
}

function addLimit(appName) {
  document.getElementById('limit-app').value = appName;
  document.getElementById('limit-process').value = '';
  document.getElementById('limit-minutes').value = 120;
  document.getElementById('limit-cancel').classList.add('hidden');
  editingLimit = null;
}

document.getElementById('limit-save').addEventListener('click', async () => {
  const app = document.getElementById('limit-app').value.trim();
  const proc = document.getElementById('limit-process').value.trim();
  const minutes = parseInt(document.getElementById('limit-minutes').value);
  if (!app || !minutes) return;
  // If process name is provided, register mapping first
  if (proc) {
    await api('/api/apps', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ processName: proc, appName: app }) });
  }
  await api('/api/limits', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ appName: app, dailyMaxMinutes: minutes, enabled: true }) });
  loadLimits();
  document.getElementById('limit-app').value = '';
  document.getElementById('limit-process').value = '';
  document.getElementById('limit-minutes').value = 120;
  document.getElementById('limit-cancel').classList.add('hidden');
  editingLimit = null;
});

document.getElementById('limit-cancel').addEventListener('click', () => {
  document.getElementById('limit-app').value = '';
  document.getElementById('limit-minutes').value = 120;
  document.getElementById('limit-cancel').classList.add('hidden');
  editingLimit = null;
});

async function deleteLimit(appName) {
  if (!confirm(`Remove limit for ${appName}?`)) return;
  await api(`/api/limits/${encodeURIComponent(appName)}`, { method: 'DELETE' });
  loadLimits();
}

async function forgetApp(appName, procName) {
  if (!confirm(`Remove all mapping & data for "${appName}" (${procName})?`)) return;
  try {
    await api(`/api/apps/${encodeURIComponent(procName)}`, { method: 'DELETE' });
    // Also remove limit if one exists
    await api(`/api/limits/${encodeURIComponent(appName)}`, { method: 'DELETE' }).catch(() => {});
    loadLimits();
  } catch (e) {
    log(e);
    alert('Failed to forget app.');
  }
}

// Schedule
async function loadSchedule() {
  try {
    const [rules, known, limits] = await Promise.all([
      api('/api/schedule'),
      api('/api/apps'),
      api('/api/limits')
    ]);
    syncScheduleAppOptions(known, limits);
    const table = document.getElementById('schedule-table');
    let html = '<table><thead><tr><th>Applies To</th><th>Day</th><th>Start</th><th>End</th><th>Enabled</th><th>Actions</th></tr></thead><tbody>';
    rules.forEach(r => {
      html += `<tr>
        <td>${r.appName ? esc(r.appName) : 'All apps'}</td>
        <td>${r.dayOfWeek}</td>
        <td>${r.startTime}</td>
        <td>${r.endTime}</td>
        <td><input type="checkbox" ${r.enabled ? 'checked' : ''} onchange="toggleSchedule(${r.id}, this.checked)"></td>
        <td class="actions"><button class="btn-danger" onclick="deleteSchedule(${r.id})">Delete</button></td>
      </tr>`;
    });
    if (rules.length === 0) html += '<tr><td colspan="6" style="color:#666;text-align:center">No schedule rules configured</td></tr>';
    html += '</tbody></table>';
    table.innerHTML = html;
  } catch (e) { log(e); }
}

function syncScheduleAppOptions(known, limits) {
  const select = document.getElementById('schedule-app');
  const current = select.value;
  const apps = [...new Set([
    ...Object.keys(known).map(k => known[k]),
    ...limits.map(l => l.appName)
  ])].filter(Boolean).sort();

  select.innerHTML = '<option value="">All apps</option>' +
    apps.map(app => `<option value="${escAttr(app)}">${esc(app)}</option>`).join('');
  if (apps.includes(current)) select.value = current;
}

async function toggleSchedule(id, enabled) {
  const rules = await api('/api/schedule');
  const rule = rules.find(r => r.id === id);
  if (rule) {
    rule.enabled = enabled;
    await api('/api/schedule', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(rule) });
    loadSchedule();
  }
}

async function deleteSchedule(id) {
  if (!confirm('Delete this schedule rule?')) return;
  await api(`/api/schedule/${id}`, { method: 'DELETE' });
  loadSchedule();
}

document.getElementById('schedule-save').addEventListener('click', async () => {
  const appName = document.getElementById('schedule-app').value;
  const day = document.getElementById('schedule-day').value;
  const start = document.getElementById('schedule-start').value;
  const end = document.getElementById('schedule-end').value;
  if (!day || !start || !end) return;
  await api('/api/schedule', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ appName, dayOfWeek: day, startTime: start, endTime: end, enabled: true }) });
  loadSchedule();
});

// Logs
async function loadLogs() {
  try {
    const count = parseInt(document.getElementById('logs-count').value) || 200;
    let lines = await api(`/api/logs?count=${count}`);
    const filter = document.getElementById('logs-filter').value.trim().toLowerCase();
    if (filter) {
      lines = lines.filter(l => l.toLowerCase().includes(filter));
    }
    document.getElementById('logs-stats').textContent = `${lines.length} events`;
    const out = document.getElementById('logs-output');
    out.innerHTML = lines.map(l => {
      let cls = 'log-info';
      if (l.includes('[WARN]')) cls = 'log-warn';
      else if (l.includes('[ERROR]')) cls = 'log-error';
      return `<span class="${cls}">${esc(l)}</span>`;
    }).join('\n');
    out.scrollTop = out.scrollHeight;
  } catch (e) { log(e); }
}

document.getElementById('logs-refresh').addEventListener('click', loadLogs);
document.getElementById('logs-count').addEventListener('change', loadLogs);
document.getElementById('logs-filter').addEventListener('input', () => {
  clearTimeout(window._logFilterTimer);
  window._logFilterTimer = setTimeout(loadLogs, 300);
});
document.getElementById('logs-clear').addEventListener('click', async () => {
  if (!confirm('Clear event log history file?')) return;
  try {
    await api('/api/logs', { method: 'DELETE' });
    loadLogs();
  } catch (e) { log(e); }
});

// Settings
async function loadSettings() {
  try {
    const s = await api('/api/settings');
    document.getElementById('set-kill-delay').value = s.killDelay;
    document.getElementById('set-show-warning').value = s.showWarning.toString();
    document.getElementById('set-warning-msg').value = s.warningMessage || '';
    document.getElementById('set-webhook-url').value = s.webhookUrl || '';
    document.getElementById('set-email-addr').value = s.emailAddress || '';
    document.getElementById('set-email-allowed-sender').value = s.emailAllowedSender || s.emailAddress || '';
    document.getElementById('set-auto-start').checked = s.autoStart;
    document.getElementById('set-email-notify').checked = s.emailNotifyEnabled;
    document.getElementById('set-email-start-notify').checked = s.emailStartNotifyEnabled;
    document.getElementById('set-email-control').checked = s.emailControlEnabled;
    const port = window.location.port || '5000';
    const viaHostname = s.hostname ? `http://${esc(s.hostname)}:${port}` : esc(window.location.href);
    const viaIps = s.localIps ? s.localIps.map(ip => `http://${esc(ip)}:${port}`).join('<br>') : viaHostname;
    document.getElementById('access-url').innerHTML = s.remoteDashboardEnabled
      ? `<strong>Computer name:</strong> ${viaHostname}<br><strong>IP addresses:</strong><br>${viaIps}`
      : 'Remote dashboard is disabled. Use this dashboard on the child PC, or enable remote access in appsettings.json.';
    const tokenNote = document.getElementById('admin-token-note');
    tokenNote.textContent = s.adminPasswordSet
      ? 'Dashboard changes and shutdown require the admin password.'
      : 'Set an admin password to protect dashboard changes and shutdown.';
    loadHealth();
  } catch (e) { log(e); }
}

async function loadHealth() {
  try {
    const h = await api('/api/health');
    const r = h.runtime || {};
    const items = [
      ['Watchdog', `${r.watchdog?.installed ? 'Installed' : 'Not installed'} (${r.watchdog?.status || 'unknown'})`],
      ['Autostart', r.autoStart?.enabled ? 'Enabled' : 'Disabled'],
      ['Dashboard', `${r.dashboard?.bindAddress || 'unknown'}:${r.dashboard?.port || ''}${r.dashboard?.remoteEnabled ? ' remote' : ' local'}`],
      ['Email', r.email?.configured ? 'Configured' : 'Not configured'],
      ['Database', `${r.database?.exists ? 'Found' : 'Missing'} - ${r.database?.path || ''}`]
    ];
    document.getElementById('health-status').innerHTML = items.map(([label, value]) =>
      `<div class="health-item"><div class="label">${esc(label)}</div><div class="value">${esc(value)}</div></div>`
    ).join('');
  } catch (e) {
    log(e);
    document.getElementById('health-status').innerHTML =
      '<div class="health-item"><div class="label">Health</div><div class="value">Unable to load</div></div>';
  }
}

document.getElementById('settings-health-refresh').addEventListener('click', loadHealth);

document.getElementById('settings-admin-password').addEventListener('click', async () => {
  const el = document.getElementById('admin-password-result');
  const currentPassword = document.getElementById('set-admin-current-pw').value;
  const newPassword = document.getElementById('set-admin-pw').value;
  el.textContent = 'Saving...';
  try {
    const r = await api('/api/auth/password', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentPassword, newPassword })
    });
    if (r.token) sessionStorage.setItem('dashboardAdminToken', r.token);
    document.getElementById('set-admin-current-pw').value = '';
    document.getElementById('set-admin-pw').value = '';
    el.style.color = '#44bb44';
    el.textContent = 'Saved';
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || 'Failed';
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-logout').addEventListener('click', () => {
  sessionStorage.removeItem('dashboardAdminToken');
  window.location.reload();
});

document.getElementById('settings-rotate-token').addEventListener('click', async () => {
  if (!confirm('Rotate the dashboard session token? Other open dashboard sessions will need to log in again.')) return;
  const el = document.getElementById('session-result');
  el.textContent = 'Rotating...';
  try {
    const r = await api('/api/auth/token/rotate', { method: 'POST' });
    if (r.token) sessionStorage.setItem('dashboardAdminToken', r.token);
    el.style.color = '#44bb44';
    el.textContent = 'Token rotated';
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || 'Failed';
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-save').addEventListener('click', async () => {
  const killDelay = parseInt(document.getElementById('set-kill-delay').value);
  const showWarning = document.getElementById('set-show-warning').value === 'true';
  const warningMessage = document.getElementById('set-warning-msg').value.trim();
  const webhookUrl = document.getElementById('set-webhook-url').value.trim();
  const emailAddress = document.getElementById('set-email-addr').value.trim();
  const emailPassword = document.getElementById('set-email-pw').value;
  const emailAllowedSender = document.getElementById('set-email-allowed-sender').value.trim();
  const emailNotifyEnabled = document.getElementById('set-email-notify').checked;
  const emailStartNotifyEnabled = document.getElementById('set-email-start-notify').checked;
  const emailControlEnabled = document.getElementById('set-email-control').checked;
  const autoStart = document.getElementById('set-auto-start').checked;
  const payload = { killDelay, showWarning, warningMessage, webhookUrl, emailAddress, emailAllowedSender, autoStart, emailNotifyEnabled, emailStartNotifyEnabled, emailControlEnabled };
  if (emailPassword) payload.emailPassword = emailPassword;
  await api('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
  document.getElementById('set-email-pw').value = '';
  alert('Settings saved.');
});

document.getElementById('settings-webhook-test').addEventListener('click', async () => {
  const el = document.getElementById('webhook-test-result');
  el.textContent = 'Sending...';
  try {
    await api('/api/settings/webhook-test', { method: 'POST' });
    el.style.color = '#44bb44';
    el.textContent = 'Test sent. Check your webhook endpoint.';
  } catch {
    el.style.color = '#ff4444';
    el.textContent = 'Failed';
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-email-test').addEventListener('click', async () => {
  const el = document.getElementById('email-test-result');
  el.textContent = 'Sending...';
  try {
    const emailAddress = document.getElementById('set-email-addr').value.trim();
    const emailPassword = document.getElementById('set-email-pw').value;
    const r = await api('/api/settings/email-test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ emailAddress, emailPassword })
    });
    if (r.status === 'test_sent') {
      el.style.color = '#44bb44';
      el.textContent = 'Test sent. Check your inbox.';
    } else {
      el.style.color = '#ff4444';
      el.textContent = (r.error || 'Failed');
    }
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = (e.message || 'Failed');
  }
  setTimeout(() => el.textContent = '', 10000);
});

document.getElementById('settings-email-start-test').addEventListener('click', async () => {
  const el = document.getElementById('email-test-result');
  el.textContent = 'Sending app-start test...';
  try {
    const r = await api('/api/settings/email-start-test', { method: 'POST' });
    if (r.status === 'test_sent') {
      el.style.color = '#44bb44';
      el.textContent = 'App-start test sent.';
    } else {
      el.style.color = '#ff4444';
      el.textContent = r.error || 'Failed';
    }
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || 'Failed';
  }
  setTimeout(() => el.textContent = '', 10000);
});

document.getElementById('settings-email-start-reset').addEventListener('click', async () => {
  const el = document.getElementById('email-test-result');
  el.textContent = 'Resetting...';
  try {
    await api('/api/settings/email-start-reset', { method: 'POST' });
    el.style.color = '#44bb44';
    el.textContent = 'App-start email markers reset.';
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || 'Failed';
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-export-config').addEventListener('click', async () => {
  const el = document.getElementById('config-backup-result');
  el.textContent = 'Exporting...';
  try {
    const backup = await api('/api/config/export');
    const blob = new Blob([JSON.stringify(backup, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const stamp = new Date().toISOString().slice(0, 10);
    a.href = url;
    a.download = `monitor-config-${stamp}.json`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    el.style.color = '#44bb44';
    el.textContent = 'Exported';
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || 'Export failed';
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-import-config').addEventListener('click', async () => {
  const el = document.getElementById('config-backup-result');
  const file = document.getElementById('settings-import-file').files[0];
  if (!file) {
    el.style.color = '#ff4444';
    el.textContent = 'Choose a JSON backup first.';
    return;
  }
  if (!confirm('Import this config backup? Current limits, schedules, and app mappings will be replaced.')) return;

  el.textContent = 'Importing...';
  try {
    const text = await file.text();
    JSON.parse(text);
    await api('/api/config/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: text
    });
    el.style.color = '#44bb44';
    el.textContent = 'Imported';
    loadSettings();
    loadLimits();
    loadSchedule();
    loadLiveToday();
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || 'Import failed';
  }
  setTimeout(() => el.textContent = '', 10000);
});

function renderTable(id, data, props, headers) {
  const el = document.getElementById(id);
  let html = '<table><thead><tr>' + headers.map(h => `<th>${h}</th>`).join('') + '</tr></thead><tbody>';
  if (data.length === 0) {
    html += '<tr><td colspan="' + headers.length + '" style="color:#666;text-align:center">No data</td></tr>';
  } else {
    data.forEach(item => {
      html += '<tr>' + props.map(p => `<td>${esc(String(item[p] || '-'))}</td>`).join('') + '</tr>';
    });
  }
  html += '</tbody></table>';
  el.innerHTML = html;
}

// Init
startApp();
