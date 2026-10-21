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
let afterConnect = null;
const deliveredNotifications = new Set();
const newRequestId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,'0')).join('');
const stored = {get:key => { try { return localStorage.getItem(key); } catch { return null; } },set:(key,value) => { try { localStorage.setItem(key,value); } catch {} }};
document.documentElement.dataset.theme = localStorage.getItem('jobRunnerTheme') || 'light';
function desktopNotify(body,onclick) {
  if (!('Notification' in window) || Notification.permission !== 'granted') return;
  const notice = new Notification('SQL Job Runner',{body});
  notice.onclick = () => { window.focus(); onclick?.(); notice.close(); };
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
// Requests from others that arrived since this browser last looked; the first visit only records the baseline.
function announceRequests() {
  if (!state.requests) return;
  const latestId = Math.max(0,...state.requests.map(r => r.requestId));
  const seen = stored.get('jobRunnerSeenRequest');
  if (seen !== null) {
    const fresh = state.requests.filter(r => r.requestId > Number(seen) && r.requestedBy !== state.currentUser);
    if (fresh.length === 1) { toast(`${t('Yeni istek')}: ${fresh[0].jobName}`); desktopNotify(`${t('Yeni istek')}: ${fresh[0].jobName} (${fresh[0].requestedBy})`,() => showRequest(fresh[0].requestId)); }
    else if (fresh.length > 1) { toast(`${fresh.length} ${t('yeni istek')}`); desktopNotify(`${fresh.length} ${t('yeni istek')}`,() => showRequest(fresh[0].requestId)); }
  }
  if (seen === null || latestId > Number(seen)) stored.set('jobRunnerSeenRequest',String(latestId));
}
function toast(text) { $('toast').textContent = message(text); $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true,6500); }
async function api(path, method = 'GET', body) {
  const response = await fetch(path,{method,headers:body === undefined ? {} : {'Content-Type':'application/json'},body:body === undefined ? undefined : JSON.stringify(body)});
  const data = response.status === 204 ? null : await response.json();
  if (!response.ok) { const error = new Error(data?.error || t('İstek tamamlanamadı.')); error.data = data; throw error; }
  return data;
}
async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try {
    state = await api('/api/state');
    if (state.demoMode && $('demoTools').hidden) {
      const cases = await api('/api/demo/cases');
      $('demoCase').innerHTML = cases.map(c => `<option value="${esc(c.name)}">${esc(c.title)}</option>`).join('');
      $('demoCase').onchange = () => $('demoDescription').textContent = cases.find(c => c.name === $('demoCase').value)?.description || '';
      $('demoCase').onchange(); $('demoTools').hidden = false;
    }
    $('healthText').textContent = state.requestsError ? message(state.requestsError) : t('Hazır');
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
  $('connectionLabel').textContent = state.connection?.server || t('Bağlantı seçin');
  if($('jobDialog').open) $('executionTarget').textContent=t('İstek tabloya kaydedilir; çalıştırılmaz.');
  const requests = state.requests || [];
  $('totalCount').textContent = requests.length;
  $('pendingCount').textContent = requests.filter(r => r.status === 'Pending').length;
  $('activeCount').textContent = requests.filter(r => requestActive(r.status) && r.status !== 'Unknown').length;
  $('successCount').textContent = requests.filter(r => r.status === 'Succeeded').length;
  $('attentionCount').textContent = requests.filter(r => ['Failed','Unknown','Canceled'].includes(r.status)).length;
  $('unreadCount').textContent = state.notifications.filter(n => !n.read).length;
  renderTable(); renderDetail();
  if ($('notificationDialog').open) renderNotifications();
}
function renderRequests() {
  const filter = $('statusFilter').value, query = $('search').value.toLocaleLowerCase('tr');
  const rows = (state.requests || []).filter(r => JSON.stringify(r).toLocaleLowerCase('tr').includes(query) && (!filter || (filter === 'active' ? requestActive(r.status) : r.status === filter)));
  $('tableHead').innerHTML = `<tr><th>#</th><th>${t("Job adı")}</th><th>${t("İsteyen")}</th><th>${t("İstek zamanı")}</th><th>${t("Durum")}</th><th>${t("Aktif adım")}</th><th>${t("İşlemler")}</th></tr>`;
  $('tableBody').innerHTML = rows.map(r => {
    const id = String(r.requestId);
    return `<tr data-kind="request" data-id="${esc(id)}" tabindex="0" class="${selection?.kind === 'request' && selection.id === id ? 'selected' : ''}">
      <td class="job-name">${esc(id)}</td><td><div class="job-name">${esc(r.jobName)}</div>${r.agentJobName ? `<div class="job-sub">${esc(r.agentJobName)}</div>` : ''}</td><td>${esc(r.requestedBy)}</td>
      <td>${clock(r.requestedAt)}</td><td>${badge(r.status)}</td><td>${esc(message(r.currentStep || '—'))}</td>
      <td><div class="row-actions">${r.status === 'Pending' ? `<button class="btn primary" data-action="run-request" data-request="${esc(id)}">${t("▷ Çalıştır")}</button>` : `<button class="btn" data-action="detail">${t("Detay")}</button>`}</div></td></tr>`;
  }).join('');
  $('emptyState').hidden = rows.length > 0;
  $('emptyState').querySelector('h2').textContent = state.requests?.length ? t('Kayıt bulunamadı.') : t('Henüz istek yok.');
  $('rowCount').textContent = `${rows.length} ${t("kayıt")}`;
}
function renderTable() {
  if (view === 'requests') return renderRequests();
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
  const {run,job,request} = chosen();
  $('detailTitle').textContent = job?.jobName || t('Job detayları');
  if (!job) { $('detailBody').innerHTML = `<div class="detail-placeholder">${t("Bir kayıt seçin.")}</div>`; return; }
  if (request && detailTab !== 'parameters' && !(run && detailTab === 'steps')) return renderRequestDetail(request,run);
  if (detailTab === 'parameters') { $('detailBody').innerHTML = `<pre class="code">${esc(script(job,run))}</pre>`; return; }
  if (detailTab === 'steps') {
    const steps = run?.steps || [];
    $('detailBody').innerHTML = `${run?.currentStep ? `<div class="message-box">${esc(message(run.currentStep))}</div>` : ''}${steps.length ? `<table class="steps-table"><thead><tr><th>${t("Adım")}</th><th>${t("Durum")}</th><th>${t("Süre")}</th><th>${t("Mesaj")}</th></tr></thead><tbody>${steps.map(s => `<tr><td>${s.stepId}. ${esc(s.name)}</td><td>${badge(({0:'Failed',1:'Succeeded',2:'Retry',3:'Canceled',4:'Running'})[s.status])}</td><td>${String(s.duration).padStart(6,'0').replace(/(\d+)(\d{2})(\d{2})$/,'$1:$2:$3')}</td><td class="step-message">${esc(s.message)}</td></tr>`).join('')}</tbody></table>` : `<div class="detail-placeholder">${t("Adım kaydı yok.")}</div>`}`;
    return;
  }
  if (detailTab === 'logs') {
    $('detailBody').innerHTML = `<pre class="code">${esc(run ? `[${clock(run.createdUtc)}] ${t("Çalışma kaydedildi.")}\n${t("Durum")}: ${t(labels[run.status])}\n${run.currentStep ? t('Aktif adım')+': '+run.currentStep+'\n' : ''}\n${message(run.message)}\n\n${(run.steps || []).map(s => `[${t("Adım")} ${s.stepId}: ${s.name}]\n${s.message}`).join('\n\n')}` : t('Henüz çalışma yok.'))}</pre>`; return;
  }
  const status = selection.kind === 'job' ? jobStatus(job) : run.status;
  const entries = [[t('Durum'),badge(status)],['Agent job',esc(run?.agentJobName || '—')],[t('Başlangıç'),clock(run?.createdUtc)],[t('Bitiş'),clock(run?.finishedUtc)],[t('Süre'),duration(run)],[t('Aktif adım'),esc(message(run?.currentStep || '—'))]];
  $('detailBody').innerHTML = `<div class="detail-grid">${entries.map(([label,value]) => `<div><label>${label}</label><div class="detail-value">${value}</div></div>`).join('')}</div>${run && ['Failed','Unknown','Unverified'].includes(status) ? `<div class="message-box warning">${esc(message(run.message))}</div>` : ''}${run?.status === 'Unknown' ? `<button class="btn sm close-tracking" id="closeTrackingBtn">${t("İzlemeyi kapat")}</button>` : ''}`;
  if ($('closeTrackingBtn')) $('closeTrackingBtn').onclick = () => confirmAction(t('İzlemeyi kapat'),t('SQL Server Agent üzerindeki gerçek job durdurulmaz. İzleme sonlandırılır ve yeni çalıştırma kilidi kaldırılır. Yeniden çalıştırmadan önce Agent durumunu kontrol edin.'),job.jobName,t('İzlemeyi kapat'),async () => { await api(`/api/runs/${run.id}/close-tracking`,'POST'); toast(t('İzleme kapatıldı. Job sonucu doğrulanmadı.')); });
}
// Everyone sees the request table state; step details exist only where the request was run.
function renderRequestDetail(request,run) {
  if (detailTab === 'steps') { $('detailBody').innerHTML = `${request.currentStep ? `<div class="message-box">${esc(message(request.currentStep))}</div>` : ''}<div class="detail-placeholder">${t("Adım ayrıntıları isteği çalıştıran bilgisayarda görünür.")}</div>`; return; }
  if (detailTab === 'logs') { $('detailBody').innerHTML = `<pre class="code">${esc(`[${clock(request.requestedAt)}] ${t("İstek oluşturuldu.")} ${request.requestedBy}\n${request.executedAt ? `[${clock(request.executedAt)}] ${t("Çalıştıran")}: ${request.executedBy || '—'}\n` : ''}${t("Durum")}: ${t(labels[request.status] || request.status)}\n\n${message(request.statusMessage || '')}`)}</pre>`; return; }
  const entries = [[t('Durum'),badge(request.status)],[t('İsteyen'),esc(request.requestedBy)],[t('İstek zamanı'),clock(request.requestedAt)],[t('Çalıştıran'),esc(request.executedBy || '—')],['Agent job',esc(request.agentJobName || '—')],[t('Başlangıç'),clock(request.executedAt)],[t('Bitiş'),clock(request.finishedAt)],[t('Aktif adım'),esc(message(request.currentStep || '—'))]];
  $('detailBody').innerHTML = `<div class="detail-grid">${entries.map(([label,value]) => `<div><label>${label}</label><div class="detail-value">${value}</div></div>`).join('')}</div>${request.statusMessage && ['Failed','Unknown','Unverified','Canceled'].includes(request.status) ? `<div class="message-box warning">${esc(message(request.statusMessage))}</div>` : ''}${request.status === 'Pending' ? `<button class="btn primary sm close-tracking" id="runRequestBtn">${t("▷ Çalıştır")}</button>` : ''}${run?.status === 'Unknown' ? `<button class="btn sm close-tracking" id="closeTrackingBtn">${t("İzlemeyi kapat")}</button>` : ''}`;
  if ($('runRequestBtn')) $('runRequestBtn').onclick = () => runRequest(request);
  if ($('closeTrackingBtn')) $('closeTrackingBtn').onclick = () => confirmAction(t('İzlemeyi kapat'),t('SQL Server Agent üzerindeki gerçek job durdurulmaz. İzleme sonlandırılır ve yeni çalıştırma kilidi kaldırılır. Yeniden çalıştırmadan önce Agent durumunu kontrol edin.'),request.jobName,t('İzlemeyi kapat'),async () => { await api(`/api/runs/${run.id}/close-tracking`,'POST'); toast(t('İzleme kapatıldı. Job sonucu doğrulanmadı.')); });
}
// The first run in an application session asks for a SQL Server connection; later runs reuse it.
function runRequest(request) {
  if (!state.sessionConnected) {
    openConnection(); afterConnect = () => runRequest(request);
    connectionStatus(t('İsteği çalıştırmak için SQL Server bağlantısı gerekli.'));
    return;
  }
  confirmAction(t('İsteği çalıştır'),t('İstek şimdi çalıştırılsın mı?'),`${t("Sunucu: ")}${state.connection?.server}\nJob: ${request.jobName}\n${t("İsteyen")}: ${request.requestedBy}`,t('Çalıştır'),async () => {
    try { await api(`/api/requests/${request.requestId}/run`,'POST',{requestId:newRequestId()}); }
    catch (error) {
      if (!error.data?.needsConnection) throw error;
      state.sessionConnected = false; $('confirmDialog').close(); runRequest(request); return;
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
  $('executionTarget').textContent=t('İstek tabloya kaydedilir; çalıştırılmaz.');
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
  f.server.value = c?.server || ''; f.auth.value = c?.integratedSecurity === false ? 'sql' : 'windows';
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
function openConnection() { $('connectionForm').reset(); applyConnection(state.connection); loadRecentConnections(); $('connectionDialog').showModal(); $('cServer').focus(); }
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
$('langBtn').onclick = () => { language = language === 'tr' ? 'en' : 'tr'; localStorage.setItem('jobRunnerLang',language); translateTransient(); applyLanguage(); $('saveJobBtn').textContent=t(jobSubmitting ? 'Gönderiliyor…' : 'İsteği gönder'); render(); if ($('jobDialog').open) { $('jobDialogTitle').textContent = t($('jobForm').elements.id.value ? 'Düzenle ve gönder' : 'Yeni istek'); importExec(); } if ($('connectionDialog').open) loadRecentConnections(); };
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
$('connectionDialog').addEventListener('close',() => { afterConnect = null; });
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
    await api('/api/connections/test','POST',data); await api('/api/connection','PUT',data);
    const next = afterConnect; afterConnect = null;
    event.target.elements.password.value = ''; $('connectionDialog').close(); await refresh(); toast(t('Bağlantı kuruldu.'));
    next?.();
  } catch (error) { connectionStatus(error.message,'error'); }
  finally { $('connectBtn').disabled = false; }
};
$('tableBody').onclick = async event => {
  const row = event.target.closest('tr[data-id]'); if (!row) return;
  selection = {kind:row.dataset.kind,id:row.dataset.id}; renderTable(); renderDetail();
  const action = event.target.closest('[data-action]'); if (!action) return;
  if (action.dataset.action === 'run-request') { const request = state.requests.find(r => String(r.requestId) === action.dataset.request); if (request) runRequest(request); return; }
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
const DEFAULT_REQUEST_SERVER = 'SRVDEV\\PASIFIK';
function settingsStatus(text,kind = '') { $('settingsStatus').textContent = message(text); $('settingsStatus').className = `conn-status ${kind}`; }
$('settingsBtn').onclick = async () => {
  settingsStatus(''); $('settingsDialog').showModal();
  try {
    const s = await api('/api/settings'), f = $('settingsForm').elements;
    f.server.value = s.server; f.encrypt.value = String(s.encrypt); f.trustServerCertificate.value = String(s.trustServerCertificate);
  } catch (error) { settingsStatus(error.message,'error'); }
};
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
