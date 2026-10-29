const m=await import(__COMMON__);
if(m[__ENVIRONMENT__]!=='cn-cn-prod'||window.__TAURI_INTERNALS__?.metadata?.currentWindow?.label!=='main')throw new Error('应用环境不匹配');
const s=m[__STORE__];
if(!s||typeof m[__LOCKED__]!=='function'||typeof m[__USERINFO__]!=='function'||typeof m[__DECODE__]!=='function')throw new Error('桌宠版本接口不匹配');
const locked=()=>m[__LOCKED__]();
const account=s.userInfo?.cuid;
const accountKey=account==null||account===''?null:Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode('tappo:'+m[__ENVIRONMENT__]+':'+String(account))))).map(v=>v.toString(16).padStart(2,'0')).join('');
const sameAccount=()=>s.isLoggedIn&&s.userInfo?.cuid===account&&accountKey!==null;
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
  if(!q||q.accountKey!==accountKey)return 0;
  q.nextAt=Math.max(Date.now(),q.requiredAt??0);
  return Math.max(0,q.nextAt-Date.now());
};
const readServer=async()=>{
  if(!sameAccount()||locked())throw new Error('账号或更新状态已变化');
  let q=globalThis.__TAPPO_HELPER_QUERY__;
  if(!q||q.accountKey!==accountKey)q=globalThis.__TAPPO_HELPER_QUERY__={accountKey,nextAt:0,failures:0,promise:null};
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
        const data=m[__DECODE__](await bounded(Promise.resolve().then(()=>m[__USERINFO__]()),8000));
        if(!sameAccount()||data?.cuid==null||String(data.cuid)!==String(account))throw new Error('账号已变化');
        q.failures=0;return data;
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
  }
  const data=await bounded(q.promise,8000);
  if(!sameAccount())throw new Error('账号已变化');return data;
};
const snapshot=()=>({time:Date.now(),accountKey:sameAccount()?accountKey:null,loggedIn:s.isLoggedIn,locked:locked(),
  available:s.totalClickCount,cumulative:s.cumulativeClickCount,local:s.localClickCount,consumed:s.serverClickConsume,serverTotal:s.serverClickTotal,
  left:Number(s.userInfo?.left_treasure_chest),right:Number(s.userInfo?.right_treasure_chest),
  claimBusy:!!(globalThis.__TAPPO_HELPER_TX__?.busy&&globalThis.__TAPPO_HELPER_TX__.accountKey===accountKey)});
