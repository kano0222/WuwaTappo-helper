const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const bridge=name=>fs.readFileSync(path.join(__dirname,'../src/Bridge/'+name+'.js'),'utf8');
const claim=bridge('claim');
const transport=claim.slice(claim.indexOf('  const requestChest='),claim.indexOf('  const id='));
const injected=`
  const s=store,m=api,accountKey='fixture',sameAccount=()=>store.userInfo.cuid==='fixture',locked=()=>false;
  const claimCountCurrent=(total,v)=>v.serverTotal<=total;
  const readServer=async()=>({...projection});
  const snapshot=()=>({time:Date.now(),accountKey,consumed:s.userInfo.total_click_consume,
    left:s.userInfo.left_treasure_chest,right:s.userInfo.right_treasure_chest,available:s.userInfo.total_click_count+15-s.userInfo.total_click_consume,
    local:15,cumulative:s.userInfo.total_click_count+15,serverTotal:s.userInfo.total_click_count});
  const bounded=p=>p;
  const failure=(e,stage)=>({code:e.code??0,httpStatus:0,retryAfterMs:0});
`;
function fixture(kind){
  const baseline={accountKey:'fixture',total:3000,consumed:1000,left:1000,right:2000};
  const journal={id:'first',server:baseline,before:{left:1000,right:2000},left:{id:'first',outcome:'armed'}};
  const projection={total_click_count:3000,total_click_consume:1000,left_treasure_chest:1000,right_treasure_chest:2000};
  const store={serverClickSpend:0,userInfo:{cuid:'fixture',...projection},updateUserInfo(info){this.userInfo=info;}};
  const requests=[],bag={42:1,43:1};
  let refreshes=0;
  const context=vm.createContext({store,projection,Date:{now:()=>100000},
    api:{
      decode(value){if(value.code)throw Object.assign(Error('rejected'),{code:value.code});return value.data;},
      bag:()=>({data:Object.entries(bag).map(([asset_id,quantity])=>({asset_id,quantity}))}),
      cookie:()=> 'fixture',toast:()=>{},
      async renew(){
        if(kind==='renew-conflict'&&requests.length)store.serverClickSpend=1;
        if(kind==='renew-count-race'&&requests.length)store.userInfo.total_click_count+=6;
      }
    },
    fetch:async(url,options)=>{
      const body=JSON.parse(options.body);
      requests.push({body,trace:options.headers['X-BLM-FRID']});
      if(kind==='unknown-right'&&body.place==='right')throw Error('reply lost');
      return {ok:true,json:async()=>({code:0,data:{click_consume:1000,
        asset_id:body.place==='left'||kind==='same-asset'?42:43}})};
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
  return {journal,projection,store,requests,bag,execute,submit,refreshes:()=>refreshes,
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
      const expected=kind==='account-conflict'||kind==='renew-count-race'?'skipped':kind==='unknown-right'||kind==='renew-conflict'?'unknown':'success';
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
  early.journal.left.outcome='armed';
  assert.equal((await early.execute('left')).outcome,'unknown');
  assert.equal(early.requests.length,2);
  console.log('PASS: 10 fixed-slot bridge scenarios.');
})().catch(error=>{console.error(error);process.exitCode=1;});
