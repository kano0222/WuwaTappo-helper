(async()=>{ __STATE__ const before=snapshot(),delta=__DELTA__,account=s.userInfo?.cuid;
  if (!before.loggedIn || before.locked || account===undefined || account===null || account==='' || before.available!==__BEFORE_AVAILABLE__ || before.cumulative!==__BEFORE_CUMULATIVE__) return {skipped:true,reason:'计数或账号状态已变化'};
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
    await window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-panel-action',payload:{type:'boost-local-clicks',delta}});
    for(let i=0;i<12;i++) {
      await new Promise(r=>setTimeout(r,250)); const value=result();
      if(value.boostOverwritten || !sameContext(value) || value.cumulative>=before.cumulative+delta && value.available>=before.available+delta) return value;
    }
    return result();
  } finally {unsubscribe();}
})()
