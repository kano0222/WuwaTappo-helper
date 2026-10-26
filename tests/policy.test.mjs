import { test } from 'node:test';
import assert from 'node:assert/strict';
import { chooseAction, confirmClaim, confirmRejectedClaim, profiles, stateSourceFor } from '../lib/tappo.mjs';

const base = { loggedIn: true, locked: false, available: 2000, cumulative: 4000, consumed: 2000, time: 5000, left: 6000, right: 7000, buttons: { left: true, right: true } };
test('不提前领取，即使旧按钮仍在', () => assert.equal(chooseAction(base).kind, 'wait'));
test('足够余额时不重复加次数', () => assert.equal(chooseAction({ ...base, available: 3000 }).kind, 'wait'));
test('到期后仅补足2000，不按固定数无休止累加', () => assert.deepEqual(chooseAction({ ...base, left: 5000, available: 721 }), { kind: 'boost', delta: 1279, delayMs: 0 }));

test('只有暴力模式等待宝箱到期，其他模式提前累积', () => {
  const s = { ...base, available: 0, buttons: { left: false, right: false } };
  assert.equal(chooseAction(s, { mode: 'brute' }).kind, 'wait');
  assert.deepEqual(chooseAction(s, { mode: 'clicker' }), { kind: 'boost', delta: 100, delayMs: 1000 });
  assert.deepEqual(chooseAction(s, { mode: 'human', random: () => 0.5 }), { kind: 'boost', delta: 5, delayMs: 1250 });
});
test('提前累积只到2000，不提前领取，并在消耗后继续补充', () => {
  for (const mode of ['clicker','human']) {
    assert.equal(chooseAction({ ...base, available: 1999 }, { mode, random: () => 0.5 }).delta, 1);
    assert.equal(chooseAction(base, { mode }).kind, 'wait');
    assert.equal(chooseAction({ ...base, available: 0 }, { mode, random: () => 0.5 }).kind, 'boost');
    assert.equal(chooseAction({ ...base, available: 0 }, { mode, boostDue: false }).kind, 'wait');
  }
});
test('连点器每批最多100，最后一批不超过目标', () => {
  assert.deepEqual(chooseAction({ ...base, left: 5000, available: 0 }, { mode: 'clicker' }), { kind: 'boost', delta: 100, delayMs: 1000 });
  assert.equal(chooseAction({ ...base, available: 1990 }, { mode: 'clicker' }).delta, 10);
});
test('等待下一批时不会重复补数，但有足够余额仍可领取', () => {
  assert.equal(chooseAction({ ...base, left: 5000, available: 0 }, { mode: 'clicker', boostDue: false }).kind, 'wait');
  assert.equal(chooseAction({ ...base, left: 5000 }, { boostDue: false }).kind, 'claim');
});
test('波动模式有低速批次与间歇，且最后一批不超过目标', () => {
  assert.deepEqual(chooseAction({ ...base, left: 5000, available: 0 }, { mode: 'human', random: () => 0.5 }), { kind: 'boost', delta: 5, delayMs: 1250 });
  assert.deepEqual(chooseAction({ ...base, left: 5000, available: 0 }, { mode: 'human', random: () => 0 }), { kind: 'wait', reason: '波动模式短暂间歇', delayMs: 2000 });
  assert.equal(chooseAction({ ...base, available: 1999 }, { mode: 'human', random: () => 0.9 }).delta, 1);
  assert.throws(() => chooseAction(base, { mode: 'bad' }), /未知增加模式/);
});
test('两个箱子串行选择，余额1000也只领取一个', () => assert.deepEqual(chooseAction({ ...base, available: 1000, left: 5000, right: 4000 }), { kind: 'claim', side: 'left' }));

test('两个箱子同时到期：确认左侧后才选择右侧，动画期间不操作', () => {
  const before = { ...base, left: 4000, right: 4000 };
  assert.deepEqual(chooseAction(before), { kind: 'claim', side: 'left' });
  const afterLeft = { ...before, consumed: 3000, available: 1000, left: 9000, claimBusy: true };
  assert.equal(chooseAction(afterLeft, { mode: 'human' }).kind, 'wait');
  afterLeft.claimBusy = false;
  confirmClaim('left', before, afterLeft, before, afterLeft);
  assert.deepEqual(chooseAction(afterLeft, { mode: 'human' }), { kind: 'claim', side: 'right' });
  const afterRight = { ...afterLeft, consumed: 4000, available: 0, right: 9000 };
  confirmClaim('right', afterLeft, afterRight, afterLeft, afterRight);
  assert.equal(chooseAction(afterRight, { mode: 'clicker' }).delta, 100);
});

test('错误槽位、重复扣除、动画未结束或服务器不同步均不确认成功', () => {
  const before = { ...base, left: 4000, right: 4000 };
  const correct = { ...before, consumed: 3000, available: 1000, left: 9000, claimBusy: false };
  assert.doesNotThrow(() => confirmClaim('left', before, correct, before, correct));
  for (const bad of [
    { ...correct, left: 4000, right: 9000 },
    { ...correct, right: 9000 },
    { ...correct, consumed: 4000 },
    { ...correct, claimBusy: true },
  ]) assert.throws(() => confirmClaim('left', before, bad, before, bad), /不会自动重发/);
  assert.throws(() => confirmClaim('left', before, correct, before, { ...correct, left: 9500 }), /不会自动重发/);
});

test('明确冷却拒绝并且未扣数才允许结束待确认记录，不自动再次领取', () => {
  assert.match(confirmRejectedClaim(70006, base, base, base, base), /冷却中/);
  assert.throws(() => confirmRejectedClaim(70006, base, { ...base, consumed: 3000 }, base, base), /保留记录/);
  assert.throws(() => confirmRejectedClaim(70006, base, base, base, { ...base, right: 9000 }), /保留记录/);
  assert.throws(() => confirmRejectedClaim(12345, base, base, base, base), /保留记录/);
});

test('保留已知版本参考配置，未校验的对象不能生成调用链', () => {
  assert.deepEqual(profiles.map(p => p.version), ['1.0.0','1.0.1']);
  for (const profile of profiles) {
    const source = stateSourceFor(profile);
    assert.ok(source.includes(profile.commonPath));
    assert.ok(source.includes('m[' + JSON.stringify(profile.exports.store) + ']'));
    assert.ok(source.includes('m[' + JSON.stringify(profile.exports.userInfo) + ']'));
    assert.match(source, /cn-cn-prod/);
  }
  assert.throws(() => stateSourceFor({version:'unknown'}), /不支持/);
});
test('到期但入口不可用时不绕过应用状态', () => assert.equal(chooseAction({ ...base, left: 0, buttons: { left: false, right: false } }).kind, 'wait'));
test('未登录或强制更新时不变更计数', () => {
  assert.equal(chooseAction({ ...base, loggedIn: false, available: 0 }).kind, 'wait');
  assert.equal(chooseAction({ ...base, locked: true, available: 0 }).kind, 'wait');
});
test('不把缺失或损坏的时间和计数当作到期', () => {
  assert.throws(() => chooseAction({ ...base, left: NaN }));
  assert.throws(() => chooseAction({ ...base, available: -1 }));
});
