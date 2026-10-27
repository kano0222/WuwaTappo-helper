const m = await import(__COMMON__);
  if (m[__ENVIRONMENT__] !== 'cn-cn-prod' || window.__TAURI_INTERNALS__?.metadata?.currentWindow?.label !== 'main') throw new Error('应用环境不匹配');
  const s = m[__STORE__];
  if (!s || typeof m[__LOCKED__] !== 'function' || typeof m[__USERINFO__] !== 'function' || typeof m[__DECODE__] !== 'function') throw new Error('桌宠版本接口不匹配');
  const locked = () => m[__LOCKED__]();
  const readServer = async () => m[__DECODE__](await m[__USERINFO__]());
  const snapshot = () => ({ time: Date.now(), loggedIn: s.isLoggedIn, locked: locked(),
    available: s.totalClickCount, cumulative: s.cumulativeClickCount, local: s.localClickCount,
    consumed: s.serverClickConsume, serverTotal: s.serverClickTotal,
    left: Number(s.userInfo?.left_treasure_chest), right: Number(s.userInfo?.right_treasure_chest),
    claimBusy: !!document.querySelector('.reward-btn:disabled, .reward-btn--press, .reward-btn--open'),
    buttons: Object.fromEntries(['left','right'].map(side => { const b = document.querySelector('.reward-anchor--'+side+' .reward-btn'); return [side, !!b && !b.disabled]; })) });