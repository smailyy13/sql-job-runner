-- SQL Job Runner istek tablosu. Uygulama bu tabloyu oluşturmaz; script elle bir kez çalıştırılır.
-- Tablo yalnız isteği ve isteği kimin aldığını tutar. Job durumu msdb'den okunur, buraya yazılmaz.
USE EDWUTIL;
GO

CREATE TABLE OPR.EdwJobGeneratorRequests
(
    RequestId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EdwJobGeneratorRequests PRIMARY KEY,
    -- EdwJobGenerator parametreleri
    JobName            VARCHAR(128)  NOT NULL,
    MIGPackageName     VARCHAR(300)  NULL,
    StgPackageName     VARCHAR(300)  NULL,
    StgFullInitialLoad VARCHAR(3)    NOT NULL CONSTRAINT DF_EdwJobGeneratorRequests_Stg DEFAULT ('NO'),
    SkeyPackageName    VARCHAR(300)  NULL,
    LryPackageName     VARCHAR(300)  NULL,
    Sql1               VARCHAR(4000) NULL,
    EdwPackageName     VARCHAR(300)  NULL,
    Sql2               VARCHAR(4000) NULL,
    EdwdmPackageName   VARCHAR(300)  NULL,
    -- İsteyen: danışmanın Windows kullanıcısı (ALANADI\kullanıcı)
    RequestedBy        NVARCHAR(128) NOT NULL,
    RequestedAt        DATETIMEOFFSET(0) NOT NULL CONSTRAINT DF_EdwJobGeneratorRequests_RequestedAt DEFAULT (SYSDATETIMEOFFSET()),
    -- Çalıştıran: NULL ise istek bekliyor. Çalışanın kendi bağlantısıyla SUSER_SNAME() ve SYSDATETIMEOFFSET() yazılır.
    -- RunToken, msdb'de oluşan BTVY_<RunToken>_<tarih> job'ını bulmak için kullanılır.
    ExecutedBy         NVARCHAR(128) NULL,
    RunToken           VARCHAR(50)   NULL,
    ExecutedAt         DATETIMEOFFSET(0) NULL,
    CONSTRAINT CK_EdwJobGeneratorRequests_Stg CHECK (StgFullInitialLoad IN ('YES','NO'))
);
GO

-- sureviewx yetkileri. sureviewx her iki veritabanında da kullanıcı olmalıdır; zaten verilen yetkileri atlayın.
-- İstek ekleme ve listeleme (UPDATE yok; danışmanlar isteği alamaz):
GRANT SELECT, INSERT ON OPR.EdwJobGeneratorRequests TO [sureviewx];
-- Job oluşturma hatasını görmek için:
GRANT SELECT ON OPR.EdwJobGeneratorLog TO [sureviewx];
GO

-- Çalışanlar isteği kendi SQL hesaplarıyla alır; ExecutedBy = SUSER_SNAME().
-- [CALISAN_ROLU] yerine çalışanların kullanıcı/rol adını yazın. Yalnız bu üç kolon güncellenebilir.
GRANT SELECT ON OPR.EdwJobGeneratorRequests TO [CALISAN_ROLU];
GRANT UPDATE (ExecutedBy, RunToken, ExecutedAt) ON OPR.EdwJobGeneratorRequests TO [CALISAN_ROLU];
GO

-- Job durumunu herkesin görebilmesi için msdb okuma (yalnız SELECT):
USE msdb;
GO
GRANT SELECT ON dbo.sysjobs        TO [sureviewx];
GRANT SELECT ON dbo.sysjobactivity TO [sureviewx];
GRANT SELECT ON dbo.sysjobhistory  TO [sureviewx];
GRANT SELECT ON dbo.syssessions    TO [sureviewx];
GO
