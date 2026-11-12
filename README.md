# SQL Job Runner

Windows üzerinde yerel SQL Server Agent job çalıştırma ve takip uygulaması. Arayüz SchemaDiff'in açık/koyu temasını kullanır.

## Kaynak koddan kurulum

GitHub'da **Code → Download ZIP** ile indirdiyseniz ZIP'i tamamen çıkarıp proje klasöründeki **Start.cmd** dosyasına çift tıklayın. Başlatıcı .NET 8 veya daha yeni bir SDK olup olmadığını kontrol eder, projeyi derler ve uygulama hazır olunca tarayıcıyı açar. Kaynak kod için yalnız Runtime yeterli değildir; ilk derlemede NuGet paketlerini indirmek için internet bağlantısı gerekir. Hata oluşursa konsol penceresi açık kalır. Uygulamayı kullanırken bu pencereyi açık tutun.

PowerShell'den tek komutla da açabilirsiniz:

```powershell
.\Start.cmd
```

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

ZIP'i hedef Windows bilgisayarda tamamen bir klasöre çıkarın. **Gerçek ortam için `Start.cmd` dosyasına çift tıklayın veya `SqlJobRunner.exe` çalıştırın**, ardından **http://127.0.0.1:5295/** adresini açın. Yalnız EXE'yi taşımayın; `wwwroot` ve diğer paket dosyaları da gereklidir. Bu paket kendi .NET çalışma ortamını içerir; hedef bilgisayara SDK veya .NET yüklemek gerekmez.

## SQL Server olmadan demo

Kaynak koddan:

```powershell
dotnet run --project .\SqlJobRunner.csproj -c Release --no-launch-profile -- --DemoMode=true
```

Hazır pakette **Start-Demo.cmd** dosyasını açın. Demo adresi **http://127.0.0.1:5296/**. Normal EXE, `--DemoMode=true` verilmedikçe gerçek modda açılır.

Demo üst çubuğundan senaryo seçip EXEC örneğini yükleyin ve Çalıştır'a basın. Başarı, hata, iptal, yeniden deneme, uzun çalışma, belirsiz durum ve oluşturma hatası senaryoları bulunur. Demo SQL bağlantısı açmaz; gerçek prosedür, paket ve yetki doğrulaması yapmaz. Kayıtları `%LOCALAPPDATA%\SqlJobRunnerDemo` altında tutulur. Ayrıntılı kontrol listesi: [TEST-SENARYOLARI.md](TEST-SENARYOLARI.md).

## Ortam ve bağlantı

Varsayılan ortam PASIFIK'tir. İlk Çalıştır tıklamasında hesapla giriş ekranı açılır; sonraki çalıştırmalar aynı doğrulanmış bağlantıyı kullanır. Windows kimlik doğrulaması veya SQL hesabı kullanılabilir. Başarılı bağlantıdan sonra seçili istek için çalıştırma onayı gösterilir. Hesabın gerçek çalıştırma yetkilerini SQL Server denetler. Sunucu Ayarlar'daki ortamdır; çalıştırma girişinde değiştirilemez. Oturum, üç saat başarılı kullanıcı işlemi yapılmadığında sona erer. Başarılı çalıştırma, istek gönderme, silme ve elle yenileme gibi işlemler süreyi yeniler; otomatik GET sorguları ve arka plan izleme yenilemez. Ortam değişirse, hesap ayarı değiştirilirse veya uygulama yeniden başlatılırsa yeniden giriş gerekir. Süre dolması çalışan job'ları durdurmaz. Tarayıcı yalnızca oturum belirtecini sessionStorage'da tutar; parola tarayıcıya kaydedilmez. Eski job çalıştırma API yolları bu akışı atlayamaz.

Ortamı Ayarlar → Sunucu adı alanından değiştirin. İstek tablosu, yeni çalıştırmalar ve SSIS hata listesi aynı ortamı kullanır. Gerekirse Ayarlar → Kimlik doğrulama bölümünden o sunucu için farklı kimlik bilgileri kaydedilebilir. Başlatılmış işler kaydedildikleri bağlantı kopyasıyla izlenir; sonraki ortam değişikliği bu işleri başka sunucuya yönlendirmez.

