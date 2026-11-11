'use strict';
const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const labels = {Pending:'Bekliyor',Queued:'Kuyrukta',Submitting:'Başlatılıyor',Starting:'Agent bekleniyor',Running:'Çalışıyor',Succeeded:'Başarılı',Failed:'Hatalı',Canceled:'İptal edildi',Unknown:'Durum belirsiz',Unverified:'İzleme kapatıldı',Retry:'Yeniden deneme',ready:'Hazır'};
const active = status => ['Queued','Submitting','Starting','Running','Unknown'].includes(status);
const clock = value => value ? new Date(value).toLocaleString((language === 'tr' ? 'tr-TR' : 'en-GB'),{timeZone:'Europe/Istanbul',day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit',second:'2-digit'}) : '—';
const badge = status => `<span class="pill ${esc(status)}">${esc(t(labels[status] || status))}</span>`;
const requestActive = status => ['Queued','Starting','Running','Unknown'].includes(status);
let state = {jobs:[],runs:[],notifications:[],requests:[],connection:null}, view = 'requests', detailTab = 'summary', selection = null;
let refreshing = false, toastTimer, pendingAction = null, firstLoad = true;
let execValid = false;
let jobSubmitting = false, executionRequestId = '';
let afterConnect = null, runLoginRequest = null;
let runAuthorizationToken = null;
try { runAuthorizationToken = sessionStorage.getItem('jobRunnerExecutionSession'); } catch {}
function saveRunAuthorization(token) {
  runAuthorizationToken = token;
  try {
    if (token) sessionStorage.setItem('jobRunnerExecutionSession',token);
    else sessionStorage.removeItem('jobRunnerExecutionSession');
  } catch {}
}
const deliveredNotifications = new Set();
const newRequestId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,'0')).join('');
const stored = {get:key => { try { return localStorage.getItem(key); } catch { return null; } },set:(key,value) => { try { localStorage.setItem(key,value); } catch {} }};
document.documentElement.dataset.theme = localStorage.getItem('jobRunnerTheme') || 'light';
function desktopNotify(body,onclick) {
  if (!('Notification' in window) || Notification.permission !== 'granted') return;
  try {
    const notice = new Notification('SQL Job Runner',{body});
    notice.onclick = () => { window.focus(); onclick?.(); notice.close(); };
  } catch { /* The in-app toast still announces the request. */ }
}
// Ask once when the site opens; browsers that require a user gesture get asked on the first click instead.
async function setupDesktopNotifications() {
  if (!('Notification' in window)) return;
  if (Notification.permission === 'default') { try { await Notification.requestPermission(); } catch {} }
  if (Notification.permission === 'default') { document.addEventListener('click',setupDesktopNotifications,{once:true}); return; }
  if (Notification.permission === 'granted' && !stored.get('jobRunnerWelcomeNotified')) {
    desktopNotify(t('Masaüstü bildirimleri açık. Yeni job istekleri burada bildirilecek.'));
    stored.set('jobRunnerWelcomeNotified','1');
  }
}
function showRequest(id) {
  view = 'requests'; selection = {kind:'request',id:String(id)};
  document.querySelectorAll('[data-view]').forEach(b => b.classList.toggle('active',b.dataset.view === 'requests'));
  renderTable(); renderDetail();
}
// Announce new requests from other users; the first visit records the baseline.
function announceRequests() {
  if (!state.requests) return;
  const latestId = Math.max(0,...state.requests.map(r => r.requestId));
  const seen = stored.get('jobRunnerSeenRequest');
  if (seen !== null) {
    const fresh = state.requests.filter(r => r.requestId > Number(seen) && !r.isImported && r.requestedBy !== state.currentUser);
    for (const request of fresh) {
      const body = `${t('Yeni istek')}: ${request.jobName} (${request.requestedBy})`;
      toast(`${t('Yeni istek')}: ${request.jobName}`);
      desktopNotify(body,() => showRequest(request.requestId));
    }
  }
  if (seen === null || latestId > Number(seen)) stored.set('jobRunnerSeenRequest',String(latestId));
}
function toast(text) { $('toast').textContent = message(text); $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true,6500); }
async function api(path, method = 'GET', body) {
  const headers = body === undefined ? {} : {'Content-Type':'application/json'};
  if (method !== 'GET' && runAuthorizationToken) headers['X-Execution-Session'] = runAuthorizationToken;
  const response = await fetch(path,{method,headers,body:body === undefined ? undefined : JSON.stringify(body)});
  const data = response.status === 204 ? null : await response.json();
  if (!response.ok) { const error = new Error(data?.error || t('İstek tamamlanamadı.')); error.data = data; throw error; }
  return data;
}
async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try {
    state = await api('/api/state');
    if (!state.sessionConnected) saveRunAuthorization(null);
    if (state.demoMode && $('demoTools').hidden) {
      const cases = await api('/api/demo/cases');
      $('demoCase').innerHTML = cases.map(c => `<option value="${esc(c.name)}">${esc(c.title)}</option>`).join('');
      $('demoCase').onchange = () => $('demoDescription').textContent = cases.find(c => c.name === $('demoCase').value)?.description || '';
      $('demoCase').onchange(); $('demoTools').hidden = false;
    }
    $('healthText').textContent = state.requestsError ? t('İstekler yüklenemedi.') : t('Hazır');
    $('healthText').title = state.requestsError ? message(state.requestsError) : '';
    announceRequests();
    $('refreshTime').textContent = new Date().toLocaleTimeString((language === 'tr' ? 'tr-TR' : 'en-GB'));
    for (const n of state.notifications) {
      if (!firstLoad && !n.read && !deliveredNotifications.has(n.id)) {
        toast(`${n.jobName}: ${t(labels[n.status])}`);
        if ('Notification' in window && Notification.permission === 'granted') {
          const notice = new Notification('SQL Job Runner',{body:`${n.jobName}: ${t(labels[n.status])}`,tag:n.id});
          notice.onclick = () => { window.focus(); selection = {kind:'run',id:n.runId}; renderDetail(); notice.close(); };
        }
      }
      deliveredNotifications.add(n.id);
    }
    firstLoad = false; render();
  } catch (error) { $('healthText').textContent = t('Bağlantı kesildi'); }
  finally { refreshing = false; }
}
function latest(jobId) { return state.runs.find(r => r.jobId === jobId); }
function jobStatus(job) { return latest(job.id)?.status || 'ready'; }
function duration(run) {
  if (!run) return '—';
  const seconds = Math.max(0,Math.floor(((run.finishedUtc ? new Date(run.finishedUtc) : new Date()) - new Date(run.createdUtc))/1000));
  return `${Math.floor(seconds/3600).toString().padStart(2,'0')}:${Math.floor(seconds%3600/60).toString().padStart(2,'0')}:${(seconds%60).toString().padStart(2,'0')}`;
}
function render() {
  $('healthText').textContent = state.requestsError ? t('İstekler yüklenemedi.') : $('healthText').textContent;
  $('healthText').title = state.requestsError ? message(state.requestsError) : '';
  $('connectionLabel').textContent = state.connection?.server || t('Bağlantı seçin');
  const requests = state.requests || [];
  $('totalCount').textContent = requests.filter(r => r.status === 'Pending').length;
  $('activeCount').textContent = requests.filter(r => requestActive(r.status) && r.status !== 'Unknown').length;
  $('unreadCount').textContent = state.notifications.filter(n => !n.read).length;
  renderTable(); renderDetail();
  if ($('notificationDialog').open) renderNotifications();
  renderSsisMessage();
  if ($('ssisDialog').open) {
    if (!ssisCache.pending && Date.now() - ssisCache.updated > 3000) loadSsisErrors();
    else renderSsisErrors();
  }
}
const requestTime = r => Date.parse(r.executedAt || r.requestedAt) || 0;
const sameJob = (a,b) => a.trim().toLocaleUpperCase('en') === b.trim().toLocaleUpperCase('en');
// Another request for the same job is running; the server refuses a second run as well.
function runAction(r) {
  if (r.status !== 'Pending') return `<button class="btn" data-action="detail">${t("Detay")}</button>`;
  return `<button class="btn primary" data-action="run-request" data-request="${esc(String(r.requestId))}">${t("▷ Çalıştır")}</button>`;
}

