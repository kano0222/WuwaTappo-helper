  const requestChest=async (payload,beforeSend=()=>{},trace=null)=>{
    if(!__CHESTHOST__){beforeSend();return m[__OPENCHEST__](payload);}
    // The native bag preflight already performs token renewal, including on
    // Global builds where renewal is private. Keep credentials inside the page.
    if(__CHESTRENEW__)await m[__CHESTRENEW__]();
    if(!sameAccount()||locked())throw new Error('账号或更新状态已变化');
    const token=m[__CHESTCOOKIE__]('infoToken');
    if(typeof token!=='string'||!token)throw new Error('桌宠尚未登录');
    beforeSend();
    try{
      const response=await fetch(__CHESTHOST__+'/api/frontend/chests/open',{
        method:'POST',headers:{Authorization:'Bearer '+token,'Content-Type':'application/json',
          'X-BLM-CID':localStorage.getItem('KURO_YCK_REQ_CID')||'','X-BLM-FRID':trace??crypto.randomUUID().replaceAll('-','')},
        body:JSON.stringify(payload),credentials:'omit',cache:'no-store',redirect:'error',signal:AbortSignal.timeout(10000)});
      let envelope;
      try{envelope=await response.json();}catch{throw Object.assign(new Error('领取响应格式异常'),{httpStatus:response.status,retryAfter:response.headers.get('Retry-After')});}
      if(!response.ok){
        // Preserve business rejections and Retry-After on HTTP error replies.
        let error;
        try{m[__DECODE__](envelope);}catch(cause){error=cause;}
        throw Object.assign(error||new Error('领取请求失败'),{httpStatus:response.status,retryAfter:response.headers.get('Retry-After')});
      }
      // Decode through the native contract (including login-expiry handling).
      m[__DECODE__](envelope);return envelope;
    }catch(error){
      if(Number(error?.code)!==70006){try{m[__CHESTTOAST__](error);}catch{}}
      throw error;
    }
  };
  const receiptDeadline=(value,baseline)=>Number.isSafeInteger(value)&&value<=253402300799999&&
    value>Math.max(baseline,Date.now())?value:null;
