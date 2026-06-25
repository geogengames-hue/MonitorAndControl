// State
let todayChart = null;
let historyChart = null;
let editingLimit = null;
let editingGroup = null;
let currentLimitGroups = [];
let currentGroupApps = [];
let currentDays = 7;
let historyRaw = [];
let historyFiltered = [];
let groupHistoryRaw = [];
let historyRange = { mode: 'days', days: 7, from: '', to: '' };
let alertEventSource = null;
let alertReconnectTimer = null;
let uiLanguage = localStorage.getItem('uiLanguage') || 'en';

let translations = {};

async function loadTranslations(language) {
  uiLanguage = language || localStorage.getItem('uiLanguage') || 'en';
  if (uiLanguage === 'en') {
    translations = {};
    localStorage.setItem('uiLanguage', uiLanguage);
    return;
  }

  try {
    const response = await fetch(`/i18n/${encodeURIComponent(uiLanguage)}.json`, { cache: 'no-cache' });
    if (!response.ok) throw new Error(`Missing translation file: ${uiLanguage}`);
    translations = await response.json();
  } catch (e) {
    console.error(e);
    translations = {};
    uiLanguage = 'en';
  }
  localStorage.setItem('uiLanguage', uiLanguage);
}

function t(key, vars) {
  let value = translations[key] || key;
  if (vars) Object.keys(vars).forEach(k => value = value.replace(`{${k}}`, vars[k]));
  return value;
}

function translateHealthStatus(value) {
  const raw = value || 'unknown';
  return t(raw) === raw ? raw : t(raw);
}

function applyTranslations(root = document.body) {
  document.title = t('Monitor & Control');
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = [];
  while (walker.nextNode()) nodes.push(walker.currentNode);
  nodes.forEach(node => {
    node._i18nSource ??= node.nodeValue.trim();
    const source = node._i18nSource;
    if (!source) return;
    const translated = t(source);
    node.nodeValue = node.nodeValue.replace(node.nodeValue.trim(), translated);
  });
  root.querySelectorAll?.('[placeholder]').forEach(el => {
    const source = el.dataset.i18nPlaceholder || el.getAttribute('placeholder');
    el.dataset.i18nPlaceholder = source;
    if (source) el.setAttribute('placeholder', t(source));
  });
  root.querySelectorAll?.('[title]').forEach(el => {
    const source = el.dataset.i18nTitle || el.getAttribute('title');
    el.dataset.i18nTitle = source;
    if (source) el.setAttribute('title', t(source));
  });
}

// Discover
document.getElementById('discover-scan').addEventListener('click', loadDiscover);

async function loadDiscover() {
  const el = document.getElementById('discover-results');
  el.innerHTML = `<p style="color:#888;text-align:center;padding:20px">${t('Scanning...')}</p>`;
  try {
    const [apps, processes] = await Promise.all([
      api('/api/discover'),
      api('/api/processes')
    ]);

    if (apps.length === 0 && processes.length === 0) {
      el.innerHTML = `<p style="color:#888;text-align:center;padding:20px">${t('No new apps found. Everything is already tracked.')}</p>`;
      return;
    }

    let html = `<h3 style="margin:10px 0">${t('Installed Games & Apps')}</h3>`;
    html += `<table><thead><tr><th>${t('App')}</th><th>${t('Process')}</th><th>${t('Source')}</th><th>${t('Actions')}</th></tr></thead><tbody>`;
    apps.forEach(a => {
      const displayName = a.displayName || a.processName.replace('.exe','');
      html += `<tr>
        <td><strong>${esc(displayName)}</strong></td>
        <td style="font-size:12px;color:#888">${esc(a.processName)}</td>
        <td style="font-size:12px">${esc(a.source)}</td>
        <td class="actions">
          <button onclick="addDiscoveredApp('${escAttr(a.processName)}','${escAttr(displayName)}')">${t('Add & Set Limit')}</button>
        </td>
      </tr>`;
    });
    html += '</tbody></table>';

    if (processes.length > 0) {
      html += `<h3 style="margin:15px 0 10px">${t('Currently Running (untracked)')}</h3>`;
      html += `<table><thead><tr><th>${t('Process')}</th><th>${t('Window Title')}</th><th>${t('Actions')}</th></tr></thead><tbody>`;
      processes.forEach(p => {
        // Use process name (not window title) as the app name - avoids storing
        // junk like "Loading..." or "War Thunder (DirectX 12, 64bit)" as the app name
        const appName = p.name.replace(/\.exe$/i, '');
        html += `<tr>
          <td style="font-size:12px;color:#888">${esc(p.name)}</td>
          <td>${esc(p.title)}</td>
          <td class="actions">
            <button onclick="addDiscoveredApp('${escAttr(p.name)}','${escAttr(appName)}')">${t('Track')}</button>
          </td>
        </tr>`;
      });
      html += '</tbody></table>';
    }

    el.innerHTML = html;
  } catch (e) {
    log(e);
    el.innerHTML = `<p style="color:#ff4444;text-align:center;padding:20px">${t('Scan failed. Try again.')}</p>`;
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
    alert(t('"{app}" is now tracked with 120 min daily limit.', { app: displayName }));
    loadDiscover();
    loadLimits();
  } catch (e) {
    log(e);
    alert(t('Failed to add app. Try again.'));
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
  const evtSource = new EventSource('/api/alerts/stream');
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
    bar.textContent = t('{app}: limit reached! Closing in {seconds}s', { app: data.appName, seconds: data.value });
  } else if (data.type === 'countdown') {
    bar.textContent = t('{app}: {seconds}s remaining', { app: bar.textContent.split(':')[0], seconds: data.value });
  } else if (data.type === 'killed') {
    bar.className = 'alert-bar success';
    bar.textContent = t('{app} was closed.', { app: data.appName });
    setTimeout(() => bar.classList.add('hidden'), 5000);
  } else if (data.type === 'schedule_kill') {
    bar.className = 'alert-bar countdown';
    bar.textContent = t('{app} closed by schedule rule.', { app: data.appName });
    setTimeout(() => bar.classList.add('hidden'), 5000);
  }
}

async function api(url, opts) {
  opts = opts || {};
  opts.headers = opts.headers || {};
  let r = await fetch(url, opts);
  if (r.status === 401) {
    const password = prompt(t('Admin password required'));
    if (password) {
      const login = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ password })
      });
      if (login.ok) await login.json();
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
  return true;
}

