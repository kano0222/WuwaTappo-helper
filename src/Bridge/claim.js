(async()=>{ __STATE__ const before=snapshot(),side=__SIDE__;
      if (!before.loggedIn || before.locked || before.claimBusy || before.available<1000 || before.left!==__BEFORE_LEFT__ || before.right!==__BEFORE_RIGHT__ || before[side]>Date.now() || before.consumed!==__BEFORE_CONSUMED__) return {skipped:true,reason:'领取条件已变化'};
      const button=document.querySelector('.reward-anchor--'+side+' .reward-btn');
      if (!button || button.disabled) return {skipped:true,reason:'宝箱入口尚未就绪'};
      let rejectedCode=null;
      const originalWarn=console.warn;
      // Observe the original handler's business error without changing or resending its request.
      const observeWarn=function(...args) {
        if(args[0]==='[chest] openChest failed') {
          const code=Number(args[1]?.code);
          if(Number.isSafeInteger(code) && code>0) rejectedCode=code;
        }
        return Reflect.apply(originalWarn,console,args);
      };
      console.warn=observeWarn;
      try {
        button.click();
        const until=Date.now()+20000;
        while(Date.now()<until) { await new Promise(r=>setTimeout(r,250)); const now=snapshot();
          if(rejectedCode!==null) return {...now,rejected:true,code:rejectedCode};
          const other=side==='left'?'right':'left';
          if(now.consumed>before.consumed+1000 || now[other]!==before[other]) throw new Error('领取期间出现其他计数或槽位变化，已暂停且不会自动重发');
          if(now.consumed===before.consumed+1000 && now[side]>before[side] && !now.claimBusy) return now;
        }
        throw new Error('未确认领取结果，不自动重试');
      } finally { if(console.warn===observeWarn) console.warn=originalWarn; }
      })()