const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const bridge=name=>fs.readFileSync(path.join(__dirname,'../src/Bridge/'+name+'.js'),'utf8');
const selection=bridge('state').split('const claimClickNum=')[1];
const transport=bridge('claim-transport');
const injected=`
  const s=store,m=api,account='fixture',accountKey='fixture',sameAccount=()=>store.userInfo.cuid==='fixture',locked=()=>false;
  const claimClickNum=${selection}
  const readServer=async()=>({accountKey,total:projection.total_click_count,consumed:projection.total_click_consume,
    left:projection.left_treasure_chest,right:projection.right_treasure_chest,observedAt:Date.now()});
  const freshClaimRead=server=>Date.now()-server.observedAt<5000;
  const snapshot=()=>({time:Date.now(),accountKey,consumed:s.userInfo.total_click_consume,
    left:s.userInfo.left_treasure_chest,right:s.userInfo.right_treasure_chest,available:s.userInfo.total_click_count+15-s.userInfo.total_click_consume,
    local:15,cumulative:s.userInfo.total_click_count+15,serverTotal:s.userInfo.total_click_count,reportedClickTotal:s.reportedClickTotal??null});
  const bounded=p=>p;
  const failure=(e,stage)=>({code:e.code??0,httpStatus:0,retryAfterMs:0});
`;
function fixture(kind,receiptFields={}){
  const baseline={accountKey:'fixture',total:3000,consumed:1000,left:1000,right:2000};
  const journal={id:'first',server:baseline,before:{left:1000,right:2000},left:{id:'first',outcome:'armed'}};
  const projection={total_click_count:3000,total_click_consume:1000,left_treasure_chest:1000,right_treasure_chest:2000};
  const store={serverClickSpend:0,userInfo:{cuid:'fixture',...projection},updateUserInfo(info){this.userInfo=info;}};
  if(kind==='reported-query-count')store.reportedClickTotal=store.userInfo.total_click_count=3006;
  const requests=[],cooldowns=[],bag={42:1,43:1};
  let refreshes=0;
  const context=vm.createContext({store,projection,Date:{now:()=>100000},
    __TAPPO_HELPER_NATIVE_COOLDOWN__:{apply:(bound,cuid,slot,deadline)=>cooldowns.push([bound===store,cuid,slot,deadline,store.userInfo[slot+'_treasure_chest']])},
    api:{
      decode(value){if(value.code)throw Object.assign(Error('rejected'),{code:value.code});return value.data;},
      bag:()=>({data:Object.entries(bag).map(([asset_id,quantity])=>({asset_id,quantity}))}),
      cookie:()=> 'fixture',toast:()=>{},
      async renew(){
        if(kind==='renew-conflict'&&requests.length)store.serverClickSpend=1;
        if(kind==='renew-count-race'&&requests.length)store.userInfo.total_click_count+=6;
        if(kind==='reported-query-count'&&requests.length)store.reportedClickTotal=store.userInfo.total_click_count=3012;
      }
    },
    fetch:async(url,options)=>{
      const body=JSON.parse(options.body);
      requests.push({body,trace:options.headers['X-BLM-FRID']});
      if(kind==='unknown-right'&&body.place==='right')throw Error('reply lost');
      return {ok:true,json:async()=>({code:0,data:{click_consume:1000,
        asset_id:body.place==='left'||kind==='same-asset'?42:43,...receiptFields[body.place]}})};
    },AbortSignal,localStorage:{getItem:()=>''},
    window:{__TAURI_INTERNALS__:{invoke:async()=>{refreshes++;}}}
  });
  const execute=command=>vm.runInContext(bridge('pair').replace('__STATE__',injected)
    .replace('__CLAIM_TRANSPORT__',transport).replaceAll('__CHESTHOST__','"https://fixture.invalid"')
    .replaceAll('__CHESTCOOKIE__','"cookie"').replaceAll('__CHESTRENEW__','"renew"')
    .replaceAll('__CHESTTOAST__','"toast"').replaceAll('__OPENCHEST__','"open"')
    .replaceAll('__REFRESHPANEL__','"ready"').replaceAll('__DECODE__','"decode"')
    .replaceAll('__BAGLIST__','"bag"').replace('__PAIR_JOURNAL__',JSON.stringify(journal))
    .replace('__PAIR_COMMAND__',JSON.stringify(command)),context);
  async function submit(command){
    const result=await execute(command);
    if(result.slot)journal[command]=result.slot;
    if(command==='left')journal.bagBefore=result.bagBefore;
    return result;
  }
  return {journal,projection,store,requests,cooldowns,bag,execute,submit,refreshes:()=>refreshes,
    armRight:()=>{journal.right={id:'second',outcome:'armed'};},
    restart:()=>vm.runInContext('delete globalThis.__TAPPO_HELPER_PAIR__',context)};
}
(async()=>{
  for(const kind of ['success','same-asset','unknown-right','account-conflict','renew-conflict','renew-count-race','restart-first','restart-both']){
    const f=fixture(kind);
    const first=await f.submit('left');
    assert.equal(first.outcome,'success',kind);assert.equal(first.slot.applied,true);
    assert.equal(f.store.userInfo.total_click_consume,2000);
    if(kind==='restart-first')f.restart();else f.armRight();
    if(kind==='account-conflict')f.projection.total_click_consume=4000;
    if(kind!=='restart-first'){
      const second=await f.submit('right');
      const expected=['account-conflict','renew-count-race','renew-conflict'].includes(kind)?'skipped':kind==='unknown-right'?'unknown':'success';
      assert.equal(second.outcome,expected,kind);
      if(['account-conflict','renew-conflict','renew-count-race'].includes(kind)){assert.equal(f.requests.length,1);continue;}
      assert.equal(f.requests.length,2);
      assert.equal(f.requests[1].trace,'second');
      assert.equal(f.requests[1].body.click_num,3000);
      assert.equal((await f.execute('right')).outcome,'unknown');
    }
    if(kind==='restart-both'){
      f.restart();f.store.userInfo.total_click_consume=1000;
    }
    const both=kind!=='restart-first';
    f.projection.total_click_consume=both?3000:2000;
    f.projection.left_treasure_chest=1801000;
    if(both)f.projection.right_treasure_chest=1802000;
    f.bag[42]=2;
    let result=await f.execute('observe');
    if(kind==='unknown-right'){
      assert.equal(result.outcome,'unknown');assert.equal(f.requests.length,2);continue;
    }
    if(both){
      assert.equal(result.outcome,'pending');assert.equal(result.stage,'reward-verify');
      assert.equal(f.store.userInfo.right_treasure_chest,1802000);
      if(kind==='same-asset')f.bag[42]=3;else f.bag[43]=2;
      result=await f.execute('observe');
    }
    assert.equal(result.outcome,'confirmed',kind);
    assert.equal(result.state.local,15);
    assert.equal(result.state.consumed,both?3000:2000);
    assert.equal(f.refreshes(),1);
    assert.equal(f.requests.length,both?2:1);
  }
  {
    const f=fixture('reported-query-count',{left:{left_next_time:1801000},right:{right_next_time:1802000}});
    assert.equal((await f.submit('left')).outcome,'success');
    f.armRight();
    assert.equal((await f.submit('right')).outcome,'success');
    assert.deepEqual(f.requests.map(x=>x.body.click_num),[3006,3012]);
    assert.equal(f.store.userInfo.left_treasure_chest,1801000);
    assert.equal(f.store.userInfo.right_treasure_chest,1802000);
    Object.assign(f.projection,{total_click_consume:3000,left_treasure_chest:1801000,right_treasure_chest:1802000});
    f.bag[42]=f.bag[43]=2;
    const pending=await f.execute('observe');
    assert.equal(pending.outcome,'pending');assert.equal(pending.timing.awaitingTotal,true);
    f.projection.total_click_count=2999;
    assert.equal((await f.execute('observe')).outcome,'unknown','regression below the query baseline was accepted');
    f.projection.total_click_count=3012;
    assert.equal((await f.execute('observe')).outcome,'confirmed');
    assert.equal(f.requests.length,2);
  }
  const early=fixture('success');
  await early.submit('left');early.armRight();await early.submit('right');
  assert.equal(early.store.userInfo.left_treasure_chest,1000);
  assert.equal(early.store.userInfo.right_treasure_chest,2000);
  assert.equal(early.refreshes(),0);
  early.projection.left_treasure_chest=1801000;
  const partial=await early.execute('observe');
  assert.equal(partial.outcome,'pending');
  assert.equal(partial.server.consumed,1000);
  assert.equal(partial.state.left,1801000);assert.equal(partial.state.right,2000);
  assert.equal(partial.state.consumed,3000);assert.equal(partial.state.local,15);
  assert.equal(early.refreshes(),1);assert.equal(early.requests.length,2);
  assert.deepEqual(early.cooldowns,[[true,'fixture','left',1801000,1801000]]);
  early.journal.left.outcome='armed';
  assert.equal((await early.execute('left')).outcome,'unknown');
  assert.equal(early.requests.length,2);
  const fields={left:{left_next_time:1801000,right_next_time:0},right:{left_next_time:0,right_next_time:1802000}};
  const receipted=fixture('success',fields);
  assert.equal((await receipted.submit('left')).slot.receiptCooldown,1801000);
  assert.equal(receipted.store.userInfo.left_treasure_chest,1000);
  receipted.armRight();
  assert.equal((await receipted.submit('right')).slot.receiptCooldown,1802000);
  let observed=await receipted.execute('observe');
  assert.equal(observed.outcome,'pending');assert.equal(observed.state.left,1801000);assert.equal(observed.state.right,1802000);
  receipted.projection.left_treasure_chest=1800500;
  receipted.projection.right_treasure_chest=1802000;
  observed=await receipted.execute('observe');
  assert.equal(observed.outcome,'pending');assert.equal(observed.state.left,1801000);
  receipted.projection.total_click_consume=3000;
  receipted.bag[42]=receipted.bag[43]=2;
  observed=await receipted.execute('observe');
  assert.equal(observed.outcome,'confirmed');assert.equal(observed.state.left,1800500);assert.equal(observed.state.right,1802000);
  assert.equal(receipted.requests.length,2);
  assert.deepEqual(receipted.cooldowns,[[true,'fixture','left',1801000,1801000],[true,'fixture','right',1802000,1802000]]);
  const conflict=fixture('success',fields);
  await conflict.submit('left');conflict.armRight();await conflict.submit('right');
  conflict.store.userInfo.left_treasure_chest=1801500;
  conflict.projection.left_treasure_chest=1800500;
  assert.equal((await conflict.execute('observe')).stage,'local-slot-conflict');
  const unknown=fixture('unknown-right',fields);
  await unknown.submit('left');unknown.armRight();
  assert.equal((await unknown.submit('right')).outcome,'unknown');
  assert.equal(unknown.store.userInfo.left_treasure_chest,1801000);
  assert.equal(unknown.store.userInfo.right_treasure_chest,2000);
  assert.equal((await unknown.execute('observe')).outcome,'unknown');
  assert.equal(unknown.requests.length,2);
  console.log('PASS: fixed-slot handoff, receipt cooldown projection, and conflict checks.');
})().catch(error=>{console.error(error);process.exitCode=1;});
