function observeClickReportTiming() {
  const existing=globalThis.__TAPPO_HELPER_REPORT_TIMING__;
  if(existing)return existing;
  const timing={startedAt:Date.now(),loginAt:null,loginAccount:null,firstNativeSuccessAt:null,nativeAccount:null,store:null};
  globalThis.__TAPPO_HELPER_REPORT_TIMING__=timing;
  const reset=loginAt=>{
    timing.loginAt=loginAt;
    timing.loginAccount=loginAt===null?null:timing.store?.userInfo?.cuid??null;
    timing.firstNativeSuccessAt=timing.nativeAccount=null;
  };
  try {
    window.addEventListener?.('cat-auth-login-success',()=>reset(Date.now()));
    window.addEventListener?.('cat-auth-logout',()=>reset(null));
    window.addEventListener?.('cat-click-report-success',()=>{
      const account=timing.store?.userInfo?.cuid??null;
      if(timing.firstNativeSuccessAt===null||timing.nativeAccount!==null&&timing.nativeAccount!==account) {
        timing.firstNativeSuccessAt=Date.now();
        timing.nativeAccount=account;
      }
    });
  } catch {}
  return timing;
}

function observeClickReports(store,environment) {
  let report=globalThis.__TAPPO_HELPER_CLICK_REPORT__;
  if(report?.store===store&&report.environment===environment)return report;
  report?.unsubscribe?.();
  if(typeof store.$onAction!=='function')return null;
  const timing=observeClickReportTiming();
  timing.store=store;
  report={store,environment,account:null,total:null,registeredAt:Date.now(),registeredLoggedIn:!!store.isLoggedIn,
    timingAccount:null,timingLoginAt:null,firstStoreSuccessAt:null,evidenceAt:null,evidenceLostAt:null,evidenceLostReason:null};
  const invalidate=reason=>{
    if(report.total!==null){
      report.evidenceLostAt=Date.now();
      report.evidenceLostReason=reason;
      report.evidenceLostAccount=report.account;
    }
    report.account=report.total=null;
  };
  report.unsubscribe=store.$onAction(({name,args,after})=>{
    const account=store.userInfo?.cuid, total=store.serverClickTotal;
    if(name==='resetState'||name==='applyChestClickConsume'){
      invalidate(name==='resetState'?'store-reset':'manual-consume');
    }else if(name==='updateUserInfo'){
      after(()=>{
        if(store.isLoggedIn&&store.userInfo?.cuid!=null&&store.userInfo.cuid!==account) {
          report.accountStoreReadyAt=Date.now();
          report.readyAccount=store.userInfo.cuid;
        }
        if(!store.isLoggedIn||store.userInfo?.cuid!==account||store.serverClickTotal!==total)
          invalidate(!store.isLoggedIn||store.userInfo?.cuid!==account?'account-change':'store-total-changed');
      });
    }else if(name==='applyClickReportSuccess'){
      invalidate('new-report-action');
      const replacedAt=report.evidenceLostAt;
      const reported=args[0];
      if(!store.isLoggedIn||account==null||account===''||!Number.isSafeInteger(reported)||reported<0)return;
      after(()=>{
        if(store.isLoggedIn&&store.userInfo?.cuid===account){
          if(report.timingAccount!==account||report.timingLoginAt!==timing.loginAt) {
            report.timingAccount=account;
            report.timingLoginAt=timing.loginAt;
            report.firstStoreSuccessAt=null;
            report.evidenceAt=null;
          }
          report.firstStoreSuccessAt??=Date.now();
        }
        if(store.isLoggedIn&&store.userInfo?.cuid===account&&store.serverClickTotal===reported&&store.localClickCount===0){
          report.account=account;
          report.total=reported;
          report.evidenceAt=Date.now();
          if(report.evidenceLostReason==='new-report-action'&&report.evidenceLostAt===replacedAt&&report.evidenceLostAccount===account) {
            report.evidenceLostAt=report.evidenceLostReason=report.evidenceLostAccount=null;
          }
        }
      });
    }
  },true);
  return globalThis.__TAPPO_HELPER_CLICK_REPORT__=report;
}

function clickReportTiming(report,account) {
  const timing=globalThis.__TAPPO_HELPER_REPORT_TIMING__;
  if(!timing||!report)return null;
  const current=report.timingAccount===account&&report.timingLoginAt===timing.loginAt;
  const native=timing.nativeAccount===null||timing.nativeAccount===account;
  const loginAt=timing.loginAccount===null||timing.loginAccount===account?timing.loginAt:null;
  const lost=report.evidenceLostAccount===account&&report.evidenceLostAt>=(loginAt??0);
  return {observedFrom:timing.startedAt,loginAt,listenerAt:report.registeredAt,
    accountStoreReadyAt:report.readyAccount===account?report.accountStoreReadyAt:null,
    loggedInAtRegistration:report.registeredLoggedIn,
    firstNativeSuccessAt:native?timing.firstNativeSuccessAt:null,
    nativeSuccessAccountKnown:native&&timing.nativeAccount!==null,
    firstStoreSuccessAt:current?report.firstStoreSuccessAt:null,
    evidenceAt:current&&report.evidenceAt>=(loginAt??0)?report.evidenceAt:null,
    evidenceLostAt:lost?report.evidenceLostAt:null,evidenceLostReason:lost?report.evidenceLostReason:null};
}
