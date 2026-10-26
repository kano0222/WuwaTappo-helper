import { test } from 'node:test';
import assert from 'node:assert/strict';
import { inspectModule, inspectSources } from '../lib/compatibility.mjs';
import { profiles } from '../lib/tappo.mjs';

function moduleFixture(keys) {
  const lock = { value: false };
  function isForceUpdateLocked(){return lock.value}
  // Deliberately undefined: discovery must never call this network function.
  function userInfo(){return jsonpClient.get("api/frontend/user/me",{},{})}
  const isApiSuccess = code => code === 0;
  function unwrapApiData(r) {
    const code = Number(r?.code);
    if (!Number.isFinite(code) || !isApiSuccess(code)) throw new Error('bad response');
    return r.data??r.result;
  }
  // Match minified shipping signatures while keeping fixture implementation independent.
  const decode = new Function('isApiSuccess', 'return '+unwrapApiData.toString().replace(/\s+/g,' ').replace('r.data??r.result','r.data??r.result'))(isApiSuccess);
  const values = { environment: 'cn-cn-prod', store: { isLoggedIn: false, totalClickCount: 0, cumulativeClickCount: 0,
    localClickCount: 0, serverClickConsume: 0, serverClickTotal: 0, userInfo: null, addLocalClickCount() {} },
  locked: isForceUpdateLocked, userInfo, decode };
  return Object.fromEntries(Object.entries(keys).map(([role, key]) => [key, values[role]]));
}

test('已知两版及未知版本的导出重命名均按能力识别，不调用用户接口', () => {
  for (const keys of [...profiles.map(p => p.exports), {environment:'newEnv',store:'newStore',locked:'newLock',decode:'newDecode',userInfo:'newUser'}]) {
    assert.deepEqual(inspectModule(moduleFixture(keys)), keys);
  }
});

test('接口缺失、计数字段损坏、环境不符及多个候选均拒绝', () => {
  const keys = profiles[0].exports;
  for (const change of [m=>delete m[keys.locked], m=>m[keys.store].totalClickCount=NaN,
    m=>m[keys.environment]='test', m=>m.duplicate=m[keys.store], m=>m[keys.userInfo]=function userInfo(){throw new Error('must not call')}]) {
    const m=moduleFixture(keys); change(m); assert.throws(()=>inspectModule(m), /兼容性检查失败/);
  }
});

const sourceFixture = () => [
  ['/assets/main-new.js', 'case"boost-local-clicks":{const n=Math.max(0,Math.floor(Number(event.delta)||0));n>0&&(store.addLocalClickCount(n));break} reward-anchor--left reward-anchor--right reward-btn [chest] openChest failed'],
  ['/assets/events-new.js', '"cat-panel-action"'],
  ['/assets/quitApp-new.js', 'async function quit(){await invoke("exit_app")}export{quit as quitApp};']
];
test('资源文件名变化可发现；事件、槽位或退出契约变化拒绝', () => {
  assert.equal(inspectSources(sourceFixture()), '/assets/quitApp-new.js');
  for (const token of ['addLocalClickCount','reward-anchor--right','cat-panel-action','exit_app']) {
    assert.throws(()=>inspectSources(sourceFixture().map(([p,s])=>[p,s.replace(token,'changed')])), /兼容性检查失败/);
  }
  assert.throws(()=>inspectSources([...sourceFixture(),sourceFixture()[0]]), /不唯一/);
});
