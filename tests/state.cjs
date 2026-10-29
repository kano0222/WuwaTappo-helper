const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {webcrypto,createHash}=require('node:crypto');
const source=fs.readFileSync(path.join(__dirname,'../src/Bridge/state.js'),'utf8');
const drain=async()=>{for(let i=0;i<20;i++)await Promise.resolve()};
(async()=>{
 let now=0,calls=0,nextId=0,mode='success';const timers=new Map();
 const store={isLoggedIn:true,userInfo:{cuid:'private-test-id'},totalClickCount:2000,cumulativeClickCount:3000,localClickCount:0,serverClickConsume:1000,serverClickTotal:3000};
 const module={env:'cn-cn-prod',store,locked:()=>false,decode:x=>x,
   async user(){calls++;if(mode==='hang')return new Promise(()=>{});if(mode==='offline')throw Error('offline');if(mode==='limited'||mode==='retry-after')throw Object.assign(Error('limited'),{status:mode==='limited'?429:503,retryAfterMs:120000});
     return {cuid:store.userInfo.cuid,total_click_count:3000,total_click_consume:1000,left_treasure_chest:50000,right_treasure_chest:60000};}};
 const math=Object.create(Math);math.random=()=>0.5;
 const context=vm.createContext({module,crypto:webcrypto,TextEncoder,Uint8Array,Math:math,Date:{now:()=>now,parse:Date.parse},
   window:{__TAURI_INTERNALS__:{metadata:{currentWindow:{label:'main'}}}},
   document:new Proxy({},{get(){throw Error('DOM access forbidden')}}),
   setTimeout(fn,ms){const id=++nextId;timers.set(id,{fn,at:now+ms});return id},clearTimeout:id=>timers.delete(id)});
 const script=source.replace('await import(__COMMON__)','module').replaceAll('__ENVIRONMENT__','"env"').replaceAll('__STORE__','"store"')
  .replaceAll('__LOCKED__','"locked"').replaceAll('__USERINFO__','"user"').replaceAll('__DECODE__','"decode"');
 const api=await vm.runInContext('(async()=>{'+script+'return {snapshot,readServer,failure,retryQuery}})()',context);
 const state=api.snapshot();assert.equal(state.accountKey,createHash('sha256').update('tappo:cn-cn-prod:private-test-id').digest('hex'));
 assert(!JSON.stringify(state).includes('private-test-id'));assert.equal(state.claimBusy,false);
 vm.runInContext('globalThis.__TAPPO_HELPER_TX__={busy:true,accountKey:'+JSON.stringify(state.accountKey)+'}',context);assert.equal(api.snapshot().claimBusy,true);
 vm.runInContext('globalThis.__TAPPO_HELPER_TX__=null',context);
 const dated=api.failure({response:{status:429,headers:{get:()=>new Date(now+3600000).toUTCString()}}},'query');assert(dated.retryAfterMs>=3599000,'long Retry-After was shortened');
 const first=api.readServer(),joined=api.readServer();await Promise.all([first,joined]);assert.equal(calls,1,'parallel reads were not joined');
 now=1000;const delayed=api.readServer();await drain();assert.equal(calls,1,'query spacing ignored');
 now=2000;for(const [id,t]of [...timers])if(t.at<=now){timers.delete(id);t.fn()};await delayed;assert.equal(calls,2);
 now=4000;mode='limited';let error;try{await api.readServer()}catch(e){error=e};assert(error);assert.equal(api.failure(error,'query').httpStatus,429);
 assert(error.retryAfterMs>=120000);const count=calls;await assert.rejects(api.readServer());assert.equal(calls,count,'retry-after ignored');
 assert.equal(api.retryQuery(),120000,'manual retry bypassed rate limit');await assert.rejects(api.readServer());assert.equal(calls,count);
 now=124000;mode='success';await api.readServer();assert.equal(calls,count+1);
 now=126000;mode='hang';const hung=api.readServer();const rejected=assert.rejects(hung);await drain();const hangingCalls=calls;
 now=134000;for(const [id,t]of [...timers])if(t.at<=now){timers.delete(id);t.fn()};await rejected;await drain();
 mode='success';now=140000;await api.readServer();assert.equal(calls,hangingCalls+1,'permanent pending read prevented recovery');
 now=142000;mode='offline';await assert.rejects(api.readServer());const failedCalls=calls;
 assert.equal(api.retryQuery(),2000,'manual retry bypassed minimum query spacing');now=144000;
 assert.equal(api.retryQuery(),0);mode='success';await api.readServer();assert.equal(calls,failedCalls+1,'manual retry retained ordinary backoff');
 now=146000;mode='retry-after';await assert.rejects(api.readServer());assert.equal(api.retryQuery(),120000,'manual retry bypassed non-429 Retry-After');
 mode='success';now=266000;await api.readServer();
 now=267000;const beforeChange=calls,changedWhileWaiting=api.readServer();store.userInfo.cuid='other';
 now=268000;for(const [id,t]of [...timers])if(t.at<=now){timers.delete(id);t.fn()};await assert.rejects(changedWhileWaiting);
 assert.equal(api.snapshot().accountKey,null);await assert.rejects(api.readServer());
 assert.equal(calls,beforeChange,'query sent after account change');
 console.log('PASS: state/query checks; account hashing, shared reads, manual retry, hung-read recovery and mandatory Retry-After.');
})().catch(e=>{console.error(e);process.exitCode=1});