Önceki SRVDEV\PASIFIK varsayılanı yerel ayarlarda kayıtlıysa ilk yükseltmede bir kez PASIFIK'e geçirilir. Daha sonra Ayarlar'dan açıkça seçilen ortam korunur. Şifreleme ve sertifika seçenekleri Ayarlar'dan yönetilir.

## Çalıştırma

Windows paketini tamamen bir klasöre çıkarıp `SqlJobRunner.exe` çalıştırın. .NET kurulumu gerekmez. Tarayıcı adresi: http://127.0.0.1:5295

Kaynak koddan .NET 8 SDK ile:

```powershell
.\Start.ps1
# veya
 dotnet run --project .\SqlJobRunner.csproj -c Release
```

`Publish.ps1` Windows x64 paketini üretir.

## İstek akışı (danışman / çalışan)

Danışmanlar job'ı doğrudan çalıştırmaz; uygulamadan `EDWUTIL.OPR.EdwJobGeneratorRequests` tablosuna istek gönderir. Yetkili çalışanlar listeden isteği seçip kendi SQL bağlantılarıyla çalıştırır.

1. Tabloyu `sql/EdwJobGeneratorRequests.sql` ile elle oluşturun. Script `sureviewx` için tablo, `EdwJobGeneratorLog` ve msdb job tabloları üzerinde gereken yetkileri de içerir. Uygulama tabloyu oluşturmaz veya değiştirmez; tablo yoksa yalnız "İstek tablosu okunamadı" uyarısı gösterir.
2. İstek tablosunun sunucusu üst çubuktaki **Ayarlar** penceresinden girilir; varsayılan `SRVDEV\PASIFIK`. Kaydedince tablo hemen okunur, erişilemiyorsa hata pencerede gösterilir. Ayar o bilgisayarın yerel kayıt dosyasında tutulur. Kaydedilmemişse paket klasöründeki `appsettings.json` (`Requests:Server`) kullanılır.
3. Tablo okuma, istek ekleme ve job durumunu okuma uygulamaya gömülü `sureviewx` hesabıyla yapılır; danışman şifre girmez. Hesap bilgisi kaynakta şifreli tutulur. Anahtar uygulamayla birlikte dağıtıldığı için bu yöntem bilgiyi kod okuyandan gizler, ancak EXE'yi decompile eden birinden korumaz. Hesabın yetkisini yalnız bu tabloyla sınırlı tutun.
4. **Yeni istek** penceresine EXEC kodunu yapıştırıp **İsteği gönder**'e basın. İstek tabloya yazılır, çalıştırılmaz. `ExecutedBy` boş olduğu sürece istek bekliyor sayılır.
5. Uygulama tabloyu yaklaşık 3 saniyede bir okur. Başka bir kullanıcıdan yeni istek geldiğinde, siteyi açık tutan herkese uygulama içi ve masaüstü bildirimi gösterilir. Site ilk açıldığında bildirim izni istenir ve izin verilirse bir hoş geldin bildirimi gönderilir.
6. Çalışan **▷ Çalıştır**'a bastığında, uygulama oturumunda ilk kez çalıştırıyorsa bağlantı penceresi açılır. Bağlandıktan sonra uygulama kapanana kadar tekrar sorulmaz. İstek, çalışanın kendi SQL bağlantısıyla tek bir UPDATE ile alınır: `ExecutedBy = SUSER_SNAME()`, `RunToken`, `ExecutedAt = SYSDATETIMEOFFSET()`. İki çalışan aynı isteği çalıştıramaz. Çalışan hesabının bu üç kolon ve FinishedAt için UPDATE yetkisi olmalıdır ve bağlandığı sunucu istek tablosunun bulunduğu sunucu olmalıdır.
7. Durum tabloya yazılmaz. Diğer kullanıcılar `RunToken` ile prosedürün oluşturduğu `BTVY_<RunToken>_<tarih>` job'ını msdb'de bulur ve durumu, son tamamlanan adımı ve sonucu oradan okur. Oluşturma hatası `EdwJobGeneratorLog`'dan okunur. Tüm kullanıcılar Adımlar ve Mesajlar sekmelerinde ortak Agent geçmişini görür. Çalıştıran kişi uygulamayı kapatsa da diğerleri güncel Agent durumunu görür. Agent geçmişi temizlenen eski isteklerin sonucu görünmez.

## Kullanım

