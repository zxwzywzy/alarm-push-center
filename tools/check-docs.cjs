// Check local Markdown destinations without accessing public URLs or private state.
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const roots = ['README.md','CHANGELOG.md','RELEASE_NOTES.md','OPENWRT_RELEASE_NOTES.md','PRODUCT.md','DESIGN.md','docs','openwrt/README.md','mobile/README.md','AlarmPushCenter/README.md','钉钉互动卡片模板说明.md'];
const files = [];
function collect(p) {
  if (fs.statSync(p).isDirectory()) for (const n of fs.readdirSync(p)) collect(path.join(p,n));
  else if (p.endsWith('.md')) files.push(p);
}
for(const p of roots) collect(path.join(root,p));
let links = 0; const missing = [];
for (const file of files) {
  const content = fs.readFileSync(file,'utf8');
  for(const m of content.matchAll(/!?\[[^\]]*\]\(([^)]+)\)/g)) {
    const link = m[1].split(/\s+"/)[0].replace(/^<|>$/g,'');
    if (/^(https?:|mailto:|#)/i.test(link)) continue;
    const dest = decodeURIComponent(link.split('#')[0]);
    if(!dest) continue;
    links++;
    if(!fs.existsSync(path.resolve(path.dirname(file),dest))) missing.push(`${path.relative(root,file)} -> ${dest}`);
  }
}
if(missing.length) { console.error(missing.join('\n')); process.exitCode=1; }
else console.log(`Documentation links passed: ${links} local destinations in ${files.length} documents.`);
