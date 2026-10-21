using System.Text.Json;

namespace SqlJobRunner;

public sealed class DemoAgentClient : IAgentClient
{
    public static readonly object[] Cases = [
        new { name = "DEMO_SUCCESS", title = "Başarılı / Success", description = "~20 sn: üç adım tamamlanır, başarı bildirimi gelir." },
        new { name = "DEMO_FAILURE", title = "Hata / Failure", description = "~15 sn: ikinci adım hata verir; geçmiş ve hata mesajını inceleyin." },
        new { name = "DEMO_CANCEL", title = "İptal / Canceled", description = "~15 sn: Agent tarafından iptal edilmiş çalışma gösterilir." },
        new { name = "DEMO_RETRY", title = "Yeniden deneme / Retry", description = "~30 sn: ikinci adım yeniden denenir ve başarılı biter." },
        new { name = "DEMO_SLOW", title = "Uzun çalışma / Long run", description = "~65 sn: aktif adımları izleyin, aynı job için ikinci çalıştırmanın engellendiğini deneyin." },
        new { name = "DEMO_UNKNOWN", title = "Belirsiz / Unknown", description = "~15 sn: durum belirsiz kalır. Mesajı okuyup İzlemeyi kapat düğmesini deneyin." },
        new { name = "DEMO_GENERATOR_ERROR", title = "Job oluşturma hatası / Generator error", description = "Prosedürün job oluşturamadığı durumu ve hata bildirimini gösterir." }
    ];
    public static object Test(ConnectionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Server)) throw new ArgumentException("Sunucu zorunlu.");
        if (input.Server.Equals("DEMO-FAIL", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Demo: connection refused (simulated).");
        return new { server = input.Server, visibleJobs = 0, hasAgentSession = true };
    }
    public Task Prepare(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        Test(input);
        GeneratorContract.Validate(run.Definition);
        run.GeneratorJobName = GeneratorContract.Token(run);
        run.ServerSubmittedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }
    public Task Submit(RunRecord run, ConnectionInput input, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> Resolve(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        if (run.Definition.JobName.Equals("DEMO_GENERATOR_ERROR", StringComparison.OrdinalIgnoreCase))
        {
            run.Status = "Failed";
            run.Message = "Prosedür job oluşturamadı: Demo @owner_login_name is invalid (simulated).";
            return Task.FromResult(false);
        }
        run.AgentJobId = run.Id;
        run.AgentJobName = "DEMO_" + run.GeneratorJobName;
        return Task.FromResult(true);
    }
    public Task Start(RunRecord run, ConnectionInput input, CancellationToken ct) => Task.CompletedTask;
    public Task Poll(RunRecord run, ConnectionInput input, CancellationToken ct)
    {
        var scenario = run.Definition.JobName.ToUpperInvariant();
        var elapsed = (DateTime.UtcNow - run.ServerSubmittedAt!.Value).TotalSeconds;
        var segment = scenario == "DEMO_SLOW" ? 20 : scenario == "DEMO_RETRY" ? 10 : 6;
        var step = Math.Min(3, (int)(elapsed / segment) + 1);
        var names = new[] { "1. Prepare", "2. Process", "3. Validate" };
        run.CurrentStep = names[step - 1];
        run.Status = "Running";
        run.Message = "SQL Server Agent çalışması izleniyor.";
        var steps = new List<AgentStep>();
        for (var i = 1; i < step; i++) steps.Add(new(i, names[i - 1], 1, "Demo step completed.", segment));
        if (scenario == "DEMO_RETRY" && step == 3) steps.Insert(1, new(2, names[1], 2, "Demo: transient failure; retry recorded.", 5));
        if (scenario == "DEMO_UNKNOWN" && elapsed >= 12)
        {
            run.Status = "Unknown";
            run.Message = "Bu çalışmanın bitiş kaydı henüz doğrulanamadı. Agent geçmişi/servisi ve job adı kontrol edilmeli. İzleme sürüyor; otomatik tekrar çalıştırılmayacak.";
        }
        else if ((scenario == "DEMO_FAILURE" || scenario == "DEMO_CANCEL") && elapsed >= 12)
        {
            run.Status = scenario == "DEMO_FAILURE" ? "Failed" : "Canceled";
            run.CurrentStep = names[1];
            run.Message = scenario == "DEMO_FAILURE" ? "Demo: step 2 failed — simulated constraint violation." : "Demo: canceled by SQL Server Agent (simulated).";
            steps = [new(1, names[0], 1, "Demo step completed.", 6), new(2, names[1], scenario == "DEMO_FAILURE" ? 0 : 3, run.Message, 6)];
        }
        else if (elapsed >= segment * 3)
        {
            run.Status = "Succeeded"; run.CurrentStep = "";
            run.Message = "Demo: all steps completed successfully.";
            steps.Add(new(3, names[2], 1, run.Message, segment));
        }
        else if (scenario == "DEMO_RETRY" && step == 2)
        {
            run.Message = "Agent yeniden deneme aralığında.";
            steps.Add(new(2, names[1], 2, "Demo: transient failure; retry pending.", 5));
        }
        run.StepsJson = JsonSerializer.Serialize(steps, Store.Json);
        return Task.CompletedTask;
    }
}
