(async()=>{ __STATE__
  const id=__BEFORE_OPERATIONID__,expectedAccount=__BEFORE_ACCOUNTKEY__,delta=__DELTA__,incremental=__COUNTMODE__==='increment';
  let tx=globalThis.__TAPPO_HELPER_BOOST__;
  const answer=()=>({...snapshot(),operationId:id,outcome:tx.result?.outcome??(tx.busy?'pending':'unknown'),failureStage:'boost-receipt',...(tx.result??{})});
  if(__RECOVER__){
    if(!tx||tx.id!==id||tx.accountKey!==expectedAccount||tx.store!==s||!sameAccount()||accountKey!==expectedAccount)
      return {...snapshot(),operationId:id,outcome:'unknown',failureStage:'receipt-missing'};
    return answer();
  }
  if(tx?.id===id)return answer();
  if(tx?.busy)throw new Error('上次补数仍在处理中');
  tx={id,accountKey:expectedAccount,store:s,busy:true,result:null};globalThis.__TAPPO_HELPER_BOOST__=tx;
  const perform=async()=>{
  const clicks=incremental?await import(__CLICKMODULE__):null;
  const before=snapshot(),account=s.userInfo?.cuid;
  if(!sameAccount()||accountKey!==expectedAccount)throw new Error('补数账号已变化');
  if (!before.loggedIn || before.locked || before.claimBusy || account===undefined || account===null || account==='' || before.available!==__BEFORE_AVAILABLE__ || before.cumulative!==__BEFORE_CUMULATIVE__) return {skipped:true,reason:'计数或账号状态已变化'};
  if(!Number.isSafeInteger(delta)||delta<1||delta>2000||!Number.isSafeInteger(before.cumulative+delta)||!Number.isSafeInteger(before.local+delta))throw new Error('计数超出范围');
  if(typeof s.$onAction!=='function' || typeof s.applyClickReportSuccess!=='function') throw new Error('补数回执能力不匹配');
  const sameContext = value => value.loggedIn && !value.locked && !value.claimBusy &&
    value.consumed===before.consumed && value.left===before.left && value.right===before.right && s.userInfo?.cuid===account;
  let appliedTotal=null,reportTotal=null;
  // Observe original synchronous store actions; never replace them or alter their counts.
  const unsubscribe=s.$onAction(({name,args,after})=>{
    if(name==='addLocalClickCount' && args[0]===delta && appliedTotal===null) {
      const start=snapshot();
      if(!sameContext(start) || start.cumulative!==before.cumulative || start.available!==before.available) return;
      after(()=>{const end=snapshot();
        if(sameContext(end) && end.cumulative===before.cumulative+delta && end.available===before.available+delta) appliedTotal=end.cumulative;
      });
    } else if(name==='applyClickReportSuccess' && appliedTotal===before.cumulative+delta && Number(args[0])===before.cumulative) {
      after(()=>{const end=snapshot();
        if(sameContext(end) && end.serverTotal===before.cumulative && end.local===0 && end.cumulative===before.cumulative) reportTotal=end.serverTotal;
      });
    }
  },true);
  const result=()=>{
    if(s.userInfo?.cuid!==account) throw new Error('补数期间账号变化，结果未确认，不自动重发');
    const value=snapshot();
    if(sameContext(value) && appliedTotal===before.cumulative+delta && reportTotal===before.cumulative &&
       value.cumulative===before.cumulative && value.available===before.available && value.serverTotal===reportTotal && value.local===0)
      return {...value,boostOverwritten:true,boostAppliedTotal:appliedTotal,boostReportTotal:reportTotal};
    return value;
  };
  try {
    if(incremental) {
      if(typeof s.incrementLocalClickCount!=='function'||typeof clicks[__SAVECLICKS__]!=='function')throw new Error('原生计数能力不匹配');
      // One synchronous batch: no animation, event delivery ambiguity, or yielded writes between increments.
      for(let i=0;i<delta;i++) {
        s.incrementLocalClickCount();const value=snapshot();
        if(!sameContext(value)||value.local!==before.local+i+1||value.cumulative!==before.cumulative+i+1||value.available!==before.available+i+1)
          throw new Error('原生计数结果未确认');
      }
      appliedTotal=before.cumulative+delta;
      let failed=false;const original=console.warn,saving=snapshot();
      const observe=function(...args){if(args[0]==='[localClickCount] save failed')failed=true;return Reflect.apply(original,console,args);};
      console.warn=observe;
      try{await bounded(clicks[__SAVECLICKS__](undefined,{force:true}),8000);}
      finally{if(console.warn===observe)console.warn=original;}
      const value=result();
      if(failed||!sameContext(value)||value.local!==saving.local||value.cumulative!==saving.cumulative||value.available!==saving.available)
        throw new Error('计数保存结果未确认');
      return value;
    }
    // A lost invoke reply does not prove the event was not delivered. Observe its receipt once.
    Promise.resolve().then(()=>window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-panel-action',payload:{type:'boost-local-clicks',delta}})).catch(()=>{});
    for(let i=0;i<180;i++) {
      await new Promise(r=>setTimeout(r,250)); const value=result();
      if(value.boostOverwritten)return value;
      if(!sameContext(value))throw new Error('补数期间状态变化');
      if(appliedTotal===before.cumulative+delta&&value.cumulative>=before.cumulative+delta&&value.available>=before.available+delta)return value;
    }
    throw new Error('补数回执未确认');
  } finally {unsubscribe();}
  };
  tx.task=perform().then(value=>{tx.result={...value,outcome:value.skipped?'skipped':'confirmed'};},()=>{tx.result={outcome:'unknown'};}).finally(()=>{tx.busy=false;});
  await bounded(tx.task,4000).catch(()=>{});return answer();
})()
