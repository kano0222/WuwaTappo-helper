import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';

// Execute the actual worker in an isolated VM: no app, account, or real child process.
for (const action of ['disconnect', 'stop', 'signal', 'poll']) {
  test(`${action} 退出路径：保留历史记录且仅显式恢复才启动桌宠`, () => {
    const result = spawnSync(process.execPath, ['--experimental-vm-modules', '--input-type=module', '-e', `
      import vm from 'node:vm'; import fs from 'node:fs'; import os from 'node:os';
      import path from 'node:path'; import {pathToFileURL,fileURLToPath} from 'node:url';
      import assert from 'node:assert/strict';
      import { ChestClock } from './lib/tappo.mjs';
      const root=fs.mkdtempSync(path.join(os.tmpdir(),'tappo-lifecycle-'));
      const runtime=path.join(root,'runtime'); fs.mkdirSync(runtime);
      const action=${JSON.stringify(action)};
      fs.writeFileSync(path.join(runtime,'control.json'),JSON.stringify({id:'test',action}));
      const journal='{"historical":true}'; fs.writeFileSync(path.join(runtime,'pending.json'),journal);
      let quits=0,spawns=0,closes=0,reads=0,loops=0,now=100000; const handlers={};
      const context=vm.createContext({console,process:{pid:999999,argv:['node','automation','--run'],env:{},on:(n,f)=>handlers[n]=f},
        fetch:async()=>{throw new Error('closed')},AbortSignal,performance});
      const modules={
        'node:fs':{default:fs},'node:path':{default:path},'node:url':{fileURLToPath},
        'node:child_process':{spawn:()=>{spawns++;return {once(n,f){if(n==='spawn')queueMicrotask(f)},unref(){}}}},
        './lib/tappo.mjs':{connect:async()=>{if(action==='signal')handlers.SIGTERM();return {snapshot:async()=>({loggedIn:true,locked:false}),server:async()=>{reads++;return {left:10,right:20}},quit:async()=>{quits++},close:()=>{closes++}}},
          chooseAction:()=>{throw new Error('unexpected account operation')},confirmClaim(){},confirmRejectedClaim(){},ChestClock:class extends ChestClock {constructor(){super(()=>now)}},sleep:async()=>{now+=30000;if(++loops===2)handlers.SIGTERM()}},
        './lib/storage.mjs':{replaceSnapshot:fs.renameSync},'./lib/settings.mjs':{loadSettings:()=>({appPath:'C:\\\\Fixture\\\\WuwaTappo.exe'})},
        './lib/modes.mjs':{modes:['brute']}
      };
      try {
        const worker=new vm.SourceTextModule(fs.readFileSync('automation.mjs','utf8'),{context,initializeImportMeta:m=>m.url=pathToFileURL(path.join(root,'automation.mjs')).href});
        await worker.link(name=>new vm.SyntheticModule(Object.keys(modules[name]),function(){for(const [k,v] of Object.entries(modules[name]))this.setExport(k,v)},{context}));
        await worker.evaluate();
        assert.equal(reads,action==='poll'?2:0);
        assert.equal(quits,action==='stop'?1:0); assert.equal(spawns,action==='stop'?1:0); assert.equal(closes,1);
        assert.equal(fs.readFileSync(path.join(runtime,'pending.json'),'utf8'),journal);
        const status=JSON.parse(fs.readFileSync(path.join(runtime,'status.json')));
        assert.equal(status.running,false); assert.equal(status.automationDisconnected,true);
        assert.equal(status.normalStartupRestored,action==='stop');
        assert.equal(fs.existsSync(path.join(runtime,'lock.json')),false);
      } finally {fs.rmSync(root,{recursive:true,force:true})}
    `], { cwd: new URL('..', import.meta.url), encoding: 'utf8', timeout: 10000 });
    assert.equal(result.status, 0, result.stderr || result.error?.message);
  });
}
