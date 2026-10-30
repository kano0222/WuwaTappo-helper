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
      .replace('__COUNTMODE__','"event"')
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
  checks += await nativeChecks();
  console.log('PASS: ' + checks + ' boost receipt checks; no desktop app or account access.');
}
async function nativeChecks() {
  let checks=0;
  for(const scenario of ['normal','hundred','two-thousand','noop','partial','throw','logout','slot-change','overflow','busy','save-warning','save-throw','save-timeout','save-pending','report-during-save','missing-method','missing-save']) {
    let local=40,total=75175,right=2000,calls=0,saves=0,release;
    const subscriptions=new Set();
    const store={userInfo:{cuid:'test-account'},$onAction(f){subscriptions.add(f);return ()=>subscriptions.delete(f);},
      incrementLocalClickCount(){
        calls++;
        if(scenario==='throw'&&calls===3)throw Error('count failed');
        if(scenario!=='noop'&&!(scenario==='partial'&&calls===3))local++;
        if(scenario==='logout')store.userInfo.cuid='other-account';
        if(scenario==='slot-change')right++;
      },applyClickReportSuccess(value){
        const callbacks=[];
        for(const f of subscriptions)f({name:'applyClickReportSuccess',args:[value],after:f=>callbacks.push(f)});
        total=value;local=0;callbacks.forEach(f=>f());
      }};
    if(scenario==='missing-method')delete store.incrementLocalClickCount;
    const snapshot=()=>({accountKey:'key',loggedIn:true,locked:false,claimBusy:scenario==='busy',local,
      cumulative:scenario==='overflow'?Number.MAX_SAFE_INTEGER:total+local,
      available:total+local-74000,consumed:74000,serverTotal:total,left:1000,right});
    const warnings=[];
    const save=async(_value,options)=>{
      saves++;assert.equal(options.force,true);checks++;
      if(scenario==='save-warning')context.console.warn('[localClickCount] save failed');
      if(scenario==='save-throw')throw Error('disk failed');
      if(scenario==='save-timeout')await new Promise(()=>{});
      if(scenario==='save-pending')await new Promise(resolve=>{release=resolve;});
      if(scenario==='report-during-save')store.applyClickReportSuccess(75215);
    };
    const context=vm.createContext({testStore:store,readSnapshot:snapshot,
      console:{warn:(...args)=>warnings.push(args)},
      loadClicks:async()=>scenario==='missing-save'?{}:{save},
      window:{__TAURI_INTERNALS__:{invoke(){throw Error('native counting must not emit events');}}}});
    const delta=scenario==='hundred'?100:scenario==='two-thousand'?2000:7;
    const expression=source.replace('__STATE__','const s=testStore;const snapshot=readSnapshot;const accountKey="key";const sameAccount=()=>s.userInfo.cuid==="test-account";const bounded=p=>p;')
      .replace('__COUNTMODE__','"increment"').replace('await import(__CLICKMODULE__)','await loadClicks()')
      .replaceAll('__SAVECLICKS__','"save"').replaceAll('__DELTA__',String(delta))
      .replace('__BEFORE_AVAILABLE__','1215').replace('__BEFORE_CUMULATIVE__',scenario==='overflow'?'Number.MAX_SAFE_INTEGER':'75215')
      .replace('__BEFORE_OPERATIONID__','"native-op"').replace('__BEFORE_ACCOUNTKEY__','"key"');
    const originalWarn=context.console.warn;
    let result=await vm.runInContext(expression.replace('__RECOVER__','false').replace('const bounded=p=>p;',
      scenario==='save-pending'?'const bounded=(p,ms)=>ms===4000?Promise.reject(Error("observer timeout")):p;':
      scenario==='save-timeout'?'const bounded=(p,ms)=>ms===8000?Promise.reject(Error("save timeout")):p;':'const bounded=p=>p;'),context);
    if(scenario==='save-pending') {
      // Give the pending import/save continuation time to enter the persistence call.
      await new Promise(resolve=>setImmediate(resolve));
      assert.equal(result.outcome,'pending');checks++;
      const pending=await vm.runInContext(expression.replace('__RECOVER__','true'),context);
      assert.equal(pending.outcome,'pending');assert.equal(calls,delta);checks+=2;
      release();await vm.runInContext('globalThis.__TAPPO_HELPER_BOOST__.task',context);
      result=await vm.runInContext(expression.replace('__RECOVER__','true'),context);
    }
    const success=['normal','hundred','two-thousand','save-pending'].includes(scenario);
    assert.equal(result.outcome,scenario==='busy'?'skipped':success?'confirmed':'unknown',scenario);checks++;
    if(success){assert.equal(local,40+delta);assert.equal(calls,delta);assert.equal(saves,1);checks+=3;}
    if(['overflow','busy','missing-method','missing-save'].includes(scenario)){assert.equal(calls,0);assert.equal(saves,0);checks+=2;}
    const applied=calls;
    const replay=await vm.runInContext(expression.replace('__RECOVER__','false'),context);
    const recovered=await vm.runInContext(expression.replace('__RECOVER__','true'),context);
    assert.equal(replay.outcome,result.outcome);assert.equal(recovered.outcome,result.outcome);assert.equal(calls,applied);checks+=3;
    const wrong=await vm.runInContext(expression.replace('__RECOVER__','true').replace('const id="native-op"','const id="other"'),context);
    assert.equal(wrong.outcome,'unknown');assert.equal(subscriptions.size,0);assert.equal(context.console.warn,originalWarn);checks+=3;
  }
  return checks;
}
if (require.main === module) run().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = run;
