# SQL Job Runner

Windows üzerinde yerel SQL Server Agent job çalıştırma ve takip uygulaması. Arayüz SchemaDiff'in açık/koyu temasını kullanır.

## Kaynak koddan kurulum

Gereksinimler: Windows x64, Git ve .NET 8 SDK. Arayüz için Node.js gerekmez; Node.js yalnız arayüz testlerini çalıştırırken kullanılır. Proje şirketin mevcut `EDWUTIL.OPR.EdwJobGenerator` prosedürüne bağlıdır; genel amaçlı herhangi bir SQL Server'a kurulduğunda bu prosedürü kendisi oluşturmaz.

```powershell
git clone https://github.com/smailyy13/sql-job-runner.git
cd sql-job-runner
dotnet restore .\SqlJobRunner.csproj
dotnet run --project .\SqlJobRunner.csproj -c Release --no-launch-profile
```

Tarayıcıda **http://127.0.0.1:5295/** adresini açın. Private repoyu klonlamak için GitHub hesabınızın repoya erişimi olmalıdır. Uygulamayı bu terminalden başlattıysanız Ctrl+C ile kapatabilirsiniz.

## Windows EXE paketi oluşturma

Kaynak kod klasöründe PowerShell'den:

```powershell
.\Publish.ps1
Compress-Archive -Path .\publish\* -DestinationPath .\SqlJobRunner-win-x64.zip -Force
```

PowerShell yerel script çalıştırmayı engelliyorsa aynı klasörde `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Publish.ps1` komutunu kullanabilirsiniz; bu yalnız ilgili işlem için geçerlidir. Paket üretirken publish klasöründen çalışan uygulamayı kapatın.

ZIP'i hedef Windows bilgisayarda tamamen bir klasöre çıkarın. **Gerçek ortam için `SqlJobRunner.exe` çalıştırın**, ardından **http://127.0.0.1:5295/** adresini açın. Yalnız EXE'yi taşımayın; `wwwroot` ve diğer paket dosyaları da gereklidir. Bu paket kendi .NET çalışma ortamını içerir; hedef bilgisayara SDK veya .NET yüklemek gerekmez.

## SQL Server olmadan demo

Kaynak koddan:

```powershell
dotnet run --project .\SqlJobRunner.csproj -c Release --no-launch-profile -- --DemoMode=true
```

Hazır pakette **Start-Demo.cmd** dosyasını açın. Demo adresi **http://127.0.0.1:5296/**. Normal EXE, `--DemoMode=true` verilmedikçe gerçek modda açılır.

Demo üst çubuğundan senaryo seçip EXEC örneğini yükleyin ve Çalıştır'a basın. Başarı, hata, iptal, yeniden deneme, uzun çalışma, belirsiz durum ve oluşturma hatası senaryoları bulunur. Demo SQL bağlantısı açmaz; gerçek prosedür, paket ve yetki doğrulaması yapmaz. Kayıtları `%LOCALAPPDATA%\SqlJobRunnerDemo` altında tutulur. Ayrıntılı kontrol listesi: [TEST-SENARYOLARI.md](TEST-SENARYOLARI.md).

## İlk gerçek bağlantı

1. SQL Server Agent servisi çalışıyor olmalı.
2. `EDWTEMP` veritabanı, `EDWUTIL.OPR.EdwJobGenerator` prosedürü ve `EDWUTIL.OPR.EdwJobGeneratorLog` tablosu mevcut olmalı. Uygulama bu nesneleri kurmaz.
3. Uygulamada sunucu adını ve Windows veya SQL kimlik doğrulamasını seçin. Windows kimlik doğrulaması EXE'yi çalıştıran Windows hesabını kullanır.
4. Varsayılan bağlantı ayarları **Encrypt: Optional (False)** ve **Trust server certificate: True**. Kurumunuzun bağlantı politikasına göre ayarlayın.
5. Hesabın prosedür çalıştırma, log okuma ve ilgili Agent job'larını oluşturma, başlatma ve izleme yetkilerini DBA ile doğrulayın. Sunucuya bağlanabilmek bu yetkilerin tümünün bulunduğu anlamına gelmez.

