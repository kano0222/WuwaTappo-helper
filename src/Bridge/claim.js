(async()=>{ __STATE__
  __CLAIM_TRANSPORT__
  const id=__BEFORE_OPERATIONID__,side=__SIDE__,other=side==='left'?'right':'left',expectedAccount=__BEFORE_ACCOUNTKEY__;
  const baseline=__CLAIM_SERVER__;
  const uncertain=()=>new Error('结果无法确认');
  const contextMatches=()=>sameAccount()&&accountKey===expectedAccount&&!locked();
  const hasUnsettledSpend=()=>s.serverClickSpend!==undefined&&s.serverClickSpend!==0;
  const readBag=async()=>{
    const data=m[__DECODE__](await bounded(m[__BAGLIST__](),8000));
    if(!contextMatches()||!Array.isArray(data)||!data.every(x=>x?.asset_id!=null&&Number.isSafeInteger(Number(x.asset_id))&&Number(x.asset_id)>0&&
      x.quantity!=null&&Number.isSafeInteger(Number(x.quantity))&&Number(x.quantity)>=0))throw uncertain();return data;
  };
  const quantity=(bag,id)=>bag.filter(x=>Number(x?.asset_id)===id).reduce((sum,x)=>{
    const n=Number(x.quantity);if(x.quantity==null||!Number.isSafeInteger(n)||n<0||!Number.isSafeInteger(sum+n))throw uncertain();return sum+n;
  },0);
  const answer=tx=>({...snapshot(),claimBusy:false,operationId:id,outcome:tx.result?.outcome??(tx.busy&&Date.now()-tx.attemptAt>45000?'unknown':'pending'),
    failureStage:tx.error?.failureStage??tx.stage,errorType:tx.error?.errorType??(tx.busy&&Date.now()-tx.attemptAt>45000?'TimeoutError':null),httpStatus:tx.error?.httpStatus??0,
    retryAfterMs:Math.max(0,(tx.nextRetryAt??0)-Date.now()),code:tx.rejection??tx.error?.code??0,
    requiredRetryAfterMs:Math.max(0,(tx.requiredRetryAt??0)-Date.now()),
    waitReason:tx.error?null:tx.waitReason??null,reward:tx.reward??null,uiRefreshRequested:!!tx.uiRefreshRequested,
    observedServer:tx.observedServer??null,serverObservedAt:tx.serverObservedAt??null,
    clickNum:tx.clickNum??null,receiptCooldown:tx.receiptCooldown??null,claimReceivedAt:tx.receivedAt??null,...(tx.result??{})});
  let tx=globalThis.__TAPPO_HELPER_TX__;
  if(!contextMatches())return {...snapshot(),operationId:id,outcome:'unknown',failureStage:'account'};
  if(__RECOVER__){
    if(!tx||tx.id!==id||tx.accountKey!==expectedAccount||tx.store!==s)
      return {...snapshot(),operationId:id,outcome:'unknown',failureStage:'receipt-missing'};
    const observed=answer(tx);
    if(tx.sent&&!tx.busy&&Date.now()>=(tx.nextRetryAt??0)&&!['confirmed','rejected','skipped'].includes(tx.result?.outcome)){tx.result=null;tx.start();}
    return observed;
  }
  if(tx?.id===id)return answer(tx);
  if(tx?.busy)return {...snapshot(),operationId:id,skipped:true,outcome:'skipped',reason:'上次请求仍在处理中'};
  if(typeof m[__OPENCHEST__]!=='function'||typeof m[__BAGLIST__]!=='function'||typeof s.updateUserInfo!=='function')throw new Error('桌宠版本接口不匹配');
  tx={id,accountKey,store:s,side,busy:false,sent:false,applied:false,receipt:false,rejection:null,result:null,error:null,stage:'preflight',startedAt:Date.now()};
  globalThis.__TAPPO_HELPER_TX__=tx;
  const check=()=>{if(!contextMatches()||globalThis.__TAPPO_HELPER_TX__!==tx)throw uncertain();};
  const syncSlots=async(server,final,refresh=true)=>{
    tx.stage='local-sync';
    const current=snapshot();
    if(current.consumed!==tx.before.consumed+1000||hasUnsettledSpend())return null;
    if(current[side]!==server[side]||(final&&current[other]!==server[other])){
      const slots=final?{left_treasure_chest:server.left,right_treasure_chest:server.right}
        :{[side+'_treasure_chest']:server[side]};
      s.updateUserInfo({...s.userInfo,...slots});
    }
    const synced=snapshot();
    if(synced.consumed!==current.consumed||synced.cumulative!==current.cumulative||synced.available!==current.available||
       synced.serverTotal!==current.serverTotal||synced.local!==current.local||
       synced[side]!==server[side]||synced[other]!==(final?server[other]:current[other]))throw uncertain();
    check();
    syncNativeCooldown(side,current[side],synced[side]);
    if(refresh&&__REFRESHPANEL__==='ready'&&(tx.refreshCooldown!==server[side]||final&&!tx.uiRefreshRequested)){
      tx.refreshCooldown=server[side];
      try{await bounded(window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-panel-action',payload:{type:'ready'}}),3000);tx.uiRefreshRequested=true;}
      catch{tx.uiRefreshRequested=false;}
    }
    check();return synced;
  };
  const verify=async()=>{
    check();tx.stage='verify';
    const server=await readServer();check();const local=snapshot();
    tx.observedServer=server;tx.serverObservedAt=server.observedAt;
    if(tx.rejection!==null){tx.result={...local,claimBusy:false,outcome:'rejected',rejected:true,code:tx.rejection,left:server.left,right:server.right,confirmedServer:server};return;}
    if(!tx.receipt||!tx.applied||local.consumed!==tx.before.consumed+1000||server.consumed<tx.initial.consumed||
       server.consumed>tx.initial.consumed+1000||server[other]!==tx.initial[other]){tx.waitReason=null;tx.result={outcome:'unknown'};return;}
    const waits=[];
    if(server.consumed!==tx.initial.consumed+1000)waits.push('consumption');
    if(server[side]<=tx.initial[side])waits.push('cooldown');
    if(server.total<(tx.clickNum??tx.initial.total))waits.push('cumulative');
    // The account query can supply a fresh slot before the debit or reward is visible.
    const olderThanReceipt=local[side]===tx.receiptCooldown&&server[side]<tx.receiptCooldown;
    if(tx.reward&&!waits.includes('cooldown')&&server.total>=tx.initial.total&&!olderThanReceipt){
      if(!await syncSlots(server,false)){tx.result={outcome:'unknown'};return;}
      tx.stage='verify';
    }
    if(waits.length){tx.waitReason=waits.join('.');tx.result=null;return;}
    if(tx.waitReason!=='reward')tx.waitReason=null;
    tx.stage='reward-receipt';
    if(!tx.reward){tx.result={outcome:'unknown'};return;}
    tx.stage='reward-verify';
    tx.reward.quantityAfter=quantity(await readBag(),tx.reward.assetId);check();
    if(tx.reward.quantityAfter<=tx.reward.quantityBefore){tx.waitReason='reward';tx.result=Date.now()-tx.startedAt>60000?{outcome:'unknown'}:null;return;}
    tx.waitReason=null;
    const final=await syncSlots(server,true);
    if(!final){tx.result={outcome:'unknown'};return;}
    check();tx.result={...final,claimBusy:false,outcome:'confirmed',confirmedServer:server,reward:tx.reward,rewardVerified:true};
  };
  const run=async()=>{
    check();
    if(!tx.sent){
      tx.stage='reward-preflight';tx.bagBefore=await readBag();check();
      tx.stage='preflight';tx.initial=await readServer(undefined,baseline);check();
      const local=snapshot();
      tx.before={...local,left:tx.initial.left,right:tx.initial.right};const before=tx.before;
      const skip=(reason='领取条件已变化')=>({outcome:'skipped',skipped:true,reason,confirmedServer:tx.initial});
      const clickNum=claimClickNum(tx.initial.total,before);
      if(clickNum===null){tx.result=skip('等待服务器同步点击数');return;}
      if(!Number.isSafeInteger(before.cumulative)||!Number.isSafeInteger(before.available)||before.available<1000||
         tx.initial.total-tx.initial.consumed<1000||hasUnsettledSpend()||
         before.left!==__BEFORE_LEFT__||before.right!==__BEFORE_RIGHT__||before[side]>Date.now()||
         before.consumed!==__BEFORE_CONSUMED__||tx.initial.consumed!==before.consumed){tx.result=skip();return;}
      tx.stage='request';
      let data,invalidated=false;
      const payload={place:side,click_num:clickNum};
      try{data=m[__DECODE__](await requestChest(payload,()=>{
        check();
        const current=snapshot(), selected=claimClickNum(tx.initial.total,current);
        if(selected===null||selected<clickNum||current.consumed!==before.consumed||
           current.left!==local.left||current.right!==local.right||
           !Number.isSafeInteger(current.available)||current.available<1000||hasUnsettledSpend()){
          invalidated=true;throw uncertain();
        }
        tx.clickNum=payload.click_num=selected;tx.sent=true;
      }));}
      catch(error){
        if(invalidated){tx.result=skip();return;}
        const code=Number(error?.code);if(!tx.sent||![70004,70005,70006].includes(code)||Number(error?.httpStatus)>=400)throw error;tx.rejection=code;
      }
      check();
      if(tx.rejection===null){
        tx.stage='response';
        tx.receipt=Number(data?.click_consume)===1000;
        if(!tx.receipt)throw uncertain();
        tx.receivedAt=Date.now();
        const assetId=Number(data.asset_id),assetName=String(data.asset_name??'').trim(),assetType=String(data.asset_type??'').trim();
        if(Number.isSafeInteger(assetId)&&assetId>0&&assetName.length>0&&assetName.length<=200&&assetType.length>0&&assetType.length<=100)
          tx.reward={assetId,assetName,assetType,quantityBefore:quantity(tx.bagBefore,assetId),quantityAfter:0};
        tx.stage='local-apply';const posted=snapshot();
        if(posted.consumed!==before.consumed||posted.cumulative<before.cumulative||posted.available<1000||hasUnsettledSpend())throw uncertain();
        // The request only used server-confirmed clicks. Leave the local increment to the native report.
        s.updateUserInfo({...s.userInfo,total_click_consume:posted.consumed+1000});
        const applied=snapshot();
        if(applied.consumed!==before.consumed+1000||applied.local!==posted.local||applied.serverTotal!==posted.serverTotal||
           applied.cumulative!==posted.cumulative||applied.available!==posted.available-1000)throw uncertain();
        tx.applied=true;
        const cooldown=receiptDeadline(data[side+'_next_time'],before[side]);
        if(tx.reward&&cooldown!==null&&applied[side]===before[side]){
          if(await syncSlots({[side]:cooldown},false,false))tx.receiptCooldown=cooldown;
        }
      }
    }
    await verify();
  };
  tx.start=()=>{
    if(tx.busy)return;tx.busy=true;tx.error=null;tx.attemptAt=Date.now();
    tx.task=run().catch(error=>{
      tx.error=failure(error,tx.stage);
      if(!tx.sent){
        tx.requiredRetryAt=Date.now()+tx.error.requiredRetryAfterMs;
        tx.nextRetryAt=Date.now()+tx.error.retryAfterMs;
        tx.result={outcome:'skipped',skipped:true,reason:'领取条件已变化',confirmedServer:tx.initial};
        return;
      }
      tx.failures=(tx.failures??0)+1;
      tx.nextRetryAt=Date.now()+Math.max(Math.min(60000,5000*2**Math.min(4,tx.failures-1))+Math.floor(Math.random()*1000),tx.error.retryAfterMs,[403,429].includes(tx.error.httpStatus)?60000:0);
      // After submission, recovery can verify the result but cannot repeat the request.
      if((tx.sent&&!tx.receipt&&tx.rejection===null)||['local-apply','local-sync'].includes(tx.stage)||!contextMatches())tx.result={outcome:'unknown'};
    }).finally(()=>{tx.busy=false;});
  };
  tx.start();return answer(tx);
})()
