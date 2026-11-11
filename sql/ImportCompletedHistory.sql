/*
PASIFIK / EDWUTIL - Completed historical Agent executions -> EdwJobGeneratorRequests.
DEFAULT: PREVIEW ONLY. EdwJobGenerator, SSIS packages and Agent jobs are never executed. No existing row is updated/deleted.
Install the accompanying application version BEFORE applying imported records.

1) Run with @Apply=0. Review every preview row, dates, CurrentServer and count.
2) Copy PreviewFingerprint, PreviewCount and CurrentServer into the three @Expected... settings.
3) Keep dates/batch size unchanged, set @Apply=1 and rerun the complete script.
   Any changed candidate list, target duplicate or wrong server aborts the transaction.
Defaults: previous 30 full days, maximum 100 oldest eligible executions per batch.
The current day, running jobs, missing/ambiguous log-job matches and missing Agent outcomes are excluded.
*/
USE EDWUTIL;
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 5000;
SET DEADLOCK_PRIORITY LOW;

DECLARE @Apply BIT = 0;
DECLARE @ExpectedServer SYSNAME = NULL;       -- exact CurrentServer returned by preview; PASIFIK may be an alias
DECLARE @ExpectedCount INT = NULL;
DECLARE @ExpectedFingerprint VARCHAR(64) = NULL;
DECLARE @FromDate DATE = DATEADD(DAY,-30,CONVERT(DATE,GETDATE()));
DECLARE @BeforeDate DATE = CONVERT(DATE,GETDATE());  -- exclusive; today excluded
DECLARE @BatchLimit INT = 100;                     -- 1..1000
DECLARE @CurrentServer SYSNAME = CONVERT(SYSNAME,SERVERPROPERTY('ServerName'));

IF DB_NAME()<>N'EDWUTIL' THROW 51000,'Wrong target database.',1;
IF @FromDate IS NULL OR @BeforeDate IS NULL OR @FromDate>=@BeforeDate
    THROW 51001,'Choose an explicit valid date range.',1;
IF @Apply IS NULL THROW 51012,'Apply must be explicitly 0 or 1.',1;
IF @BatchLimit IS NULL OR @BatchLimit<1 OR @BatchLimit>1000
    THROW 51002,'Batch limit must be between 1 and 1000.',1;
IF OBJECT_ID(N'OPR.EdwJobGeneratorRequests',N'U') IS NULL OR COL_LENGTH(N'OPR.EdwJobGeneratorRequests',N'FinishedAt') IS NULL
    THROW 51003,'Target request table or FinishedAt column does not exist.',1;
IF OBJECT_ID(N'OPR.EdwJobGeneratorLog',N'U') IS NULL
    THROW 51004,'Source EdwJobGeneratorLog does not exist.',1;
IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'OPR.EdwJobGeneratorRequests') AND is_disabled=0)
    THROW 51014,'Target has an enabled trigger. Review its side effects before importing; nothing was inserted.',1;

DECLARE @FromDay INT = CONVERT(INT,CONVERT(CHAR(8),@FromDate,112));
DECLARE @BeforeDay INT = CONVERT(INT,CONVERT(CHAR(8),@BeforeDate,112));
DECLARE @Offset INT = DATEPART(TZOFFSET,SYSDATETIMEOFFSET());
DECLARE @ReaderNote NVARCHAR(128)=N'[Geçmiş aktarımı; çalıştıran doğrulanmadı]';

IF OBJECT_ID('tempdb..#Candidates') IS NOT NULL DROP TABLE #Candidates;
IF OBJECT_ID('tempdb..#Eligible') IS NOT NULL DROP TABLE #Eligible;
;WITH Logs AS
(
    SELECT l.*, COUNT_BIG(*) OVER (PARTITION BY l.JobName) AS MatchingLogCount
    FROM OPR.EdwJobGeneratorLog l
    WHERE l.JobCreationStatus='CREATED'
), Source AS
(
    SELECT l.JobName,l.StgPackageName,l.StgFullInitialLoad,l.SkeyPackageName,l.LryPackageName,
        l.Sql1,l.EdwPackageName,l.Sql2,l.UserName,l.JobCreationTime,
        j.job_id,h.instance_id,h.run_status,h.message AS AgentMessage,h.run_duration,
        msdb.dbo.agent_datetime(h.run_date,h.run_time) AS AgentStart
    FROM Logs l
    JOIN msdb.dbo.sysjobs j ON j.name COLLATE DATABASE_DEFAULT=l.JobName COLLATE DATABASE_DEFAULT
    JOIN msdb.dbo.sysjobhistory h ON h.job_id=j.job_id AND h.step_id=0
    WHERE l.MatchingLogCount=1
      AND h.run_status IN (0,1,3) AND h.run_date>=@FromDay AND h.run_date<@BeforeDay
      AND l.JobCreationTime IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM msdb.dbo.sysjobactivity active
          WHERE active.job_id=j.job_id
            AND active.session_id=(SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            AND active.start_execution_date IS NOT NULL AND active.stop_execution_date IS NULL
      )
)
SELECT * INTO #Eligible FROM Source WHERE AgentStart>=DATEADD(SECOND,-1,JobCreationTime) AND AgentStart>=@FromDate AND AgentStart<@BeforeDate;

