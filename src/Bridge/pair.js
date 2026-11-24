(async()=>{ __STATE__
  __CLAIM_TRANSPORT__
  const journal=__PAIR_JOURNAL__, command=__PAIR_COMMAND__, base=journal.server;
  const slots=['left','right'], due=side=>base[side]<=Date.now()-3000;
  const context=()=>sameAccount()&&!locked()&&accountKey===base.accountKey;
  const spendClear=()=>s.serverClickSpend===undefined||s.serverClickSpend===0;
  const timing={};
  const answer=(outcome,stage,extra={})=>({operationId:journal.id,accountKey,outcome,stage,timing,...extra});
  const readAccount=async()=>{
    timing.queryStartedAt=Date.now();
    let d;
    try{d=await readServer(timing);}
    finally{timing.queryFinishedAt=Date.now();}
    const values=['total_click_count','total_click_consume','left_treasure_chest','right_treasure_chest']
      .map(key=>d?.[key]==null?NaN:Number(d[key]));
    if(!context()||!values.every(n=>Number.isSafeInteger(n)&&n>=0))throw Error('账号或查询异常');
    const [total,consumed,left,right]=values;return {accountKey,total,consumed,left,right};
  };
  const readBag=async()=>{
    const items=m[__DECODE__](await bounded(m[__BAGLIST__](),8000));
    if(!context()||!Array.isArray(items))throw Error('背包查询异常');
    const quantities={};
    for(const item of items){
      const id=Number(item?.asset_id),q=item?.quantity==null?NaN:Number(item.quantity);
      if(!Number.isSafeInteger(id)||id<1||!Number.isSafeInteger(q)||q<0||
         !Number.isSafeInteger((quantities[id]??0)+q))throw Error('背包数量异常');
      quantities[id]=(quantities[id]??0)+q;
    }
    return quantities;
  };
  const mutateLocal=(fields,expectedConsumed)=>{
    const before=snapshot();
    const debit=expectedConsumed-before.consumed;
    if(!context()||!spendClear()||![before.consumed,before.available,before.local,before.serverTotal,before.cumulative,expectedConsumed]
      .every(n=>Number.isSafeInteger(n)&&n>=0)||debit<0||debit>before.available)throw Error('本地消费状态已变化');
    if(Object.keys(fields).length)s.updateUserInfo({...s.userInfo,...fields});
    const after=snapshot();
    if(!spendClear()||after.consumed!==expectedConsumed||after.local!==before.local||
       after.serverTotal!==before.serverTotal||after.cumulative!==before.cumulative||
       after.available!==before.available-debit)throw Error('本地计数异常');
    return after;
  };
  const validReceipt=slot=>slot?.outcome==='success'&&slot.cost===1000&&
    Number.isSafeInteger(slot.assetId)&&slot.assetId>0;
  let stage='account', sent=false, request, observed;
  try{
    if(!context()||!spendClear())return answer('unknown',stage);
    if(command==='observe'){
      stage='verify';
      const server=observed=await readAccount();
      if(journal.left.outcome==='rejected'&&!journal.right){
        const local=snapshot();
        if(server.consumed===base.consumed&&server.left===base.left&&server.right===base.right&&
           local.consumed===base.consumed&&local.left===base.left&&local.right===base.right)
          return answer('rejected',stage,{server,state:local});
        return answer('unknown','conflict',{server});
      }
      const selected=[journal.left,...(journal.right&&!['skipped','rejected'].includes(journal.right.outcome)?[journal.right]:[])];
      if(!selected.every(validReceipt)||!journal.bagBefore)return answer('unknown','receipt-missing',{server});
      const expectedDebit=1000*selected.length, debit=server.consumed-base.consumed;
      const local=snapshot(), localDebit=local.consumed-base.consumed;
      if(![0,1000,2000].includes(debit)||debit>expectedDebit||![0,1000,2000].includes(localDebit)||
         localDebit>expectedDebit||server.total<Math.max(...selected.map(slot=>slot.clickNum))||
         server.left<base.left||server.right<base.right||(selected.length===1&&server.right!==base.right))
        return answer('unknown','conflict',{server});
      const complete=debit===expectedDebit&&server.left>base.left&&(selected.length===1||server.right>base.right);
      const fields={};
      for(const side of slots)
        if(server[side]>base[side]&&local[side]!==server[side]){
          if(local[side]>server[side])return answer('unknown','local-slot-conflict',{server});
          fields[side+'_treasure_chest']=server[side];
        }
      if(complete&&localDebit<expectedDebit)fields.total_click_consume=server.consumed;
      timing.awaitingDebit=debit!==expectedDebit;
      timing.leftEligible=server.left>base.left;
      timing.rightEligible=selected.length===2&&server.right>base.right;
      stage='local-sync';
      const synced=mutateLocal(fields,complete?server.consumed:local.consumed);
      for(const side of slots){
        const written=fields[side+'_treasure_chest'];
        if(written!==undefined&&written===synced[side])timing[side+'WrittenAt']=Date.now();
      }
      if(Object.keys(fields).some(field=>field.endsWith('_treasure_chest'))&&__REFRESHPANEL__==='ready'){
        try{await bounded(window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-panel-action',payload:{type:'ready'}}),3000);}
        catch{}
      }
      if(!complete)return answer('pending','verify',{server,state:synced});
      stage='reward-verify';
      timing.bagStartedAt=Date.now();
      let bag;
      try{bag=await readBag();}
      finally{timing.bagFinishedAt=Date.now();}
      const needed={};
      for(const slot of selected)needed[slot.assetId]=(needed[slot.assetId]??0)+1;
      if(Object.entries(needed).some(([id,n])=>(bag[id]??0)-(journal.bagBefore[id]??0)<n))
        return answer('pending',stage,{server,state:synced});
      const final=snapshot();
      if(!context()||!spendClear()||final.consumed!==server.consumed||
         final.left!==server.left||final.right!==server.right)return answer('unknown','local-sync',{server});
      return answer('confirmed',stage,{server,state:final});
    }
    const side=command, slot=journal[side], first=side==='left';
    if(!slots.includes(side)||slot?.outcome!=='armed')return answer('unknown','intent-missing');
    let live=globalThis.__TAPPO_HELPER_PAIR__;
    if(first){
      if(live?.id===journal.id)return answer('unknown','pair-context');
      if(snapshot().claimBusy||live?.busy)return answer('skipped','active');
      live=globalThis.__TAPPO_HELPER_PAIR__={id:journal.id,store:s,busy:false,sent:{}};
    }
    if(live?.id!==journal.id||live.store!==s||live.busy||live.sent[side]||
       (!first&&(!validReceipt(journal.left)||!journal.left.applied||live.leftId!==journal.left.id)))
      return answer('unknown','pair-context');
    const owned=()=>context()&&globalThis.__TAPPO_HELPER_PAIR__===live&&spendClear();
    live.busy=true;
    try{
      stage='preflight';
      const bagBefore=first?await readBag():null;
      const server=await readAccount();
      const local=snapshot(), expected=base.consumed+(first?0:1000);
      if(!claimCountCurrent(server.total,local))return answer('skipped','click-baseline',{server});
      if(!owned()||!due('left')||!due('right')||server.total<base.total||
         server.right!==base.right||server.left<base.left||
         (first?server.left!==base.left||server.consumed!==base.consumed:
           server.consumed!==base.consumed&&server.consumed!==expected)||
         server.total-expected<(first?2000:1000)||local.consumed!==expected||
         local.left!==journal.before.left||local.right!==journal.before.right||
         !Number.isSafeInteger(local.available)||local.available<1000)
        return answer('skipped',stage,{server});
      stage='request';
      request={id:slot.id,outcome:'unknown',clickNum:server.total};
      let data,countChanged=false;
      try{data=m[__DECODE__](await requestChest({place:side,click_num:request.clickNum},()=>{
        const current=snapshot();
        if(!owned()||current.consumed!==expected||current.left!==local.left||current.right!==local.right)
          throw Error('请求发送前条件已变化');
        if(!claimCountCurrent(request.clickNum,current)){countChanged=true;throw Error('等待服务器同步点击数');}
        live.sent[side]=sent=true;request.sentAt=Date.now();
      },slot.id));}
      catch(e){
        if(countChanged)return answer('skipped','click-baseline',{server});
        const info=failure(e,stage);
        if(sent&&info.httpStatus===0&&[70004,70005,70006].includes(info.code)&&owned())
          return answer('rejected',stage,{slot:{...request,outcome:'rejected',code:info.code,receivedAt:Date.now()},server});
        throw e;
      }
      const assetId=Number(data?.asset_id);
      if(!owned()||Number(data?.click_consume)!==1000||!Number.isSafeInteger(assetId)||assetId<1)
        return answer('unknown','receipt',{slot:request});
      const receipt={...request,outcome:'success',receivedAt:Date.now(),cost:1000,assetId,applied:false};
      if(first)live.leftId=slot.id;
      stage='local-apply';
      if(snapshot().consumed===expected){
        try{
          mutateLocal({total_click_consume:expected+1000},expected+1000);
          receipt.applied=true;
        }
        catch{}
      }
      return answer('success',stage,{slot:receipt,bagBefore,server});
    }finally{live.busy=false;}
  }catch(error){
    const info=failure(error,stage);
    timing.errorType=info.errorType;
    return answer('unknown',stage,{server:observed,slot:sent?request:null,retryAfterMs:info.retryAfterMs,httpStatus:info.httpStatus});
  }
})()
