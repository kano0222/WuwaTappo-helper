(async()=>{ __STATE__
  const id=__BEFORE_OPERATIONID__,side=__SIDE__,other=side==='left'?'right':'left',expectedAccount=__BEFORE_ACCOUNTKEY__;
  const uncertain=()=>new Error('结果无法确认');
  const serverState=data=>{
    const fields=['total_click_count','total_click_consume','left_treasure_chest','right_treasure_chest'];
    if(!fields.every(k=>data?.[k]!=null&&Number.isSafeInteger(Number(data[k]))&&Number(data[k])>=0))throw uncertain();
    return {accountKey,total:Number(data.total_click_count),consumed:Number(data.total_click_consume),left:Number(data.left_treasure_chest),right:Number(data.right_treasure_chest)};
  };
  const contextMatches=()=>sameAccount()&&accountKey===expectedAccount&&!locked();
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
    reward:tx.reward??null,uiRefreshRequested:!!tx.uiRefreshRequested,...(tx.result??{})});
  let tx=globalThis.__TAPPO_HELPER_TX__;
  if(!contextMatches())return {...snapshot(),operationId:id,outcome:'unknown',failureStage:'account'};
  if(__RECOVER__){
    if(!tx||tx.id!==id||tx.accountKey!==expectedAccount||tx.store!==s)
      return {...snapshot(),operationId:id,outcome:'unknown',failureStage:'receipt-missing'};
    const observed=answer(tx);
    if(!tx.busy&&Date.now()>=(tx.nextRetryAt??0)&&!['confirmed','rejected','skipped'].includes(tx.result?.outcome)){tx.result=null;tx.start();}
    return observed;
  }
  if(tx?.id===id)return answer(tx);
  if(tx?.busy)return {...snapshot(),operationId:id,skipped:true,outcome:'skipped',reason:'上次请求仍在处理中'};
  const clicks=await import(__CLICKMODULE__);
  if(typeof m[__OPENCHEST__]!=='function'||typeof m[__BAGLIST__]!=='function'||typeof s.applyChestClickConsume!=='function'||typeof s.updateUserInfo!=='function'||typeof clicks[__SAVECLICKS__]!=='function')throw new Error('桌宠版本接口不匹配');
  tx={id,accountKey,store:s,side,busy:false,sent:false,applied:false,saved:false,receipt:false,rejection:null,result:null,error:null,stage:'preflight',startedAt:Date.now()};
  globalThis.__TAPPO_HELPER_TX__=tx;
  const check=()=>{if(!contextMatches()||globalThis.__TAPPO_HELPER_TX__!==tx)throw uncertain();};
  const save=async()=>{
    if(tx.saved)return;
    if(tx.saveTask&&!tx.saveSettled)throw uncertain();
    check();tx.stage='local-save';let failed=false;const original=console.warn;
    const observe=function(...args){if(args[0]==='[localClickCount] save failed')failed=true;return Reflect.apply(original,console,args);};
    console.warn=observe;
    tx.saveSettled=false;
    tx.saveTask=Promise.resolve().then(()=>clicks[__SAVECLICKS__](undefined,{force:true}));
    tx.saveTask.then(()=>{tx.saveSettled=true},()=>{tx.saveSettled=true});
    try{await bounded(tx.saveTask,8000);}
    finally{if(console.warn===observe)console.warn=original;}
    if(failed)throw uncertain();check();tx.saved=true;
  };
  const verify=async()=>{
    check();tx.stage='verify';
    const server=serverState(await readServer());check();const local=snapshot();
    if(tx.rejection!==null){tx.result={...local,claimBusy:false,outcome:'rejected',rejected:true,code:tx.rejection,left:server.left,right:server.right,confirmedServer:server};return;}
    if(!tx.receipt||!tx.applied||local.consumed!==tx.before.consumed+1000||server.consumed<tx.initial.consumed||
       server.consumed>tx.initial.consumed+1000||server[other]!==tx.initial[other]){tx.result={outcome:'unknown'};return;}
    if(server.consumed!==tx.initial.consumed+1000||server[side]<=tx.initial[side]||server.total<tx.before.cumulative){tx.result=null;return;}
    tx.stage='reward-receipt';
    if(!tx.reward){tx.result={outcome:'unknown'};return;}
    tx.stage='reward-verify';
    tx.reward.quantityAfter=quantity(await readBag(),tx.reward.assetId);check();
    if(tx.reward.quantityAfter<=tx.reward.quantityBefore){tx.result=Date.now()-tx.startedAt>60000?{outcome:'unknown'}:null;return;}
    await save();check();tx.stage='local-sync';
    const current=snapshot();
    if(current.consumed!==tx.before.consumed+1000||(s.serverClickSpend!==undefined&&s.serverClickSpend!==0)){tx.result={outcome:'unknown'};return;}
    s.updateUserInfo({...s.userInfo,left_treasure_chest:server.left,right_treasure_chest:server.right});
    const final=snapshot();
    if(final.consumed!==current.consumed||final.cumulative!==current.cumulative||final.available!==current.available||final.left!==server.left||final.right!==server.right)throw uncertain();
    check();
    if(__REFRESHPANEL__==='ready') {
      try{await bounded(window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-panel-action',payload:{type:'ready'}}),3000);tx.uiRefreshRequested=true;}
      catch{tx.uiRefreshRequested=false;}
    }
    check();tx.result={...final,claimBusy:false,outcome:'confirmed',confirmedServer:server,reward:tx.reward,rewardVerified:true};
  };
  const run=async()=>{
    check();
    if(!tx.sent){
      tx.stage='reward-preflight';tx.bagBefore=await readBag();check();
      tx.stage='preflight';tx.initial=serverState(await readServer());check();
      tx.before={...snapshot(),left:tx.initial.left,right:tx.initial.right};const before=tx.before;
      if(!Number.isSafeInteger(before.cumulative)||before.cumulative<tx.initial.total||before.available<1000||
         before.left!==__BEFORE_LEFT__||before.right!==__BEFORE_RIGHT__||before[side]>Date.now()||
         before.consumed!==__BEFORE_CONSUMED__||tx.initial.consumed!==before.consumed){tx.result={outcome:'skipped',skipped:true,reason:'领取条件已变化',confirmedServer:tx.initial};return;}
      tx.stage='request';tx.sent=true;
      let data;
      try{data=m[__DECODE__](await m[__OPENCHEST__]({place:side,click_num:before.cumulative}));}
      catch(error){const code=Number(error?.code);if(![70004,70005,70006].includes(code))throw error;tx.rejection=code;}
      check();
      if(tx.rejection===null){
        tx.stage='response';
        tx.receipt=Number(data?.click_consume)===1000;
        if(!tx.receipt)throw uncertain();
        const assetId=Number(data.asset_id),assetName=String(data.asset_name??'').trim(),assetType=String(data.asset_type??'').trim();
        if(Number.isSafeInteger(assetId)&&assetId>0&&assetName.length>0&&assetName.length<=200&&assetType.length>0&&assetType.length<=100)
          tx.reward={assetId,assetName,assetType,quantityBefore:quantity(tx.bagBefore,assetId),quantityAfter:0};
        tx.stage='local-apply';const posted=snapshot();
        if(posted.consumed!==before.consumed||posted.cumulative<before.cumulative||posted.available<1000)throw uncertain();
        tx.applied=true;s.applyChestClickConsume(1000);const applied=snapshot();
        if(applied.consumed!==before.consumed+1000||applied.cumulative!==posted.cumulative||applied.available!==posted.available-1000)throw uncertain();
        await save();
      }
    }
    if(tx.receipt&&tx.applied&&!tx.saved){
      if(snapshot().consumed!==tx.before.consumed+1000){tx.result={outcome:'unknown'};return;}
      await save();
    }
    await verify();
  };
  tx.start=()=>{
    if(tx.busy)return;tx.busy=true;tx.error=null;tx.attemptAt=Date.now();
    tx.task=run().catch(error=>{
      tx.error=failure(error,tx.stage);
      tx.failures=(tx.failures??0)+1;
      tx.nextRetryAt=Date.now()+Math.max(Math.min(60000,5000*2**Math.min(4,tx.failures-1))+Math.floor(Math.random()*1000),tx.error.retryAfterMs,[403,429].includes(tx.error.httpStatus)?60000:0);
      // Only read/verification and idempotent persistence stages may resume. Never repeat a submitted request.
      if((tx.sent&&!tx.receipt&&tx.rejection===null)||['local-apply','local-sync'].includes(tx.stage)||!contextMatches())tx.result={outcome:'unknown'};
    }).finally(()=>{tx.busy=false;});
  };
  tx.start();return answer(tx);
})()