`owner_login_name is invalid` hatası, paylaşılan prosedürdeki sabit `saProd` sahibinin geçerliliğinin/yetkilerinin DBA tarafından kontrol edilmesini gerektirir. Uygulama owner parametresi göndermez. SSMS'teki `(1 row affected)` tek başına job başarısı değildir; prosedür hata loguna satır yazmış olabilir.

Kontrollü ilk örnek:

```sql
EXEC EDWUTIL.[OPR].[EdwJobGenerator]
    @JobName = 'TEST_SELECT_ONLY',
    @Sql1 = 'SELECT 1 AS TestResult',
    @Sql2 = NULL;
```

SELECT iş tablolarını değiştirmez; prosedür Agent job ve log kayıtları oluşturabilir. Sonucu uygulamanın çalışma geçmişinden ve SQL Server Agent geçmişinden doğrulayın.

## Çalıştırma

Windows paketini tamamen bir klasöre çıkarıp `SqlJobRunner.exe` çalıştırın. .NET kurulumu gerekmez. Tarayıcı adresi: http://127.0.0.1:5295

Kaynak koddan .NET 8 SDK ile:

```powershell
.\Start.ps1
# veya
 dotnet run --project .\SqlJobRunner.csproj -c Release
```

`Publish.ps1` Windows x64 paketini üretir.

## Kullanım

1. SQL Server bağlantısını açın, sunucu ve giriş bilgilerini belirtip Bağlan'a basın. Başlangıç veritabanı otomatik EDWTEMP'tir; veritabanı seçimi yoktur.
2. Yeni job ekranına `EDWUTIL.OPR.EdwJobGenerator` EXEC çağrısını yapıştırın. Parametreler otomatik çözümlenir. Ayrı manuel parametre alanı yoktur.
3. Bildirim tercihlerini belirleyip gösterilen sunucuyu kontrol ederek Çalıştır'a basın. Tek işlemle tanım ve çalışma kaydı oluşturulur, job kuyruğa alınır. Ayrı Kaydet adımı yoktur.
4. Çalışma durumu otomatik açılır. Daha önceki bir EXEC'i düzenleyip yeniden çalıştırabilir veya listeden tekrar başlatabilirsiniz.
5. Aktif adım, mesajlar ve çalışma geçmişinden sonucu izleyin.

Her onay yalnız bir çalışma başlatır. Tarih/saat seçimi, yerel zamanlama, Agent takvimi ve tekrarlı çalıştırma yoktur. Yeniden çalıştırma yalnız yeni bir Çalıştır onayıyla yapılır.

EXEC ayrıştırıcısı NULL, N'...' metinlerini, çiftlenmiş tek tırnakları, çok satırlı SQL'i ve SQL yorumlarını destekler. Yalnız tek EdwJobGenerator çağrısı ve sabit parametre değerleri kabul edilir. Yapıştırmak SQL çalıştırmaz. Hatalı kodda Çalıştır kapalı kalır. Çift tıklama ve aynı ağ isteğinin yeniden gönderilmesi aynı çalışmayı tekrar başlatmaz.

## SQL çalıştırma

Tüm bağlantılar, eski bağlantı ve çalışma kayıtları dahil, EDWTEMP başlangıç veritabanını kullanır. Sunucuda EDWTEMP bulunmalı ve bağlantı hesabı erişebilmelidir. Bu ayar işin veri hedefini değiştirmez: generator EDWUTIL.OPR.EdwJobGenerator, Agent sorguları msdb, paket ve SQL hedefleri ise prosedür/EXEC içindeki tam nitelikli adlardır.

Paylaşılan prosedür job'ı oluşturur; uygulama oluşturulan job'ı bulup gerçek job_id ile `msdb.dbo.sp_start_job` çağırır. Her çalışmada benzersiz etiket üretilir, prosedürün `BTVY_<etiket>_<tarih_saat>` adına dönüştürdüğü job bulunur ve kimliğe sabitlenir.

