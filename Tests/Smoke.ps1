param([string]$BaseUrl = 'http://127.0.0.1:5295')
$ErrorActionPreference = 'Stop'
# Run only against a disposable --DataDirectory instance. Never submits a SQL job.
$initial = Invoke-RestMethod "$BaseUrl/api/state"
if ($initial.jobs.Count -ne 0 -or $initial.connection) { throw 'Smoke testi bos, gecici veri klasoru gerektirir.' }
$executeBody=@{job=@{jobName='execute_smoke';sql1='SELECT 1;'};requestId=[guid]::NewGuid().ToString('N')} | ConvertTo-Json
try {
    $null=Invoke-RestMethod "$BaseUrl/api/jobs/execute" -Method Post -ContentType application/json -Body $executeBody
    throw 'Missing connection must reject execution.'
} catch [Microsoft.PowerShell.Commands.HttpResponseException] {
    if ([int]$_.Exception.Response.StatusCode -ne 400) { throw }
}
$afterRejected=Invoke-RestMethod "$BaseUrl/api/state"
if ($afterRejected.jobs.Count -ne 0 -or $afterRejected.runs.Count -ne 0) { throw 'Rejected execution must not save partial records.' }
$page = Invoke-WebRequest $BaseUrl
if ($page.Content -notmatch 'SQL Job Runner') { throw 'HTML bulunamadi.' }
foreach ($asset in 'app.js','app.css','theme.css') { $null = Invoke-WebRequest "$BaseUrl/$asset" }
$connection = @{server='invalid-smoke-server'; integratedSecurity=$true; encrypt=$true; trustServerCertificate=$false} | ConvertTo-Json
$null = Invoke-RestMethod "$BaseUrl/api/connection" -Method Put -ContentType application/json -Body $connection
$job = @{jobName='smoke_definition'; sql1='SELECT 1;'} | ConvertTo-Json
$saved = Invoke-RestMethod "$BaseUrl/api/jobs" -Method Post -ContentType application/json -Body $job
$state = Invoke-RestMethod "$BaseUrl/api/state"
if ($state.jobs.Count -ne 1 -or $state.runs.Count -ne 0) { throw 'Kaydetme yanlislikla calisma olusturdu.' }
if ($state.connection.PSObject.Properties.Name -contains 'password') { throw 'Parola API yanitinda var.' }
try {
    $null = Invoke-WebRequest "$BaseUrl/api/jobs" -Method Post -ContentType application/json -Body $job -Headers @{Origin='https://untrusted.example'}
    throw 'Cross-origin istegi reddedilmedi.'
} catch [Microsoft.PowerShell.Commands.HttpResponseException] {
    if ([int]$_.Exception.Response.StatusCode -ne 403) { throw }
}
$null = Invoke-RestMethod "$BaseUrl/api/jobs/$($saved.id)" -Method Delete
$state = Invoke-RestMethod "$BaseUrl/api/state"
if ($state.jobs.Count -ne 0 -or $state.runs.Count -ne 0) { throw 'Silme/kayit sonucu beklenmiyor.' }
Write-Output 'PASS: HTML/assets, connection redaction, job save/list/delete, no implicit submission, cross-origin protection'