async function startApp() {
  const ok = await ensureAuthenticatedOnOpen();
  if (!ok) {
    document.getElementById('error-bar').classList.remove('hidden');
    document.getElementById('error-bar').textContent = t('Admin password required');
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
      status.textContent = until ? t('Enforcement paused until {time}', { time: until }) : t('Enforcement paused');
      status.classList.add('paused');
    } else {
      status.textContent = t('Enforcement active');
      status.classList.remove('paused');
    }
    renderTrackingDiagnostics(data);
  } catch (e) { log(e); }
}

function renderTrackingDiagnostics(data) {
  const summary = document.getElementById('tracking-summary');
  const container = document.getElementById('tracking-diagnostics');
  const idleSeconds = Math.max(0, Math.round(data.idleSeconds || 0));
  const idle = idleSeconds < 60 ? `${idleSeconds}s` : formatDuration(idleSeconds);
  const summaryText = data.trackingState === 'locked'
    ? t('Usage tracking paused: Windows is locked')
    : data.trackingState === 'idle'
      ? t('Usage tracking paused: user is idle ({time})', { time: idle })
      : data.pauseWhenIdle
        ? t('Usage tracking active (idle {time})', { time: idle })
        : t('Usage tracking active (idle pausing disabled)');
  summary.textContent = summaryText;
  summary.classList.toggle('paused', !!data.trackingSuspended);

  const diagnostics = data.diagnostics || [];
  const visible = diagnostics.filter(item => item.isRunning || item.isForeground);
  if (visible.length === 0) {
    container.innerHTML = `<p>${t('No configured tracked processes are running.')}</p>`;
    return;
  }
  const stateLabels = {
    foreground: t('Counted: foreground'),
    background: t('Counted: background'),
    running_not_counted: t('Not counted: background disabled'),
    locked: t('Paused: Windows locked'),
    idle: t('Paused: user idle'),
    not_running: t('Not running')
  };
  container.innerHTML = `<table><thead><tr><th>${t('App')}</th><th>${t('Process')}</th><th>${t('Reason')}</th></tr></thead><tbody>` +
    visible.map(item => `<tr>
      <td>${esc(item.appName)}</td>
      <td>${esc(item.processName)}</td>
      <td><span class="tracking-state state-${escAttr(item.state)}">${esc(stateLabels[item.state] || item.state)}</span></td>
    </tr>`).join('') + '</tbody></table>';
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
    alert(t('Action failed.'));
  }
}

document.getElementById('action-pause-15').addEventListener('click', () =>
  runQuickAction('pause', { minutes: 15 }));
document.getElementById('action-pause-30').addEventListener('click', () =>
  runQuickAction('pause', { minutes: 30 }));
document.getElementById('action-resume').addEventListener('click', () =>
  runQuickAction('resume'));
document.getElementById('action-reset-today').addEventListener('click', () =>
  runQuickAction('reset-today', null, t("Reset today's usage for all apps?")));
document.getElementById('action-block-all').addEventListener('click', () =>
  runQuickAction('block-all', null, t('Close all running tracked apps now?')));

document.getElementById('quick-extend-15').addEventListener('click', () => quickExtend(15));
document.getElementById('quick-extend-30').addEventListener('click', () => quickExtend(30));
document.getElementById('quick-extend-bedtime').addEventListener('click', quickExtendUntilBedtime);

async function quickExtend(minutes) {
  const appName = document.getElementById('quick-extend-app').value;
  if (!appName) {
    alert(t('Choose an app first.'));
    return;
  }
  await grantBonus(appName, minutes);
}

async function quickExtendUntilBedtime() {
  const appName = document.getElementById('quick-extend-app').value;
  if (!appName) {
    alert(t('Choose an app first.'));
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
    alert(t('Failed to extend until bedtime.'));
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
      list.innerHTML = `<div class="live-app-item"><span class="name" style="color:#666">${t('No activity yet today')}</span></div>`;
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
          ${limit ? `<div class="remaining">${remaining > 0 ? Math.floor(remaining / 60) + ' ' + t('m remaining') : t('Limit exceeded')}${bonus ? ` (+${bonus}m ${t('bonus')})` : ''}</div>` : ''}
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
    const [usage, groupUsage] = await Promise.all([
      api('/api/usage/today'),
      api('/api/usage/groups/today')
    ]);
    renderTodayChart(usage);
    const breakdown = document.getElementById('today-breakdown').checked;
    renderUsageTable('today-table', usage, false, breakdown);
    updateBreakdownNote('today-breakdown-note', usage, breakdown);
    renderGroupUsageTable('today-groups-table', groupUsage, false);
  } catch (e) { log(e); }
}

document.getElementById('today-breakdown').addEventListener('change', loadToday);

