using System.Text.Json;

namespace SqlJobRunner;

public sealed class JobWorker(Store store, IAgentClient agent, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A crash during submission has an ambiguous result. Never resubmit it.
        foreach (var run in store.Runs().Where(r => r.Status == "Submitting"))
        {
            run.Status = "Unknown"; run.Message = "Uygulama başlatma sırasında kapanmış. Yeniden gönderilmeden Agent üzerinden kontrol ediliyor."; store.SaveRun(run);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var run in store.Runs().Where(r => RunStates.Active(r.Status)).OrderBy(r => r.CreatedUtc))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await Process(run, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Job worker döngüsü tamamlanamadı"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
    internal async Task Process(RunRecord run, CancellationToken ct)
    {
        var submitted = run.Status != "Queued";
        try
        {
            var connection = JsonSerializer.Deserialize<ConnectionInput>(run.ConnectionSnapshot!, Store.Json)!;
            if (!submitted)
            {
                await agent.Prepare(run, connection, ct);
                run.Status = "Submitting"; run.Message = "Prosedür çağrısı gönderiliyor."; store.SaveRun(run);
                submitted = true;
                await agent.Submit(run, connection, ct);
                run.Status = "Starting"; run.Message = "Prosedür döndü. Oluşturulan job eşleştiriliyor."; store.SaveRun(run);
            }
            if (run.ServerSubmittedAt is null)
            {
                run.Status = "Unknown"; run.Message = "Eşleştirme zamanı bulunamadı; elle inceleme gerekiyor."; store.SaveRun(run); return;
            }
            if (run.GeneratorJobName is not null)
            {
                if (run.AgentJobId is null && !await agent.Resolve(run, connection, ct))
                {
                    if (RunStates.Active(run.Status)) store.SaveRun(run); else store.Complete(run);
                    return;
                }
                if (!run.StartRequested)
                {
                    // Persist job_id and intent BEFORE sending a non-idempotent Agent start request.
                    run.StartRequested = true; run.Status = "Starting"; run.Message = "Oluşturulan job Agent üzerinden başlatılıyor.";
                    if (!store.SaveRun(run)) return;
                    await agent.Start(run, connection, ct);
                }
            }
            await agent.Poll(run, connection, ct);
            if (!RunStates.Active(run.Status)) store.Complete(run); else store.SaveRun(run);
        }
        catch (Exception ex)
        {
            // SQL errors/timeouts can occur after the server accepted the command.
            // Once submitted, monitor rather than retrying a possibly destructive job.
            run.Status = submitted ? "Unknown" : "Failed";
            run.Message = submitted ? "Başlatma/izleme sonucu belirsiz; tekrar gönderilmeyecek. " + ex.Message : ex.Message;
            if (submitted) store.SaveRun(run); else store.Complete(run);
            if (!ct.IsCancellationRequested) logger.LogWarning("Çalışma {Id}: {Message}", run.Id, ex.Message);
        }
    }
}
