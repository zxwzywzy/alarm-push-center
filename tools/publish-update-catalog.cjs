// Called only after release assets have been uploaded. No private config or signing keys are read.
const fs = require('node:fs');
const {execFileSync} = require('node:child_process');
const repo = process.env.UPDATE_RELEASE_REPOSITORY || 'zxwzywzy/alarm-push-center';
if(!['zxwzywzy/alarm-push-center','zxwzywzy/alarm-push-center-downloads'].includes(repo))throw Error('Untrusted release repository');
function selectAssets(releases) {
  const platforms = {};
  const definitions = [
    ['windows', /^v(\d+\.\d+\.\d+)$/, v=>`AlarmPushCenter-Setup-${v}-win-x64.exe`],
    ['openwrt', /^openwrt-v(\d+\.\d+\.\d+)$/, v=>`alarm-push-center_${v}-1_aarch64_cortex-a53.ipk`],
    ['android', /^mobile-v(\d+\.\d+\.\d+)$/, v=>`AlarmHub-${v}-android.apk`],
    ['ios', /^mobile-v(\d+\.\d+\.\d+)$/, v=>`AlarmHub-${v}-unsigned.ipa`],
  ];
  const newer=(a,b)=> {const x=a.split('.').map(Number),y=b.split('.').map(Number);for(let i=0;i<3;i++){if(x[i]!==y[i])return x[i]>y[i];}return false;};
  for(const release of releases) {
    if(release.draft || release.prerelease) continue;
    for(const [platform,pattern,file] of definitions) {
      const match=release.tag_name.match(pattern); if(!match)continue;
      const version=match[1],name=file(version);
      const asset=release.assets.find(a=>a.name===name && a.state==='uploaded');
      if(!asset || !/^sha256:[a-f0-9]{64}$/.test(asset.digest||'') || !(asset.size>0 && asset.size<=300*1024*1024)) continue;
      const url=`https://github.com/${repo}/releases/download/${release.tag_name}/${name}`;
      if(asset.browser_download_url!==url)throw Error('Unexpected asset origin');
      if(!platforms[platform] || newer(version,platforms[platform].version))
        platforms[platform]={version,file_name:name,url,sha256:asset.digest.slice(7),size:asset.size,notes_url:`https://github.com/${repo}/releases/tag/${release.tag_name}`,...(Number.isSafeInteger(asset.id)&&asset.id>0?{api_url:`https://api.github.com/repos/${repo}/releases/assets/${asset.id}`}:{})};
    }
  }
  for(const p of ['windows','openwrt','android','ios'])if(!platforms[p])throw Error(`No complete verified release for ${p}`);
  return {schema:1,platforms};
}
if(require.main===module) {
  const releases=JSON.parse(execFileSync('gh',['api',`repos/${repo}/releases?per_page=100`],{encoding:'utf8'}));
  const catalog=selectAssets(releases),tag=process.env.RELEASE_TAG||'';
  const groups=tag.startsWith('openwrt-v')?['openwrt']:tag.startsWith('mobile-v')?['android','ios']:tag.startsWith('v')?['windows']:[];
  const version=tag.replace(/^(?:openwrt-|mobile-)?v/,'');
  if(groups.some(p=>catalog.platforms[p].version!==version))throw Error('Release assets not ready; retry after all uploads finish');
  fs.mkdirSync('updates',{recursive:true});
  fs.writeFileSync('updates/stable.json',JSON.stringify(catalog,null,2)+'\n');
  console.log('Verified release catalog generated.');
}
module.exports={selectAssets};