function renderTodayChart(usage) {
  const ctx = document.getElementById('today-chart').getContext('2d');
  if (todayChart) todayChart.destroy();
  const labels = usage.map(u => u.appName);
  const breakdown = document.getElementById('today-breakdown').checked;
  const colors = ['#f0a45d', '#69a7a0', '#cf766d', '#8caf72', '#a887b8', '#d2bc68', '#c08a62', '#7893ad'];
  const hasLegacy = usage.some(u => u.unclassifiedSeconds > 0);
  const datasets = breakdown
    ? [
        {
          label: t('Foreground'),
          data: usage.map(u => +(u.foregroundSeconds / 60).toFixed(1)),
          backgroundColor: '#f0a45d',
          borderRadius: 4
        },
        {
          label: t('Background'),
          data: usage.map(u => +(u.backgroundSeconds / 60).toFixed(1)),
          backgroundColor: '#75a99b',
          borderRadius: 4
        },
        ...(hasLegacy ? [{
          label: t('Legacy / unclassified'),
          data: usage.map(u => +(u.unclassifiedSeconds / 60).toFixed(1)),
          backgroundColor: '#777',
          borderRadius: 4
        }] : [])
      ]
    : [{
        label: t('Minutes'),
        data: usage.map(u => +(u.totalSeconds / 60).toFixed(1)),
        backgroundColor: labels.map((_, i) => colors[i % colors.length]),
        borderRadius: 6
      }];
  todayChart = new Chart(ctx, {
    type: 'bar',
    data: {
      labels,
      datasets
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: { duration: 700, easing: 'easeOutQuart' },
      plugins: { legend: { display: breakdown, labels: { color: '#7f8b85' } } },
      scales: {
        y: { stacked: breakdown, beginAtZero: true, ticks: { color: '#7f8b85' }, grid: { color: '#2b3531' } },
        x: { stacked: breakdown, ticks: { color: '#7f8b85' }, grid: { display: false } }
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
    alert(t('Pick both start and end dates.'));
    return;
  }
  document.querySelectorAll('.history-range button').forEach(b => b.classList.remove('active'));
  historyRange = { mode: 'custom', days: currentDays, from, to };
  loadHistory(currentDays);
});

document.getElementById('history-app-filter').addEventListener('change', renderHistoryDashboard);
document.getElementById('history-day-filter').addEventListener('change', renderHistoryDashboard);
document.getElementById('history-chart-mode').addEventListener('change', renderHistoryDashboard);
document.getElementById('history-breakdown').addEventListener('change', renderHistoryDashboard);

document.getElementById('history-clear').addEventListener('click', async () => {
  if (!confirm(t('Clear all usage history? Limits, schedules, app mappings, and settings will stay.'))) return;
  try {
    await api('/api/usage/history', { method: 'DELETE' });
    loadHistory(currentDays);
    loadLiveToday();
  } catch (e) {
    log(e);
    alert(t('Failed to clear usage history.'));
  }
});

async function loadHistory(days) {
  try {
    const url = historyRange.mode === 'custom'
      ? `/api/usage/history?from=${encodeURIComponent(historyRange.from)}&to=${encodeURIComponent(historyRange.to)}`
      : `/api/usage/history?days=${days}`;
    const groupUrl = historyRange.mode === 'custom'
      ? `/api/usage/groups/history?from=${encodeURIComponent(historyRange.from)}&to=${encodeURIComponent(historyRange.to)}`
      : `/api/usage/groups/history?days=${days}`;
    const [appUsage, groupUsage] = await Promise.all([api(url), api(groupUrl)]);
    historyRaw = appUsage
      .map(u => ({ ...u, date: formatDateOnly(u.date) }));
    groupHistoryRaw = groupUsage.map(u => ({ ...u, date: formatDateOnly(u.date) }));
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
  const days = [...new Set([...historyRaw.map(u => u.date), ...groupHistoryRaw.map(u => u.date)])].sort().reverse();

  appSelect.innerHTML = `<option value="">${t('All apps')}</option>` +
    apps.map(app => `<option value="${escAttr(app)}">${esc(app)}</option>`).join('');
  daySelect.innerHTML = `<option value="">${t('All days')}</option>` +
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

  const breakdown = document.getElementById('history-breakdown').checked;
  document.getElementById('history-chart-mode').disabled = breakdown;

  renderHistoryStats(historyFiltered);
  renderHistoryTopApps(historyFiltered);
  renderHistoryDayDetail(historyRaw, day);
  renderHistoryChart(historyFiltered);
  renderUsageTable('history-table', historyFiltered, true, breakdown);
  updateBreakdownNote('history-breakdown-note', historyFiltered, breakdown);
  renderGroupUsageTable('history-groups-table', groupHistoryRaw.filter(item => !day || item.date === day), true);
}

function renderGroupUsageTable(id, usage, includeDate) {
  const props = includeDate ? ['date', 'groupName', 'durationFormatted'] : ['groupName', 'durationFormatted'];
  const headers = includeDate ? [t('Date'), t('Group'), t('Shared Total')] : [t('Group'), t('Shared Total')];
  renderTable(id, usage, props, headers);
}

function renderUsageTable(id, usage, includeDate, breakdown) {
  const props = includeDate ? ['date', 'appName'] : ['appName'];
  const headers = includeDate ? [t('Date'), t('App')] : [t('App')];
  if (breakdown) {
    props.push('foregroundDurationFormatted', 'backgroundDurationFormatted');
    headers.push(t('Foreground'), t('Background'));
    if (usage.some(u => u.unclassifiedSeconds > 0)) {
      props.push('unclassifiedDurationFormatted');
      headers.push(t('Legacy'));
    }
  }
  props.push('durationFormatted');
  headers.push(t('Total'));
  renderTable(id, usage, props, headers);
}

function updateBreakdownNote(id, usage, breakdown) {
  const note = document.getElementById(id);
  const hasLegacy = breakdown && usage.some(u => u.unclassifiedSeconds > 0);
  note.classList.toggle('hidden', !hasLegacy);
  note.textContent = hasLegacy
    ? t('Legacy time was recorded before foreground/background breakdown tracking and cannot be classified.')
    : '';
}

function renderHistoryStats(usage) {
  const totalSeconds = usage.reduce((sum, u) => sum + u.totalSeconds, 0);
  const activeDays = new Set(usage.map(u => u.date)).size;
  const apps = new Set(usage.map(u => u.appName)).size;
  const byApp = groupSeconds(usage, 'appName');
  const top = Object.entries(byApp).sort((a, b) => b[1] - a[1])[0];
  const dailyAvg = activeDays ? Math.round(totalSeconds / activeDays) : 0;
  document.getElementById('history-stats').innerHTML = [
    statCard(t('Total Time'), formatDuration(totalSeconds), `${usage.length} ${t('records')}`),
    statCard(t('Daily Avg'), formatDuration(dailyAvg), `${activeDays || 0} ${t('active days')}`),
    statCard(t('Top App'), top ? esc(top[0]) : '-', top ? formatDuration(top[1]) : t('No usage')),
    statCard(t('Apps'), apps.toString(), t('in current filter'))
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
    : `<p>${t('No usage for this filter.')}</p>`;
}

function renderHistoryDayDetail(allUsage, selectedDay) {
  const day = selectedDay || [...new Set(allUsage.map(u => u.date))].sort().reverse()[0];
  const rows = allUsage
    .filter(u => u.date === day)
    .sort((a, b) => b.totalSeconds - a.totalSeconds);
  const max = rows[0]?.totalSeconds || 1;
  document.getElementById('history-day-detail').innerHTML = rows.length
    ? `<p style="margin-top:0;color:#888">${esc(day)}</p>` + rows.slice(0, 8).map(u => historyBarRow(u.appName, u.totalSeconds, max)).join('')
    : `<p>${t('No day selected.')}</p>`;
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
  const breakdown = document.getElementById('history-breakdown').checked;
  const byDate = {};
  usage.forEach(u => {
    if (!byDate[u.date]) byDate[u.date] = {};
    byDate[u.date][u.appName] = (byDate[u.date][u.appName] || 0) + u.totalSeconds / 60;
  });
  const dates = Object.keys(byDate).sort();
  const apps = [...new Set(usage.map(u => u.appName))];
  const colors = ['#f0a45d', '#69a7a0', '#cf766d', '#8caf72', '#a887b8', '#d2bc68', '#c08a62', '#7893ad'];
  const hasLegacy = usage.some(u => u.unclassifiedSeconds > 0);
  const sourceByDate = {};
  usage.forEach(u => {
    if (!sourceByDate[u.date]) sourceByDate[u.date] = { foreground: 0, background: 0, legacy: 0 };
    sourceByDate[u.date].foreground += u.foregroundSeconds / 60;
    sourceByDate[u.date].background += u.backgroundSeconds / 60;
    sourceByDate[u.date].legacy += u.unclassifiedSeconds / 60;
  });
  const datasets = breakdown
    ? [
        { label: t('Foreground'), data: dates.map(d => sourceByDate[d]?.foreground || 0), backgroundColor: '#f0a45d', borderRadius: 3 },
        { label: t('Background'), data: dates.map(d => sourceByDate[d]?.background || 0), backgroundColor: '#75a99b', borderRadius: 3 },
        ...(hasLegacy ? [{ label: t('Legacy / unclassified'), data: dates.map(d => sourceByDate[d]?.legacy || 0), backgroundColor: '#777', borderRadius: 3 }] : [])
      ]
    : mode === 'total'
    ? [{
        label: 'Total minutes',
        data: dates.map(d => Object.values(byDate[d]).reduce((sum, v) => sum + v, 0)),
        backgroundColor: '#f0a45d',
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
      animation: { duration: 700, easing: 'easeOutQuart' },
      plugins: { legend: { labels: { color: '#7f8b85' } } },
      scales: {
        x: { stacked: breakdown || mode !== 'total', ticks: { color: '#7f8b85' }, grid: { display: false } },
        y: { stacked: breakdown || mode !== 'total', beginAtZero: true, ticks: { color: '#7f8b85' }, grid: { color: '#2b3531' }, title: { display: true, text: t('Minutes'), color: '#7f8b85' } }
      }
    }
  });
}

// Limits
async function loadLimits() {
  try {
    const [limits, known, today, mappings, bonuses, groups] = await Promise.all([
      api('/api/limits'),
      api('/api/apps'),
      api('/api/usage/today'),
      api('/api/mappings'),
      api('/api/bonus/today'),
      api('/api/limit-groups')
    ]);
    renderLimitGroups(groups, known, limits);
    const table = document.getElementById('limits-table');
    if (!Array.isArray(mappings)) {
      table.innerHTML = `<p style="color:#ff4444;text-align:center">${t('Failed to load app mappings')}</p>`;
      return;
    }
    const mappingByProcess = new Map(mappings.map(m => [m.processName.toLowerCase(), m]));
    const rows = Object.entries(known)
      .map(([processName, appName]) => ({
        processName,
        appName,
        policy: mappingByProcess.get(processName.toLowerCase())
      }));
    const appsWithProcesses = new Set(rows.map(row => row.appName.toLowerCase()));
    limits.forEach(limit => {
      if (!appsWithProcesses.has(limit.appName.toLowerCase()))
        rows.push({ processName: '', appName: limit.appName, policy: null });
    });
    rows.sort((a, b) => a.appName.localeCompare(b.appName) || a.processName.localeCompare(b.processName));

    let html = `<table><thead><tr><th>${t('App')}</th><th>${t('Process')}</th><th>${t('Group')}</th><th>${t('Background')}</th><th>${t('Filter overlays')}</th><th>${t('Daily Max')}</th><th>${t('Today Used')}</th><th>${t('Status')}</th><th>${t('Actions')}</th></tr></thead><tbody>`;
    for (const row of rows) {
      const app = row.appName;
      const procName = row.processName;
      const limit = limits.find(l => l.appName === app);
      const usage = today.find(u => u.appName === app);
      const bonus = bonuses.find(b => b.appName === app)?.bonusMinutes || 0;
      const used = usage ? usage.totalSeconds : 0;
      const max = limit ? (limit.dailyMaxMinutes + bonus) * 60 : 0;
      const pct = max > 0 ? Math.round((used / max) * 100) : 0;
      const exceeded = max > 0 && used >= max;
      const enabled = limit ? limit.enabled : false;
      const group = groups.find(g => g.appNames.some(name => name.toLowerCase() === app.toLowerCase()));
      html += `<tr data-tracking-row>
        <td><strong>${esc(app)}</strong></td>
        <td style="font-size:11px;color:#666;max-width:250px;word-break:break-all">${esc(procName) || '-'}</td>
        <td>${group ? `<span style="color:#f0a45d">${esc(group.name)}</span>` : '-'}</td>
        <td>${procName ? `<input class="tracking-policy" type="checkbox" data-kind="background" data-process="${escAttr(procName)}" data-app="${escAttr(app)}" style="width:auto" ${row.policy?.countInBackground ? 'checked' : ''}>` : '-'}</td>
        <td>${procName ? `<input class="tracking-policy" type="checkbox" data-kind="overlay" data-process="${escAttr(procName)}" data-app="${escAttr(app)}" style="width:auto" ${row.policy?.ignoreOverlayFocus ? 'checked' : ''}>` : '-'}</td>
        <td>${limit ? limit.dailyMaxMinutes + ' min' + (bonus ? ` <span style="color:#ffcc66">(+${bonus})</span>` : '') : `<span style="color:#666">${t('No limit')}</span>`}</td>
        <td>${usage ? esc(usage.durationFormatted) : '0m'}</td>
        <td>${enabled ? (exceeded ? `<span style="color:#ff4444">${t('Exceeded')}</span>` : (pct > 80 ? '<span style="color:#ff8800">' + pct + '%</span>' : '<span style="color:#44bb44">' + pct + '%</span>')) : `<span style="color:#666">${t('Disabled')}</span>`}</td>
        <td class="actions">
          ${limit ? `<button onclick="editLimit('${escAttr(app)}')">${t('Edit')}</button>` : ''}
          ${limit ? `<button class="btn-secondary" onclick="grantBonus('${escAttr(app)}',15)">+15m</button><button class="btn-secondary" onclick="grantBonus('${escAttr(app)}',30)">+30m</button>` : ''}
          ${limit ? `<button class="btn-danger" onclick="deleteLimit('${escAttr(app)}')">${t('Remove')}</button>` : `<button onclick="addLimit('${escAttr(app)}')">${t('Add')}</button>`}
          ${procName ? `<button class="btn-danger" onclick="forgetApp('${escAttr(app)}','${escAttr(procName)}')" title="${t('Delete mapping & all data')}" style="background:#882222">&times; ${t('Forget')}</button>` : ''}
        </td>
      </tr>`;
    }
    html += '</tbody></table>';
    table.innerHTML = html;
    table.querySelectorAll('.tracking-policy').forEach(checkbox =>
      checkbox.addEventListener('change', saveTrackingPolicy));
  } catch (e) { log(e); }
}

function renderLimitGroups(groups, known, limits) {
  currentLimitGroups = groups;
  currentGroupApps = [...new Set([
    ...Object.values(known),
    ...limits.map(limit => limit.appName)
  ])].sort((a, b) => a.localeCompare(b));

  const table = document.getElementById('limit-groups-table');
  if (!groups.length) {
    table.innerHTML = `<p style="color:#888;text-align:center;padding:12px">${t('No shared limit groups configured.')}</p>`;
  } else {
    let html = `<table><thead><tr><th>${t('Group')}</th><th>${t('Apps')}</th><th>${t('Daily Max')}</th><th>${t('Today Used')}</th><th>${t('Status')}</th><th>${t('Actions')}</th></tr></thead><tbody>`;
    groups.forEach(group => {
      const max = group.dailyMaxMinutes * 60;
      const pct = max ? Math.round(group.todaySeconds / max * 100) : 0;
      const exceeded = group.enabled && group.todaySeconds >= max;
      html += `<tr>
        <td><strong>${esc(group.name)}</strong></td>
        <td>${group.appNames.map(esc).join(', ')}</td>
        <td>${group.dailyMaxMinutes} ${t('min')}</td>
        <td>${formatDuration(group.todaySeconds)}</td>
        <td>${group.enabled ? (exceeded ? `<span style="color:#ff4444">${t('Exceeded')}</span>` : `<span style="color:${pct > 80 ? '#ff8800' : '#44bb44'}">${pct}%</span>`) : `<span style="color:#666">${t('Disabled')}</span>`}</td>
        <td class="actions"><button onclick="editLimitGroup(${group.id})">${t('Edit')}</button><button class="btn-danger" onclick="deleteLimitGroup(${group.id})">${t('Remove')}</button></td>
      </tr>`;
    });
    table.innerHTML = html + '</tbody></table>';
  }

  const selected = editingGroup?.appNames || [];
  document.getElementById('group-members').innerHTML = currentGroupApps.length
    ? currentGroupApps.map(app => `<label class="group-member"><input type="checkbox" value="${escAttr(app)}" ${selected.some(name => name.toLowerCase() === app.toLowerCase()) ? 'checked' : ''}> ${esc(app)}</label>`).join('')
    : `<p>${t('Add tracked apps before creating a group.')}</p>`;
}

function editLimitGroup(id) {
  editingGroup = currentLimitGroups.find(group => group.id === id) || null;
  if (!editingGroup) return;
  document.getElementById('group-name').value = editingGroup.name;
  document.getElementById('group-minutes').value = editingGroup.dailyMaxMinutes;
  document.getElementById('group-enabled').checked = editingGroup.enabled;
  document.getElementById('group-cancel').classList.remove('hidden');
  renderLimitGroups(currentLimitGroups, Object.fromEntries(currentGroupApps.map((app, i) => [i, app])), []);
}

function resetLimitGroupForm() {
  editingGroup = null;
  document.getElementById('group-name').value = '';
  document.getElementById('group-minutes').value = 180;
  document.getElementById('group-enabled').checked = true;
  document.getElementById('group-cancel').classList.add('hidden');
  renderLimitGroups(currentLimitGroups, Object.fromEntries(currentGroupApps.map((app, i) => [i, app])), []);
}

document.getElementById('group-save').addEventListener('click', async () => {
  const name = document.getElementById('group-name').value.trim();
  const dailyMaxMinutes = parseInt(document.getElementById('group-minutes').value);
  const appNames = [...document.querySelectorAll('#group-members input:checked')].map(input => input.value);
  if (!name || !dailyMaxMinutes || !appNames.length) {
    alert(t('Enter a group name, daily limit, and select at least one app.'));
    return;
  }
  try {
    await api('/api/limit-groups', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id: editingGroup?.id || 0, name, dailyMaxMinutes, enabled: document.getElementById('group-enabled').checked, appNames })
    });
    resetLimitGroupForm();
    loadLimits();
  } catch (e) { log(e); alert(e.message); }
});

