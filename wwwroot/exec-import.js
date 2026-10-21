'use strict';

function parseJobExec(source) {
  if (source.length > 64000) throw new Error('EXEC metni en fazla 64.000 karakter olabilir.');
  const tokens = [];
  let i = 0;
  while (i < source.length) {
    if (/\s|\uFEFF/.test(source[i])) { i++; continue; }
    if (source.startsWith('--',i)) { while (i < source.length && !/[\r\n]/.test(source[i])) i++; continue; }
    if (source.startsWith('/*',i)) {
      let depth = 1; i += 2;
      while (i < source.length && depth) {
        if (source.startsWith('/*',i)) { depth++; i += 2; }
        else if (source.startsWith('*/',i)) { depth--; i += 2; }
        else i++;
      }
      if (depth) throw new Error('SQL yorumu kapanmamış.');
      continue;
    }
    if ((source[i] === 'N' || source[i] === 'n') && source[i+1] === "'") i++;
    if (source[i] === "'") {
      let value = '', closed = false; i++;
      while (i < source.length) {
        if (source[i] === "'") {
          if (source[i+1] === "'") { value += "'"; i += 2; }
          else { i++; closed = true; break; }
        } else value += source[i++];
      }
      if (!closed) throw new Error('Metin değerinin tek tırnağı kapanmamış.');
      tokens.push({kind:'string',value}); continue;
    }
    if (source[i] === '[') {
      let value = '', closed = false; i++;
      while (i < source.length) {
        if (source[i] === ']') {
          if (source[i+1] === ']') { value += ']'; i += 2; }
          else { i++; closed = true; break; }
        } else value += source[i++];
      }
      if (!closed) throw new Error('Köşeli parantez kapanmamış.');
      tokens.push({kind:'identifier',value}); continue;
    }
    const name = /^[A-Za-z_@][A-Za-z0-9_@$]*/.exec(source.slice(i));
    if (name) { tokens.push({kind:name[0].startsWith('@') ? 'parameter' : 'word',value:name[0]}); i += name[0].length; continue; }
    const number = /^\d+/.exec(source.slice(i));
    if (number) { tokens.push({kind:'number',value:number[0]}); i += number[0].length; continue; }
    if ('.=,;'.includes(source[i])) { tokens.push({kind:source[i],value:source[i++]}); continue; }
    throw new Error('Parametrelerde sabit metin, NULL veya YES/NO kullanın.');
  }
  let pos = 0;
  const is = value => tokens[pos]?.kind === 'word' && tokens[pos].value.toUpperCase() === value;
  const take = kind => { const t = tokens[pos++]; if (!t || t.kind !== kind) throw new Error('EXEC parametre biçimi geçersiz.'); return t; };
  if (is('USE')) {
    pos++; const db = tokens[pos++];
    if (!db || !['word','identifier'].includes(db.kind) || db.value.toUpperCase() !== 'EDWUTIL') throw new Error('USE veritabanı EDWUTIL olmalı.');
    if (tokens[pos]?.kind === ';') pos++;
    if (is('GO')) pos++;
  }
  if (!is('EXEC') && !is('EXECUTE')) throw new Error('EdwJobGenerator EXEC çağrısını yapıştırın.');
  pos++;
  const parts = [];
  do {
    const part = tokens[pos++];
    if (!part || !['word','identifier'].includes(part.kind)) throw new Error('Prosedür adı geçersiz.');
    parts.push(part.value.toLowerCase());
    if (tokens[pos]?.kind !== '.') break;
    pos++;
  } while (true);
  if (!['edwjobgenerator','opr.edwjobgenerator','edwutil.opr.edwjobgenerator'].includes(parts.join('.')))
    throw new Error('Yalnızca EDWUTIL.OPR.EdwJobGenerator içe aktarılabilir.');
  const mapping = {jobname:'jobName',migpackagename:'migPackageName',stgpackagename:'stgPackageName',skeypackagename:'skeyPackageName',lrypackagename:'lryPackageName',sql1:'sql1',edwpackagename:'edwPackageName',sql2:'sql2',edwdmpackagename:'edwdmPackageName',stgfullinitialload:'stgFullInitialLoad'};
  const fields = Object.fromEntries(Object.values(mapping).map(key => [key,key === 'stgFullInitialLoad' ? false : '']));
  const seen = new Set();
  while (tokens[pos]?.kind === 'parameter') {
    const parameter = take('parameter').value.slice(1).toLowerCase(), field = mapping[parameter];
    if (!field) throw new Error(`Bilinmeyen parametre: @${parameter}`);
    if (seen.has(field)) throw new Error(`Parametre birden fazla verilmiş: @${parameter}`);
    seen.add(field); take('=');
    const value = tokens[pos++];
    if (!value) throw new Error(`Parametre değeri eksik: @${parameter}`);
    const nil = value.kind === 'word' && value.value.toUpperCase() === 'NULL';
    if (field === 'stgFullInitialLoad') {
      const flag = value.value.toUpperCase();
      if (nil) fields[field] = false;
      else if (['string','number','word'].includes(value.kind) && ['YES','NO','1','0'].includes(flag)) fields[field] = flag === 'YES' || flag === '1';
      else throw new Error('StgFullInitialLoad YES, NO veya NULL olmalı.');
    } else {
      if (!nil && value.kind !== 'string') throw new Error(`@${parameter} için tek tırnaklı metin veya NULL kullanın.`);
      fields[field] = nil ? '' : value.value;
      const max = field === 'jobName' ? 128 : field.startsWith('sql') ? 4000 : 300;
      if (fields[field].length > max) throw new Error(`@${parameter} en fazla ${max} karakter olabilir.`);
    }
    if (tokens[pos]?.kind !== ',') break;
    pos++;
    if (tokens[pos]?.kind !== 'parameter') throw new Error('Virgülden sonra parametre bekleniyor.');
  }
  if (!seen.size) throw new Error('İçe aktarılacak parametre bulunamadı.');
  if (tokens[pos]?.kind === ';') pos++;
  if (is('GO')) pos++;
  if (pos !== tokens.length) throw new Error('Tek bir EXEC çağrısı yapıştırın; değişkenler ve ek komutlar desteklenmiyor.');
  return {fields,count:seen.size};
}
if (typeof module !== 'undefined') module.exports = {parseJobExec};
