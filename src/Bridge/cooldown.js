(()=>{
  try {
    const probe=globalThis.__TAPPO_HELPER_NATIVE_COOLDOWN__;
    if(probe?.id!==__PROBE_ID__||!probe.pending||probe.apply)return false;
    probe.apply=(store,cuid,side,deadline)=>{
      if(Xr==null||store!==A||!A.isLoggedIn||!St.value||A.userInfo?.cuid!==cuid||
         (side!=='left'&&side!=='right')||!Number.isSafeInteger(deadline)||
         Number(A.userInfo[side+'_treasure_chest'])!==deadline||deadline<=ys())return false;
      const privateEnd=side==='left'?xr:Or;
      if(!Number.isFinite(privateEnd)||privateEnd>performance.now())return false;
      if(side==='left'){xr=null;Xo.value=0;}
      else{Or=null;zo.value=0;}
      oo();
      return true;
    };
  } catch {}
  return false;
})()