document.getElementById('group-cancel').addEventListener('click', resetLimitGroupForm);

async function deleteLimitGroup(id) {
  const group = currentLimitGroups.find(item => item.id === id);
  if (!group || !confirm(t('Remove shared limit group {group}?', { group: group.name }))) return;
  await api(`/api/limit-groups/${id}`, { method: 'DELETE' });
  resetLimitGroupForm();
  loadLimits();
}

async function saveTrackingPolicy(event) {
  const checkbox = event.currentTarget;
  const row = checkbox.closest('[data-tracking-row]');
  const background = row.querySelector('[data-kind="background"]');
  const overlay = row.querySelector('[data-kind="overlay"]');
  const previous = !checkbox.checked;
  background.disabled = true;
  overlay.disabled = true;
  try {
    await api('/api/apps', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        processName: checkbox.dataset.process,
        appName: checkbox.dataset.app,
        countInBackground: background.checked,
        ignoreOverlayFocus: overlay.checked
      })
    });
  } catch (e) {
    checkbox.checked = previous;
    log(e);
    alert(t('Failed to update tracking settings.'));
  } finally {
    background.disabled = false;
    overlay.disabled = false;
  }
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
    alert(t('Failed to grant bonus time.'));
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
  Promise.all([api('/api/mappings'), api('/api/apps')]).then(([mappings, known]) => {
    const m = mappings.find(x => x.appName === appName);
    document.getElementById('limit-process').value = m ? m.processName : (Object.keys(known).find(k => known[k] === appName) || '');
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
    const existingMappings = await api('/api/mappings');
    const existing = existingMappings.find(m => m.processName.toLowerCase() === proc.toLowerCase());
    await api('/api/apps', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        processName: proc,
        appName: app,
        countInBackground: !!existing?.countInBackground,
        ignoreOverlayFocus: !!existing?.ignoreOverlayFocus
      })
    });
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
  if (!confirm(t('Remove limit for {app}?', { app: appName }))) return;
  await api(`/api/limits/${encodeURIComponent(appName)}`, { method: 'DELETE' });
  loadLimits();
}