IF EXISTS
(
    SELECT 1 FROM #Eligible WHERE DATALENGTH(CONVERT(VARCHAR(MAX),JobName))>128
       OR DATALENGTH(CONVERT(NVARCHAR(MAX),UserName))>256
       OR DATALENGTH(CONVERT(VARCHAR(MAX),StgPackageName))>300
       OR DATALENGTH(CONVERT(VARCHAR(MAX),SkeyPackageName))>300
       OR DATALENGTH(CONVERT(VARCHAR(MAX),LryPackageName))>300
       OR DATALENGTH(CONVERT(VARCHAR(MAX),EdwPackageName))>300
       OR DATALENGTH(CONVERT(VARCHAR(MAX),Sql1))>4000 OR DATALENGTH(CONVERT(VARCHAR(MAX),Sql2))>4000
       OR UPPER(LTRIM(RTRIM(ISNULL(StgFullInitialLoad,'NO')))) NOT IN ('YES','NO')
) THROW 51013,'Source contains out-of-range values. Nothing was imported; narrow the date range and inspect source.',1;

;WITH Prepared AS
(
    SELECT CONVERT(VARCHAR(50),'H:'+REPLACE(CONVERT(VARCHAR(36),job_id),'-','')+':'+CONVERT(VARCHAR(10),instance_id)+':'+CONVERT(VARCHAR(1),run_status)) AS ImportToken,
        CONVERT(VARCHAR(128),JobName) AS JobName,
        CONVERT(VARCHAR(300),StgPackageName) AS StgPackageName,
        CONVERT(VARCHAR(3),UPPER(LTRIM(RTRIM(ISNULL(StgFullInitialLoad,'NO'))))) AS StgFullInitialLoad,
        CONVERT(VARCHAR(300),SkeyPackageName) AS SkeyPackageName,
        CONVERT(VARCHAR(300),LryPackageName) AS LryPackageName,
        CONVERT(VARCHAR(4000),Sql1) AS Sql1,
        CONVERT(VARCHAR(300),EdwPackageName) AS EdwPackageName,
        CONVERT(VARCHAR(4000),Sql2) AS Sql2,
        CONVERT(NVARCHAR(128),COALESCE(NULLIF(UserName,N''),N'[Kaynak logda kullanıcı yok]')) AS RequestedBy,
        CONVERT(DATETIMEOFFSET(0),TODATETIMEOFFSET(CONVERT(DATETIME2,JobCreationTime),@Offset)) AS RequestedAt,
        @ReaderNote AS ExecutedBy,
        CONVERT(DATETIMEOFFSET(0),TODATETIMEOFFSET(CONVERT(DATETIME2,AgentStart),@Offset)) AS ExecutedAt,
        CONVERT(DATETIMEOFFSET(0),TODATETIMEOFFSET(DATEADD(SECOND,
            (run_duration/10000)*3600+((run_duration/100)%100)*60+(run_duration%100),CONVERT(DATETIME2,AgentStart)),@Offset)) AS FinishedAt,
        job_id AS SourceJobId,instance_id AS SourceHistoryId,run_status AS SourceStatus,AgentMessage
    FROM #Eligible
)
SELECT TOP(@BatchLimit) p.*
INTO #Candidates
FROM Prepared p
WHERE NOT EXISTS
(
    SELECT 1 FROM OPR.EdwJobGeneratorRequests r
    WHERE r.RunToken LIKE LEFT(p.ImportToken,LEN(p.ImportToken)-1)+'%'
       OR (r.RunToken IS NOT NULL AND r.RunToken NOT LIKE 'H:%'
           AND LEFT(p.JobName,LEN('BTVY_'+r.RunToken+'_'))='BTVY_'+r.RunToken+'_')
)
ORDER BY p.ExecutedAt,p.SourceHistoryId;