Parametreler kaynakla aynı tiptedir: JobName VARCHAR(50), paketler VARCHAR(300), SQL alanları VARCHAR(4000), StgFullInitialLoad VARCHAR(3). Bayrak YES/NO gönderilir. Diğer boş parametreler NULL'dır. Prosedürün kabul etmediği çift tırnaklar ve uzunluk aşımı kaydetme sırasında reddedilir.

Prosedür hataları CATCH içinde loga yazabildiğinden çağrının dönmesi başarı sayılmaz. Oluşturma hatası `EDWUTIL.OPR.EdwJobGeneratorLog` üzerinden; çalışma sonucu msdb Agent aktivite ve geçmiş kayıtlarından kontrol edilir. Son LOG UPDATE adımına erişemeyen bir job logda NOT EXECUTED kalabilir; nihai sonuç Agent geçmişinden alınır.

Aktif adım `sp_help_job.current_execution_step` alanından okunur. Uygulama adım numarasını tahmin etmez. Takip yaklaşık 5 saniyede bir, ekran yenilemesi 3 saniyede bir yapılır. Ağ beklemeleri bu süreleri uzatabilir.

## Çift çalıştırma ve yeniden açılış

Çalışma kaydı, parametre/bağlantı kopyası ve başlatma niyeti SQL isteğinden önce kalıcı saklanır. Aynı tanımın aktif veya belirsiz çalışması varken ikinci başlatma engellenir. İstekten sonra bağlantı kopması veya uygulama kapanması sonucu belirsizse başlatma otomatik yeniden gönderilmez; gerçek durum sorgulanır.

Tarayıcı kapansa da EXE çalıştığı sürece takip devam eder. SQL Server Agent üzerinde başlamış job uygulama kapansa da sunucuda çalışır. Uygulama yeniden açılınca kayıtlı çalışma yeniden izlenir. Sonucu belirsiz bir çalışma için SSMS kontrolünden sonra İzlemeyi kapat kullanılabilir; bu gerçek SQL job'ını durdurmaz.

Başarı ve hata bildirimleri yerel veritabanında kalıcı tutulur. Açık sayfada toast; tarayıcı izni varsa masaüstü bildirimi gösterilir. Kapalı bilgisayara anlık bildirim veya e-posta gönderimi yoktur.

## Veriler ve bağlantı

Varsayılan kayıt dosyası `%LOCALAPPDATA%\SqlJobRunner\jobs.db`. Özel klasör: `--DataDirectory "D:\SqlJobRunnerData"`. Yedek için uygulamayı durdurup veri klasörünü kopyalayın. Aynı veri klasörünü kullanan ikinci uygulama dosya kilidiyle engellenir.

Parolayı hatırla açıksa SQL parolası Windows DPAPI CurrentUser ile şifrelenir. Kapalıysa parola yalnız uygulama sürecinde tutulur; tekrar açılışta girilmelidir. Parola API yanıtlarında bulunmaz. Windows kimlik doğrulaması uygulama/servis hesabını kullanır.

Bağlantı hesabı generator EXECUTE, generator log SELECT ve ilgili msdb job oluşturma/başlatma/görünürlük yetkilerine sahip olmalıdır. Prosedürdeki sabit owner hesabının da sunucuda geçerli olması gerekir. Uygulama login veya yetki oluşturmaz, mevcut prosedüre ALTER uygulamaz.

Uygulama yalnız 127.0.0.1:5295 dinler; çok kullanıcılı/ağa açık sunucu olarak tasarlanmamıştır. Host/origin kontrolü vardır. SQL ifadeleri ve hata mesajları yerel kayıt dosyasında bulunur; veri klasörü erişimi ilgili hesapla sınırlandırılmalıdır.

## Testler

```powershell
dotnet test .\Tests\SqlJobRunner.Tests.csproj -c Release
```

DOM ve EXEC testleri için `Tests` klasöründe `npm install` ve `npm run test:ui`. `Tests/Smoke.ps1` yalnız boş/geçici veri klasörüyle açılmış uygulamaya karşı çalıştırılır; SQL job göndermez.

Şirket SQL Server'ında gerçek job oluşturma, başlatma ve yetki kombinasyonu canlı olarak doğrulanmamıştır. İlk deneme kontrollü bir test job'ıyla yapılmalıdır.
