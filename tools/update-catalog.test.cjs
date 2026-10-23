const {test}=require('node:test'),assert=require('node:assert/strict');
const {selectAssets}=require('./publish-update-catalog.cjs');
function release(tag,files){return {tag_name:tag,draft:false,prerelease:false,assets:files.map(name=>({name,state:'uploaded',size:100,digest:'sha256:'+'a'.repeat(64),browser_download_url:`https://github.com/zxwzywzy/alarm-push-center/releases/download/${tag}/${name}`}))};}
test('Catalog keeps newest complete verified platform artifacts and ignores drafts and missing hashes',()=>{
 const list=[release('v1.9.0',['AlarmPushCenter-Setup-1.9.0-win-x64.exe']),release('v1.10.0',['AlarmPushCenter-Setup-1.10.0-win-x64.exe']),release('openwrt-v1.7.0',['alarm-push-center_1.7.0-1_aarch64_cortex-a53.ipk']),release('mobile-v1.2.0',['AlarmHub-1.2.0-android.apk','AlarmHub-1.2.0-unsigned.ipa'])];
 const draft=release('v9.0.0',['AlarmPushCenter-Setup-9.0.0-win-x64.exe']);draft.draft=true;list.unshift(draft);
 assert.equal(selectAssets(list).platforms.windows.version,'1.10.0');
 const missing=structuredClone(list);missing.at(-1).assets[1].digest=null;
 assert.throws(()=>selectAssets(missing),/ios/);
 const bad=structuredClone(list);bad[1].assets[0].browser_download_url='https://evil.example/file';assert.throws(()=>selectAssets(bad),/origin/);
});
