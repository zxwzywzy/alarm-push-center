import {test} from 'node:test';import assert from 'node:assert/strict';import {parseField} from '../src/fields.js';
test('Device edits preserve protocol parameter types',()=>{
 assert.equal(parseField('false',true),false);assert.equal(parseField('300',1),300);
 assert.deepEqual(parseField('{"node":"ns=4;s=Alarm"}',{}),{node:'ns=4;s=Alarm'});
 assert.equal(parseField('ns=4;s=Alarm',''),'ns=4;s=Alarm');
 assert.throws(()=>parseField('yes',false));assert.throws(()=>parseField('Infinity',1));assert.throws(()=>parseField('',1));assert.throws(()=>parseField('{bad}',{}));
});
