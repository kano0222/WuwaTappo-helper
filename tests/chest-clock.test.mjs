import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ChestClock } from '../lib/tappo.mjs';

test('接口首次读取及每30秒刷新；事务读取也更新显示并推迟轮询', async () => {
  let now=1000, reads=0;
  const clock=new ChestClock(()=>now);
  const read=async()=>{ reads++; return {left:90000,right:120000}; };
  await clock.poll(read);
  assert.equal(clock.value.updatedAt,1000);
  for (now=2000;now<31000;now+=1000) await clock.poll(read);
  assert.equal(reads,1);
  await clock.poll(read); assert.equal(reads,2);
  now=40000; clock.accept({left:180000,right:120000});
  now=61000; await clock.poll(read); assert.equal(reads,2);
  assert.equal(clock.value.left,180000);
});

test('失败不伪造新时间或高频重试；下次只读成功清除查询错误', async () => {
  let now=0, reads=0;
  const clock=new ChestClock(()=>now);
  clock.accept({left:123,right:456});
  now=30000;
  const fail=async()=>{reads++;throw new Error('timeout')};
  await clock.poll(fail);
  assert.ok(clock.value.error); assert.equal(clock.value.updatedAt,0);
  now=31000; await clock.poll(fail); assert.equal(reads,1);
  now=60000; await clock.poll(async()=>({left:789,right:456}));
  assert.equal(clock.value.error,null); assert.equal(clock.value.updatedAt,60000);
  clock.reset(); assert.equal(clock.value.updatedAt,null);
  await clock.poll(async()=>({left:800,right:456}));
  assert.equal(clock.value.left,800);
});

test('缺失、负数、非整数时间不能作为有效接口结果', async () => {
  for (const bad of [undefined,null,NaN,-1,1.5,'100']) {
    const clock=new ChestClock(()=>0);
    await clock.poll(async()=>({left:bad,right:10}));
    assert.equal(clock.value.updatedAt,null); assert.ok(clock.value.error);
  }
});
