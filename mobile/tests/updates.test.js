import {test} from 'node:test';
import assert from 'node:assert/strict';
import {validateRelease,isNewer} from '../src/update-core.js';
test('Updates compare numeric versions and reject wrong origin, identity, digest and platform',()=>{
 const item={version:'1.10.0',file_name:'AlarmHub-1.10.0-android.apk',url:'https://github.com/zxwzywzy/alarm-push-center/releases/download/mobile-v1.10.0/AlarmHub-1.10.0-android.apk',sha256:'a'.repeat(64),size:1024,notes_url:'https://github.com/zxwzywzy/alarm-push-center/releases/tag/mobile-v1.10.0'};
 assert.equal(isNewer('1.10.0','1.9.0'),true);assert.equal(isNewer('1.9.0','1.10.0'),false);assert.equal(isNewer('1.2.0','1.2.0'),false);
 assert.equal(validateRelease(item,'android'),item);
 for(const patch of [{url:'http://127.0.0.1/internal'},{url:item.url+'?other=1'},{sha256:'bad'},{size:0},{file_name:'../file.apk'},{version:'latest'}])assert.throws(()=>validateRelease({...item,...patch},'android'));
 assert.throws(()=>validateRelease(item,'ios'));assert.throws(()=>validateRelease(item,'web'));
 assert.throws(()=>validateRelease({...item,api_url:'https://api.github.com/repos/other/project/releases/assets/1'},'android'));
 assert.equal(validateRelease({...item,api_url:'https://api.github.com/repos/zxwzywzy/alarm-push-center/releases/assets/123'},'android').size,1024);
});