async function forgetApp(appName, procName) {
  if (!confirm(t('Remove all mapping & data for "{app}" ({proc})?', { app: appName, proc: procName }))) return;
  try {
    await api(`/api/apps/${encodeURIComponent(procName)}`, { method: 'DELETE' });
    loadLimits();
  } catch (e) {
    log(e);
    alert(t('Failed to forget app.'));
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
    let html = `<table><thead><tr><th>${t('Applies To')}</th><th>${t('Day')}</th><th>${t('Start')}</th><th>${t('End')}</th><th>${t('Enabled')}</th><th>${t('Actions')}</th></tr></thead><tbody>`;
    rules.forEach(r => {
      html += `<tr>
        <td>${r.appName ? esc(r.appName) : t('All apps')}</td>
        <td>${r.dayOfWeek}</td>
        <td>${r.startTime}</td>
        <td>${r.endTime}</td>
        <td><input type="checkbox" ${r.enabled ? 'checked' : ''} onchange="toggleSchedule(${r.id}, this.checked)"></td>
        <td class="actions"><button class="btn-danger" onclick="deleteSchedule(${r.id})">${t('Delete')}</button></td>
      </tr>`;
    });
    if (rules.length === 0) html += `<tr><td colspan="6" style="color:#666;text-align:center">${t('No schedule rules configured')}</td></tr>`;
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

  select.innerHTML = `<option value="">${t('All apps')}</option>` +
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
  if (!confirm(t('Delete this schedule rule?'))) return;
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
    document.getElementById('logs-stats').textContent = `${lines.length} ${t('events')}`;
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
  if (!confirm(t('Clear event log history file?'))) return;
  try {
    await api('/api/logs', { method: 'DELETE' });
    loadLogs();
  } catch (e) { log(e); }
});

