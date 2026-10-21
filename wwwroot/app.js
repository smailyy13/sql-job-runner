'use strict';
const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const labels = {Queued:'Kuyrukta',Submitting:'Başlatılıyor',Starting:'Agent bekleniyor',Running:'Çalışıyor',Succeeded:'Başarılı',Failed:'Hatalı',Canceled:'İptal edildi',Unknown:'Durum belirsiz',Unverified:'İzleme kapatıldı',Retry:'Yeniden deneme',ready:'Hazır'};
const active = status => ['Queued','Submitting','Starting','Running','Unknown'].includes(status);
const clock = value => value ? new Date(value).toLocaleString((language === 'tr' ? 'tr-TR' : 'en-GB'),{timeZone:'Europe/Istanbul',day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit',second:'2-digit'}) : '—';
const badge = status => `<span class="pill ${esc(status)}">${esc(t(labels[status] || status))}</span>`;
let state = {jobs:[],runs:[],notifications:[],connection:null}, view = 'jobs', detailTab = 'summary', selection = null;
let refreshing = false, toastTimer, pendingAction = null, firstLoad = true;
let execValid = false;
let jobSubmitting = false, executionRequestId = '';
const deliveredNotifications = new Set();
document.documentElement.dataset.theme = localStorage.getItem('jobRunnerTheme') || 'light';
function toast(text) { $('toast').textContent = message(text); $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true,6500); }
async function api(path, method = 'GET', body) {
  const response = await fetch(path,{method,headers:body === undefined ? {} : {'Content-Type':'application/json'},body:body === undefined ? undefined : JSON.stringify(body)});
  const data = response.status === 204 ? null : await response.json();
  if (!response.ok) throw new Error(data?.error || t('İstek tamamlanamadı.'));
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
    $('healthText').textContent = t('Hazır');
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
  if($('jobDialog').open) $('executionTarget').textContent=state.connection?.server || t('Bağlantı seçilmedi');
  $('totalCount').textContent = state.jobs.length;
  $('activeCount').textContent = state.runs.filter(r => active(r.status) && r.status !== 'Unknown').length;
  $('successCount').textContent = state.runs.filter(r => r.status === 'Succeeded').length;
  $('attentionCount').textContent = state.runs.filter(r => ['Failed','Unknown'].includes(r.status)).length;
  $('unreadCount').textContent = state.notifications.filter(n => !n.read).length;
  renderTable(); renderDetail();
  if ($('notificationDialog').open) renderNotifications();
}
function renderTable() {
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
  const run = selection.kind === 'run' ? state.runs.find(r => r.id === selection.id) : latest(selection.id);
  const job = selection.kind === 'job' ? state.jobs.find(j => j.id === selection.id) : run?.definition;
  return {run,job};
}
function renderDetail() {
  const {run,job} = chosen();
  $('detailTitle').textContent = job?.jobName || t('Job detayları');
  if (!job) { $('detailBody').innerHTML = `<div class="detail-placeholder">${t("Bir kayıt seçin.")}</div>`; return; }
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
function script(job,run) {
  const params = [['MIGPackageName',job.migPackageName],['JobName',run?.generatorJobName || t('<benzersiz çalışma etiketi>')],['StgPackageName',job.stgPackageName],['StgFullInitialLoad',job.stgFullInitialLoad === true ? 'YES' : 'NO'],['SkeyPackageName',job.skeyPackageName],['LryPackageName',job.lryPackageName],['Sql1',job.sql1],['EdwPackageName',job.edwPackageName],['Sql2',job.sql2],['EdwdmPackageName',job.edwdmPackageName]];
  const sqlValue = v => v == null || v === '' ? 'NULL' : "'" + v.replace(/'/g,"''") + "'";
  return 'EXEC EDWUTIL.[OPR].[EdwJobGenerator]\n' + params.map(([k,v]) => `    @${k.padEnd(18)} = ${sqlValue(v)}`).join(',\n') + ';';
}
function openJob(job) {
  jobSubmitting=false; executionRequestId=Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,'0')).join('');
  $('jobForm').reset(); $('jobError').hidden = true;
  $('executionTarget').textContent=state.connection?.server || t('Bağlantı seçilmedi');
  $('execImportStatus').textContent = ''; $('execImportStatus').className = '';
  for (const el of $('jobForm').elements) {
    if (!el.name) continue;
    const v = job?.[el.name];
    if (el.type === 'checkbox') el.checked = v ?? true;
    else el.value = v == null ? (el.name === 'stgFullInitialLoad' ? 'false' : '') : String(v);
  }
  $('jobDialogTitle').textContent = job ? t('Düzenle ve çalıştır') : t('Yeni job');
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
$('langBtn').onclick = () => { language = language === 'tr' ? 'en' : 'tr'; localStorage.setItem('jobRunnerLang',language); translateTransient(); applyLanguage(); $('saveJobBtn').textContent=t(jobSubmitting ? 'Başlatılıyor…' : 'Çalıştır'); render(); if ($('jobDialog').open) { $('jobDialogTitle').textContent = t($('jobForm').elements.id.value ? 'Düzenle ve çalıştır' : 'Yeni job'); importExec(); } if ($('connectionDialog').open) loadRecentConnections(); };
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
  const submit = form.querySelector('[type="submit"]'); jobSubmitting=true;submit.disabled = true;submit.textContent=t('Başlatılıyor…');
  try { const result = await api('/api/jobs/execute','POST',{job:data,requestId:executionRequestId}); $('jobDialog').close(); selection = {kind:'run',id:result.runId}; detailTab='summary'; document.querySelectorAll('[data-tab]').forEach(b=>b.classList.toggle('active',b.dataset.tab==='summary')); await refresh(); toast(t('Job kuyruğa alındı.')); }
  catch (error) { $('jobError').textContent = message(error.message); $('jobError').hidden = false; }
  finally { jobSubmitting=false;submit.disabled = !execValid;submit.textContent=t('Çalıştır'); }
};
$('connectionForm').onsubmit = async event => {
  event.preventDefault(); $('connectBtn').disabled = true;
  try {
    const data = readConnection(); connectionStatus(t('Bağlantı kontrol ediliyor…'));
    await api('/api/connections/test','POST',data); await api('/api/connection','PUT',data);
    event.target.elements.password.value = ''; $('connectionDialog').close(); await refresh(); toast(t('Bağlantı kuruldu.'));
  } catch (error) { connectionStatus(error.message,'error'); }
  finally { $('connectBtn').disabled = false; }
};
$('tableBody').onclick = async event => {
  const row = event.target.closest('tr[data-id]'); if (!row) return;
  selection = {kind:row.dataset.kind,id:row.dataset.id}; renderTable(); renderDetail();
  const action = event.target.closest('[data-action]'); if (!action) return;
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
applyLanguage(); refresh(); setInterval(refresh,3000);
