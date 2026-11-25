const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {webcrypto,createHash}=require('node:crypto');
const reportSource=fs.readFileSync(path.join(__dirname,'../src/Bridge/report.js'),'utf8');
const source=fs.readFileSync(path.join(__dirname,'../src/Bridge/state.js'),'utf8')
  .replace('__REPORT_OBSERVER__',reportSource);
const drain=async()=>{for(let i=0;i<20;i++)await Promise.resolve()};
(async()=>{
 let now=0,calls=0,nextId=0,mode='success';const timers=new Map();
 let listener;
 const store={isLoggedIn:true,userInfo:{cuid:'private-test-id'},totalClickCount:2000,cumulativeClickCount:3000,localClickCount:0,serverClickConsume:1000,serverClickTotal:3000,
   $onAction(fn,detached){assert.equal(detached,true);assert.equal(listener,undefined);listener=fn;return ()=>{listener=undefined;};}};
 const action=(name,args,apply)=>{let done;listener({name,args,after:fn=>{done=fn;}});apply();done?.();};
 const module={env:'cn-cn-prod',store,locked:()=>false,decode:x=>x,
   async user(){calls++;if(mode==='hang')return new Promise(()=>{});if(mode==='offline')throw Error('offline');if(mode==='limited'||mode==='retry-after')throw Object.assign(Error('limited'),{status:mode==='limited'?429:503,retryAfterMs:120000});
     return {cuid:store.userInfo.cuid,total_click_count:3000,total_click_consume:1000,left_treasure_chest:50000,right_treasure_chest:60000};}};
 const math=Object.create(Math);math.random=()=>0.5;
 const context=vm.createContext({module,crypto:webcrypto,TextEncoder,Uint8Array,Math:math,Date:{now:()=>now,parse:Date.parse},
   window:{__TAURI_INTERNALS__:{metadata:{currentWindow:{label:'main'}}}},
   document:new Proxy({},{get(){throw Error('DOM access forbidden')}}),
   setTimeout(fn,ms){const id=++nextId;timers.set(id,{fn,at:now+ms});return id},clearTimeout:id=>timers.delete(id)});
 const script=source.replace('await import(__COMMON__)','module').replaceAll('__ENVIRONMENT__','"env"').replaceAll('__STORE__','"store"')
  .replaceAll('__LOCKED__','"locked"').replaceAll('__USERINFO__','"user"').replaceAll('__DECODE__','"decode"').replaceAll('__COOKIE__','"cookie"');
 let api=await vm.runInContext('(async()=>{'+script+'return {snapshot,readServer,failure,retryQuery,claimClickNum}})()',context);
 const state=api.snapshot();assert.equal(state.accountKey,createHash('sha256').update('tappo:cn-cn-prod:private-test-id').digest('hex'));
 module.env='global-global-prod';
 assert.equal(api.snapshot().accountKey,null,'environment change retained old account');
 await assert.rejects(api.readServer());assert.equal(calls,0,'environment change sent a query');
 const globalApi=await vm.runInContext('(async()=>{'+script+'return {snapshot,readServer}})()',context);
 const globalKey=globalApi.snapshot().accountKey;
 assert.equal(globalKey,createHash('sha256').update('tappo:global-global-prod:private-test-id').digest('hex'));
 assert.notEqual(globalKey,state.accountKey,'servers share account identity');
 store.isLoggedIn=false;assert.equal(globalApi.snapshot().accountKey,null);await assert.rejects(globalApi.readServer());assert.equal(calls,0);
 store.isLoggedIn=true;
 module.env='unknown';await assert.rejects(vm.runInContext('(async()=>{'+script+'})()',context),/应用环境/);
 module.env='cn-cn-prod';
 // Domestic builds with a private cache namespace still use the native query.
 module.env=true;
 const domesticApi=await vm.runInContext('(async()=>{'+script+'return {snapshot,readServer,queryUser}})()',context);
 assert.equal(domesticApi.snapshot().accountKey,state.accountKey,'region flags changed persisted account identity');
 await domesticApi.queryUser();assert.equal(calls,1);calls=0;
 module.env=false;assert.equal(domesticApi.snapshot().accountKey,null);
 await assert.rejects(domesticApi.readServer());assert.equal(calls,0);
 module.env='cn-cn-prod';
 // Global release has no exported userInfo: only the fixed read endpoint is permitted.
 const globalScript=script.replaceAll('"user"','""');
 let requests=0,token='test-token',status=200;
 const fallbackModule={...module,store:{...store,$onAction:undefined},env:true,cookie:()=>token};
 const fallbackContext=vm.createContext({module:fallbackModule,crypto:webcrypto,TextEncoder,Uint8Array,AbortSignal,
   window:{__TAURI_INTERNALS__:{metadata:{currentWindow:{label:'main'}}}},setTimeout,clearTimeout,
   fetch:async(url,options)=>{requests++;assert.equal(url,'https://api-wutheringwaves-wuwatappo.kurogames-global.com/api/frontend/user/me');
     assert.equal(options.method,'GET');assert.equal(options.headers.Authorization,'Bearer test-token');assert.equal(options.redirect,'error');
     return {ok:status===200,status,headers:{get:()=> '120'},json:async()=>({cuid:'private-test-id',total_click_count:3000,
       total_click_consume:1000,left_treasure_chest:50000,right_treasure_chest:60000})};}});
 const fallback=await vm.runInContext('(async()=>{'+globalScript+'return {snapshot,readServer,queryUser,failure}})()',fallbackContext);
 assert.equal(fallback.snapshot().accountKey,globalKey);await fallback.readServer();assert.equal(requests,1);
 assert(!JSON.stringify(fallback.snapshot()).includes(token));
 token='';await assert.rejects(fallback.queryUser());assert.equal(requests,1);token='test-token';
 status=429;await assert.rejects(fallback.queryUser(),error=>{const info=fallback.failure(error,'query');assert.equal(info.httpStatus,429);assert.equal(info.retryAfterMs,120000);return true});
 status=401;await assert.rejects(fallback.queryUser(),error=>error.httpStatus===401);
 fallbackModule.env=false;await assert.rejects(fallback.readServer());assert.equal(requests,3);
 assert(!JSON.stringify(state).includes('private-test-id'));assert.equal(state.claimBusy,false);
 vm.runInContext('globalThis.__TAPPO_HELPER_TX__={busy:true,accountKey:'+JSON.stringify(state.accountKey)+'}',context);assert.equal(api.snapshot().claimBusy,true);
 vm.runInContext('globalThis.__TAPPO_HELPER_TX__=null',context);
 const dated=api.failure({response:{status:429,headers:{get:()=>new Date(now+3600000).toUTCString()}}},'query');assert(dated.retryAfterMs>=3599000,'long Retry-After was shortened');
 const first=api.readServer(),joined=api.readServer();const [baseline]=await Promise.all([first,joined]);assert.equal(calls,1,'parallel reads were not joined');
 now=1000;const reused=await api.readServer(undefined,baseline);
 assert.equal(reused.observedAt,baseline.observedAt);assert.equal(calls,1,'valid claim baseline caused a second query');
 const delayed=api.readServer();await drain();assert.equal(calls,1,'query spacing ignored');
 now=2000;for(const [id,t]of [...timers])if(t.at<=now){timers.delete(id);t.fn()};await delayed;assert.equal(calls,2);
 now=4000;mode='limited';let error;try{await api.readServer()}catch(e){error=e};assert(error);assert.equal(api.failure(error,'query').httpStatus,429);
 assert(error.retryAfterMs>=120000);const count=calls;await assert.rejects(api.readServer());assert.equal(calls,count,'retry-after ignored');
 const limitedFailure=api.failure(error,'query');assert(limitedFailure.retryAfterMs>=limitedFailure.requiredRetryAfterMs,'required wait exceeds automatic query delay');
 await assert.rejects(api.readServer(),e=>{const waitingFailure=api.failure(e,'query');assert(waitingFailure.retryAfterMs>=waitingFailure.requiredRetryAfterMs,'cached query wait omitted required deadline');return true});
 assert.equal(api.retryQuery(),120000,'manual retry bypassed rate limit');await assert.rejects(api.readServer());assert.equal(calls,count);
 now=124000;mode='success';await api.readServer();assert.equal(calls,count+1);
 now=130000;mode='hang';const hung=api.readServer(undefined,baseline);const rejected=assert.rejects(hung);await drain();const hangingCalls=calls;
 now=138000;for(const [id,t]of [...timers])if(t.at<=now){timers.delete(id);t.fn()};await rejected;await drain();
 mode='success';now=144000;await api.readServer();assert.equal(calls,hangingCalls+1,'permanent pending read prevented recovery');
 now=146000;mode='offline';await assert.rejects(api.readServer());const failedCalls=calls;
 assert.equal(api.retryQuery(),2000,'manual retry bypassed minimum query spacing');now=148000;
 assert.equal(api.retryQuery(),0);mode='success';await api.readServer();assert.equal(calls,failedCalls+1,'manual retry retained ordinary backoff');
 now=150000;mode='retry-after';await assert.rejects(api.readServer());assert.equal(api.retryQuery(),120000,'manual retry bypassed non-429 Retry-After');
 mode='success';now=270000;await api.readServer();
 // Reopening the state view must share the listener and must not infer an ACK from a gross count.
 api=await vm.runInContext('(async()=>{'+script+'return {snapshot,readServer,claimClickNum,beginNativeCooldown,syncNativeCooldown}})()',context);
 assert.equal(api.claimClickNum(3000,api.snapshot()),3000);
 store.serverClickTotal=3006;assert.equal(api.claimClickNum(3000,api.snapshot()),null);
 action('applyClickReportSuccess',[3006],()=>{store.serverClickTotal=3006;store.localClickCount=0;});
 assert.equal(api.claimClickNum(3000,api.snapshot()),3006);
 action('updateUserInfo',[],()=>{store.serverClickConsume=2000;});
 assert.equal(api.claimClickNum(3000,api.snapshot()),3006);
 action('applyChestClickConsume',[],()=>{store.serverClickTotal=3012;});
 assert.equal(api.claimClickNum(3000,api.snapshot()),null);
 action('applyClickReportSuccess',[3012],()=>{});
 assert.equal(api.claimClickNum(3000,api.snapshot()),3012);
 now=271000;const beforeChange=calls,changedWhileWaiting=api.readServer();
 action('updateUserInfo',[],()=>{store.userInfo.cuid='other';});
 now=272000;for(const [id,t]of [...timers])if(t.at<=now){timers.delete(id);t.fn()};await assert.rejects(changedWhileWaiting);
 assert.equal(api.snapshot().accountKey,null);await assert.rejects(api.readServer());
 assert.equal(calls,beforeChange,'query sent after account change');
 action('updateUserInfo',[],()=>{store.userInfo.cuid='private-test-id';});
 assert.equal(api.claimClickNum(3000,api.snapshot()),null);
 const key='__TAPPO_HELPER_NATIVE_COOLDOWN__',applied=[];
 const capture=(owner='worker')=>{
   assert.ok(api.beginNativeCooldown(owner));
   const probe=context[key];
   probe.apply=(bound,cuid,side,deadline)=>applied.push([bound,cuid,side,deadline]);
   return probe;
 };
 let probe=capture();
 assert.equal(api.beginNativeCooldown('worker'),null);
 store.userInfo.left_treasure_chest=300000;
 api.syncNativeCooldown('left',200000,300000);
 store.userInfo.left_treasure_chest=310000;
 api.syncNativeCooldown('left',300000,310000);
 store.userInfo.right_treasure_chest=320000;
 api.syncNativeCooldown('right',200000,320000);
 assert.equal(applied.length,0,'native scope was used before debugger cleanup');
 store.userInfo.right_treasure_chest=330000;
 probe.complete(true);
 probe.complete(true);
 assert.deepEqual(applied,[[store,'private-test-id','left',310000]],'queued refresh replayed an obsolete or duplicate deadline');
 api.syncNativeCooldown('right',320000,330000);
 assert.deepEqual(applied[1],[store,'private-test-id','right',330000]);
 vm.runInContext('globalThis.'+key+'=undefined',context);
 probe=capture();
 api.syncNativeCooldown('left',300000,310000);
 action('updateUserInfo',[],()=>{store.userInfo.cuid='other';});
 probe.complete(true);
 assert.equal(applied.length,2,'queued cooldown crossed an account change');
 action('updateUserInfo',[],()=>{store.userInfo.cuid='private-test-id';});
 vm.runInContext('globalThis.'+key+'=undefined',context);
 probe=capture();
 api.syncNativeCooldown('left',300000,310000);
 probe=capture('reconnected-worker');
 probe.complete(false);
 probe.complete(true);
 api.syncNativeCooldown('left',300000,310000);
 assert.equal(applied.length,2,'failed capture applied or replayed a cooldown');
 // The native notification listener can see a success before Pinia is discovered.
 const nativeEvents=new Map();
 let clock=100, observeAction;
 const startupStore={isLoggedIn:true,userInfo:{cuid:'first-account'},serverClickTotal:3000,localClickCount:0,
   $onAction(cb){observeAction=cb;return ()=>{};}};
 const timings=vm.runInNewContext(reportSource+';observeClickReportTiming();({observeClickReports,clickReportTiming})',{
   Date:{now:()=>clock},window:{addEventListener(name,fn){nativeEvents.set(name,fn);}}
 });
 clock=110;nativeEvents.get('cat-auth-login-success')();
 clock=120;nativeEvents.get('cat-click-report-success')();
 clock=130;const startup=timings.observeClickReports(startupStore,'fixture');
 const before=timings.clickReportTiming(startup,'first-account');
 assert.equal(before.loginAt,110);
 assert.equal(before.firstNativeSuccessAt,120);
 assert.equal(before.listenerAt,130);
 assert.equal(before.nativeSuccessAccountKnown,false);
 assert.equal(before.firstStoreSuccessAt,null);
 assert.equal(startup.total,null,'passive timing fabricated claim evidence');
 let callback;
 clock=140;observeAction({name:'applyClickReportSuccess',args:[3000],after:fn=>{callback=fn;}});
 callback();nativeEvents.get('cat-click-report-success')();
 assert.equal(timings.clickReportTiming(startup,'first-account').firstStoreSuccessAt,140);
 clock=150;observeAction({name:'applyChestClickConsume',args:[1000],after(){}});
 assert.equal(timings.clickReportTiming(startup,'first-account').evidenceLostReason,'manual-consume');
 assert.equal(startup.total,null);
 clock=160;nativeEvents.get('cat-auth-logout')();
 clock=170;observeAction({name:'updateUserInfo',args:[],after:fn=>{callback=fn;}});
 startupStore.userInfo={cuid:'second-account'};callback();
 clock=180;nativeEvents.get('cat-auth-login-success')();
 const switched=timings.clickReportTiming(startup,'second-account');
 assert.equal(switched.loginAt,180);
 assert.equal(switched.accountStoreReadyAt,170);
 assert.equal(switched.firstStoreSuccessAt,null);
 assert.equal(switched.evidenceLostAt,null);
 assert.equal(switched.firstNativeSuccessAt,null);
 assert.ok(!JSON.stringify(switched).includes('second-account'));
 console.log('PASS: state/query checks; account hashing, shared reads, and retry.');
})().catch(e=>{console.error(e);process.exitCode=1});
