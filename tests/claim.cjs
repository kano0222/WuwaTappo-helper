// Transaction/recovery checks use an isolated fake app, never a desktop app or account.
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../src/Bridge/claim.js'),'utf8');
const drain=async()=>{for(let i=0;i<25;i++)await Promise.resolve()};
function fixture(kind='success',side='left'){
  let time=5000,calls=0,reads=0,applies=0,saves=0,refreshes=0,local=0,release,saveRelease,bagReads=0,emits=0,saveTimedOut=false;
  let server={total_click_count:3000,total_click_consume:1000,left_treasure_chest:3000,right_treasure_chest:2000};
  const initial={...server},other=side==='left'?'right':'left';
  const store={isLoggedIn:true,userInfo:{cuid:'test',total_click_count:3000,total_click_consume:1000,left_treasure_chest:1000,right_treasure_chest:2000,appearance:'keep'},
    applyChestClickConsume(cost){applies++;const total=this.userInfo.total_click_count+local;this.userInfo={...this.userInfo,total_click_count:total,total_click_consume:this.userInfo.total_click_consume+(kind==='bad-action'?999:cost)};local=0;},
    updateUserInfo(value){refreshes++;this.userInfo={...value};if(kind==='bad-refresh')this.userInfo.total_click_count++;}};
  if(kind==='poor')store.userInfo.total_click_consume=2500;
  if(kind==='future')server[side+'_treasure_chest']=9000;
  let context;
  const takeSnapshot=()=>({time,loggedIn:store.isLoggedIn,accountKey:store.userInfo.cuid==='test'?'test-key':null,
    locked:kind==='locked',claimBusy:!!context?.__TAPPO_HELPER_TX__?.busy,available:store.userInfo.total_click_count+local-store.userInfo.total_click_consume,
    cumulative:kind==='invalid-count'?NaN:store.userInfo.total_click_count+local,consumed:store.userInfo.total_click_consume,
    left:store.userInfo.left_treasure_chest,right:store.userInfo.right_treasure_chest});
  const consoleStub={warn(){}};
  const api={decode(value){if(value.code!==0)throw Object.assign(Error('business'),{code:value.code});return value.data},
    async bag(){bagReads++;
      if(kind==='bad-inventory')return {code:0,data:[{asset_id:123,quantity:null}]};
      if(kind==='bag-account'&&bagReads===2)store.userInfo.cuid='other';
      if(kind==='bag-error'&&bagReads===2)throw Error('inventory temporarily unavailable');
      if(kind==='bag-preflight-error'&&bagReads===1)throw Error('inventory preflight unavailable');
      return {code:0,data:[{asset_id:123,quantity:5+(calls&&kind!=='bag-unchanged'&&!(kind==='bag-delayed'&&bagReads<4)?1:0)}]};
    },async open(payload){calls++;assert.deepEqual(JSON.parse(JSON.stringify(payload)),{place:side,click_num:3000});
      if(kind.startsWith('reject'))return {code:Number(kind.slice(6))};
      if(kind==='long')await new Promise(r=>release=r);
      if(kind==='timeout')throw Error('request timeout');
      if(kind==='account')store.userInfo.cuid='other';
      if(kind==='counter-race')store.userInfo.total_click_consume+=1000;
      if(kind==='clicks')local+=5;
      if(kind==='spend')store.serverClickSpend=10;
      if(kind!=='unchanged')server={...server,total_click_consume:2000,[side+'_treasure_chest']:9000};
      if(kind==='timeout-after-server')throw Error('response lost');
      if(kind==='other')server[other+'_treasure_chest']=9500;
      if(kind==='excess')server.total_click_consume=3000;
      if(kind==='missing-time')server[side+'_treasure_chest']=null;
      return {code:0,data:{click_consume:kind==='bad-cost'?500:1000,asset_id:123,asset_name:kind==='missing-reward'?'':'test-item',asset_type:'accessory'}};
    }};
  context=vm.createContext({store,api,console:consoleStub,takeSnapshot,
    window:{__TAURI_INTERNALS__:{async invoke(command,payload){emits++;assert.equal(command,'plugin:event|emit');assert.equal(payload.payload.type,'ready');if(kind==='ui-error')throw Error('UI not ready');}}},
    persistence:{async save(value,options){saves++;assert.equal(value,undefined);assert.equal(options.force,true);if(kind==='save-hung'&&saves===1)await new Promise(r=>{saveRelease=r;});if(kind==='save-error'&&saves===1)throw Error('disk temporarily busy');if((kind==='save-swallowed'&&saves===1)||kind==='save-persistent')consoleStub.warn('[localClickCount] save failed',Error('storage failed'));}},
    observeBudget:(p,ms)=>{
      if(kind==='save-hung'&&ms===8000&&context.__TAPPO_HELPER_TX__?.saveTask===p&&!saveTimedOut){saveTimedOut=true;return Promise.race([p,Promise.reject(Error('save observation timed out'))]);}
      return p;
    },
    read:async()=>{reads++;
      if((kind==='query-error'&&reads===2)||(kind==='preflight-error'&&reads===1))throw Object.assign(Error('read failed'),{status:503,retryAfterMs:7000});
      return {...(kind==='delayed'&&calls&&reads<4?initial:server)};
    },Date:{now:()=>time},setTimeout,clearTimeout});
  const injected='const s=store,m=api,snapshot=takeSnapshot,readServer=read,accountKey="test-key",sameAccount=()=>store.isLoggedIn&&store.userInfo.cuid==="test",locked=()=>snapshot().locked;const bounded=observeBudget;const failure=(e,stage)=>({failureStage:stage,errorType:e.name??"Error",httpStatus:e.status??0,code:e.code??0,retryAfterMs:e.retryAfterMs??0});';
  async function evaluate(recover=false,id='op-1'){
    const script=source.replace('__STATE__',injected).replace('__SIDE__',JSON.stringify(side)).replaceAll('__RECOVER__',String(recover))
      .replace('await import(__CLICKMODULE__)','persistence').replaceAll('__SAVECLICKS__','"save"').replaceAll('__OPENCHEST__','"open"').replaceAll('__DECODE__','"decode"')
      .replaceAll('__BAGLIST__','"bag"').replaceAll('__REFRESHPANEL__','"ready"')
      .replaceAll('__BEFORE_OPERATIONID__',JSON.stringify(id)).replaceAll('__BEFORE_ACCOUNTKEY__','"test-key"')
      .replaceAll('__BEFORE_LEFT__','3000').replaceAll('__BEFORE_RIGHT__','2000').replaceAll('__BEFORE_CONSUMED__','1000');
    let timer;
    try{return await Promise.race([vm.runInContext(script,context),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('observer blocked on a remote request')),1000)})]);}
    finally{clearTimeout(timer);}
  }
  return {evaluate,drain,store,context,release:()=>release?.(),releaseSave:()=>saveRelease?.(),stats:()=>({calls,reads,applies,saves,refreshes,bagReads,emits,state:takeSnapshot()}),
    advance:ms=>{time+=ms},
    async settle(){let result;for(let i=0;i<12;i++){time+=10000;await drain();result=await evaluate(true);if(['confirmed','rejected','skipped','unknown'].includes(result.outcome))return result;}return result;}};
}
(async()=>{
  let checks=0;
  for(const [kind,side]of [['success','left'],['success','right'],['clicks','left'],['delayed','left'],['preflight-error','left'],['query-error','left'],['save-error','left'],['save-swallowed','left'],['bag-error','left'],['bag-preflight-error','left'],['bag-delayed','left'],['ui-error','left']]){
    const f=fixture(kind,side);await f.evaluate();const result=await f.settle(),stats=f.stats();
    assert.equal(result.outcome,'confirmed',kind);assert.equal(result.confirmedServer.accountKey,'test-key');assert.equal(result[side],9000);
    assert.equal(stats.calls,1);assert.equal(stats.applies,1);assert.equal(result.available,kind==='clicks'?1005:1000);assert.equal(f.store.userInfo.appearance,'keep');
    assert(stats.saves>=1);assert(!stats.state.claimBusy);checks++;
    assert.equal(result.rewardVerified,true);assert.equal(result.reward.assetId,123);assert.equal(result.reward.quantityBefore,5);assert.equal(result.reward.quantityAfter,6);assert.equal(stats.emits,1);assert.equal(result.uiRefreshRequested,kind!=='ui-error');
  }
  for(const kind of ['other','excess','account','timeout','timeout-after-server','bad-cost','bad-action','bad-refresh','counter-race','reject12345','spend','missing-reward','bag-account']){
    const f=fixture(kind);await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'unknown',kind);await f.evaluate(true);await drain();
    assert.equal(f.stats().calls,1,kind+' resent a request');assert(f.stats().applies<=1);checks++;
  }
  {
    const f=fixture('save-hung');await f.evaluate();await drain();assert.equal(f.stats().saves,1);
    f.advance(30000);await f.evaluate(true);await drain();assert.equal(f.stats().saves,1,'overlapped an unresolved claim save');
    f.releaseSave();await drain();const result=await f.settle();
    assert.equal(result.outcome,'confirmed');assert.equal(f.stats().calls,1);assert.equal(f.stats().applies,1);assert.equal(f.stats().saves,2);checks++;
  }
  {
    const f=fixture('bad-inventory');await f.evaluate();await f.settle();assert.equal(f.stats().calls,0,'invalid inventory baseline allowed a claim');checks++;
  }
  {
    const f=fixture('bag-unchanged');await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'unknown');
    f.advance(70000);await f.evaluate(true);await drain();const unverified=await f.evaluate(true);
    assert.equal(unverified.outcome,'unknown');assert.equal(unverified.failureStage,'reward-verify');assert.equal(f.stats().calls,1);assert.equal(f.stats().emits,0);checks++;
  }
  for(const kind of ['unchanged','save-persistent','missing-time']){
    const f=fixture(kind);await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'pending');assert.equal(f.stats().calls,1);assert.equal(f.stats().applies,1);checks++;
  }
  for(const code of [70004,70005,70006]){
    const f=fixture('reject'+code);await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'rejected');assert.equal(result.code,code);assert.equal(f.stats().calls,1);assert.equal(f.stats().applies,0);checks++;
  }
  for(const kind of ['future','poor','locked','invalid-count']){
    const f=fixture(kind);const result=await f.evaluate();await drain();const final=kind==='locked'?result:await f.evaluate(true);
    assert(['skipped','unknown'].includes(final.outcome),kind);assert.equal(f.stats().calls,0);checks++;
  }
  {
    const f=fixture();await f.evaluate();await drain();vm.runInContext('globalThis.__TAPPO_HELPER_TX__=null',f.context);
    const result=await f.evaluate(true);assert.equal(result.outcome,'unknown');assert.equal(result.failureStage,'receipt-missing');assert.equal(f.stats().calls,1);checks++;
  }
  {
    const f=fixture('long');const first=await f.evaluate();await drain();assert.equal(first.outcome,'pending');
    assert.equal((await f.evaluate(false,'op-2')).outcome,'skipped');for(let i=0;i<5;i++)await f.evaluate(true);
    f.advance(60000);const slow=await f.evaluate(true);assert.equal(slow.outcome,'unknown');assert.equal(slow.errorType,'TimeoutError');
    assert.equal(f.stats().calls,1);f.release();const final=await f.settle();assert.equal(final.outcome,'confirmed');assert.equal(f.stats().calls,1);checks++;
  }
  {
    const f=fixture('save-error');await f.evaluate();await drain();assert.equal(f.stats().saves,1);
    const waiting=await f.evaluate(true);assert.equal(waiting.failureStage,'local-save');assert(waiting.retryAfterMs>=5000);
    assert.equal(f.stats().saves,1,'storage retry ignored backoff');const final=await f.settle();assert.equal(final.outcome,'confirmed');assert.equal(f.stats().calls,1);assert.equal(f.stats().applies,1);checks++;
  }
  {
    const f=fixture('long');await f.evaluate();await drain();f.store.userInfo.cuid='other';
    assert.equal((await f.evaluate(true)).failureStage,'account');f.release();await drain();assert.equal(f.stats().applies,0);assert.equal(f.stats().calls,1);checks++;
  }
  console.log('PASS: '+checks+' transaction/recovery scenarios; no duplicate submissions, DOM dependency or account access.');
})().catch(e=>{console.error(e);process.exitCode=1});
