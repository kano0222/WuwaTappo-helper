// Transaction/recovery checks use an isolated fake app, never a desktop app or account.
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../src/Bridge/claim.js'),'utf8');
const drain=async()=>{for(let i=0;i<25;i++)await Promise.resolve()};
function fixture(kind='success',side='left',quiet=false){
  let time=5000,calls=0,reads=0,applies=0,folds=0,saves=0,local=0,release,bagReads=0,emits=0;
  const startingLocal=['cumulative-lag','server-poor','report-before','old-report','report-after'].includes(kind)?15:0;
  let persistedLocal=local=startingLocal;
  let server={total_click_count:3000,total_click_consume:1000,left_treasure_chest:3000,right_treasure_chest:2000};
  const initial={...server},other=side==='left'?'right':'left';
  const store={isLoggedIn:true,serverClickSpend:0,userInfo:{cuid:'test',total_click_count:3000,total_click_consume:1000,left_treasure_chest:1000,right_treasure_chest:2000,appearance:'keep'},
    applyChestClickConsume(){folds++;throw Error('claim folded local clicks');},
    updateUserInfo(value){
      const debit=value.total_click_consume!==this.userInfo.total_click_consume;
      this.userInfo={...value};this.serverClickSpend=0;
      if(debit){applies++;if(kind==='bad-action')this.userInfo.total_click_consume--;}
      else if(kind==='bad-refresh')this.userInfo.total_click_count++;
    }};
  if(kind==='server-poor')store.userInfo.total_click_consume=server.total_click_consume=2010;
  if(kind==='spend-before')store.serverClickSpend=10;
  if(kind==='future')server[side+'_treasure_chest']=9000;
  if(kind==='older-query-count')store.userInfo.total_click_count=3006;
  let context,toasts=0;
  const report=total=>{server.total_click_count=total;store.userInfo.total_click_count=total;local=persistedLocal=0;};
  const takeSnapshot=()=>({time,loggedIn:store.isLoggedIn,accountKey:store.userInfo.cuid==='test'?'test-key':null,
    locked:kind==='locked',claimBusy:!!context?.__TAPPO_HELPER_TX__?.busy,available:store.userInfo.total_click_count+local-store.userInfo.total_click_consume-store.serverClickSpend,
    local,serverTotal:store.userInfo.total_click_count,
    cumulative:kind==='invalid-count'?NaN:store.userInfo.total_click_count+local,consumed:store.userInfo.total_click_consume,
    left:store.userInfo.left_treasure_chest,right:store.userInfo.right_treasure_chest});
  const consoleStub={warn(){}};
  const api={decode(value){if(value.code!==0)throw Object.assign(Error('business'),{code:value.code});return value.data},
    cookie:()=> 'test-token',toast(){toasts++},
    async bag(){bagReads++;
      if(kind==='bad-inventory')return {code:0,data:[{asset_id:123,quantity:null}]};
      if(kind==='bag-account'&&bagReads===2)store.userInfo.cuid='other';
      if(kind==='spend-before-sync'&&bagReads===2)store.serverClickSpend=10;
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
      if(kind==='report-before')report(3015);
      if(kind==='old-report')report(3000);
      if(kind==='spend')store.serverClickSpend=10;
      if(kind!=='unchanged')server={...server,total_click_consume:2000,[side+'_treasure_chest']:9000};
      if(kind==='cooldown-first')server.total_click_consume=1000;
      if(kind==='timeout-after-server')throw Error('response lost');
      if(kind==='other')server[other+'_treasure_chest']=9500;
      if(kind==='excess')server.total_click_consume=3000;
      if(kind==='server-regressed')server.total_click_count=2999;
      if(kind==='missing-time')server[side+'_treasure_chest']=null;
      return {code:0,data:{click_consume:kind==='bad-cost'?500:1000,asset_id:123,asset_name:kind==='missing-reward'?'':'test-item',asset_type:'accessory'}};
    }};
  context=vm.createContext({store,api,console:consoleStub,takeSnapshot,AbortSignal,
    localStorage:{getItem:()=>''},crypto:{randomUUID:()=> '11111111-1111-4111-8111-111111111111'},
    fetch:async(url,options)=>{const reply=await api.open(JSON.parse(options.body));return {ok:true,status:200,json:async()=>reply};},
    window:{__TAURI_INTERNALS__:{async invoke(command,payload){emits++;assert.equal(command,'plugin:event|emit');assert.equal(payload.payload.type,'ready');if(kind==='ui-error'||kind==='ui-transient'&&emits===1)throw Error('UI not ready');}}},
    persistence:{async save(){saves++;throw Error('claim wrote to native click storage');}},
    observeBudget:p=>p,
    read:async()=>{reads++;
      if((kind==='query-error'&&reads===2)||(kind==='preflight-error'&&reads===1))throw Object.assign(Error('read failed'),{status:503,retryAfterMs:7000});
      return {...(['delayed','report-after'].includes(kind)&&calls&&reads<4?initial:server)};
    },Date:{now:()=>time},setTimeout,clearTimeout});
  const injected='const s=store,m=api,snapshot=takeSnapshot,readServer=read,accountKey="test-key",sameAccount=()=>store.isLoggedIn&&store.userInfo.cuid==="test",locked=()=>snapshot().locked;const claimCountCurrent=(total,v)=>v.serverTotal<=total;const bounded=observeBudget;const failure=(e,stage)=>({failureStage:stage,errorType:e.name??"Error",httpStatus:e.status??0,code:e.code??0,retryAfterMs:e.retryAfterMs??0});';
  async function evaluate(recover=false,id='op-1'){
    const script=source.replace('__STATE__',injected).replace('__SIDE__',JSON.stringify(side)).replaceAll('__RECOVER__',String(recover))
      .replace('await import(__CLICKMODULE__)','persistence').replaceAll('__SAVECLICKS__','"save"').replaceAll('__OPENCHEST__','"open"').replaceAll('__DECODE__','"decode"')
      .replaceAll('__BAGLIST__','"bag"').replaceAll('__REFRESHPANEL__','"ready"')
      .replaceAll('__CHESTHOST__',JSON.stringify(quiet?'https://api-mc-wuwatappo.kurogames-global.com':'')).replaceAll('__CHESTCOOKIE__','"cookie"').replaceAll('__CHESTTOAST__','"toast"').replaceAll('__CHESTRENEW__','""')
      .replaceAll('__BEFORE_OPERATIONID__',JSON.stringify(id)).replaceAll('__BEFORE_ACCOUNTKEY__','"test-key"')
      .replaceAll('__BEFORE_LEFT__','3000').replaceAll('__BEFORE_RIGHT__','2000').replaceAll('__BEFORE_CONSUMED__',kind==='server-poor'?'2010':'1000');
    let timer;
    try{return await Promise.race([vm.runInContext(script,context),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('observer blocked on a remote request')),1000)})]);}
    finally{clearTimeout(timer);}
  }
  return {evaluate,drain,store,context,report,release:()=>release?.(),stats:()=>({calls,reads,applies,folds,saves,bagReads,emits,toasts,state:takeSnapshot()}),
    restartTotal:()=>server.total_click_count+persistedLocal,
    advance:ms=>{time+=ms},
    reflectDebit:()=>{server.total_click_consume=2000;},
    async settle(){let result;for(let i=0;i<12;i++){time+=10000;await drain();result=await evaluate(true);if(['confirmed','rejected','skipped','unknown'].includes(result.outcome))return result;}return result;}};
}
(async()=>{
  let checks=0;
  // Exercise the real request wrapper without a desktop, token, or network.
  const requestSource=source.slice(source.indexOf('  const requestChest='),source.indexOf('  const id='))+'return requestChest;';
  for(const host of ['https://api-mc-wuwatappo.kurogames-global.com','https://api-wutheringwaves-wuwatappo.kurogames-global.com']){
    for(const kind of ['success','cooldown','insufficient','expired','http-limit','http-cooldown','network','bad-json','renew-error','account','missing-token','private-renew']){
      let calls=0,toasts=0,renews=0,native=0;
      const script=requestSource.replaceAll('__CHESTHOST__',JSON.stringify(host)).replaceAll('__OPENCHEST__','"open"')
        .replaceAll('__CHESTCOOKIE__','"cookie"').replaceAll('__CHESTTOAST__','"toast"').replaceAll('__CHESTRENEW__','"renew"').replaceAll('__DECODE__','"decode"');
      const requestScript=kind==='private-renew'?script.replaceAll('"renew"','""'):script;
      const code=['cooldown','http-cooldown'].includes(kind)?70006:kind==='insufficient'?70005:kind==='expired'?10002:0;
      const module={open(){native++;throw Error('native auto-toast path used')},cookie:()=>kind==='missing-token'?'':'test-token',
        toast(){toasts++},async renew(){renews++;if(kind==='renew-error')throw Error('renew failed')},
        decode(e){if(e.code!==0)throw Object.assign(Error('business'),{code:e.code});return e.data}};
      const request=await vm.runInNewContext('(async()=>{'+requestScript+'})()',{
        m:module,sameAccount:()=>kind!=='account',locked:()=>false,AbortSignal,
        localStorage:{getItem:()=>''},crypto:{randomUUID:()=> '11111111-1111-4111-8111-111111111111'},
        fetch:async(url,options)=>{calls++;assert.equal(url,host+'/api/frontend/chests/open');assert.equal(options.method,'POST');
          assert.equal(options.headers.Authorization,'Bearer test-token');assert.equal(options.redirect,'error');
          assert.equal(options.credentials,'omit');assert.equal(options.body,'{"place":"right","click_num":3000}');
          if(kind==='network')throw Error('connection lost');
          return {ok:!['http-limit','http-cooldown'].includes(kind),status:['http-limit','http-cooldown'].includes(kind)?429:200,headers:{get:()=> '120'},
            async json(){if(kind==='bad-json')throw Error('invalid JSON');return {code,data:{click_consume:1000}}}};
        }});
      if(['success','private-renew'].includes(kind))assert.equal((await request({place:'right',click_num:3000})).code,0);
      else await assert.rejects(request({place:'right',click_num:3000}),e=>{
        if(['cooldown','insufficient','expired'].includes(kind))assert.equal(e.code,code);
        if(['http-limit','http-cooldown'].includes(kind)){assert.equal(e.httpStatus,429);assert.equal(e.retryAfter,'120');}
        return true;
      });
      assert.equal(calls,['renew-error','account','missing-token'].includes(kind)?0:1);
      assert.equal(toasts,['success','private-renew','cooldown','http-cooldown','renew-error','account','missing-token'].includes(kind)?0:1);
      assert.equal(renews,kind==='private-renew'?0:1);assert.equal(native,0);checks++;
    }
  }
  for(const [kind,side]of [['success','left'],['success','right'],['clicks','left'],['delayed','left'],['preflight-error','left'],['query-error','left'],['report-before','left'],['bag-error','left'],['bag-preflight-error','left'],['bag-delayed','left'],['ui-error','left'],['ui-transient','left']]){
    const f=fixture(kind,side);await f.evaluate();const result=await f.settle(),stats=f.stats();
    assert.equal(result.outcome,'confirmed',kind);assert.equal(result.confirmedServer.accountKey,'test-key');assert.equal(result[side],9000);
    assert.equal(stats.calls,1);assert.equal(stats.applies,1);assert.equal(result.available,kind==='clicks'?1005:kind==='report-before'?1015:1000);assert.equal(f.store.userInfo.appearance,'keep');
    assert.equal(stats.state.local,kind==='clicks'?5:0);assert.equal(stats.folds,0);assert.equal(stats.saves,0);assert(!stats.state.claimBusy);checks++;
    assert.equal(result.rewardVerified,true);assert.equal(result.reward.assetId,123);assert.equal(result.reward.quantityBefore,5);assert.equal(result.reward.quantityAfter,6);assert.equal(stats.emits,kind.startsWith('ui-')?2:1);assert.equal(result.uiRefreshRequested,kind!=='ui-error');
  }
  for(const kind of ['other','excess','account','timeout','timeout-after-server','bad-cost','bad-action','bad-refresh','counter-race','reject12345','spend','spend-before-sync','old-report','missing-reward','bag-account']){
    const f=fixture(kind);await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'unknown',kind);await f.evaluate(true);await drain();
    assert.equal(f.stats().calls,1,kind+' resent a request');assert(f.stats().applies<=1);
    if(kind==='spend'||kind==='spend-before-sync')assert.equal(f.store.serverClickSpend,10,'unsettled spend was reset');
    if(kind==='spend'||kind==='counter-race'||kind==='old-report')assert.equal(f.stats().applies,0);
    if(['other','excess','account','timeout-after-server','bad-cost','missing-reward'].includes(kind))assert.equal(f.stats().emits,0,kind);
    checks++;
  }
  {
    const f=fixture('report-after');await f.evaluate();await drain();
    const waiting=await f.evaluate(true);assert.equal(waiting.waitReason,'consumption.cooldown');
    assert.equal(f.stats().state.local,15);
    f.report(3015);const result=await f.settle();
    assert.equal(result.outcome,'confirmed');assert.equal(result.consumed,2000);assert.equal(result.cumulative,3015);
    assert.equal(f.stats().calls,1);assert.equal(f.stats().applies,1);assert.equal(f.stats().saves,0);checks++;
  }
  {
    const f=fixture('bad-inventory');await f.evaluate();await f.settle();assert.equal(f.stats().calls,0,'invalid inventory baseline allowed a claim');checks++;
  }
  {
    const f=fixture('bag-unchanged');await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'unknown');
    f.advance(70000);await f.evaluate(true);await drain();const unverified=await f.evaluate(true);
    assert.equal(unverified.outcome,'unknown');assert.equal(unverified.failureStage,'reward-verify');assert.equal(unverified.waitReason,'reward');assert.equal(f.stats().calls,1);assert.equal(f.stats().emits,1);checks++;
  }
  for(const [kind,wait] of [['cooldown-first','consumption'],['bag-delayed','reward']]){
    const f=fixture(kind);await f.evaluate();await drain();
    const pending=await f.evaluate(true),early=f.stats();
    assert.equal(pending.outcome,'pending');assert.equal(pending.waitReason,wait);
    assert.equal(pending.confirmedServer,undefined);assert.equal(pending.rewardVerified,undefined);
    assert.equal(pending.observedServer.left,9000);assert.equal(pending.serverObservedAt,5000);
    assert.equal(early.state.left,9000);assert.equal(early.state.right,2000);
    assert.equal(early.state.consumed,2000);assert.equal(early.emits,1);
    if(kind==='cooldown-first') {
      assert.equal(early.bagReads,1);assert.equal(pending.observedServer.consumed,1000);
      f.reflectDebit();
    }
    assert.equal((await f.settle()).outcome,'confirmed');
    const final=f.stats();
    assert.equal(final.emits,1);assert.equal(final.calls,1);assert.equal(final.applies,1);
    assert.equal(final.folds,0);assert.equal(final.saves,0);checks++;
  }
  {
    const f=fixture('cooldown-first');await f.evaluate();await drain();
    assert.equal(f.stats().state.left,9000);
    vm.runInContext('globalThis.__TAPPO_HELPER_TX__=null',f.context);
    const result=await f.evaluate(true);
    assert.equal(result.outcome,'unknown');assert.equal(result.failureStage,'receipt-missing');
    assert.equal(f.stats().calls,1);assert.equal(f.stats().emits,1);checks++;
  }
  {
    const f=fixture('cumulative-lag');await f.evaluate();const final=await f.settle();
    assert.equal(final.outcome,'confirmed');assert.equal(final.waitReason,null);
    assert.equal(final.confirmedServer.total,3000);assert.equal(final.cumulative,3015);assert.equal(final.local,15);
    assert.equal(f.restartTotal(),3015);assert.equal(f.stats().calls,1);assert.equal(f.stats().folds,0);assert.equal(f.stats().saves,0);
    vm.runInContext('globalThis.__TAPPO_HELPER_TX__=null',f.context);
    assert.equal((await f.evaluate(true)).failureStage,'receipt-missing');assert.equal(f.stats().calls,1);assert.equal(f.restartTotal(),3015);checks++;
  }
  for(const kind of ['unchanged','server-regressed','missing-time']){
    const f=fixture(kind);await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'pending');assert.equal(f.stats().calls,1);assert.equal(f.stats().applies,1);checks++;
    if(kind==='unchanged'){
      assert.equal(result.waitReason,'consumption.cooldown');assert.equal(result.observedServer.left,3000);
      assert.equal(f.stats().state.left,1000);assert.equal(f.stats().emits,0);
    }
    if(kind==='server-regressed')assert.equal(result.waitReason,'cumulative');
  }
  for(const code of [70004,70005,70006]){
    const f=fixture('reject'+code);await f.evaluate();const result=await f.settle();assert.equal(result.outcome,'rejected');assert.equal(result.code,code);assert.equal(f.stats().calls,1);assert.equal(f.stats().applies,0);checks++;
  }
  for(const kind of ['success','reject70006']){
    const f=fixture(kind,'right',true);await f.evaluate();const result=await f.settle();
    assert.equal(result.outcome,kind==='success'?'confirmed':'rejected');
    assert.equal(f.stats().toasts,0);assert.equal(f.stats().calls,1);
    if(kind==='reject70006'){assert.equal(result.code,70006);assert.equal(f.stats().applies,0);assert.equal(f.stats().saves,0);}
    await f.evaluate(true);await drain();assert.equal(f.stats().calls,1,'silent request replayed');checks++;
  }
  for(const kind of ['future','server-poor','spend-before','locked','invalid-count','older-query-count']){
    const f=fixture(kind);const result=await f.evaluate();await drain();const final=kind==='locked'?result:await f.evaluate(true);
    assert(['skipped','unknown'].includes(final.outcome),kind);assert.equal(f.stats().calls,0);checks++;
    if(kind==='spend-before')assert.equal(f.store.serverClickSpend,10);
    if(kind==='older-query-count')assert.equal(final.reason,'等待服务器同步点击数');
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
    const f=fixture('long');await f.evaluate();await drain();f.store.userInfo.cuid='other';
    assert.equal((await f.evaluate(true)).failureStage,'account');f.release();await drain();assert.equal(f.stats().applies,0);assert.equal(f.stats().calls,1);checks++;
  }
  console.log('PASS: '+checks+' transaction/recovery scenarios; no duplicate submissions, DOM dependency or account access.');
})().catch(e=>{console.error(e);process.exitCode=1});
