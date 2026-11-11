# Yerel test ortamı

`Start-Demo.ps1` dosyasını PowerShell ile çalıştırın ve http://127.0.0.1:5296/ adresini açın. Normal uygulama 5295 portundadır. Test verileri `%LOCALAPPDATA%\SqlJobRunnerDemo` klasöründe tutulur. Test ortamı SQL bağlantısı açmaz; EXEC ve paketlerin içeriği çalıştırılmaz. Gerçek SQL yetkilerini, paketleri ve EdwJobGenerator prosedürünü bu ortam doğrulamaz.

Üstteki TEST / DEMO satırından senaryo seçip **EXEC örneği / Load EXEC** düğmesine, ardından **Çalıştır** düğmesine basın. Bağlantı başlangıçta DEMO olarak hazırdır. Süreler yaklaşık olup 3 saniyelik izleme döngüsüne göre birkaç saniye değişebilir.

| Senaryo | Beklenen sonuç |
|---|---|
| DEMO_SUCCESS | Yaklaşık 20 saniye, üç başarılı adım ve bildirim |
| DEMO_FAILURE | Yaklaşık 15 saniye, ikinci adımda hata ve hata bildirimi |
| DEMO_CANCEL | Yaklaşık 15 saniye, Agent tarafından iptal edilmiş durum |
| DEMO_RETRY | İkinci adımda yeniden deneme kaydı, yaklaşık 30 saniyede başarı |
| DEMO_SLOW | Yaklaşık 65 saniye, üç adımın sırayla ilerlemesi |
| DEMO_UNKNOWN | Yaklaşık 15 saniye sonra belirsiz durum; kendiliğinden tekrar başlamaz |
| DEMO_GENERATOR_ERROR | Job oluşturma hatası; Agent çalışması başlamaz |

## İstek akışı

Demo modunda istek tablosu bellekte tutulur ve uygulama kapanınca sıfırlanır.

1. **EXEC örneği** ile pencereyi açıp **İsteği gönder**'e basın. İstek "Bekliyor" durumunda listelenmeli, çalışmamalı.
2. Satırdaki **▷ Çalıştır**'a basın. Bağlantı penceresi açılmalı. `DEMO` ile bağlanınca çalıştırma onayı gelmeli; onaylayınca durum Kuyrukta → Çalışıyor → sonuç olarak ilerlemeli.
3. İkinci bir isteği çalıştırın. Bağlantı penceresi tekrar açılmamalı. Uygulamayı kapatıp açtıktan sonra ilk çalıştırmada yeniden açılmalı.

## Arayüz testleri

1. Başarılı senaryoyu çalıştırın. Satıra tıklayın; Özet, Adımlar, Mesajlar ve Parametreler sekmelerini inceleyin. Çalışma geçmişinde sonucu görün.
2. Uzun senaryoyu çalıştırın. Aynı job için ikinci çalıştırma engellenmeli; başka bir senaryo eş zamanlı başlatılabilmeli.
3. Başarılı senaryoda başarı bildirimini kaldırın. Tamamlanınca yeni bildirim gelmemeli. İşaretleyip yeniden çalıştırın; uygulama içi bildirim gelmeli. Masaüstü bildirimi için bildirim penceresinden izin verin ve uygulamayı açık tutun.
4. Belirsiz senaryonun detayında İzlemeyi kapat seçeneğini kullanın. Sonuç başarılıya dönmemeli, izleme kapatıldı olmalı ve yeni çalıştırma mümkün olmalı.
5. Bağlantı penceresinde sunucuya `DEMO-FAIL` yazıp Bağlan deyin. Simüle edilmiş bağlantı hatası görünmeli. `DEMO` ile yeniden bağlanın. Gerçek parola kullanmanız gerekmez.
6. TR/EN ve açık/koyu tema seçeneklerini değiştirip sayfayı yenileyin. Tercihler korunmalı. EXEC alanına yazılmış metin dil değiştirirken değişmemeli.
7. Başarıyla biten job tanımını silin. Çalışma geçmişi korunmalı. Arama ve durum filtrelerini deneyin.
8. Uzun çalışma sürerken sayfayı kapatıp tekrar açın. Uygulama işlemi açık kaldığı için izleme devam etmeli. Test süreci kapatılıp açılırsa kalıcı geçmiş korunur; bu simülasyon gerçek Agent servisinin yeniden başlatılma davranışını doğrulamaz.

## Parser testleri

Yeni job penceresine aşağıdakileri ayrı ayrı yapıştırın. Geçersiz örneklerde Çalıştır düğmesi kapalı olmalı ve geçmişe yeni çalışma eklenmemeli.

Geçerli: çok satır, yorum, NULL ve kaçırılmış tırnak:

```sql
EXEC EDWUTIL.[OPR].[EdwJobGenerator]
    @JobName = 'DEMO_SUCCESS',
    @MIGPackageName = NULL,
    @StgFullInitialLoad = 'YES',
    @Sql1 = 'SELECT ''İstanbul'' AS City,
                   1 AS DemoResult'; -- örnek
```

Eksik kapanış tırnağı:

```sql
EXEC EdwJobGenerator @JobName = 'DEMO_SUCCESS
```

Aynı parametre iki kez:

```sql
EXEC EdwJobGenerator @JobName='DEMO_SUCCESS', @JobName='duplicate', @Sql1='SELECT 1';
```

İzin verilmeyen prosedür:

```sql
EXEC OtherProcedure @JobName='DEMO_SUCCESS';
```

Ek komut:

```sql
EXEC EdwJobGenerator @JobName='DEMO_SUCCESS', @Sql1='SELECT 1'; SELECT 2;
```

SQL/paket adımı eksik (parser kabul eder, Çalıştır sonrası sunucu doğrulama hatası verir):

```sql
EXEC EdwJobGenerator @JobName='DEMO_EMPTY', @Sql1=NULL;
```

## Ortak ayrıntılı geçmiş
1. Birinci bilgisayarda iki adımlı isteği çalıştırın; ikinci adım hata versin.
2. Başka bir bilgisayarda geçmişten aynı isteği seçin. Adımlar ve Mesajlar sekmelerinde iki adımın sonuçları, süreleri ve hata metinleri görünmeli.
3. Her iki bilgisayarda da hatalı/başarılı/iptal edilmiş istek için tekrar çalıştır düğmesi bulunmamalı.
4. msdb okuma yetkisi olmayan bağlantıda ayrıntıların okunamadığı açıkça belirtilmeli.
