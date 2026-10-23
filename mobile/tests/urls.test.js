import {test} from 'node:test';
import assert from 'node:assert/strict';
import {normalizeUrl,parsePairing} from '../src/api.js';
test('Separate plugin QR links reject router credentials and insecure public hosts',()=>{
 assert.equal(normalizeUrl('https://alarm.zxzyzy.cn/'),'https://alarm.zxzyzy.cn');
 assert.equal(normalizeUrl('http://192.168.1.1:5080/'),'http://192.168.1.1:5080');
 assert.equal(normalizeUrl('https://[2409:8d60::1]/'),'https://[2409:8d60::1]');
 for(const url of ['http://example.com','http://fdbad.com','https://root:secret@example.com','file:///tmp/test','https://example.com?secret=1'])assert.throws(()=>normalizeUrl(url));
 const code='a'.repeat(48);
 const p=parsePairing(`alarmhub://pair?url=${encodeURIComponent('https://alarm.zxzyzy.cn')}&code=${code}`);
 assert.equal(p.url,'https://alarm.zxzyzy.cn');assert.equal(p.code,code);
 for(const input of ['https://lu.zxzyzy.cn','alarmhub://router?url=https://lu.zxzyzy.cn','alarmhub://pair?url=https://alarm.zxzyzy.cn&code=short'])assert.throws(()=>parsePairing(input));
});
