// DOM unit tests only; no browser or network requests are used.
const {JSDOM} = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.join(__dirname,'../wwwroot');
const dom = new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'http://127.0.0.1:5295',runScripts:'outside-only'});
const w = dom.window, d = w.document, requests = [];
let serverSession = false, settings = {server:'SRVDEV\\PASIFIK',encrypt:false,trustServerCertificate:true};
const state = {demoMode:true,jobs:[],runs:[],notifications:[],requests:[],sessionConnected:false,currentUser:'CORP\\me',connection:{server:'test-server',integratedSecurity:true}};
const json = (body,status = 200) => ({ok:status < 400,status,json:async () => body});
w.HTMLDialogElement.prototype.showModal = function() { this.setAttribute('open',''); };
w.HTMLDialogElement.prototype.close = function() { this.removeAttribute('open'); };
w.setInterval = () => 0; w.setTimeout = () => 0;
const notices = [];
w.Notification = class { static permission = 'granted'; static async requestPermission() { return 'granted'; } constructor(title,options) { notices.push(options.body); } close() {} };
w.fetch = async (url,options = {}) => {
  requests.push({url,options});
  if (url === '/api/demo/cases') return json([{name:'DEMO_SUCCESS',title:'Success',description:'Three steps'}]);
  if (url === '/api/connections/recent') return json([{id:'recent1',server:'recent-server',database:'EDWUTIL',integratedSecurity:true,encrypt:true,trustServerCertificate:false}]);
  if (url === '/api/connections/test') return json({server:'test-server',visibleJobs:5,hasAgentSession:true});
  if (url === '/api/connection' && options.method === 'PUT') { const {password,...safe} = JSON.parse(options.body); state.connection = {...safe,hasPassword:!!password}; serverSession = state.sessionConnected = true; return json({saved:true}); }
  if (url === '/api/settings' && options.method === 'PUT') { settings = JSON.parse(options.body); return json({saved:true,error:settings.server === 'BAD' ? 'İstek tablosu okunamadı: unreachable' : null}); }
  if (url === '/api/settings') return json(settings);
  if (url === '/api/requests') {
    const job = JSON.parse(options.body), requestId = state.requests.length + 1;
    state.requests.unshift({...job,requestId,requestedBy:'CORP\\me',requestedAt:new Date().toISOString(),status:'Pending'});
    return json({requestId});
  }
  const run = /^\/api\/requests\/(\d+)\/run$/.exec(url);
  if (run) {
    if (!serverSession) return json({error:'İsteği çalıştırmak için önce SQL Server\'a bağlanın.',needsConnection:true},409);
    assert.match(JSON.parse(options.body).requestId,/^[0-9a-f]{32}$/);
    const request = state.requests.find(r => r.requestId === Number(run[1])); request.status = 'Queued'; request.executedBy = 'CORP\\me';
    return json({runId:'run-' + run[1],jobId:'job-' + run[1]});
  }
  return json(structuredClone(state));
};
w.eval(fs.readFileSync(path.join(root,'localization.js'),'utf8') + '\nwindow.t=t;window.message=message;window.applyLanguage=applyLanguage;window.translateTransient=translateTransient;window.setLanguage=value=>language=value;window.getLanguage=()=>language;Object.defineProperty(window,"language",{get:()=>language,set:value=>language=value});');
w.eval(fs.readFileSync(path.join(root,'exec-import.js'),'utf8') + '\nwindow.parseJobExec = parseJobExec;');
w.eval(fs.readFileSync(path.join(root,'app.js'),'utf8') + '\nwindow.refreshForTest = refresh;');
const settle = async (n = 3) => { for (let i = 0; i < n; i++) await new Promise(resolve => setImmediate(resolve)); };
const posts = url => requests.filter(r => r.url === url && r.options.method === 'POST').length;
const runButton = id => d.querySelector(`[data-action="run-request"][data-request="${id}"]`);
(async () => {
  await settle(); assert.equal(d.getElementById('totalCount').textContent,'0');
  assert.equal(d.getElementById('demoTools').hidden,false);
  assert.ok(d.getElementById('connectionBtn').hidden,'connection picker is not shown on the main screen');
  assert.ok(d.querySelector('[data-view="requests"]').classList.contains('active'));
  assert.ok(d.querySelector('[data-view="jobs"]').hidden);
  d.getElementById('loadDemoCase').click();
  assert.ok(d.getElementById('execInput').value.includes('DEMO_SUCCESS'));
  assert.equal(d.getElementById('saveJobBtn').disabled,false);
  assert.equal(requests.filter(r=>r.options.method==='POST').length,0);
  d.getElementById('addBtn').click(); assert.ok(d.getElementById('jobDialog').open);
  assert.ok(d.getElementById('saveJobBtn').disabled);
  assert.equal(d.getElementById('saveJobBtn').textContent,'İsteği gönder');
  assert.equal(d.getElementById('schedulerSelect'),null);
  assert.equal(d.getElementById('scheduleSelect'),null);
  const form = d.getElementById('jobForm');
  for (const field of ['jobName','migPackageName','sql1','sql2','stgFullInitialLoad']) assert.equal(form.elements[field].type,'hidden');
  const exec = `EXEC EDWUTIL.OPR.EdwJobGenerator @JobName='tpesaty_LKPPushPayloadType', @Sql2='UPDATE EDW.CMP.LKPPushPayloadType SET EdwValidFrom = ''2010-01-01'''`;
  d.getElementById('execInput').value = exec;
  d.getElementById('execInput').dispatchEvent(new w.Event('input'));
  assert.equal(form.elements.jobName.value,'tpesaty_LKPPushPayloadType');
  assert.equal(requests.filter(r => r.options.method === 'POST').length,0,'import must not send requests');
  d.getElementById('execInput').value = "EXEC EdwJobGenerator @JobName='broken";
  d.getElementById('execInput').dispatchEvent(new w.Event('input'));
  assert.equal(form.elements.jobName.value,'tpesaty_LKPPushPayloadType','failed imports must preserve fields');
  assert.equal(d.getElementById('execImportStatus').className,'error');
  assert.ok(d.getElementById('saveJobBtn').disabled);
  form.dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true})); await settle(); assert.equal(posts('/api/requests'),0);
  d.getElementById('execInput').value = exec;
  d.getElementById('execInput').dispatchEvent(new w.Event('input'));
  form.dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true}));
  form.dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true})); await settle();
  assert.equal(posts('/api/requests'),1,'double click must not send twice');
  const sent = JSON.parse(requests.find(r => r.url === '/api/requests').options.body);
  assert.equal(sent.schedule,undefined); assert.equal(sent.stgFullInitialLoad,false);
  assert.equal(sent.sql2,"UPDATE EDW.CMP.LKPPushPayloadType SET EdwValidFrom = '2010-01-01'");
  assert.ok(!d.getElementById('jobDialog').open);
  assert.equal(posts('/api/jobs/execute'),0,'sending a request must not run it');
  assert.equal(d.getElementById('totalCount').textContent,'1'); assert.equal(d.querySelectorAll('.counts > span').length,3);
  assert.ok(runButton(1),'pending request can be run');
  d.querySelector('[data-tab="parameters"]').click(); assert.ok(d.getElementById('detailBody').textContent.includes("''2010-01-01''"));
  d.querySelector('[data-tab="summary"]').click();
  console.log('PASS: request form, duplicate-click guard, invalid-code guard, no scheduling fields, no direct execution');

  // Requests from other users are announced; the user's own are not.
  d.getElementById('toast').textContent = '';
  await w.refreshForTest(); assert.equal(d.getElementById('toast').textContent,'');
  state.requests.unshift({requestId:2,jobName:'OtherJob',sql1:'SELECT 1',requestedBy:'CORP\\consultant',requestedAt:new Date().toISOString(),status:'Pending'});
  await w.refreshForTest();
  assert.equal(d.getElementById('toast').textContent,'Yeni istek: OtherJob');
  assert.equal(w.localStorage.getItem('jobRunnerSeenRequest'),'2');
  const beforeOwn = notices.length;
  state.requests.push({requestId:99,jobName:'OwnRequest',requestedBy:state.currentUser,status:'Pending'});
  await w.refreshForTest();
  assert.equal(notices.length,beforeOwn,'own requests do not send desktop notifications');
  await w.refreshForTest(); assert.equal(notices.length,beforeOwn,'no duplicate desktop notification');
  state.requests = state.requests.filter(r => r.requestId !== 99);
  w.localStorage.setItem('jobRunnerSeenRequest','2');
  console.log('PASS: new request desktop notification excluding own requests and seen baseline');

  // First run in a session asks for a connection, then continues to the run confirmation.
  runButton(1).click(); await settle();
  assert.ok(d.getElementById('connectionDialog').open); assert.ok(!d.getElementById('confirmDialog').open);
  assert.ok(!requests.some(r => /\/run$/.test(r.url)),'no run before connecting');
  assert.equal(d.querySelectorAll('#recentList .recent-item').length,1);
  const connectionForm = d.getElementById('connectionForm'), cf = connectionForm.elements;
  assert.equal(cf.encrypt.value,'false'); assert.equal(cf.trustServerCertificate.value,'true');
  assert.ok(d.getElementById('rowUser').classList.contains('hidden'));
  cf.auth.value = 'sql'; cf.auth.dispatchEvent(new w.Event('change')); assert.ok(!d.getElementById('rowUser').classList.contains('hidden'));
  cf.server.value = 'draft-server'; cf.userName.value = 'draft-user'; cf.password.value = 'draft-password';
  assert.equal(d.getElementById('testConnectionBtn'),null);
  assert.ok(!requests.some(r => r.url === '/api/connection'),'opening the form must not save credentials');
  assert.equal(d.getElementById('cDatabase'),null);assert.equal(cf.database,undefined);
  cf.rememberPassword.checked = true;
  connectionForm.dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true})); await settle(5);
  const probe = requests.findLast(r => r.url === '/api/connections/test'); assert.equal(JSON.parse(probe.options.body).server,'draft-server');
  const connect = requests.findLast(r => r.url === '/api/connection'); assert.equal(JSON.parse(connect.options.body).database,undefined); assert.equal(JSON.parse(connect.options.body).rememberPassword,true);
  assert.ok(!d.getElementById('connectionDialog').open); assert.equal(cf.password.value,'');
  assert.ok(d.getElementById('confirmDialog').open,'run confirmation follows the connection');
  d.getElementById('confirmYes').click(); await settle(5);
  assert.equal(posts('/api/requests/1/run'),1);
  assert.equal(state.requests.find(r => r.requestId === 1).status,'Queued'); assert.equal(runButton(1),null);
  // Still connected: the next run goes straight to confirmation.
  runButton(2).click(); await settle();
  assert.ok(!d.getElementById('connectionDialog').open); assert.ok(d.getElementById('confirmDialog').open);
  d.getElementById('confirmYes').click(); await settle(5);
  assert.equal(posts('/api/requests/2/run'),1);
  // Session lost on the server: the run asks for a connection again.
  serverSession = false;
  state.requests.unshift({requestId:3,jobName:'ThirdJob',sql1:'SELECT 1',requestedBy:'CORP\\consultant',requestedAt:new Date().toISOString(),status:'Pending'});
  await w.refreshForTest(); runButton(3).click(); await settle();
  d.getElementById('confirmYes').click(); await settle(5);
  assert.ok(d.getElementById('connectionDialog').open,'expired session asks for a connection');
  d.getElementById('connectionDialog').close();
  // A second request for a job that is already running cannot be started.
  state.requests.unshift({requestId:4,jobName:' TPESATY_lkppushpayloadtype ',sql1:'SELECT 1',requestedBy:'CORP\\consultant',requestedAt:new Date().toISOString(),status:'Pending'});
  await w.refreshForTest(); assert.ok(!runButton(4).disabled,'different requests may share a job name');
  // Requests tab: waiting and running only. History: every row, most recent activity first.
  state.requests.find(r => r.requestId === 2).status = 'Succeeded';
  await w.refreshForTest();
  const shown = () => [...d.querySelectorAll('#tableBody tr')].map(tr => tr.dataset.id);
  assert.deepEqual(shown().sort(),['1','3','4']);
  state.requests.find(r => r.requestId === 1).executedAt = new Date(Date.now() + 60000).toISOString();
  await w.refreshForTest(); d.querySelector('[data-view="history"]').click();
  assert.deepEqual(shown(),['1','4','3','2']);
  assert.ok(d.getElementById('tableHead').textContent.includes('Çalıştıran'));
  // Failed attempts can be run again from the history; running and succeeded ones cannot.
  assert.equal(runButton(2),null); assert.equal(runButton(1),null);
  state.requests.find(r => r.requestId === 2).status = 'Failed'; await w.refreshForTest();
  assert.equal(runButton(2).textContent,'↻ Tekrar çalıştır'); assert.ok(!runButton(2).disabled);
  runButton(2).click(); await settle();
  assert.ok(d.getElementById('confirmDialog').open); assert.ok(d.getElementById('confirmText').textContent.includes('Önceki deneme'));
  d.getElementById('confirmDialog').close();
  d.querySelector('[data-view="requests"]').click();
  console.log('PASS: shared job names, requests tab shows waiting/running, history shows all newest first');
  console.log('PASS: connection required once per session, recent connections, auth rows, password clearing, no database field');

  // Settings: request server is editable, defaults to SRVDEV\PASIFIK, and reports an unreachable server.
  d.getElementById('settingsBtn').click(); await settle();
  const sf = d.getElementById('settingsForm').elements;
  assert.ok(d.getElementById('settingsDialog').open); assert.equal(sf.server.value,'SRVDEV\\PASIFIK');
  sf.server.value = 'BAD'; d.getElementById('settingsForm').dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true})); await settle(5);
  assert.equal(settings.server,'BAD'); assert.ok(d.getElementById('settingsDialog').open);
  assert.ok(d.getElementById('settingsStatus').textContent.includes('unreachable'));
  d.getElementById('settingsDefaultBtn').click(); assert.equal(sf.server.value,'SRVDEV\\PASIFIK');
  d.getElementById('settingsForm').dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true})); await settle(5);
  assert.equal(settings.server,'SRVDEV\\PASIFIK'); assert.ok(!d.getElementById('settingsDialog').open);
  console.log('PASS: settings dialog, default request server, unreachable server message');

  d.getElementById('themeBtn').click(); assert.equal(d.documentElement.dataset.theme,'dark');
  d.getElementById('search').value = 'not-present'; d.getElementById('search').dispatchEvent(new w.Event('input')); assert.equal(d.getElementById('rowCount').textContent,'0 kayıt');
  // An untrusted name is rendered as text, never executable markup.
  state.requests[0].jobName = '<img src=x onerror=alert(1)>'; d.getElementById('search').value = ''; await w.refreshForTest();
  assert.ok(!d.querySelector('#tableBody img')); assert.ok(d.getElementById('tableBody').textContent.includes('<img'));
  console.log('PASS: theme, search, escaping');

  assert.equal(d.getElementById('themeBtn').nextElementSibling.id,'langBtn');
  d.getElementById('addBtn').click();
  const source = "EXEC EdwJobGenerator @JobName='TürkçeJob', @Sql1='SELECT 1'";
  d.getElementById('execInput').value=source;
  d.getElementById('execInput').dispatchEvent(new w.Event('input'));
  d.getElementById('langBtn').click();
  assert.equal(d.documentElement.lang,'en');
  assert.equal(w.localStorage.getItem('jobRunnerLang'),'en');
  const reopened = new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'http://127.0.0.1:5295',runScripts:'outside-only'});
  reopened.window.localStorage.setItem('jobRunnerLang',w.localStorage.getItem('jobRunnerLang'));
  reopened.window.eval(fs.readFileSync(path.join(root,'localization.js'),'utf8') + '\napplyLanguage();');
  assert.equal(reopened.window.document.documentElement.lang,'en');
  assert.equal(reopened.window.document.getElementById('addBtn').textContent,'New request');
  reopened.window.close();
  assert.equal(d.getElementById('langLabel').textContent,'TR');
  assert.equal(d.getElementById('addBtn').textContent,'New request');
  assert.equal(d.getElementById('statusFilter').options[0].textContent,'All statuses');
  assert.equal(d.getElementById('saveJobBtn').textContent,'Send request');
  assert.equal(d.getElementById('execInput').value,source);
  assert.ok(d.getElementById('tableHead').textContent.includes('Job name'));
  assert.ok(d.getElementById('tableHead').textContent.includes('Requested by'));
  d.getElementById('execInput').value="EXEC EdwJobGenerator @JobName='broken";
  d.getElementById('execInput').dispatchEvent(new w.Event('input'));
  assert.equal(d.getElementById('execImportStatus').textContent,'Unclosed string literal.');
  assert.equal(w.message('Bilinmeyen parametre: @oops'),'Unknown parameter: @oops');
  assert.equal(w.message('Prosedür job oluşturamadı: SQL raw error'),'The procedure could not create the job: SQL raw error');
  d.getElementById('langBtn').click();
  assert.equal(d.documentElement.lang,'tr');
  assert.equal(d.getElementById('addBtn').textContent,'Yeni istek');
  assert.equal(w.localStorage.getItem('jobRunnerLang'),'tr');
  console.log('PASS: TR/EN switch, exact button placement, persisted language, static/dynamic translations, SQL preservation and parser errors');
  dom.window.close();
})().catch(error => { console.error(error); process.exitCode = 1; dom.window.close(); });
