// Run with Node.js for development checks; the distributed helper needs no Node runtime.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/Bridge/discover.js'), 'utf8');
const boundary = source.lastIndexOf('try{return {profile:await discoverProfile()}');
assert(boundary > 0);
const testSource = source.slice(0, boundary) + 'return {waitForSources,inspectSources};})()';
const main = 'case"boost-local-clicks":{const t=Math.max(0,Math.floor(Number(e.delta)||0));t>0&&(R.addLocalClickCount(t),Sn())}' +
  ' reward-anchor--left reward-anchor--right reward-btn [chest] openChest failed "cat-panel-action"';
const common = 'function userInfo(){} function isForceUpdateLocked(){}';
const quit = 'function q(){invoke("exit_app")}export{q as quitApp}';
const assets = { '/assets/common-A.js': common, '/assets/main-A.js': main, '/assets/quitApp-A.js': quit };
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

  // CDP is ready but the lazy main module and its dependencies arrive later.
  f = await fixture(t => t < 4 ? [paths[0]] : paths);
  check(f.api.inspectSources(await f.api.waitForSources()) === '/assets/quitApp-A.js');
  check(f.ticks() === 4 && f.reads.length === 3);

  f = await fixture(() => paths.flatMap(p => [p, 'http://tauri.localhost' + p + '?cached=1'])
    .concat('http://other.local/assets/main-A.js', '/api/account'));
  check((await f.api.waitForSources()).length === 3 && f.reads.length === 3);

  f = await fixture(() => [paths[0]]);
  await assert.rejects(f.api.waitForSources(), /主界面资源未就绪或版本不兼容/); checks++;
  check(f.ticks() === 32 && f.reads.length === 1);

  f = await fixture(() => []);
  await assert.rejects(f.api.waitForSources(), /主界面资源未就绪或版本不兼容/); checks++;
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
