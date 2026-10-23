// Read-only package verification, does not connect to or change a router.
const fs = require('node:fs');
const zlib = require('node:zlib');
const assert = require('node:assert/strict');
const file = process.argv[2];
const version = process.argv[3];
if(!file || !version) throw Error('Usage: node tools/verify-ipk.cjs file.ipk version');
function untar(gzip) {
  const bytes = zlib.gunzipSync(gzip), entries = new Map();
  for(let offset=0; offset+512<=bytes.length;) {
    const h = bytes.subarray(offset,offset+512);
    if(h.every(b=>b===0)) break;
    const str=(a,b)=>h.subarray(a,b).toString('utf8').replace(/\0.*$/s,'');
    const prefix=str(345,500), name=(prefix?prefix+'/':'')+str(0,100);
    const size=parseInt(str(124,136).trim()||'0',8), mode=parseInt(str(100,108).trim()||'0',8);
    assert(Number.isSafeInteger(size) && size>=0 && offset+512+size<=bytes.length,'valid tar entry size');
    entries.set(name.replace(/^\.\//,''), {body:bytes.subarray(offset+512,offset+512+size), mode});
    offset+=512+Math.ceil(size/512)*512;
  }
  return entries;
}
const outer=untar(fs.readFileSync(file));
assert.equal(outer.get('debian-binary')?.body.toString().trim(),'2.0');
const control=untar(outer.get('control.tar.gz').body), data=untar(outer.get('data.tar.gz').body);
const metadata=control.get('control').body.toString();
assert(metadata.includes(`Version: ${version}-1`));
assert(metadata.includes('Architecture: aarch64_cortex-a53'));
assert(metadata.includes('libopenssl-legacy'));
const binary=data.get('usr/bin/alarm-push-center');
assert(binary.body.subarray(0,4).equals(Buffer.from([127,69,76,70])),'ELF binary');
assert.equal(binary.body[4],2,'64 bit');
assert.equal(binary.body.readUInt16LE(18),183,'AArch64 machine');
assert(binary.mode&0o111,'binary executable');
for(const name of ['workspace.css','workspace-ui.js','app.js','index.html','mobile-pairing.js'])
  assert(data.has('usr/bin/wwwroot/'+name),`web asset: ${name}`);
if(Number(version.split('.')[0])>1 || Number(version.split('.')[1])>=7) {
  const updater=data.get('usr/libexec/alarm-push-center-upgrade');
  assert(updater && (updater.mode&0o111),'executable upgrade helper');
  assert(!updater.body.includes(13),'Unix LF updater');
  assert(data.get('usr/bin/wwwroot/index.html').body.toString().includes('/updates.js?v='+version),'versioned update UI');
}
const init=data.get('etc/init.d/alarm-push-center');
assert(init && (init.mode&0o111),'executable init');
assert(!init.body.includes(13),'Unix LF init');
assert([...data.keys()].some(k=>k.includes('luci')&&k.includes('alarm-push-center')),'LuCI entry');
const cfg=JSON.parse(data.get('etc/alarm-push-center/config.json').body.toString());
for(const target of cfg.targets||[]) {
  assert.equal(target.enabled,false,'notification disabled');
  assert(!target.token && !target.webhook_url && !(target.recipients||[]).length,'no configured notification credentials');
  assert(!target.params?.bot_client_secret,'no application secret');
}
for(const source of cfg.sources||[]) assert.equal(source.enabled,false,'collector disabled');
const phone=JSON.parse(data.get('etc/alarm-push-center/phone-gateway.json').body.toString());
assert.equal(phone.Enabled,false,'phone disabled');
assert(!phone.Token,'no phone credential');
assert(!data.has('etc/alarm-push-center/web-password'),'no preinstalled password');
assert.equal([...data.keys()].filter(k=>/alarm-history|\.keystore$|\.jks$|\.env$/.test(k)).length,0,'no private state');
console.log(`IPK verified: ${version}, ARM64, ${data.size} entries, new UI, LuCI, defaults and permissions.`);
