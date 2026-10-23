const { test } = require('node:test');
const assert = require('node:assert/strict');
const {chart} = require('../wwwroot/telemetry-ui.js');
const series = [{key:'new_alarms',label:'新增报警',color:'#12c9e3'}, {key:'connected',label:'在线设备',color:'#a481ff',right:true}];
test('Telemetry chart never invents history before two real samples exist', () => {
  assert.match(chart([], series, 2), /积累真实/);
  assert.ok(!chart([{time:'2026-10-13T01:00:00Z',new_alarms:0,connected:0}],series,2).includes('polyline'));
});
test('Trend chart handles irregular sample times, independent axes, and invalid numeric values', () => {
  const points = [0,10,30].map((sec,i) => ({time:new Date(Date.UTC(2026,9,13,1,0,sec)).toISOString(),new_alarms:[0,5,NaN][i],connected:[1,2,0][i]}));
  const svg = chart(points,series,2);
  assert.equal((svg.match(/<polyline/g)||[]).length,2);
  assert.ok(svg.includes('315.0,'));
  assert.ok(svg.includes('stroke-dasharray="6 5"'));
  assert.ok(!svg.includes('NaN') && !svg.includes('Infinity'));
  assert.ok(svg.includes('aria-label') && svg.includes('<title>'));
});
test('Small charts use their actual width so axis labels stay readable', () => {
  const points=[{time:'2026-10-23T01:00:00Z',new_alarms:1,connected:1},{time:'2026-10-23T01:01:00Z',new_alarms:0,connected:1}];
  assert.match(chart(points,series,2,320),/viewBox="0 0 320 190"/);
  assert.match(chart(points,series,2,0),/viewBox="0 0 900 235"/);
});
