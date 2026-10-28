const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/Bridge/boost.js'), 'utf8');
async function run() {
  let checks = 0;
  for (const scenario of ['normal', 'overwritten', 'report-before', 'partial-report', 'delayed', 'missing', 'no-receipt', 'logout', 'slot-change', 'invoke-error', 'invoke-hang', 'observer-timeout']) {
    let local = 40, serverTotal = 75175, ticks = 0, emits = 0, pending = false, right = 2000;
    let release;
    const subscriptions = new Set();
    const store = { userInfo: { cuid: 'test-account' }, $onAction(callback) { subscriptions.add(callback); return () => subscriptions.delete(callback); } };
    function action(name, value, apply) {
      const callbacks = [];
      for (const subscriber of subscriptions) subscriber({ name, args: [value], after: callback => callbacks.push(callback) });
      apply();
      for (const callback of callbacks) callback();
    }
    store.addLocalClickCount = delta => action('addLocalClickCount', delta, () => { local += delta; });
    store.applyClickReportSuccess = total => action('applyClickReportSuccess', total, () => { serverTotal = total; local = 0; });
    function snapshot() { return { accountKey:store.userInfo.cuid==='test-account'?'key':null, loggedIn: true, locked: false, claimBusy: false, cumulative: serverTotal + local,
      available: serverTotal + local - 74000, consumed: 74000, serverTotal, local, left: 1000, right }; }
    const expression = source.replace('__STATE__', 'const s=testStore; const snapshot=readSnapshot; const accountKey="key"; const sameAccount=()=>s.userInfo.cuid==="test-account"; const bounded=p=>p;')
      .replaceAll('__DELTA__', '7').replace('__BEFORE_AVAILABLE__', '1215').replace('__BEFORE_CUMULATIVE__', '75215')
      .replace('__BEFORE_OPERATIONID__','"op"').replace('__BEFORE_ACCOUNTKEY__','"key"');
    const context=vm.createContext({
      testStore: store, readSnapshot: snapshot,
      setTimeout(callback) { ticks++; if(scenario==='observer-timeout'){release=callback;return;} if (pending && ticks === 3) store.addLocalClickCount(7); callback(); },
      window: { __TAURI_INTERNALS__: { async invoke() {
        emits++;
        if (scenario === 'missing') return;
        if (scenario === 'delayed') { pending = true; return; }
        if (scenario === 'report-before') store.applyClickReportSuccess(75215);
        if (scenario === 'no-receipt') local += 7; else store.addLocalClickCount(7);
        if (scenario === 'logout') store.userInfo.cuid = 'other-account';
        if (scenario === 'slot-change') right++;
        if (['overwritten', 'no-receipt', 'logout', 'slot-change'].includes(scenario)) store.applyClickReportSuccess(75215);
        if (scenario === 'partial-report') store.applyClickReportSuccess(75218);
        if (scenario === 'invoke-error') throw new Error('event call failed');
        if (scenario === 'invoke-hang') return new Promise(()=>{});
      } } },
    });
    {
      let result = await vm.runInContext(expression.replace('__RECOVER__','false').replace('const bounded=p=>p;',scenario==='observer-timeout'?'const bounded=p=>Promise.reject(Error("observer timeout"));':'const bounded=p=>p;'),context);
      if(scenario==='observer-timeout') {
        assert.equal(result.outcome,'pending');checks++;
        const stillPending=await vm.runInContext(expression.replace('__RECOVER__','true'),context);
        assert.equal(stillPending.outcome,'pending');assert.equal(emits,1);checks+=2;
        release();await vm.runInContext('globalThis.__TAPPO_HELPER_BOOST__.task',context);
        result=await vm.runInContext(expression.replace('__RECOVER__','true'),context);
      }
      const unknown=['partial-report','missing','no-receipt','logout','slot-change'].includes(scenario);
      assert.equal(result.outcome,unknown?'unknown':'confirmed');checks++;
      assert.equal(!!result.boostOverwritten, scenario === 'overwritten'); checks++;
      if (scenario === 'overwritten') {
        assert.equal(result.boostAppliedTotal, 75222); assert.equal(result.boostReportTotal, 75215);
        assert.equal(result.cumulative, 75215); assert.equal(local, 0); checks += 4;
      }
      if (['normal', 'report-before', 'delayed'].includes(scenario)) { assert.equal(result.cumulative, 75222); checks++; }
      if (scenario === 'missing') { assert.equal(ticks, 180); checks++; }
      const recovered=await vm.runInContext(expression.replace('__RECOVER__','true'),context);
      assert.equal(recovered.outcome,result.outcome);checks++;
      const wrong=await vm.runInContext(expression.replace('__RECOVER__','true').replace('const id="op"','const id="other"'),context);
      assert.equal(wrong.outcome,'unknown');checks++;
    }
    assert.equal(emits, 1); assert.equal(subscriptions.size, 0); checks += 2;
  }
  console.log('PASS: ' + checks + ' boost receipt checks; no desktop app or account access.');
}
if (require.main === module) run().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = run;
