export function isNewer(a,b) {
 if(!/^\d+\.\d+\.\d+$/.test(a)||!/^\d+\.\d+\.\d+$/.test(b))throw Error('版本号格式不正确');
 const x=a.split('.').map(Number),y=b.split('.').map(Number);
 for(let i=0;i<3;i++)if(x[i]!==y[i])return x[i]>y[i];return false;
}
export function validateRelease(r,platform) {
 if(!r||!['android','ios'].includes(platform)||!/^\d+\.\d+\.\d+$/.test(r.version))throw Error('当前平台没有有效安装包');
 const name=`AlarmHub-${r.version}-${platform==='ios'?'unsigned.ipa':'android.apk'}`;
 const repo=['alarm-push-center','alarm-push-center-downloads'].find(repo=>r.url===`https://github.com/zxwzywzy/${repo}/releases/download/mobile-v${r.version}/${name}`);
 const base=`https://github.com/zxwzywzy/${repo}/releases/`;
 if(r.file_name!==name||!repo||r.notes_url!==`${base}tag/mobile-v${r.version}`||!/^[a-f0-9]{64}$/i.test(r.sha256)||!Number.isInteger(r.size)||r.size<1||r.size>300*1024*1024)throw Error('更新来源或校验信息无效');
 if(r.api_url&&!new RegExp(`^https://api\\.github\\.com/repos/zxwzywzy/${repo}/releases/assets/[1-9]\\d*$`).test(r.api_url))throw Error('更新资产来源无效');
 return r;
}