1. SQL Server bağlantı seçimi ana ekranda gösterilmez; bağlantı penceresi bir isteği oturumda ilk kez çalıştırırken açılır. Başlangıç veritabanı her zaman master'dır; veritabanı seçimi yoktur.
2. Yeni job ekranına `EDWUTIL.OPR.EdwJobGenerator` EXEC çağrısını yapıştırın. Parametreler otomatik çözümlenir. Ayrı manuel parametre alanı yoktur.
3. Bildirim tercihlerini belirleyip gösterilen sunucuyu kontrol ederek Çalıştır'a basın. Tek işlemle tanım ve çalışma kaydı oluşturulur, job kuyruğa alınır. Ayrı Kaydet adımı yoktur.
4. Çalışma durumu otomatik açılır. Daha önceki bir EXEC'i düzenleyip yeniden çalıştırabilir veya listeden tekrar başlatabilirsiniz.
5. Aktif adım, mesajlar ve çalışma geçmişinden sonucu izleyin.

Her onay yalnız bir çalışma başlatır. Tarih/saat seçimi, yerel zamanlama, Agent takvimi ve tekrarlı çalıştırma yoktur. Yeniden çalıştırma yalnız yeni bir Çalıştır onayıyla yapılır.

EXEC ayrıştırıcısı NULL, N'...' metinlerini, çiftlenmiş tek tırnakları, çok satırlı SQL'i ve SQL yorumlarını destekler. Yalnız tek EdwJobGenerator çağrısı ve sabit parametre değerleri kabul edilir. Yapıştırmak SQL çalıştırmaz. Hatalı kodda Çalıştır kapalı kalır. Çift tıklama ve aynı ağ isteğinin yeniden gönderilmesi aynı çalışmayı tekrar başlatmaz.

## SQL çalıştırma

Tüm bağlantılar, eski bağlantı ve çalışma kayıtları dahil, master başlangıç veritabanını kullanır. Bu ayar işin veri hedefini değiştirmez: generator EDWUTIL.OPR.EdwJobGenerator, Agent sorguları msdb, paket ve SQL hedefleri ise prosedür/EXEC içindeki tam nitelikli adlardır.

Paylaşılan prosedür job'ı oluşturur; uygulama oluşturulan job'ı bulup gerçek job_id ile `msdb.dbo.sp_start_job` çağırır. Her çalışmada benzersiz etiket üretilir, prosedürün `BTVY_<etiket>_<tarih_saat>` adına dönüştürdüğü job bulunur ve kimliğe sabitlenir.

Parametreler kaynakla aynı tiptedir: JobName VARCHAR(50), paketler VARCHAR(300), SQL alanları VARCHAR(4000), StgFullInitialLoad VARCHAR(3). Bayrak YES/NO gönderilir. Diğer boş parametreler NULL'dır. Prosedürün kabul etmediği çift tırnaklar ve uzunluk aşımı kaydetme sırasında reddedilir.

Prosedür hataları CATCH içinde loga yazabildiğinden çağrının dönmesi başarı sayılmaz. Oluşturma hatası `EDWUTIL.OPR.EdwJobGeneratorLog` üzerinden; çalışma sonucu msdb Agent aktivite ve geçmiş kayıtlarından kontrol edilir. Son LOG UPDATE adımına erişemeyen bir job logda NOT EXECUTED kalabilir; nihai sonuç Agent geçmişinden alınır.

Aktif adım `sp_help_job.current_execution_step` alanından okunur. Uygulama adım numarasını tahmin etmez. Takip yaklaşık 3 saniyede bir, ekran yenilemesi 3 saniyede bir yapılır. Ağ beklemeleri bu süreleri uzatabilir.

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

## Ayrıntılı geçmiş ve tek çalıştırma

Her istek yalnızca bir kez çalıştırılır. Başarılı, hatalı veya iptal edilmiş isteklerde tekrar çalıştırma yoktur. Aynı kullanıcı etiketiyle oluşturulan farklı istekler birbirinden bağımsızdır.

Çalışma geçmişinden bir kayıt seçip **Adımlar** sekmesini açın. Her bilgisayar ayrıntıları ortak msdb geçmişinden okur; çalıştıran bilgisayarda olma şartı yoktur. Adım adı, sonuç, başlangıç/bitiş, süre, SQL hata numarası, önem derecesi ve Agent'ın kaydettiği mesaj gösterilir. Adımlar sekmesindeki **Mesajı göster** bağlantısı tam kayıt metnini açar. Ayrıntılar yalnızca seçili kayıt için yüklenir ve açıkken yenilenir.