function requestDuration(r) {
  if (!r.startedAt || (!requestActive(r.status) && !r.finishedAt)) return '—';
  return duration({createdUtc:r.startedAt,finishedUtc:requestActive(r.status) ? null : r.finishedAt || null});
}
function filteredRequests(rows) {
  const filter = $('statusFilter').value, query = $('search').value.toLocaleLowerCase('tr');
  return rows.filter(r => JSON.stringify(r).toLocaleLowerCase('tr').includes(query) && (!filter || (filter === 'active' ? requestActive(r.status) : r.status === filter)));
}
// Every request in the table, most recent activity first.
function renderHistory() {
  const rows = filteredRequests((state.requests || []).filter(r => r.status !== 'Pending')).sort((a,b) => requestTime(b) - requestTime(a) || b.requestId - a.requestId);
  $('tableHead').innerHTML = `<tr><th>#</th><th>${t("Job adı")}</th><th>${t("Durum")}</th><th>${t("İsteyen")}</th><th>${t("Çalıştıran")}</th><th>${t("İstek zamanı")}</th><th>${t("Başlangıç")}</th><th>${t("Bitiş zamanı")}</th><th>${t("Süre")}</th></tr>`;
  $('tableBody').innerHTML = rows.map(r => {
    const id = String(r.requestId);
    return `<tr data-kind="request" data-id="${esc(id)}" tabindex="0" class="${selection?.kind === 'request' && selection.id === id ? 'selected' : ''}">
      <td class="job-name">${esc(id)}</td><td><div class="job-name">${esc(r.agentJobName || r.jobName)}</div></td><td>${badge(r.status)}</td>
      <td>${esc(r.requestedBy)}</td><td>${esc(r.executedBy || '—')}</td><td>${clock(r.requestedAt)}</td><td>${clock(r.startedAt)}</td><td>${clock(r.finishedAt)}</td><td class="job-name">${requestDuration(r)}</td></tr>`;
  }).join('');
  $('emptyState').hidden = rows.length > 0;
  $('emptyState').querySelector('h2').textContent = state.requests?.length ? t('Kayıt bulunamadı.') : t('Henüz istek yok.');
  $('rowCount').textContent = `${rows.length} ${t("kayıt")}`;
}
// Only requests waiting to be run or currently running; finished ones are in the history.
function renderRequests() {
  const open = (state.requests || []).filter(r => r.status === 'Pending' || requestActive(r.status));
  const rows = filteredRequests(open);
  $('tableHead').innerHTML = `<tr><th>#</th><th>${t("Job adı")}</th><th>${t("İsteyen")}</th><th>${t("İstek zamanı")}</th><th>${t("Durum")}</th><th>${t("Aktif adım")}</th><th>${t("İşlemler")}</th></tr>`;
  $('tableBody').innerHTML = rows.map(r => {
    const id = String(r.requestId);
    return `<tr data-kind="request" data-id="${esc(id)}" tabindex="0" class="${selection?.kind === 'request' && selection.id === id ? 'selected' : ''}">
      <td class="job-name">${esc(id)}</td><td><div class="job-name">${esc(r.agentJobName || r.jobName)}</div></td><td>${esc(r.requestedBy)}</td>
      <td>${clock(r.requestedAt)}</td><td>${badge(r.status)}</td><td>${esc(message(r.currentStep || '—'))}</td>
      <td><div class="row-actions">${runAction(r)}${r.status === 'Pending' ? `<button class="btn" data-action="delete-request" data-request="${esc(id)}" aria-label="${t('İsteği sil')}" title="${t('İsteği sil')}">×</button>` : ''}</div></td></tr>`;
  }).join('');
  $('emptyState').hidden = rows.length > 0;
  $('emptyState').querySelector('h2').textContent = open.length ? t('Kayıt bulunamadı.') : t('Bekleyen veya çalışan istek yok.');
  $('rowCount').textContent = `${rows.length} ${t("kayıt")}`;
}
function renderTable() {
  if (view === 'requests') return renderRequests();
  if (view === 'history') return renderHistory();
  const filter = $('statusFilter').value, query = $('search').value.toLocaleLowerCase('tr');
  let rows = view === 'jobs' ? state.jobs.map(job => ({job,run:latest(job.id),status:jobStatus(job)})) : state.runs.map(run => ({job:run.definition,run,status:run.status}));
  rows.sort((a,b) => (Date.parse(b.run?.createdUtc) || 0) - (Date.parse(a.run?.createdUtc) || 0));
  rows = rows.filter(row => JSON.stringify(row.job).toLocaleLowerCase('tr').includes(query) && (!filter || (filter === 'active' ? active(row.status) : row.status === filter)));
  $('tableHead').innerHTML = `<tr><th>${t("Job adı")}</th><th>${t("Durum")}</th><th>${view === 'jobs' ? t('Son çalışma') : t('Başlangıç')}</th><th>${t("Süre")}</th><th>${t("Aktif adım")}</th><th>${t("İşlemler")}</th></tr>`;
  $('tableBody').innerHTML = rows.map(({job,run,status}) => {
    const kind = view === 'jobs' ? 'job' : 'run', id = kind === 'job' ? job.id : run.id;
    const busy = state.runs.some(r => r.jobId === job.id && active(r.status));
    return `<tr data-kind="${kind}" data-id="${esc(id)}" tabindex="0" class="${selection?.kind === kind && selection.id === id ? 'selected' : ''}">
      <td><div class="job-name">${esc(job.jobName)}</div>${view === 'history' && run?.agentJobName ? `<div class="job-sub">${esc(run.agentJobName)}</div>` : ''}</td><td>${badge(status)}</td>
      <td>${clock(run?.createdUtc)}</td><td class="job-name">${duration(run)}</td><td>${esc(message(run?.currentStep || '—'))}</td>
      <td><div class="row-actions">${view === 'jobs' ? `<button class="btn primary" data-action="run" data-job="${esc(job.id)}" ${busy || !state.connection ? 'disabled' : ''}>${t("▷ Çalıştır")}</button><button class="btn" data-action="edit" data-job="${esc(job.id)}" ${busy ? 'disabled' : ''}>${t("Düzenle")}</button><button class="btn" data-action="delete" data-job="${esc(job.id)}" ${busy ? 'disabled' : ''} aria-label="${t("Job sil")}">×</button>` : `<button class="btn" data-action="detail">${t("Detay")}</button>`}</div></td></tr>`;
  }).join('');
  $('emptyState').hidden = rows.length > 0;
  $('emptyState').querySelector('h2').textContent = state.jobs.length ? t('Kayıt bulunamadı.') : t('Kayıtlı job yok.');
  $('rowCount').textContent = `${rows.length} ${t("kayıt")}`;
}
function chosen() {
  if (!selection) return {};
  if (selection.kind === 'request') {
    const request = state.requests?.find(r => String(r.requestId) === selection.id);
    return {request,job:request,run:request && state.runs.find(r => r.requestId === request.requestId)};
  }
  const run = selection.kind === 'run' ? state.runs.find(r => r.id === selection.id) : latest(selection.id);
  const job = selection.kind === 'job' ? state.jobs.find(j => j.id === selection.id) : run?.definition;
  return {run,job};
}
function renderDetail() {
  // Capture native disclosure state before replacing detail markup, including when switching records/tabs.
  $('detailBody').querySelectorAll('details[data-history-key]').forEach(element => {
    if (element.open) expandedHistoryMessages.add(element.dataset.historyKey);
    else expandedHistoryMessages.delete(element.dataset.historyKey);
  });
  const {run,job,request} = chosen();
  $('detailTitle').textContent = request ? request.agentJobName || request.jobName : job?.jobName || t('Job detayları');
  if (!job) { $('detailBody').innerHTML = `<div class="detail-placeholder">${t("Bir kayıt seçin.")}</div>`; return; }
  if (request && detailTab !== 'parameters') return renderRequestDetail(request,run);
  if (detailTab === 'parameters') { $('detailBody').innerHTML = `${request?.isImported ? `<p class="message-box">${t('Geçmiş kayıt; çalıştırılamaz.')}</p>` : ''}<pre class="code">${esc(script(job,run))}</pre>`; return; }
  if (detailTab === 'steps') {
    const steps = run?.steps || [];
    $('detailBody').innerHTML = `${run?.currentStep ? `<div class="message-box">${esc(message(run.currentStep))}</div>` : ''}${steps.length ? `<table class="steps-table"><thead><tr><th>${t("Adım")}</th><th>${t("Durum")}</th><th>${t("Süre")}</th><th>${t("Mesaj")}</th></tr></thead><tbody>${steps.map(s => `<tr><td>${s.stepId}. ${esc(s.name)}</td><td>${badge(({0:'Failed',1:'Succeeded',2:'Retry',3:'Canceled',4:'Running'})[s.status])}</td><td>${String(s.duration).padStart(6,'0').replace(/(\d+)(\d{2})(\d{2})$/,'$1:$2:$3')}</td><td class="step-message">${esc(s.message)}</td></tr>`).join('')}</tbody></table>` : `<div class="detail-placeholder">${t("Adım kaydı yok.")}</div>`}`;
    return;
  }
  if (detailTab === 'logs') {
    $('detailBody').innerHTML = `<pre class="code">${esc(run ? `[${clock(run.createdUtc)}] ${t("Çalışma kaydedildi.")}\n${t("Durum")}: ${t(labels[run.status])}\n${run.currentStep ? t('Aktif adım')+': '+run.currentStep+'\n' : ''}\n${message(run.message)}\n\n${(run.steps || []).map(s => `[${t("Adım")} ${s.stepId}: ${s.name}]\n${s.message}`).join('\n\n')}` : t('Henüz çalışma yok.'))}</pre>`; return;
  }
  const status = selection.kind === 'job' ? jobStatus(job) : run.status;
  const entries = [[t('Durum'),badge(status)],[t('Agent job'),esc(run?.agentJobName || '—')],[t('Başlangıç'),clock(run?.createdUtc)],[t('Bitiş'),clock(run?.finishedUtc)],[t('Süre'),duration(run)],[t('Aktif adım'),esc(message(run?.currentStep || '—'))]];
  $('detailBody').innerHTML = `<div class="detail-grid">${entries.map(([label,value]) => `<div><label>${label}</label><div class="detail-value">${value}</div></div>`).join('')}</div>${run && ['Failed','Unknown','Unverified'].includes(status) ? `<div class="message-box warning">${esc(message(run.message))}</div>` : ''}${run?.status === 'Unknown' ? `<button class="btn sm close-tracking" id="closeTrackingBtn">${t("İzlemeyi kapat")}</button>` : ''}`;
  if ($('closeTrackingBtn')) $('closeTrackingBtn').onclick = () => confirmAction(t('İzlemeyi kapat'),t('SQL Server Agent üzerindeki gerçek job durdurulmaz. İzleme sonlandırılır ve yeni çalıştırma kilidi kaldırılır. Yeniden çalıştırmadan önce Agent durumunu kontrol edin.'),job.jobName,t('İzlemeyi kapat'),async () => { await api(`/api/runs/${run.id}/close-tracking`,'POST'); toast(t('İzleme kapatıldı. Job sonucu doğrulanmadı.')); });
}
// Details are read on demand from shared Agent history, independently of the workstation that ran the request.
const requestHistoryCache = new Map();
const expandedHistoryMessages = new Set();
const historyMessageKey = (request,entry) => `${request.requestId}:${request.runToken || ""}:${entry.instanceId}:${entry.stepId}`;
const historyDuration = seconds => `${Math.floor(seconds / 3600).toString().padStart(2,'0')}:${Math.floor(seconds % 3600 / 60).toString().padStart(2,'0')}:${Math.floor(seconds % 60).toString().padStart(2,'0')}`;
function requestHistory(request) {
  const key = `${request.requestId}:${request.runToken || ''}`;
  let cache = requestHistoryCache.get(key);
  if (!request.runToken) return {entries:[],pending:false};
  if (!cache || (!cache.pending && Date.now() - cache.updated > 3000)) {
    cache = {...cache,pending:true,updated:Date.now()}; requestHistoryCache.set(key,cache);
    api(`/api/requests/${request.requestId}/history`).then(result => {
      if (result.runToken !== request.runToken) throw new Error(t('Geçmiş kaydı değişti; tekrar yükleniyor.'));
      requestHistoryCache.set(key,{entries:result.entries || [],pending:false,updated:Date.now()});
    }).catch(error => requestHistoryCache.set(key,{entries:[],error:error.message,pending:false,updated:Date.now()}))
      .finally(() => { if (selection?.kind === 'request' && selection.id === String(request.requestId) && ['steps','logs'].includes(detailTab)) renderDetail(); });
  }
  return cache;
}
let lastHistoryRender = {node:null,html:''};
function setHistoryMarkup(html) {
  const body = $('detailBody');
  if (lastHistoryRender.node === body.firstElementChild && lastHistoryRender.html === html) return;
  const top = body.scrollTop, left = body.scrollLeft;
  const innerLeft = body.querySelector('.history-scroll')?.scrollLeft || 0;
  body.innerHTML = html;
  body.scrollTop = top; body.scrollLeft = left;
  const inner = body.querySelector('.history-scroll'); if (inner) inner.scrollLeft = innerLeft;
  lastHistoryRender = {node:body.firstElementChild,html};
}
function renderSharedHistory(request) {
  const history = requestHistory(request);
  if (history.error) { $('detailBody').innerHTML = `<div class="message-box warning">${esc(message(history.error))}</div>`; return; }
  if (!history.entries?.length) {
    const empty = history.pending ? 'Agent geçmişi yükleniyor…' : request.runToken ? 'Agent geçmişinde henüz adım kaydı yok. Geçmiş temizlenmiş veya adım henüz tamamlanmamış olabilir.' : 'İstek henüz çalıştırılmadı.';
    $('detailBody').innerHTML = `<div class="detail-placeholder">${t(empty)}</div>${request.statusMessage ? `<pre class="history-message">${esc(message(request.statusMessage))}</pre>` : ''}`; return;
  }
  const entries = history.entries;
  if (detailTab === 'logs') {
    $('detailBody').innerHTML = `<pre class="history-raw-log"></pre>`;
    $('detailBody').querySelector('pre').textContent = entries.map(entry => entry.message ?? '').join('\n\n');
    return;
  }
  setHistoryMarkup(`<div class="history-scroll"><table class="steps-table"><colgroup><col class="history-name"><col class="history-status"><col class="history-time"><col class="history-time"><col class="history-duration"><col class="history-text"></colgroup><thead><tr><th>${t('Adım')}</th><th>${t('Durum')}</th><th>${t('Başlangıç')}</th><th>${t('Bitiş')}</th><th>${t('Süre')}</th><th>${t('Mesaj')}</th></tr></thead><tbody>${entries.map(e => `<tr><td>${esc(e.stepId === 0 ? t('Genel sonuç') : `${e.stepId}. ${e.name}`)}</td><td>${badge(({0:'Failed',1:'Succeeded',2:'Retry',3:'Canceled',4:'Running'})[e.status])}</td><td>${clock(e.startedAt)}</td><td>${clock(e.finishedAt)}</td><td>${historyDuration(e.durationSeconds)}</td><td><details data-history-key="${esc(historyMessageKey(request,e))}" ${expandedHistoryMessages.has(historyMessageKey(request,e)) ? 'open' : ''}><summary>${t('Mesajı göster')} · ${t('SQL hata numarası')}: ${e.sqlErrorNumber}</summary><pre class="history-message">${esc(e.message)}</pre></details></td></tr>`).join('')}</tbody></table></div>`);
}
function renderRequestDetail(request,run) {
  if (detailTab === 'steps' || detailTab === 'logs') { renderSharedHistory(request); return; }
  const entries = [[t('Durum'),badge(request.status)],[t('İsteyen'),esc(request.requestedBy)],[t('İstek zamanı'),clock(request.requestedAt)],[t('Çalıştıran'),esc(request.executedBy || '—')],[t('Job adı'),esc(request.agentJobName || request.jobName)],[t('Başlangıç'),clock(request.startedAt)],[t('Bitiş'),clock(request.finishedAt)],[t('Aktif adım'),esc(message(request.currentStep || '—'))]];
  $('detailBody').innerHTML = `<div class="detail-grid">${entries.map(([label,value]) => `<div><label>${label}</label><div class="detail-value">${value}</div></div>`).join('')}</div>${request.statusMessage && ['Failed','Unknown','Unverified','Canceled'].includes(request.status) ? `<div class="message-box warning">${esc(message(request.statusMessage))}</div>` : ''}${run?.status === 'Unknown' ? `<button class="btn sm close-tracking" id="closeTrackingBtn">${t("İzlemeyi kapat")}</button>` : ''}`;
  if ($('closeTrackingBtn')) $('closeTrackingBtn').onclick = () => confirmAction(t('İzlemeyi kapat'),t('SQL Server Agent üzerindeki gerçek job durdurulmaz. İzleme sonlandırılır ve yeni çalıştırma kilidi kaldırılır. Yeniden çalıştırmadan önce Agent durumunu kontrol edin.'),request.jobName,t('İzlemeyi kapat'),async () => { await api(`/api/runs/${run.id}/close-tracking`,'POST'); toast(t('İzleme kapatıldı. Job sonucu doğrulanmadı.')); });
}
let ssisGrouped = stored.get('jobRunnerSsisGrouped') !== 'false';
const expandedSsisGroups = new Set();
let ssisMessageSelection = null, ssisMessageTab = 'message';
$('ssisGroupToggle').checked = ssisGrouped;
let ssisCache = {pending:false, revision:0, updated:0};
async function loadSsisErrors(force = false) {
  if (ssisCache.pending) return;
  const revision = ssisCache.revision + 1;
  ssisCache = {...ssisCache,pending:true,revision};
  renderSsisErrors();
  try {
    const result = await api(force ? '/api/ssis-errors/refresh' : '/api/ssis-errors',force ? 'POST' : 'GET');
    if (ssisCache.revision === revision) ssisCache = {result,pending:false,revision,updated:Date.now()};
  } catch (error) {
    if (ssisCache.revision === revision) ssisCache = {...ssisCache,error:error.message,pending:false,revision,updated:Date.now()};
  }
  renderSsisErrors();
}
function ssisRows() {
  return (ssisCache.result?.executions || []).flatMap(execution =>
    execution.errors.map(error => ({execution,error})))
    .sort((a,b) => Date.parse(b.error.messageTime)-Date.parse(a.error.messageTime));
}
function renderSsisErrors() {
  const cache = ssisCache, content = $('ssisBody');
  $('ssisRefreshBtn').disabled = cache.pending || !!cache.result?.isRefreshing;
  $('ssisUpdated').textContent = `${t('Son güncelleme')}: ${clock(cache.result?.tableUpdatedUtc)}`;
  const signature = JSON.stringify([language,ssisGrouped,[...expandedSsisGroups],cache.error,cache.result?.error,cache.result?.server,cache.result?.tableUpdatedUtc,cache.result?.errorCount,!cache.result?.updatedUtc && cache.pending]);
  if (content.dataset.signature === signature) return;
  content.dataset.signature = signature;
  if ((cache.pending || cache.result?.isRefreshing || !cache.result?.lastAttemptUtc) && !cache.result?.updatedUtc && !cache.error && !cache.result?.error) { content.textContent = t('SSIS hataları yükleniyor…'); return; }
  const loadError = cache.error || cache.result?.error;
  let html = '';
  if (loadError) html += `<p class="message-box warning">${t('SSIS hataları yüklenemedi. Bağlantıyı ve yetkileri kontrol edin.')}</p><details><summary>${t('Hata ayrıntıları')}</summary><pre class="history-message">${esc(message(loadError))}</pre></details>`;
  const result = cache.result;
  html += `<p class="hint">${clock(result?.from)} → ${clock(result?.before)} · ${esc(result?.errorCount ?? 0)} ${t('hata kaydı')} · ${t('Türkiye saati')}</p>`;
  const rows = ssisRows();
  if (!loadError && !rows.length && result?.updatedUtc) html += `<p>${t('Son 48 saatte erişilebilir OnError kaydı bulunamadı.')}</p>`;
  if (rows.length) {
    html += `<div class="ssis-table-scroll"><table class="ssis-table"><colgroup><col class="ssis-col-time"><col class="ssis-col-id"><col class="ssis-col-project"><col class="ssis-col-package"><col class="ssis-col-source"><col class="ssis-col-message"></colgroup><thead><tr><th>${t('Hata zamanı')}</th><th>${t('Çalışma kimliği')}</th><th>${t('Klasör / Proje')}</th><th>${t('Paket')}</th><th>${t('Kaynak')}</th><th>${t('Mesaj')}</th></tr></thead>`;
    const rowHtml = ({execution,error},hidden) => `<tr class="ssis-error" ${hidden ? 'hidden' : ''}><td class="ssis-time">${clock(error.messageTime)}</td><td class="ssis-id">${esc(execution.executionId)}</td><td>${esc(execution.folder)} / ${esc(execution.project)}</td><td>${esc(error.packageName || execution.package)}</td><td>${esc(error.sourceName || '—')}</td><td><div class="ssis-preview">${esc(error.message)}</div><button class="btn ghost sm ssis-message-open" type="button" data-ssis-execution="${esc(execution.executionId)}" data-ssis-message="${esc(error.messageId)}">${t('Mesajı göster')}</button></td></tr>`;
    if (ssisGrouped) {
      const groups = new Map();
      for (const row of rows) {
        const id = row.execution.executionId;
        if (!groups.has(id)) groups.set(id,[]);
        groups.get(id).push(row);
      }
      for (const [id,entries] of groups) {
        const key = `${result.server}:${id}`, open = expandedSsisGroups.has(key);
        html += `<tbody><tr class="ssis-group-row"><td colspan="6"><button class="ssis-group-button" type="button" data-ssis-group="${esc(key)}" aria-expanded="${open}"><span aria-hidden="true">${open ? '▾' : '▸'}</span><strong>${t('Çalışma kimliği')}: ${esc(id)}</strong><span class="ssis-group-count">${entries.length} ${t('hata kaydı')}</span><span class="ssis-group-package">${esc(entries[0].execution.package)}</span><span class="hint">${clock(entries[0].error.messageTime)}</span></button></td></tr>${entries.map(row=>rowHtml(row,!open)).join('')}</tbody>`;
      }
    } else html += `<tbody>${rows.map(row=>rowHtml(row,false)).join('')}</tbody>`;
    html += '</table></div>';
  }
  const top = content.scrollTop, scroll = content.querySelector('.ssis-table-scroll');
  const left = scroll?.scrollLeft || 0, innerTop = scroll?.scrollTop || 0;
  content.innerHTML = html; content.scrollTop = top;
  const next = content.querySelector('.ssis-table-scroll');
  if (next) { next.scrollLeft = left; next.scrollTop = innerTop; }
}
$('ssisGroupToggle').onchange = event => {
  ssisGrouped = event.target.checked;
  stored.set('jobRunnerSsisGrouped',String(ssisGrouped));
  renderSsisErrors();
};
$('ssisBody').onclick = event => {
  const group = event.target.closest('[data-ssis-group]');
  if (group) {
    const key = group.dataset.ssisGroup;
    if (expandedSsisGroups.has(key)) expandedSsisGroups.delete(key); else expandedSsisGroups.add(key);
    renderSsisErrors();
    [...document.querySelectorAll('[data-ssis-group]')].find(button=>button.dataset.ssisGroup === key)?.focus({preventScroll:true});
    return;
  }
  const button = event.target.closest('[data-ssis-message]');
  if (!button) return;
  const entries = ssisRows().filter(row => !ssisGrouped || row.execution.executionId === button.dataset.ssisExecution);
  const index = entries.findIndex(row => row.execution.executionId === button.dataset.ssisExecution && row.error.messageId === button.dataset.ssisMessage);
  if (index < 0) return;
  ssisMessageSelection = {server:ssisCache.result.server,entries,index};
  ssisMessageTab = 'message';
  $('ssisMessageDialog').showModal(); renderSsisMessage();
};
function renderSsisMessage() {
  if (!$('ssisMessageDialog').open || !ssisMessageSelection) return;
  const {entries,index,server} = ssisMessageSelection, {execution,error} = entries[index];
  $('ssisMessageTitle').textContent = t('Hata ayrıntısı');
  $('ssisMessageContext').textContent = `${t('Çalışma kimliği')}: ${execution.executionId} · ${clock(error.messageTime)} · ${error.packageName || execution.package}`;
  $('ssisMessagePosition').textContent = `${index+1} / ${entries.length}`;
  $('ssisMessagePrev').disabled = index === 0;
  $('ssisMessageNext').disabled = index === entries.length-1;
  document.querySelectorAll('[data-ssis-message-tab]').forEach(button => button.classList.toggle('active',button.dataset.ssisMessageTab === ssisMessageTab));
  const body = $('ssisMessageBody');
  const signature = JSON.stringify([language,server,execution.executionId,error.messageId,ssisMessageTab]);
  if (body.dataset.signature === signature) return;
  body.dataset.signature = signature;
  if (ssisMessageTab === 'message') {
    body.innerHTML = '<pre class="ssis-full-message"></pre>';
    body.querySelector('pre').textContent = error.message;
  } else {
    const fields = [['Sunucu',server],['Çalışma kimliği',execution.executionId],['Klasör / Proje',`${execution.folder} / ${execution.project}`],['Ana paket',execution.package],['Paket',error.packageName || execution.package],['Kaynak',error.sourceName || '—'],['Başlangıç',clock(execution.startedAt)],['Bitiş',clock(execution.finishedAt)],['Alt bileşen',error.subcomponentName || '—']];
    body.innerHTML = `<dl class="ssis-info-grid">${fields.map(([label,value])=>`<div><dt>${t(label)}</dt><dd>${esc(value)}</dd></div>`).join('')}<div class="full"><dt>${t('Çalışma yolu')}</dt><dd class="ssis-path">${esc(error.executionPath || '—')}</dd></div></dl>`;
  }
  body.scrollTop = 0;
}
document.querySelectorAll('[data-ssis-message-tab]').forEach(button => button.onclick = () => { ssisMessageTab = button.dataset.ssisMessageTab; renderSsisMessage(); });
$('ssisMessagePrev').onclick = () => { if (ssisMessageSelection?.index > 0) { ssisMessageSelection.index--; renderSsisMessage(); } };
$('ssisMessageNext').onclick = () => { if (ssisMessageSelection && ssisMessageSelection.index < ssisMessageSelection.entries.length-1) { ssisMessageSelection.index++; renderSsisMessage(); } };
$('ssisMessageDialog').addEventListener('click', event => {
  if (event.target !== $('ssisMessageDialog')) return;
  const rect = $('ssisMessageDialog').getBoundingClientRect();
  if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) $('ssisMessageDialog').close();
});
$('ssisDialog').addEventListener('close',() => { if ($('ssisMessageDialog').open) $('ssisMessageDialog').close(); });

