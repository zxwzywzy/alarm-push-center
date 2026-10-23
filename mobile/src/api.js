import { Capacitor, CapacitorHttp, registerPlugin } from '@capacitor/core';
export const SecureStore = registerPlugin('SecureStore');
export function normalizeUrl(input) {
  const u = new URL(input);
  if (!['https:','http:'].includes(u.protocol) || u.username || u.password || u.search || u.hash) throw Error('请输入不含密码的 HTTP/HTTPS 地址');
  const host = u.hostname.replace(/^\[|\]$/g, '');
  const privateHost = host==='localhost' || /^(127\.|10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(host) || (host.includes(':') && /^(fc|fd|fe80:)/i.test(host));
  if (u.protocol === 'http:' && !privateHost) throw Error('公网连接必须使用 HTTPS');
  return u.origin;
}
export function parsePairing(text) {
  const u = new URL(text.trim());
  if (u.protocol !== 'alarmhub:' || u.hostname !== 'pair') throw Error('这不是报警插件连接二维码');
  const url = normalizeUrl(u.searchParams.get('url') || '');
  const code = u.searchParams.get('code') || '';
  if (!/^[a-f0-9]{48}$/.test(code)) throw Error('连接码格式不正确');
  return {url,code};
}
export async function request(url, {method='GET',body,token}={}) {
  const headers = {'Content-Type':'application/json', ...(token ? {Authorization:`Bearer ${token}`} : {})};
  let response;
  if (Capacitor.isNativePlatform()) response = await CapacitorHttp.request({url,method,headers,data:body,connectTimeout:10000,readTimeout:20000});
  else {
    const r = await fetch(url,{method,headers,body:body === undefined ? undefined : JSON.stringify(body),signal:AbortSignal.timeout(20000)});
    response = {status:r.status,data:await r.json()};
  }
  const data = typeof response.data === 'string' ? JSON.parse(response.data) : response.data;
  if (response.status < 200 || response.status >= 300) throw Error(data?.error || (response.status===401 ? '授权已失效，请重新连接' : `请求失败 (${response.status})`));
  return data;
}
export async function ubus(url, session, object, method, args={}) {
  const data = await request(url+'/ubus',{method:'POST',body:{jsonrpc:'2.0',id:1,method:'call',params:[session,object,method,args]}});
  if (data.error || data.result?.[0] !== 0) throw Error(`路由器拒绝 ${object}.${method}，请检查账号权限或重新登录`);
  return data.result[1] || {};
}
export async function saveSecret(key,value) {
  if (!Capacitor.isNativePlatform()) throw Error('请在安卓/iPhone App 中保存授权，浏览器仅支持预览');
  await SecureStore.set({key,value});
}
export async function readSecret(key) { return Capacitor.isNativePlatform() ? (await SecureStore.get({key})).value : null; }