// Settings
const hotkeyKeyOptions = [
  ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split(''),
  ...'0123456789'.split(''),
  ...Array.from({ length: 24 }, (_, i) => `F${i + 1}`),
  'Insert', 'Delete', 'Home', 'End', 'PageUp', 'PageDown', 'Up', 'Down', 'Left', 'Right', 'Space'
];

function initHotkeyKeyOptions() {
  const select = document.getElementById('set-hotkey-key');
  if (!select || select.options.length > 0) return;
  select.innerHTML = hotkeyKeyOptions.map(k => `<option value="${escAttr(k)}">${esc(k)}</option>`).join('');
}

function setHotkeyControls(modifiers, key) {
  const parts = String(modifiers || 'Control+Alt').split('+').map(x => x.trim().toLowerCase());
  document.getElementById('hotkey-ctrl').checked = parts.includes('control') || parts.includes('ctrl');
  document.getElementById('hotkey-alt').checked = parts.includes('alt');
  document.getElementById('hotkey-shift').checked = parts.includes('shift');
  document.getElementById('hotkey-win').checked = parts.includes('win') || parts.includes('windows');
  const normalizedKey = key || 'H';
  const select = document.getElementById('set-hotkey-key');
  if ([...select.options].some(o => o.value.toLowerCase() === normalizedKey.toLowerCase())) {
    select.value = [...select.options].find(o => o.value.toLowerCase() === normalizedKey.toLowerCase()).value;
  }
}

function getHotkeyModifiers() {
  const parts = [];
  if (document.getElementById('hotkey-ctrl').checked) parts.push('Control');
  if (document.getElementById('hotkey-alt').checked) parts.push('Alt');
  if (document.getElementById('hotkey-shift').checked) parts.push('Shift');
  if (document.getElementById('hotkey-win').checked) parts.push('Win');
  return parts.join('+');
}

async function loadSettings() {
  try {
    const s = await api('/api/settings');
    await loadTranslations(s.uiLanguage || localStorage.getItem('uiLanguage') || 'en');
    document.getElementById('set-language').value = uiLanguage;
    applyTranslations();
    document.getElementById('set-kill-delay').value = s.killDelay;
    document.getElementById('set-show-warning').value = s.showWarning.toString();
    document.getElementById('set-warning-msg').value = s.warningMessage || '';
    document.getElementById('set-webhook-url').value = s.webhookUrl || '';
    document.getElementById('set-email-addr').value = s.emailAddress || '';
    document.getElementById('set-email-allowed-sender').value = s.emailAllowedSender || s.emailAddress || '';
    document.getElementById('set-email-device-id').value = s.emailDeviceId || s.hostname || '';
    document.getElementById('set-auto-start').checked = s.autoStart;
    document.getElementById('set-pause-idle').checked = s.pauseTrackingWhenIdle;
    document.getElementById('set-idle-threshold').value = s.idleThresholdMinutes || 10;
    document.getElementById('set-email-breach-notify').checked = s.emailBreachNotifyEnabled ?? s.emailNotifyEnabled;
    document.getElementById('set-email-kill-notify').checked = s.emailKillNotifyEnabled ?? s.emailNotifyEnabled;
    document.getElementById('set-email-start-notify').checked = s.emailStartNotifyEnabled;
    document.getElementById('set-email-control').checked = s.emailControlEnabled;
    document.getElementById('set-summary-enabled').checked = s.summaryEnabled;
    document.getElementById('set-summary-frequency').value = s.summaryFrequency || 'weekly';
    document.getElementById('set-summary-time').value = s.summaryTime || '18:00';
    document.getElementById('set-summary-weekly-day').value = String(s.summaryWeeklyDay ?? 0);
    document.getElementById('set-summary-monthly-day').value = s.summaryMonthlyDay || 1;
    document.getElementById('set-tamper-alerts').checked = s.tamperAlertsEnabled;
    initHotkeyKeyOptions();
    setHotkeyControls(s.hotKeyModifiers, s.hotKeyKey);
    document.getElementById('current-hotkey').textContent = s.hotKey || `${s.hotKeyModifiers || 'Control+Alt'}+${s.hotKeyKey || 'H'}`;
    const port = window.location.port || '5000';
    const viaHostname = s.hostname ? `http://${esc(s.hostname)}:${port}` : esc(window.location.href);
    const viaIps = s.localIps ? s.localIps.map(ip => `http://${esc(ip)}:${port}`).join('<br>') : viaHostname;
    document.getElementById('access-url').innerHTML = s.remoteDashboardEnabled
      ? `<strong>${t('Computer name:')}</strong> ${viaHostname}<br><strong>${t('IP addresses:')}</strong><br>${viaIps}`
      : t('Remote dashboard is disabled. Use this dashboard on the child PC, or enable remote access in appsettings.json.');
    const tokenNote = document.getElementById('admin-token-note');
    tokenNote.textContent = s.adminPasswordSet
      ? t('Dashboard changes and shutdown require the admin password.')
      : t('Set an admin password to protect dashboard changes and shutdown.');
    loadUpdateStatus();
    loadHealth();
  } catch (e) { log(e); }
}