Yeni tablo veya yetki gerekmez; mevcut `sureviewx` hesabının `msdb.dbo.sysjobs` ve `msdb.dbo.sysjobhistory` SELECT yetkileri kullanılır. SQL Agent geçmişi temizlenirse adım ayrıntıları da kaybolur. Henüz tamamlanmayan adımların history kaydı olmayabilir. Mesajlar Agent'ın sakladığı kapsamla sınırlıdır. Önceden tekrar çalıştırılmış isteklerde son kayıtlı çalışma etiketi gösterilir.

## Bekleyen isteği silme

İstekler listesinin sağındaki × düğmesi yalnız bekleyen kayıtlarda görünür. Silme onayından sonra kayıt kalıcı silinir. Çalıştırılmak üzere alınmış bir istek, onay penceresi açıkken durumu değişse bile silinmez. Çalışma geçmişinde bekleyen kayıtlar ve İşlemler sütunu gösterilmez; satıra tıklayarak ayrıntılar açılır.

Silme işlemi mevcut sureviewx bağlantısıyla doğrudan tablo üzerinde yapılır; bu hesabın DELETE yetkisi olmalıdır. Ek prosedür veya kurulum scripti gerekmez. Parametreli DELETE yalnız ExecutedBy ve RunToken boş olan kaydı siler; çalıştırma ile aynı anda silme denenirse çalışan kayıt korunur.

## SSIS OnError ayrıntıları

Üst çubuğun sağındaki **SSIS Hataları** düğmesi bağımsız bir pencere açar. Hatalar varsayılan olarak çalışma kimliğine göre gruplanır; her grupta hata sayısı ve ana paket gösterilir. Gruplama anahtarıyla düz tabloya geçilebilir; seçim hatırlanır. Mesajı göster ayrı bir ayrıntı penceresi açar. Mesaj sekmesi ham metni tek kez gösterir, Çalışma bilgileri sekmesi paket/kaynak/zaman/yol bilgilerini iki sütunda gösterir. Önceki/Sonraki düğmeleri gruplu görünümde aynı çalışmanın hataları arasında ilerler. Yenileme açık grupları veya okunmakta olan mesajı kapatmaz. Son 48 saatin EDW, ERPEDW ve MessageUtilization klasörlerindeki erişilebilir tüm OnError mesajları, SQL tarafında tarih aralığı filtresiyle tek sorguda okunur. SQL sorgusu parametrelidir; mesajın zamanı esas alınır. Job eşleştirmesi ve kayıt sınırı yoktur.

Uygulama açık olduğu sürece panel ve tarayıcı kapalıyken de yaklaşık 3 saniyede bir sadece son görünür OnError mesajının kimliği sorgulanır (TOP 1, mesaj gövdesi yok). Kimlik değişmişse son 48 saatin tam listesi çekilir; değişmemişse tam mesaj sorgusu yapılmaz. COUNT ile tüm kayıtlar sayılmaz. Süresi dolan mesajlar önbellekten çıkarılır. Geç commit edilen düşük kimlikli kayıtları, görünürlük değişikliklerini ve geçmiş temizliğini yakalamak için en geç 5 dakikada bir tam liste doğrulanır. SQL hatasında 10 saniye beklenerek yeniden denenir. Bu sorgunun canlı maliyeti SSISDB büyüklüğüne, mevcut indekslere ve görünürlük filtrelerine bağlıdır.

Önbellek aynı uygulamanın tüm tarayıcı sekmeleri arasında paylaşılır; eşzamanlı sorgular birleştirilir. Yenile düğmesi tam sorguyu hemen çalıştırır. Panelde yalnız Son güncelleme gösterilir: tablodaki verinin son değiştiği andır. Yeni kayıt, veri değişikliği veya silinme/48 saatten çıkma bu zamanı yeniler. Aynı veriyi yeniden okumak veya hafif kontrol yapmak zamanı değiştirmez. İlk başarılı yükleme başlangıç zamanı kabul edilir. Hata halinde önceki veriler ve bu zaman korunur. Ortam değişince eski ortamın verisi sunulmaz.

