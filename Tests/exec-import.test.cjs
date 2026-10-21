const assert = require('node:assert/strict');
const {parseJobExec:parse} = require('../wwwroot/exec-import.js');
const example = `-- job çalıştırılması
EXEC EDWUTIL.[OPR].[EdwJobGenerator]
 @MIGPackageName = null,
 @JobName = 'tpesaty_LKPPushPayloadType',
 @StgPackageName = null,
 @StgFullInitialLoad = null,
 @SkeyPackageName = 'SKEY_CMP_PUSH_PAYLOAD_TYPE_KEY.dtsx',
 @LryPackageName = 'LRY_CMP_LKP_PUSH_PAYLOAD_TYPE.dtsx',
 @Sql1 = null,
 @EdwPackageName = 'EDW_CMP_LKP_PUSH_PAYLOAD_TYPE.dtsx',
 \u2003@Sql2 = 'UPDATE EDW.CMP.LKPPushPayloadType SET EdwValidFrom = ''2010-01-01''',
 @EdwdmPackageName = null`;
const parsed = parse(example);
assert.equal(parsed.count,10);
assert.equal(parsed.fields.jobName,'tpesaty_LKPPushPayloadType');
assert.equal(parsed.fields.sql2,"UPDATE EDW.CMP.LKPPushPayloadType SET EdwValidFrom = '2010-01-01'");
assert.equal(parsed.fields.stgFullInitialLoad,false);
assert.equal(parsed.fields.stgPackageName,'');
assert.equal(parsed.fields.skeyPackageName,'SKEY_CMP_PUSH_PAYLOAD_TYPE_KEY.dtsx');
const complex = parse(`USE [EDWUTIL]; GO /* outer /* nested */ comment */ EXECUTE [EDWUTIL].[OPR].[EdwJobGenerator]
 @JobName=N'O''Brien', -- comment
 @SQL1=N'PRINT ''a,b; -- text'';
SELECT ''/*literal*/'';', @StgFullInitialLoad='YES'; GO -- end`);
assert.equal(complex.fields.jobName,"O'Brien");
assert.equal(complex.fields.sql1,"PRINT 'a,b; -- text';\nSELECT '/*literal*/';");
assert.equal(complex.fields.stgFullInitialLoad,true);
assert.equal(parse("exec opr.EdwJobGenerator @JobName='x',@StgFullInitialLoad=0").fields.stgFullInitialLoad,false);
assert.equal(parse("exec EdwJobGenerator @JobName='x',@StgFullInitialLoad=1").fields.stgFullInitialLoad,true);
for (const bad of [
 "exec OtherProcedure @JobName='x'",
 "exec EdwJobGenerator @JobName='x'; EXEC OtherProc",
 "exec EdwJobGenerator @JobName=@variable",
 "exec EdwJobGenerator @JobName='x'+'y'",
 "exec EdwJobGenerator @JobName='unterminated",
 "exec EdwJobGenerator @JobName='x',",
 "exec EdwJobGenerator @JobName='x', @JobName='y'",
 "exec EdwJobGenerator @Unknown='x'",
 "exec EdwJobGenerator @StgFullInitialLoad='INVALID'",
 "exec EdwJobGenerator @Sql1=42",
 "USE OtherDatabase; GO exec EdwJobGenerator @JobName='x'",
 "/* unterminated exec EdwJobGenerator @JobName='x'",
 "exec EdwJobGenerator @Sql1='" + 'x'.repeat(4001) + "'"
]) assert.throws(() => parse(bad),bad);
console.log('PASS: supplied EXEC, NULLs, escaped quotes, multiline SQL, comments, Unicode whitespace, flags, qualified names, invalid/duplicate/extra statements');
