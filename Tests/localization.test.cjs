const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.join(__dirname, '../wwwroot');
const context = {localStorage:{getItem:()=>null}};
vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(root,'localization.js'),'utf8') + ';globalThis.dictionary=translations;',context);
const dictionary=context.dictionary;
let checked=0;
for (const file of ['index.html','app.js']) {
  const source=fs.readFileSync(path.join(root,file),'utf8');
  const keys=file.endsWith('.html')
    ? [...source.matchAll(/data-i18n(?:-title|-aria-label|-placeholder)?="([^"]+)"/g)].map(m=>m[1])
    : [...source.matchAll(/\bt\(\s*(['"])((?:\\.|(?!\1).)*)\1\s*\)/g)].map(m=>m[2]);
  for (const key of keys) {
    assert.ok(typeof dictionary[key]==='string' && dictionary[key].trim(),`${file}: missing English translation: ${key}`);
    checked++;
  }
}
console.log(`PASS: ${checked} static labels, accessibility attributes and dynamic translation calls have English translations`);