$('ssisBtn').onclick = () => { $('ssisDialog').showModal(); loadSsisErrors(); };
$('ssisRefreshBtn').onclick = () => loadSsisErrors(true);
$('ssisDialog').addEventListener('click', event => {
  if (event.target !== $('ssisDialog')) return;
  const rect = $('ssisDialog').getBoundingClientRect();
  if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) $('ssisDialog').close();
});
// Reuse the authenticated account until the server's three-hour idle timeout expires.
function runRequest(request) {
  if (request.status !== 'Pending') return;
  if (runAuthorizationToken && state.sessionConnected) { confirmAuthorizedRun(request,runAuthorizationToken); return; }
  openConnection();
  runLoginRequest = request;
  $('cServer').readOnly = true;
  $('connectionDialogTitle').textContent = t('Çalıştırmak için giriş yap');
  afterConnect = authorizationToken => {
    saveRunAuthorization(authorizationToken);
    confirmAuthorizedRun(request,authorizationToken);
  };
}
function confirmAuthorizedRun(request,authorizationToken) {
  confirmAction(t('İsteği çalıştır'),t('İstek şimdi çalıştırılsın mı?'),`${t("Sunucu: ")}${state.connection?.server}\nJob: ${request.jobName}\n${t("İsteyen")}: ${request.requestedBy}`,t('Çalıştır'),async () => {
    try { await api(`/api/requests/${request.requestId}/run`,'POST',{requestId:newRequestId(),authorizationToken}); }
    catch (error) {
      if (!error.data?.needsConnection) throw error;
      saveRunAuthorization(null); state.sessionConnected = false;
      $('confirmDialog').close(); runRequest(request); return;
    }
    showRequest(request.requestId); toast(t('İstek çalıştırılıyor.'));
  });
}
function script(job,run) {
  const params = [['MIGPackageName',job.migPackageName],['JobName',run?.generatorJobName || t('<benzersiz çalışma etiketi>')],['StgPackageName',job.stgPackageName],['StgFullInitialLoad',job.stgFullInitialLoad === true ? 'YES' : 'NO'],['SkeyPackageName',job.skeyPackageName],['LryPackageName',job.lryPackageName],['Sql1',job.sql1],['EdwPackageName',job.edwPackageName],['Sql2',job.sql2],['EdwdmPackageName',job.edwdmPackageName]];
  const sqlValue = v => v == null || v === '' ? 'NULL' : "'" + v.replace(/'/g,"''") + "'";
  return 'EXEC EDWUTIL.[OPR].[EdwJobGenerator]\n' + params.map(([k,v]) => `    @${k.padEnd(18)} = ${sqlValue(v)}`).join(',\n') + ';';
}
function openJob(job) {
  jobSubmitting=false; executionRequestId=newRequestId();
  $('jobForm').reset(); $('jobError').hidden = true;
  $('execImportStatus').textContent = ''; $('execImportStatus').className = '';
  for (const el of $('jobForm').elements) {
    if (!el.name) continue;
    const v = job?.[el.name];
    if (el.type === 'checkbox') el.checked = v ?? true;
    else el.value = v == null ? (el.name === 'stgFullInitialLoad' ? 'false' : '') : String(v);
  }
  $('jobDialogTitle').textContent = job ? t('Düzenle ve gönder') : t('Yeni istek');
  $('execInput').value = job ? script(job,{generatorJobName:job.jobName}) : '';
  importExec();
  $('jobDialog').showModal();
}
let recentConnections = [];
function connectionStatus(text,kind = '') { $('connectionError').textContent = message(text); $('connectionError').className = `conn-status ${kind}`; }
function syncAuthRows() { for (const id of ['rowUser','rowPass','rowRemember']) $(id).classList.toggle('hidden',$('authSelect').value !== 'sql'); }
function applyConnection(c) {
  const f = $('connectionForm').elements;
  f.server.value = runLoginRequest ? state.connection?.server || 'PASIFIK' : c?.server || ''; f.auth.value = c?.integratedSecurity === false ? 'sql' : 'windows';
  f.userName.value = c?.userName || ''; f.password.value = '';
  f.encrypt.value = String(c?.encrypt ?? false); f.trustServerCertificate.value = String(c?.trustServerCertificate ?? true);
  f.rememberPassword.checked = c?.rememberPassword ?? false;
  syncAuthRows(); connectionStatus(c?.hasPassword ? c.rememberPassword ? t('Kayıtlı parola kullanılacak.') : t('Parola yalnızca bu oturum için kayıtlı.') : '');
}
async function loadRecentConnections() {
  try {
    recentConnections = await api('/api/connections/recent');
    $('recentList').innerHTML = recentConnections.length ? recentConnections.map(c => `<div class="recent-item" data-connection="${esc(c.id)}" tabindex="0"><div class="recent-main"><div class="recent-label">${esc(c.server)}</div><div class="recent-sub">${esc(c.integratedSecurity ? 'Windows' : c.userName || 'SQL')}${c.hasPassword && c.rememberPassword ? t(' · parola kayıtlı') : ''}</div></div><button type="button" class="recent-x" data-forget="${esc(c.id)}" title="${t("Son bağlantıyı kaldır")}" aria-label="${t("Son bağlantıyı kaldır")}">✕</button></div>`).join('') : `<p class="hint">${t("Yok.")}</p>`;
  } catch { $('recentList').innerHTML = `<p class="hint">${t("Son bağlantılar okunamadı.")}</p>`; }
}
function openConnection() { runLoginRequest = null; afterConnect = null; $('cServer').readOnly = false; $('connectionDialogTitle').textContent = t('Bağlan'); $('connectionForm').reset(); applyConnection(state.connection); loadRecentConnections(); $('connectionDialog').showModal(); $('cServer').focus(); }
function readConnection() {
  const f = $('connectionForm').elements;
  return {server:f.server.value.trim(),integratedSecurity:f.auth.value === 'windows',userName:f.userName.value.trim(),password:f.password.value || null,
    rememberPassword:f.rememberPassword.checked,encrypt:f.encrypt.value === 'true',trustServerCertificate:f.trustServerCertificate.value === 'true'};
}
function renderNotifications() {
  $('notificationList').innerHTML = state.notifications.length ? state.notifications.map(n => `<button class="notification-item ${n.read ? '' : 'unread'}" data-run="${esc(n.runId)}"><strong>${esc(n.jobName)}</strong> ${badge(n.status)}<small>${clock(n.createdUtc)}</small></button>`).join('') : `<div class="detail-placeholder">${t("Henüz bildirim yok.")}</div>`;
}
function confirmAction(title,text,info,button,action) { $('confirmTitle').textContent = title; $('confirmText').textContent = message(text); $('confirmInfo').textContent = info; $('confirmYes').textContent = button; pendingAction = action; $('confirmDialog').showModal(); }
document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click',() => $(button.dataset.close).close()));
$('langBtn').onclick = () => { language = language === 'tr' ? 'en' : 'tr'; localStorage.setItem('jobRunnerLang',language); translateTransient(); applyLanguage(); $('saveJobBtn').textContent=t(jobSubmitting ? 'Gönderiliyor…' : 'İsteği gönder'); render(); if ($('jobDialog').open) { $('jobDialogTitle').textContent = t($('jobForm').elements.id.value ? 'Düzenle ve gönder' : 'Yeni istek'); importExec(); } if ($('connectionDialog').open) { $('connectionDialogTitle').textContent = t(runLoginRequest ? 'Çalıştırmak için giriş yap' : 'Bağlan'); loadRecentConnections(); } };
$('themeBtn').onclick = () => { const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'; document.documentElement.dataset.theme = theme; localStorage.setItem('jobRunnerTheme',theme); };
$('addBtn').onclick = () => openJob();
$('loadDemoCase').onclick = () => { openJob(); $('execInput').value = `EXEC EDWUTIL.[OPR].[EdwJobGenerator]\n    @JobName = '${$('demoCase').value}',\n    @Sql1 = 'SELECT 1 AS DemoResult';`; importExec(); };
function importExec() {
  const source = $('execInput').value;
  execValid = false; $('saveJobBtn').disabled = true;
  if (!source.trim()) { $('execImportStatus').textContent = ''; $('execImportStatus').className = ''; return; }
  try {
    const imported = parseJobExec(source), form = $('jobForm').elements;
    if (!imported.fields.jobName.trim()) throw new Error(t('@JobName değerini belirtin.'));
    Object.entries(imported.fields).forEach(([key,value]) => { form[key].value = String(value); });
    execValid = true; $('saveJobBtn').disabled = jobSubmitting;
    $('execImportStatus').className = ''; $('execImportStatus').textContent = imported.fields.jobName;
    $('jobError').hidden = true;
  } catch(error) { $('execImportStatus').className = 'error'; $('execImportStatus').textContent = message(error.message); }
}
$('execInput').addEventListener('input',importExec);
$('connectionBtn').onclick = openConnection;
$('connectionDialog').addEventListener('close',() => { afterConnect = null; runLoginRequest = null; });
$('authSelect').onchange = syncAuthRows;
$('recentList').onclick = async event => {
  const forget = event.target.closest('[data-forget]');
  if (forget) { try { await api(`/api/connections/recent/${forget.dataset.forget}`,'DELETE'); await loadRecentConnections(); } catch(error) { connectionStatus(error.message,'error'); } return; }
  const row = event.target.closest('[data-connection]'); if (row) applyConnection(recentConnections.find(c => c.id === row.dataset.connection));
};
$('recentList').onkeydown = event => { if (event.key === 'Enter' && event.target.matches('[data-connection]')) event.target.click(); };
$('search').oninput = renderTable; $('statusFilter').onchange = renderTable;
document.querySelectorAll('[data-view]').forEach(button => button.onclick = () => { view = button.dataset.view; document.querySelectorAll('[data-view]').forEach(b => b.classList.toggle('active',b === button)); renderTable(); });
document.querySelectorAll('[data-tab]').forEach(button => button.onclick = () => { detailTab = button.dataset.tab; document.querySelectorAll('[data-tab]').forEach(b => b.classList.toggle('active',b === button)); renderDetail(); });
$('jobForm').onsubmit = async event => {
  event.preventDefault(); if(jobSubmitting) return; importExec(); if (!execValid) return;
  const form = event.currentTarget, f = form.elements, data = {};
  for (const el of f) { if (!el.name || el.name === 'id' || el.name === 'stgFullInitialLoad') continue; data[el.name] = el.type === 'checkbox' ? el.checked : el.value.trim() || null; }
  if (f.id.value) data.id = f.id.value;
  data.stgFullInitialLoad = f.stgFullInitialLoad.value === '' ? null : f.stgFullInitialLoad.value === 'true';
  const submit = form.querySelector('[type="submit"]'); jobSubmitting=true;submit.disabled = true;submit.textContent=t('Gönderiliyor…');
  try { const result = await api('/api/requests','POST',data); $('jobDialog').close(); detailTab='summary'; document.querySelectorAll('[data-tab]').forEach(b=>b.classList.toggle('active',b.dataset.tab==='summary')); await refresh(); showRequest(result.requestId); toast(t('İstek gönderildi.')); }
  catch (error) { $('jobError').textContent = message(error.message); $('jobError').hidden = false; }
  finally { jobSubmitting=false;submit.disabled = !execValid;submit.textContent=t('İsteği gönder'); }
};
$('connectionForm').onsubmit = async event => {
  event.preventDefault(); $('connectBtn').disabled = true;
  try {
    const data = readConnection(); connectionStatus(t('Bağlantı kontrol ediliyor…'));
    let authorizationToken;
    if (runLoginRequest) {
      const result = await api(`/api/requests/${runLoginRequest.requestId}/authorize`,'POST',data);
      authorizationToken = result.authorizationToken;
    } else {
      await api('/api/connections/test','POST',data); await api('/api/connection','PUT',data);
    }
    const next = afterConnect; afterConnect = null;
    event.target.elements.password.value = ''; $('connectionDialog').close(); await refresh(); toast(t('Bağlantı kuruldu.'));
    next?.(authorizationToken);
  } catch (error) { connectionStatus(error.message,'error'); }
  finally { $('connectBtn').disabled = false; }
};
$('tableBody').onclick = async event => {
  const row = event.target.closest('tr[data-id]'); if (!row) return;
  selection = {kind:row.dataset.kind,id:row.dataset.id}; renderTable(); renderDetail();
  const action = event.target.closest('[data-action]'); if (!action) return;
  if (action.dataset.action === 'run-request') { const request = state.requests.find(r => String(r.requestId) === action.dataset.request); if (request) runRequest(request); return; }
  if (action.dataset.action === 'delete-request') {
    const request = state.requests.find(r => String(r.requestId) === action.dataset.request);
    if (!request || request.status !== 'Pending') return;
    confirmAction(t('İsteği sil'),t('Bu bekleyen istek kalıcı olarak silinecek. Devam etmek istiyor musunuz?'),`#${request.requestId} · ${request.jobName}`,t('Sil'),async () => {
      await api(`/api/requests/${request.requestId}`,'DELETE');
      if (selection?.kind === 'request' && selection.id === String(request.requestId)) selection = null;
      toast(t('İstek silindi.'));
    });
    return;
  }
  const job = state.jobs.find(j => j.id === action.dataset.job);
  if (action.dataset.action === 'edit') openJob(job);
  if (action.dataset.action === 'run') confirmAction(t('Job çalıştır'), t('Job şimdi çalıştırılsın mı?'),`${t("Sunucu: ")}${state.connection?.server}\nJob: ${job.jobName}`,t('Çalıştır'),async () => { await api(`/api/jobs/${job.id}/run`,'POST'); toast(t('Job kuyruğa alındı.')); });
  if (action.dataset.action === 'delete') {
    try { await api(`/api/jobs/${job.id}`,'DELETE'); selection = null; await refresh(); toast(t('Job tanımı silindi.')); }
    catch (error) { toast(error.message); }
  }
};
$('tableBody').onkeydown = event => { if (event.key === 'Enter' && event.target.matches('tr[data-id]')) event.target.click(); };
$('confirmCancel').onclick = () => $('confirmDialog').close();
$('confirmYes').onclick = async () => { $('confirmYes').disabled = true; try { await pendingAction?.(); $('confirmDialog').close(); await refresh(); } catch(error) { toast(error.message); } finally { $('confirmYes').disabled = false; } };
const DEFAULT_REQUEST_SERVER = 'PASIFIK';
function settingsStatus(text,kind = '') { $('settingsStatus').textContent = message(text); $('settingsStatus').className = `conn-status ${kind}`; }
$('settingsBtn').onclick = async () => {
  settingsStatus(''); $('settingsDialog').showModal();
  try {
    const s = await api('/api/settings'), f = $('settingsForm').elements;
    f.server.value = s.server; f.encrypt.value = String(s.encrypt); f.trustServerCertificate.value = String(s.trustServerCertificate);
  } catch (error) { settingsStatus(error.message,'error'); }
};
$('settingsConnectionBtn').onclick = () => { $('settingsDialog').close(); openConnection(); };
$('settingsDefaultBtn').onclick = () => { const f = $('settingsForm').elements; f.server.value = DEFAULT_REQUEST_SERVER; f.encrypt.value = 'false'; f.trustServerCertificate.value = 'true'; };
$('settingsForm').onsubmit = async event => {
  event.preventDefault(); const f = event.target.elements; $('settingsSaveBtn').disabled = true;
  try {
    settingsStatus(t('İstek tablosu kontrol ediliyor…'));
    const result = await api('/api/settings','PUT',{server:f.server.value.trim(),encrypt:f.encrypt.value === 'true',trustServerCertificate:f.trustServerCertificate.value === 'true'});
    await refresh();
    if (result.error) settingsStatus(t('Kaydedildi, ancak: ') + message(result.error),'error');
    else { $('settingsDialog').close(); toast(t('Ayarlar kaydedildi.')); }
  } catch (error) { settingsStatus(error.message,'error'); }
  finally { $('settingsSaveBtn').disabled = false; }
};
$('notificationDialog').addEventListener('click', event => {
  if (event.target !== $('notificationDialog')) return;
  const rect = $('notificationDialog').getBoundingClientRect();
  if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) $('notificationDialog').close();
});
$('notificationsBtn').onclick = () => { renderNotifications(); $('notificationDialog').showModal(); };
$('enableNotificationsBtn').onclick = async () => { if (!('Notification' in window)) return toast(t('Bildirimler desteklenmiyor.')); const permission = await Notification.requestPermission(); toast(permission === 'granted' ? t('Bildirimler açıldı.') : t('Bildirim izni verilmedi.')); };
$('readNotificationsBtn').onclick = async () => { try { await api('/api/notifications/read','POST'); await refresh(); } catch(error) { toast(error.message); } };
$('notificationList').onclick = event => { const button = event.target.closest('[data-run]'); if (!button) return; selection = {kind:'run',id:button.dataset.run}; $('notificationDialog').close(); renderDetail(); };
const splitter = $('panelSplitter'), jobsPanel = document.querySelector('.jobs-panel');
function resizePanels(ratio) {
  const height = document.querySelector('.workspace').getBoundingClientRect().height;
  if (height < 405) return;
  const pixels = Math.max(220,Math.min(height - 185,height * ratio));
  jobsPanel.style.flex = `0 0 ${pixels}px`;
  localStorage.setItem('jobRunnerSplit',String(pixels / height));
}
splitter.onpointerdown = event => { splitter.setPointerCapture(event.pointerId); };
splitter.onpointermove = event => {
  if (!splitter.hasPointerCapture(event.pointerId)) return;
  const bounds = document.querySelector('.workspace').getBoundingClientRect();
  resizePanels((event.clientY - bounds.top) / bounds.height);
};
splitter.onpointerup = event => { if (splitter.hasPointerCapture(event.pointerId)) splitter.releasePointerCapture(event.pointerId); };
splitter.onkeydown = event => {
  if (!['ArrowUp','ArrowDown'].includes(event.key)) return;
  event.preventDefault(); resizePanels(Number(localStorage.getItem('jobRunnerSplit') || .565) + (event.key === 'ArrowDown' ? .04 : -.04));
};
window.addEventListener('resize',() => resizePanels(Number(localStorage.getItem('jobRunnerSplit') || .565)));
resizePanels(Number(localStorage.getItem('jobRunnerSplit') || .565));
applyLanguage(); refresh(); setInterval(refresh,3000); setupDesktopNotifications();
