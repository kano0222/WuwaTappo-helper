const m=await import(__COMMON__);
const globalQuery=__USERINFO__===''&&m[__ENVIRONMENT__]===true;
const regionFlag=m[__ENVIRONMENT__]===true;
const environment=regionFlag?(globalQuery?'global-global-prod':'cn-cn-prod'):m[__ENVIRONMENT__];
if(!['cn-cn-prod','global-global-prod'].includes(environment)||window.__TAURI_INTERNALS__?.metadata?.currentWindow?.label!=='main')throw new Error('应用环境不匹配');
const s=m[__STORE__];
if(!s||typeof m[__LOCKED__]!=='function'||typeof m[globalQuery?__COOKIE__:__USERINFO__]!=='function'||typeof m[__DECODE__]!=='function')throw new Error('桌宠版本接口不匹配');
__REPORT_OBSERVER__
const report=observeClickReports(s,environment);
const locked=()=>m[__LOCKED__]();
const account=s.userInfo?.cuid;
const accountKey=account==null||account===''?null:Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode('tappo:'+environment+':'+String(account))))).map(v=>v.toString(16).padStart(2,'0')).join('');
const sameAccount=()=>m[__ENVIRONMENT__]===(regionFlag?true:environment)&&s.isLoggedIn&&s.userInfo?.cuid===account&&accountKey!==null;
const queryUser=async()=>{
  if(!globalQuery)return m[__USERINFO__]();
  const token=m[__COOKIE__]('infoToken');
  if(typeof token!=='string'||!token)throw new Error('桌宠尚未登录');
  // Credentials remain inside the page. Never renew tokens or retry mutations here.
  const response=await fetch('https://api-wutheringwaves-wuwatappo.kurogames-global.com/api/frontend/user/me',{
    method:'GET',headers:{Authorization:'Bearer '+token},credentials:'omit',cache:'no-store',redirect:'error',signal:AbortSignal.timeout(8000)});
  if(!response.ok){const error=new Error('服务器状态查询失败');error.httpStatus=response.status;error.retryAfter=response.headers.get('Retry-After');throw error;}
  return response.json();
};
const failure=(error,stage)=>{
  const status=Number(error?.httpStatus??error?.status??error?.response?.status??0);
  const raw=error?.response?.headers?.get?.('Retry-After')??error?.retryAfter;
  const retry=Number(error?.retryAfterMs??(Number.isFinite(Number(raw))?Number(raw)*1000:Math.max(0,Date.parse(raw)-Date.now())));
  return {failureStage:stage,errorType:['Error','TypeError','TimeoutError','AbortError'].includes(error?.name)?error.name:'Error',
    httpStatus:Number.isInteger(status)&&status>=400&&status<=599?status:0,
    code:Number.isSafeInteger(Number(error?.code))?Number(error.code):0,
    retryAfterMs:Math.min(2147480000,Math.max(0,Number.isFinite(retry)?retry:0)),
    requiredRetryAfterMs:Math.min(2147480000,Math.max(0,Number(error?.requiredRetryAfterMs??(Number.isFinite(retry)?retry:0))))};
};
const bounded=async(promise,ms)=>{
  let timer;
  try{return await Promise.race([promise,new Promise((_,reject)=>{timer=setTimeout(()=>{const e=new Error('状态查询超时');e.name='TimeoutError';reject(e)},ms)})]);}
  finally{clearTimeout(timer);}
};
const retryQuery=()=>{
  const q=globalThis.__TAPPO_HELPER_QUERY__;
  if(!q||q.accountKey!==accountKey||(q.store&&q.store!==s))return 0;
  q.nextAt=Math.max(Date.now(),q.requiredAt??0);
  return Math.max(0,q.nextAt-Date.now());
};
const freshClaimRead=state=>{
  const age=Date.now()-state?.observedAt;
  return Number.isSafeInteger(state?.observedAt)&&age>=0&&age<5000;
};
const accountState=data=>{
  if(data?.cuid==null||String(data.cuid)!==String(account))throw new Error('账号已变化');
  const values=['total_click_count','total_click_consume','left_treasure_chest','right_treasure_chest']
    .map(key=>data[key]==null?NaN:Number(data[key]));
  if(!values.every(n=>Number.isSafeInteger(n)&&n>=0))throw Error('服务端状态格式异常');
  const [total,consumed,left,right]=values;
  return {accountKey,total,consumed,left,right,observedAt:Date.now()};
};
const readServer=async(timing,baseline)=>{
  if(!sameAccount()||locked())throw new Error('账号或更新状态已变化');
  let q=globalThis.__TAPPO_HELPER_QUERY__;
  if(!q||q.accountKey!==accountKey||(q.store&&q.store!==s))
    q=globalThis.__TAPPO_HELPER_QUERY__={accountKey,store:s,nextAt:0,failures:0,promise:null};
  q.store=s;
  if(!q.promise&&!q.failures&&baseline?.accountKey===accountKey&&baseline.observedAt!=null&&
     q.state?.observedAt>=baseline.observedAt&&freshClaimRead(q.state))return q.state;
  if(!q.promise){
    const wait=q.nextAt-Date.now();
    if(wait>2000){const e=new Error('等待服务器查询重试');e.retryAfterMs=wait;e.requiredRetryAfterMs=Math.max(0,(q.requiredAt??0)-Date.now());throw e;}
    // Reserve before yielding, so concurrent observers share one request.
    q.promise=(async()=>{
      if(wait>0)await new Promise(r=>setTimeout(r,wait));
      q.nextAt=Date.now()+2000;
      q.requiredAt=q.nextAt;
      try{
        if(!sameAccount()||locked())throw new Error('账号或更新状态已变化');
        // Bound the shared read itself, not just each observer. A late reply is discarded.
        if(timing)timing.requestStartedAt=Date.now();
        const data=m[__DECODE__](await bounded(Promise.resolve().then(queryUser),8000));
        if(!sameAccount())throw new Error('账号已变化');
        q.state=accountState(data);q.failures=0;return q.state;
      }catch(error){
        const info=failure(error,'query');q.failures++;
        const backoff=Math.min(60000,5000*2**Math.min(4,q.failures-1))+Math.floor(Math.random()*1000);
        q.nextAt=Date.now()+Math.max(backoff,info.retryAfterMs,[403,429].includes(info.httpStatus)?60000:0);
        q.requiredAt=Math.max(q.requiredAt??0,Date.now()+Math.max(info.requiredRetryAfterMs,[403,429].includes(info.httpStatus)?60000:0));
        error.requiredRetryAfterMs=Math.max(0,q.requiredAt-Date.now());
        error.retryAfterMs=Math.max(info.retryAfterMs,q.nextAt-Date.now());throw error;
      }
    })();
    const current=q.promise;current.then(()=>{if(q.promise===current)q.promise=null},()=>{if(q.promise===current)q.promise=null});
  } else if(timing)timing.sharedQuery=true;
  const data=await bounded(q.promise,8000);
  if(!sameAccount())throw new Error('账号已变化');
  // A query started by an earlier helper on the same page still returns the original response.
  return data===q.state?data:(q.state=accountState(data));
};
const snapshot=()=>({time:Date.now(),accountKey:sameAccount()?accountKey:null,loggedIn:s.isLoggedIn,locked:locked(),
  available:s.totalClickCount,cumulative:s.cumulativeClickCount,local:s.localClickCount,consumed:s.serverClickConsume,serverTotal:s.serverClickTotal,
  reportedClickTotal:sameAccount()&&report&&report===globalThis.__TAPPO_HELPER_CLICK_REPORT__&&
    report.account===account&&report.total===s.serverClickTotal?report.total:null,
  clickReportTiming:sameAccount()?clickReportTiming(report,account):null,
  left:Number(s.userInfo?.left_treasure_chest),right:Number(s.userInfo?.right_treasure_chest),
  claimBusy:!!(globalThis.__TAPPO_HELPER_TX__?.busy&&globalThis.__TAPPO_HELPER_TX__.accountKey===accountKey)});