async function loadUpdateStatus() {
  const el = document.getElementById('update-status');
  if (!el) return;

  try {
    const status = await api('/api/settings/update-status');
    const state = (status.status || 'none').toLowerCase();
    if (state === 'none') {
      el.className = 'update-status';
      el.innerHTML = `<div class="update-status-empty">${t('No update has been recorded yet.')}</div>`;
      return;
    }

    const titleMap = {
      success: t('Last update succeeded'),
      failed: t('Last update failed'),
      running: t('Update is running'),
      starting: t('Update is starting'),
      unknown: t('Update status unknown')
    };
    const title = titleMap[state] || t('Update status');
    const finished = status.finishedAt ? formatDateTime(status.finishedAt) : '';
    const started = status.startedAt ? formatDateTime(status.startedAt) : '';
    const logLines = Array.isArray(status.logTail) ? status.logTail : [];
    const logHtml = logLines.length
      ? `<details><summary>${t('Show update log')}</summary><pre>${esc(logLines.join('\n'))}</pre></details>`
      : '';

    el.className = `update-status update-status-${state}`;
    el.innerHTML = `
      <div class="update-status-title">${esc(title)}</div>
      <div class="update-status-message">${esc(status.message || '')}</div>
      <div class="update-status-meta">
        ${started ? `<span>${t('Started')}: ${esc(started)}</span>` : ''}
        ${finished ? `<span>${t('Finished')}: ${esc(finished)}</span>` : ''}
      </div>
      ${status.source ? `<div class="update-status-source">${t('Source')}: ${esc(status.source)}</div>` : ''}
      ${logHtml}
    `;
  } catch (e) {
    log(e);
    el.className = 'update-status update-status-unknown';
    el.textContent = t('Could not load update status.');
  }
}

function formatDateTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value || '');
  return date.toLocaleString();
}

async function loadHealth() {
  try {
    const h = await api('/api/health');
    const r = h.runtime || {};
    const items = [
      [t('Watchdog'), `${r.watchdog?.installed ? t('Installed') : t('Not installed')} (${translateHealthStatus(r.watchdog?.status)})`],
      [t('Autostart'), r.autoStart?.enabled ? t('Enabled') : t('Disabled')],
      [t('Dashboard'), `${r.dashboard?.bindAddress || 'unknown'}:${r.dashboard?.port || ''} ${r.dashboard?.remoteEnabled ? t('remote') : t('local')}`],
      [t('Email'), r.email?.configured ? t('Configured') : t('Not configured')],
      [t('Database'), `${r.database?.exists ? t('Found') : t('Missing')} - ${r.database?.path || ''}`]
    ];
    document.getElementById('health-status').innerHTML = items.map(([label, value]) =>
      `<div class="health-item"><div class="label">${esc(label)}</div><div class="value">${esc(value)}</div></div>`
    ).join('');
  } catch (e) {
    log(e);
    document.getElementById('health-status').innerHTML =
      `<div class="health-item"><div class="label">${t('Health')}</div><div class="value">${t('Unable to load')}</div></div>`;
  }
}

document.getElementById('settings-health-refresh').addEventListener('click', loadHealth);

document.getElementById('settings-admin-password').addEventListener('click', async () => {
  const el = document.getElementById('admin-password-result');
  const currentPassword = document.getElementById('set-admin-current-pw').value;
  const newPassword = document.getElementById('set-admin-pw').value;
  el.textContent = t('Saving...');
  try {
    await api('/api/auth/password', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentPassword, newPassword })
    });
    document.getElementById('set-admin-current-pw').value = '';
    document.getElementById('set-admin-pw').value = '';
    el.style.color = '#44bb44';
    el.textContent = t('Saved');
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || t('Failed');
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-logout').addEventListener('click', async () => {
  try {
    await fetch('/api/auth/logout', { method: 'POST' });
  } catch {
  }
  window.location.replace('/login.html');
});

document.getElementById('settings-rotate-token').addEventListener('click', async () => {
  if (!confirm(t('Rotate the dashboard session token? Other open dashboard sessions will need to log in again.'))) return;
  const el = document.getElementById('session-result');
  el.textContent = t('Rotating...');
  try {
    await api('/api/auth/token/rotate', { method: 'POST' });
    el.style.color = '#44bb44';
    el.textContent = t('Token rotated');
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || t('Failed');
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
  const emailDeviceId = document.getElementById('set-email-device-id').value.trim();
  const emailBreachNotifyEnabled = document.getElementById('set-email-breach-notify').checked;
  const emailKillNotifyEnabled = document.getElementById('set-email-kill-notify').checked;
  const emailStartNotifyEnabled = document.getElementById('set-email-start-notify').checked;
  const emailControlEnabled = document.getElementById('set-email-control').checked;
  const summaryEnabled = document.getElementById('set-summary-enabled').checked;
  const summaryFrequency = document.getElementById('set-summary-frequency').value;
  const summaryTime = document.getElementById('set-summary-time').value || '18:00';
  const summaryWeeklyDay = parseInt(document.getElementById('set-summary-weekly-day').value);
  const summaryMonthlyDay = parseInt(document.getElementById('set-summary-monthly-day').value) || 1;
  const tamperAlertsEnabled = document.getElementById('set-tamper-alerts').checked;
  const autoStart = document.getElementById('set-auto-start').checked;
  const pauseTrackingWhenIdle = document.getElementById('set-pause-idle').checked;
  const idleThresholdMinutes = parseInt(document.getElementById('set-idle-threshold').value) || 10;
  const hotKeyModifiers = getHotkeyModifiers();
  const hotKeyKey = document.getElementById('set-hotkey-key').value;
  uiLanguage = document.getElementById('set-language').value;
  const payload = { killDelay, showWarning, warningMessage, webhookUrl, emailAddress, emailAllowedSender, emailDeviceId, autoStart, pauseTrackingWhenIdle, idleThresholdMinutes, emailBreachNotifyEnabled, emailKillNotifyEnabled, emailStartNotifyEnabled, emailControlEnabled, summaryEnabled, summaryFrequency, summaryTime, summaryWeeklyDay, summaryMonthlyDay, tamperAlertsEnabled, uiLanguage, hotKeyModifiers, hotKeyKey };
  if (emailPassword) payload.emailPassword = emailPassword;
  await api('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
  document.getElementById('set-email-pw').value = '';
  localStorage.setItem('uiLanguage', uiLanguage);
  document.getElementById('current-hotkey').textContent = `${hotKeyModifiers}+${hotKeyKey}`;
  alert(t('Settings saved.'));
});

document.getElementById('set-language').addEventListener('change', async e => {
  const newLang = e.target.value;
  await loadTranslations(newLang);
  try {
    await api('/api/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ uiLanguage: newLang })
    });
  } catch (err) {
    log(err);
  }
  location.reload();
});

document.getElementById('settings-webhook-test').addEventListener('click', async () => {
  const el = document.getElementById('webhook-test-result');
  el.textContent = t('Sending...');
  try {
    await api('/api/settings/webhook-test', { method: 'POST' });
    el.style.color = '#44bb44';
    el.textContent = t('Test sent. Check your webhook endpoint.');
  } catch {
    el.style.color = '#ff4444';
    el.textContent = t('Failed');
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-email-test').addEventListener('click', async () => {
  const el = document.getElementById('email-test-result');
  el.textContent = t('Sending...');
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
      el.textContent = t('Test sent. Check your inbox.');
    } else {
      el.style.color = '#ff4444';
      el.textContent = (r.error || t('Failed'));
    }
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = (e.message || t('Failed'));
  }
  setTimeout(() => el.textContent = '', 10000);
});