SSIS sunucusu Ayarlar'daki ortamdır; varsayılan PASIFIK'tir. Okuma sureviewx hesabıyla catalog.executions ve catalog.event_messages üzerinden yapılır; ilgili SSISDB kayıtlarında okuma görünürlüğü gerekir. Sorgu mevcut SQL tablolarını değiştirmez. Tam mesaj, kaynak, alt bileşen ve execution path korunur; temizlenmiş veya hesaba görünmeyen geçmiş listelenemez.

## Prod geçmişini aktarma

Önce bu sürümü kurun ve uygulama Ayarlar bölümündeki istek sunucusunu PASIFIK olarak kontrol edin. `sql/ImportCompletedHistory.sql` yalnız eski tamamlanmış çalışmaları gösterime açmak içindir. Yeni sütun veya tablo oluşturmaz. `RunToken` alanına job_id, genel history instance_id ve terminal sonucu içeren H: arşiv referansı yazılır; bu referans normal çalıştırma etiketi değildir.

Scriptin varsayılanı Apply=0 önizlemedir. Son 30 tam gün, bugün hariç, en fazla 100 en eski uygun Agent çalışması seçilir. Tarih aralığını ihtiyaç duyulan tarihlere göre daraltın. SQL Server 2016+ gerekir. SSMS'te PASIFIK sunucusunda EDWUTIL için yeni bir query penceresinde scriptin tamamını çalıştırın.

Önizleme çıktısında CurrentServer, PreviewCount, PreviewFingerprint ve bütün satırları inceleyin. PASIFIK bir alias olabilir; CurrentServer fiziksel sunucu/instance adını döndürür. Gerçek aktarım için ancak incelenen değerleri ExpectedServer, ExpectedCount ve ExpectedFingerprint ayarlarına kopyalayıp Apply=1 yapın. Script liste değiştiyse, hedefte mükerrer varsa veya sunucu farklıysa hata verip durur. INSERT kısa bir transaction içindedir; mevcut istekleri, Agent işlerini veya kaynak logları değiştirmez. Hata olursa batch geri alınır. Aktarım sonunda dönen yeni RequestId/RunToken listesini saklayın. Her sonraki batch için tekrar önizleme gerekir.

Yalnız CREATED log kaydıyla adı tam eşleşen, tek log eşleşmesi bulunan, halen var olan job'ların msdb'de terminal sonucu bulunan çalışmaları alınır. Başarılı/hatalı/iptal sonuçları Agent'tan gelir. Aktarım çalıştırılabilir bekleyen istek üretmez ve yeni istek bildirimi göndermez. Aynı job'ın farklı eski çalışmaları farklı history kimlikleriyle ayrılır. Çalıştıran kişinin gerçek login'i eski logdan doğrulanamadığı için bu alan açıkça 'Geçmiş aktarımı; çalıştıran doğrulanmadı' olarak işaretlenir. RequestedBy kaynak logdaki UserName'dir; bununla çalıştıran kimliği varsayılmaz.

Kaynak prosedür logunda MIGPackageName ve EdwdmPackageName bulunmadığından bu alanlar NULL bırakılır. Kaynakta bulunmayan parametreler tahmin edilmez; Parametreler sekmesi arşiv kaydında bunun çalıştırılabilir bir istek olmadığını belirtir. silinmiş job'lar, Agent genel sonucu silinmiş kayıtlar, yalnız oluşturma hataları ve birden fazla CREATED log eşleşmesi bu scriptin kapsamına girmez.

Adım/mesaj ayrıntıları ilgili job_id ve seçili history instance_id aralığından okunur. msdb geçmişi daha sonra temizlenirse ayrıntılar kaybolabilir; aktarılan temel sonuç ve başlangıç/bitiş bilgileri arşiv referansı ve istek satırından gösterilmeye devam eder.

## Sonuç inceleme

Alt bölümde Özet, Adımlar ve Job içeriği bulunur. Ayrı Mesajlar sekmesi kaldırılmıştır; tam Agent mesajları Adımlar içindeki Mesajı göster bölümünden okunur. Job içeriği, eski Parametreler sekmesidir ve isteğin paket/SQL içeriğini gösterir. Silme işlemi ortak hesapla yalnız bekleyen istekler için mevcut onay akışını kullanmaya devam eder.