const claimClickNum=(total,local)=>{
  if(!Number.isSafeInteger(local.serverTotal))return null;
  if(local.serverTotal<=total)return total;
  return local.reportedClickTotal===local.serverTotal?local.serverTotal:null;
};
const beginNativeCooldown=owner=>{
  const previous=globalThis.__TAPPO_HELPER_NATIVE_COOLDOWN__;
  if((previous?.id&&(!previous.pending||previous.owner===owner))||!sameAccount()||locked())return null;
  const id=crypto.randomUUID();
  globalThis.__TAPPO_HELPER_NATIVE_COOLDOWN__={id,owner,apply:null,pending:previous?.pending??{},complete(ready){
    const pending=this.pending;
    if(!pending)return;
    this.pending=null;
    if(!ready)this.apply=null;
    if(this.apply)for(const refresh of Object.values(pending))refresh(this.apply);
  }};
  return id;
};
const syncNativeCooldown=(side,before,after)=>{
  if((side!=='left'&&side!=='right')||!Number.isSafeInteger(before)||!Number.isSafeInteger(after)||
     after<=before||!sameAccount()||locked())return;
  const probe=globalThis.__TAPPO_HELPER_NATIVE_COOLDOWN__;
  if(!probe)return;
  const refresh=apply=>{
    try {
      if(sameAccount()&&!locked()&&Number(s.userInfo?.[side+'_treasure_chest'])===after)
        apply(s,account,side,after);
    } catch {}
  };
  if(probe.pending)probe.pending[side]=refresh;
  else if(probe.apply)refresh(probe.apply);
};
