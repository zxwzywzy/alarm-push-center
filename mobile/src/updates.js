import {Capacitor,registerPlugin} from '@capacitor/core';
import {request} from './api.js';
import {validateRelease,isNewer} from './update-core.js';
import packageInfo from '../package.json';
const native=registerPlugin('AppUpdate');
const manifests=['https://api.github.com/repos/zxwzywzy/alarm-push-center-downloads/contents/updates/stable.json?ref=main','https://api.github.com/repos/zxwzywzy/alarm-push-center/contents/updates/stable.json?ref=main'];
let busy=false,lastCheck=0,currentVersion=packageInfo.version,release=null,message='打开 App 时自动检查，安装会保留连接资料。';
let notice=()=>{};
export function updateMarkup(escape) {
 return `<section class="panel app-updates"><h2>应用更新</h2><p class="fine">当前版本 ${escape(currentVersion)}</p><p id="app-update-status" role="status">${escape(message)}</p><div class="actions"><button data-action="update-check" ${busy?'disabled':''}>检查更新</button><button class="primary" data-action="update-install" ${!release||busy?'disabled':''}>${Capacitor.getPlatform()==='ios'?'下载 IPA（需自签）':'下载并更新'}</button></div><p class="fine">安卓使用系统安装确认；iPhone 原生包需要重新自签。请沿用相同签名和包标识以保留授权。当前为前台检查，不提供后台推送。</p></section>`;
}
export function setUpdateNotice(callback) {notice=callback;}
export async function checkUpdates(automatic=false) {
 if(busy||automatic&&Date.now()-lastCheck<6*3600000||!Capacitor.isNativePlatform())return;
 busy=true;
 try {
   const info=await native.getInfo();currentVersion=info.version;
   let catalog,lastError;
   for(const address of manifests){try{
     const metadata=await request(address);
     if(metadata.encoding!=='base64'||typeof metadata.content!=='string'||metadata.content.length>180000)throw Error('更新目录格式不正确');
     catalog=JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(metadata.content.replace(/\s/g,'')),c=>c.charCodeAt(0))));break;
   }catch(error){lastError=error;}}
   if(!catalog)throw lastError||Error('暂时无法读取更新目录');
   const platform=Capacitor.getPlatform();
   if(catalog.schema!==1)throw Error('更新目录格式不正确');
   const item=validateRelease(catalog.platforms?.[platform],platform);
   release=isNewer(item.version,currentVersion)?item:null;
   lastCheck=Date.now();message=release?`发现新版本 ${release.version}`:'当前已是最新版本。';
   if(automatic&&release)notice(message+'，可在连接页更新。');
 }catch(error){message='检查更新失败，请稍后重试；不会影响当前连接。';if(!automatic)notice(message);}
 finally{busy=false;document.dispatchEvent(new Event('app-update-changed'));}
}
export async function installUpdate() {
 if(busy||!release)return;
 busy=true;message=Capacitor.getPlatform()==='ios'?'正在下载并校验 IPA，请自签后覆盖安装。':'正在下载、核对文件及原安装签名…';
 document.dispatchEvent(new Event('app-update-changed'));
 try {
   const result=await native.installUpdate({url:release.url,apiUrl:release.api_url||'',sha256:release.sha256,size:release.size,version:release.version});
   message=result.status==='permission_required'?'请允许此 App 安装更新，返回后再次点击“下载并更新”。':Capacitor.getPlatform()==='ios'?'IPA 已校验，请从分享菜单保存或交给自签工具；沿用当前包标识。':'请在系统安装页面确认更新，连接资料会保留。';
   notice(message);
 }catch(error){message=error.message||'更新未完成，原应用保持可用。';notice(message);}
 finally {busy=false;document.dispatchEvent(new Event('app-update-changed'));}
}
document.addEventListener('visibilitychange',()=>{if(!document.hidden)checkUpdates(true);});