document.getElementById('settings-email-start-test').addEventListener('click', async () => {
  const el = document.getElementById('email-test-result');
  el.textContent = t('Sending app-start test...');
  try {
    const r = await api('/api/settings/email-start-test', { method: 'POST' });
    if (r.status === 'test_sent') {
      el.style.color = '#44bb44';
      el.textContent = t('App-start test sent.');
    } else {
      el.style.color = '#ff4444';
      el.textContent = r.error || t('Failed');
    }
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || t('Failed');
  }
  setTimeout(() => el.textContent = '', 10000);
});

document.getElementById('settings-email-start-reset').addEventListener('click', async () => {
  const el = document.getElementById('email-test-result');
  el.textContent = t('Resetting...');
  try {
    await api('/api/settings/email-start-reset', { method: 'POST' });
    el.style.color = '#44bb44';
    el.textContent = t('App-start email markers reset.');
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || t('Failed');
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-export-config').addEventListener('click', async () => {
  const el = document.getElementById('config-backup-result');
  el.textContent = t('Exporting...');
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
    el.textContent = t('Exported');
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || t('Export failed');
  }
  setTimeout(() => el.textContent = '', 8000);
});

document.getElementById('settings-import-config').addEventListener('click', async () => {
  const el = document.getElementById('config-backup-result');
  const file = document.getElementById('settings-import-file').files[0];
  if (!file) {
    el.style.color = '#ff4444';
    el.textContent = t('Choose a JSON backup first.');
    return;
  }
  if (!confirm(t('Import this config backup? Current limits, schedules, and app mappings will be replaced.'))) return;

  el.textContent = t('Importing...');
  try {
    const text = await file.text();
    JSON.parse(text);
    await api('/api/config/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: text
    });
    el.style.color = '#44bb44';
    el.textContent = t('Imported');
    loadSettings();
    loadLimits();
    loadSchedule();
    loadLiveToday();
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || t('Import failed');
  }
  setTimeout(() => el.textContent = '', 10000);
});

document.getElementById('settings-import-file').addEventListener('change', e => {
  const file = e.target.files[0];
  const label = document.getElementById('settings-import-file-label');
  if (label) label.textContent = file ? file.name : t('No file chosen');
});

document.getElementById('settings-run-update').addEventListener('click', async () => {
  const el = document.getElementById('update-result');
  const source = document.getElementById('set-update-source').value.trim();
  const username = document.getElementById('set-update-username').value.trim();
  const password = document.getElementById('set-update-password').value;
  const sha256 = document.getElementById('set-update-sha256').value.trim();
  if (!source) {
    el.style.color = '#ff4444';
    el.textContent = t('Enter an update source first.');
    return;
  }

  if (!confirm(t('Update the app now? The dashboard will disconnect while DeviceMon restarts.'))) return;

  el.style.color = '#ffaa44';
  el.textContent = t('Starting update...');
  try {
    await api('/api/settings/update', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ source, username, password, sha256 })
    });
    document.getElementById('set-update-password').value = '';
    el.style.color = '#44bb44';
    el.textContent = t('Update started. Reopen the dashboard after the app restarts.');
    loadUpdateStatus();
  } catch (e) {
    el.style.color = '#ff4444';
    el.textContent = e.message || t('Update failed');
    loadUpdateStatus();
  }
});

function renderTable(id, data, props, headers) {
  const el = document.getElementById(id);
  let html = '<table><thead><tr>' + headers.map(h => `<th>${h}</th>`).join('') + '</tr></thead><tbody>';
  if (data.length === 0) {
    html += `<tr><td colspan="${headers.length}" style="color:#666;text-align:center">${t('No data')}</td></tr>`;
  } else {
    data.forEach(item => {
      html += '<tr>' + props.map(p => `<td>${esc(String(item[p] || '-'))}</td>`).join('') + '</tr>';
    });
  }
  html += '</tbody></table>';
  el.innerHTML = html;
}

function enhanceInterface() {
  document.querySelectorAll('.view > h2').forEach(heading => {
    const wrapper = document.createElement('div');
    wrapper.className = 'page-heading simple';
    heading.parentNode.insertBefore(wrapper, heading);
    wrapper.appendChild(heading);
  });

  const form = document.querySelector('.settings-form');
  if (form && !form.querySelector('.settings-panel')) {
    const children = [...form.children];
    let panel = document.createElement('section');
    panel.className = 'settings-panel';
    const generalHeading = document.createElement('h3');
    generalHeading.textContent = t('General & Security');
    panel.appendChild(generalHeading);

    const finishPanel = () => {
      if (panel.children.length) form.appendChild(panel);
      panel = document.createElement('section');
      panel.className = 'settings-panel';
    };

    children.forEach(child => {
      if (child.tagName === 'HR') {
        child.remove();
        finishPanel();
      } else {
        panel.appendChild(child);
      }
    });
    finishPanel();

    const actionPanel = document.getElementById('settings-save')?.closest('.settings-panel');
    if (actionPanel) actionPanel.classList.add('settings-actions');
  }

  const updateClock = () => {
    const now = new Date();
    const time = document.getElementById('live-clock-time');
    const date = document.getElementById('live-clock-date');
    if (time) time.textContent = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    if (date) date.textContent = now.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
  };
  updateClock();
  setInterval(updateClock, 30000);

  document.addEventListener('pointerdown', event => {
    const button = event.target.closest('button');
    if (!button || button.disabled) return;
    const rect = button.getBoundingClientRect();
    const ripple = document.createElement('span');
    ripple.className = 'button-ripple';
    ripple.style.left = `${event.clientX - rect.left}px`;
    ripple.style.top = `${event.clientY - rect.top}px`;
    button.appendChild(ripple);
    ripple.addEventListener('animationend', () => ripple.remove(), { once: true });
  });
}

// Init
(async function init() {
  await loadTranslations(uiLanguage);
  initHotkeyKeyOptions();
  applyTranslations();
  enhanceInterface();
  startApp();
})();