-- Final mandatory reference and timestamp validation.
IF EXISTS (SELECT 1 FROM #Candidates WHERE RequestedAt IS NULL OR ExecutedAt IS NULL OR FinishedAt IS NULL OR LEN(ImportToken)>50)
    THROW 51005,'Invalid historical source values.',1;

DECLARE @Count INT=(SELECT COUNT(*) FROM #Candidates);
DECLARE @Payload NVARCHAR(MAX)=(SELECT * FROM #Candidates ORDER BY SourceHistoryId,SourceJobId FOR JSON PATH,INCLUDE_NULL_VALUES);
DECLARE @Fingerprint VARCHAR(64)=CONVERT(VARCHAR(64),HASHBYTES('SHA2_256',CONVERT(VARBINARY(MAX),@Payload)),2);

SELECT @CurrentServer AS CurrentServer,DB_NAME() AS TargetDatabase,@FromDate AS FromDateInclusive,
    @BeforeDate AS BeforeDateExclusive,@BatchLimit AS BatchLimit,@Count AS PreviewCount,
    @Fingerprint AS PreviewFingerprint,@Apply AS ApplyRequested;
SELECT SourceJobId,SourceHistoryId,JobName,
    CASE SourceStatus WHEN 1 THEN N'Başarılı' WHEN 0 THEN N'Hatalı' WHEN 3 THEN N'İptal' END AS AgentOutcome,
    RequestedBy,RequestedAt,ExecutedBy,ExecutedAt AS AgentStartedAt,FinishedAt,ImportToken,
    StgPackageName,StgFullInitialLoad,SkeyPackageName,LryPackageName,Sql1,EdwPackageName,Sql2,AgentMessage
FROM #Candidates ORDER BY ExecutedAt,SourceHistoryId;

IF @Apply=0
BEGIN
    PRINT N'PREVIEW ONLY: target table was not modified. Review the exact rows before setting Apply=1.';
    RETURN;
END;
IF @ExpectedServer IS NULL OR @ExpectedServer<>@CurrentServer
    THROW 51006,'Expected server is missing or does not match SERVERPROPERTY(ServerName).',1;
IF @ExpectedCount IS NULL OR @ExpectedCount<>@Count OR @Count=0
    THROW 51007,'Expected row count is missing, changed or zero. Preview again.',1;
IF @ExpectedFingerprint IS NULL OR @ExpectedFingerprint<>@Fingerprint
    THROW 51008,'Candidate fingerprint changed or was not confirmed. Preview again.',1;

DECLARE @Inserted TABLE(RequestId INT,RunToken VARCHAR(50),JobName VARCHAR(128));
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult=sys.sp_getapplock @Resource=N'SqlJobRunner.HistoryImport',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=5000;
    IF @LockResult<0 THROW 51009,'Another history import is running. No rows inserted.',1;

    IF EXISTS
    (
        SELECT 1 FROM #Candidates p JOIN OPR.EdwJobGeneratorRequests r WITH(UPDLOCK,HOLDLOCK)
        ON r.RunToken LIKE LEFT(p.ImportToken,LEN(p.ImportToken)-1)+'%'
        OR (r.RunToken IS NOT NULL AND r.RunToken NOT LIKE 'H:%'
            AND LEFT(p.JobName,LEN('BTVY_'+r.RunToken+'_'))='BTVY_'+r.RunToken+'_')
    ) THROW 51010,'Target changed since preview; duplicate detected. Preview again.',1;

    INSERT INTO OPR.EdwJobGeneratorRequests
    (JobName,MIGPackageName,StgPackageName,StgFullInitialLoad,SkeyPackageName,LryPackageName,
     Sql1,EdwPackageName,Sql2,EdwdmPackageName,RequestedBy,RequestedAt,ExecutedBy,RunToken,ExecutedAt,FinishedAt)
    OUTPUT INSERTED.RequestId,INSERTED.RunToken,INSERTED.JobName INTO @Inserted
    SELECT JobName,NULL,StgPackageName,StgFullInitialLoad,SkeyPackageName,LryPackageName,
        Sql1,EdwPackageName,Sql2,NULL,RequestedBy,RequestedAt,ExecutedBy,ImportToken,ExecutedAt,FinishedAt
    FROM #Candidates;
    IF @@ROWCOUNT<>@ExpectedCount THROW 51011,'Inserted count differs from reviewed batch; rolling back.',1;
    COMMIT TRANSACTION;
    SELECT RequestId,RunToken,JobName FROM @Inserted ORDER BY RequestId;
    PRINT N'COMMITTED: only the reviewed historical request rows were inserted. Save the inserted IDs above for audit.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
