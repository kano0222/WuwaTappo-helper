// Run with Node.js for development checks; the distributed helper needs no Node runtime.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/Bridge/discover.js'), 'utf8');
const boundary = source.lastIndexOf('try{return {profile:await discoverProfile()}');
assert(boundary > 0);
const testSource = source.slice(0, boundary) + 'return {waitForSources,inspectSources,inspectCountMode,inspectModule,inspectClickModule,discoveryFailure};})()';
const main = 'case"boost-local-clicks":{const t=Math.max(0,Math.floor(Number(e.delta)||0));t>0&&(R.addLocalClickCount(t),Sn())}' +
  ' "cat-panel-action"';
const common = 'function userInfo(){} function isForceUpdateLocked(){}';
const quit = 'function q(){invoke("exit_app")}export{q as quitApp}';
const assets = { '/assets/common-A.js': common, '/assets/main-A.js': main, '/assets/quitApp-A.js': quit, '/assets/count-A.js':'"save_key_press_count"' };
const paths = Object.keys(assets);
let checks = 0;
function check(value) { assert(value); checks++; }
async function fixture(resources, overrides = {}) {
  let now = 0, ticks = 0;
  const reads = [];
  const context = {
    URL, AbortSignal,
    Date: { now: () => now },
    location: { href: 'http://tauri.localhost/', origin: 'http://tauri.localhost' },
    document: { scripts: [] },
    performance: { getEntriesByType: () => resources(ticks).map(name => ({ name })) },
    setTimeout: (callback, ms) => { ticks++; now += ms; callback(); },
    fetch: async p => { reads.push(p); return { ok: true, text: async () => (overrides[p] ?? assets[p]) }; },
  };
  const api = await vm.runInNewContext(testSource, context);
  return { api, reads, ticks: () => ticks };
}
(async () => {
  let f = await fixture(() => paths);
  check(f.api.inspectSources(await f.api.waitForSources()) === '/assets/quitApp-A.js');
  check(f.ticks() === 0);
  check(f.api.inspectCountMode(Object.entries(assets))==='event');
  const nativeMain='case"pet-clap":if(locked())break;store.incrementLocalClickCount(),animate(); "cat-panel-action"';
  const nativeCommon=common+' incrementLocalClickCount(){this.localClickCount>=Number.MAX_SAFE_INTEGER||(this.localClickCount+=1)}';
  const nativeAssets={...assets,'/assets/main-A.js':nativeMain,'/assets/common-A.js':nativeCommon};
  f=await fixture(()=>paths,nativeAssets);
  check(f.api.inspectSources(await f.api.waitForSources())==='/assets/quitApp-A.js');
  check(f.api.inspectCountMode(Object.entries(nativeAssets))==='increment');
  assert.throws(()=>f.api.inspectSources(Object.entries({...nativeAssets,'/assets/common-A.js':common})),/原生计数契约/);checks++;
  assert.throws(()=>f.api.inspectSources(Object.entries({...nativeAssets,'/assets/common-A.js':nativeCommon.replace('+=1','+=2')})),/原生计数契约/);checks++;
  assert.throws(()=>f.api.inspectSources(Object.entries({...nativeAssets,'/assets/main-A.js':nativeMain.replace('incrementLocalClickCount()','setLocalClickCount(1)')})),/原生计数契约/);checks++;

  const module=vm.runInNewContext(`(()=>{
    const transport={get(){throw Error('must not request')},post(){throw Error('must not claim')}};
    const lock={value:false};
    function userInfo(){return transport.get("api/frontend/user/me",{},{})}
    function openChest(e){return transport.post("api/frontend/chests/open",{place:e.place,click_num:e.click_num},{})}
    function isForceUpdateLocked(){return lock.value}
    function isApiSuccess(c){return c===0}
    function unwrapApiData(e){const c=Number(e?.code);if(!Number.isFinite(c)||!isApiSuccess(c))throw Error('invalid');return e.data??e.result}
    return {a:'cn-cn-prod',b:{isLoggedIn:true,totalClickCount:0,cumulativeClickCount:0,localClickCount:0,serverClickConsume:0,serverClickTotal:0,
      userInfo:{},addLocalClickCount(){},applyClickReportSuccess(){},applyChestClickConsume(){},updateUserInfo(){},$onAction(){}},
      c:userInfo,d:openChest,e:isForceUpdateLocked,f:unwrapApiData};
  })()`);
  check(f.api.inspectModule(module).openChest==='d');
  check(f.api.inspectModule({...module,b:{...module.b,addLocalClickCount:undefined,incrementLocalClickCount(){}}}).store==='b');
  assert.throws(()=>f.api.inspectModule({...module,b:{...module.b,addLocalClickCount:undefined}}),/计数状态/);checks++;
  check(f.api.inspectModule({...module,d:undefined,newAlias:module.d}).openChest==='newAlias');
  assert.throws(()=>f.api.inspectModule({...module,d:undefined}),error=>{check(f.api.discoveryFailure(error).failureKind==='incompatible');return /宝箱领取接口/.test(error.message)});checks++;
  check(f.api.discoveryFailure(new Error('fetch timed out')).failureKind==='connection-failed');
  assert.throws(()=>f.api.inspectModule({...module,duplicate:module.d}),/宝箱领取接口/);checks++;
  assert.throws(()=>f.api.inspectModule({...module,b:{...module.b,applyChestClickConsume:undefined}}),/计数状态/);checks++;
  assert.throws(()=>f.api.inspectModule({...module,b:{...module.b,updateUserInfo:undefined}}),/计数状态/);checks++;
  const persistence=vm.runInNewContext(`({async save(e,n){const count=e??store.localClickCount;try{if(n?.force)await invoke("save_key_press_count",{count})}catch(e){console.warn('[localClickCount] save failed',e)}}})`);
  check(f.api.inspectClickModule(persistence)==='save');
  assert.throws(()=>f.api.inspectClickModule({}),/保存能力/);checks++;
  assert.throws(()=>f.api.inspectClickModule({...persistence,duplicate:persistence.save}),/保存能力/);checks++;

  // CDP is ready but the lazy main module and its dependencies arrive later.
  f = await fixture(t => t < 4 ? [paths[0]] : paths);
  check(f.api.inspectSources(await f.api.waitForSources()) === '/assets/quitApp-A.js');
  check(f.ticks() === 4 && f.reads.length === 4);

  f = await fixture(() => paths.flatMap(p => [p, 'http://tauri.localhost' + p + '?cached=1'])
    .concat('http://other.local/assets/main-A.js', '/api/account'));
  check((await f.api.waitForSources()).length === 4 && f.reads.length === 4);

  f = await fixture(() => [paths[0]]);
  await assert.rejects(f.api.waitForSources(), error=>{check(f.api.discoveryFailure(error).failureKind==='main-not-ready');return /主界面资源未就绪或版本不兼容/.test(error.message)}); checks++;
  check(f.ticks() === 32 && f.reads.length === 1);

  f = await fixture(() => []);
  await assert.rejects(f.api.waitForSources(), error=>{check(f.api.discoveryFailure(error).failureKind==='main-not-ready');return /主界面资源未就绪或版本不兼容/.test(error.message)}); checks++;
  check(f.reads.length === 0);

  f = await fixture(() => paths.concat('/assets/other-A.js'), { '/assets/other-A.js': main });
  assert.throws(() => f.api.inspectSources([...Object.entries(assets), ['/assets/other-A.js', main]]), /补数处理器缺失或不唯一/); checks++;
  assert.throws(() => f.api.inspectSources(Object.entries({...assets, '/assets/main-A.js': main.replace('Math.floor', 'Math.round')})), /入口契约已变化/); checks++;
  assert.throws(() => f.api.inspectSources(Object.entries({...assets, '/assets/quitApp-A.js': 'unsupported'})), /正常退出能力/); checks++;

  f = await fixture(() => Array.from({length: 81}, (_, i) => '/assets/a' + i + '.js'));
  await assert.rejects(f.api.waitForSources(), /应用资源列表异常/); checks++;
  check(f.reads.length === 0);
  console.log('PASS: ' + checks + ' discovery checks; no desktop app or account access.');
})().catch(error => { console.error(error); process.exitCode = 1; });
