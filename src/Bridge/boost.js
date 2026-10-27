(async()=>{ __STATE__ const before=snapshot();
      if (!before.loggedIn || before.locked || before.available!==__BEFORE_AVAILABLE__ || before.cumulative!==__BEFORE_CUMULATIVE__) return {skipped:true,reason:'计数已变化'};
      await window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-panel-action',payload:{type:'boost-local-clicks',delta:__DELTA__}});
      await new Promise(r=>setTimeout(r,250)); return snapshot(); })()