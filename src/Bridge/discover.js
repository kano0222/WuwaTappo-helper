(async()=>{// These checks describe capabilities, not release versions or generated aliases.
function inspectModule(m) {
  const unique = (predicate, label) => {
    const keys = Object.keys(m).filter(k => predicate(m[k]));
    if (keys.length !== 1) throw new Error('兼容性检查失败：' + label + '缺失或不唯一');
    return keys[0];
  };
  const fn = (name, pattern) => v => typeof v === 'function' && v.name === name && pattern.test(Function.prototype.toString.call(v));
  const exports = {
    environment: unique(v => v === 'cn-cn-prod', '应用环境'),
    store: unique(v => v && typeof v === 'object' && typeof v.isLoggedIn === 'boolean' &&
      ['totalClickCount','cumulativeClickCount','localClickCount','serverClickConsume','serverClickTotal'].every(k => Number.isSafeInteger(v[k]) && v[k] >= 0) &&
      typeof v.addLocalClickCount === 'function' && 'userInfo' in v, '计数状态'),
    locked: unique(fn('isForceUpdateLocked', /^function isForceUpdateLocked\(\)\{return \w+\.value\}$/), '更新锁'),
    userInfo: unique(fn('userInfo', /^function userInfo\(\)\{return \w+\.get\("api\/frontend\/user\/me",\{\},\{\}\)\}$/), '只读用户接口'),
    decode: unique(fn('unwrapApiData', /Number\(\w+\?\.code\).*Number\.isFinite.*isApiSuccess.*\.data\?\?\w+\.result/s), '响应解码')
  };
  // Probe only local pure functions; never call userInfo or a mutation during discovery.
  if (typeof m[exports.locked]() !== 'boolean') throw new Error('兼容性检查失败：更新锁返回格式');
  const sample = { probe: true };
  if (m[exports.decode]({ code: 0, data: sample }) !== sample) throw new Error('兼容性检查失败：响应解码格式');
  let rejected = false;
  try { m[exports.decode]({}) } catch { rejected = true; }
  if (!rejected) throw new Error('兼容性检查失败：无效响应未拒绝');
  return exports;
}

function inspectSources(sources) {
  const mains = sources.filter(([, s]) => s.includes('case"boost-local-clicks":'));
  if (mains.length !== 1) throw new Error('兼容性检查失败：补数处理器缺失或不唯一');
  const source = mains[0][1];
  // Require delta normalization and the existing store increment, plus original reward UI.
  if (!/case"boost-local-clicks":\{const (\w+)=Math\.max\(0,Math\.floor\(Number\(\w+\.delta\)\|\|0\)\);\1>0&&\(\w+\.addLocalClickCount\(\1\)/.test(source) ||
      !['reward-anchor--left','reward-anchor--right','reward-btn','[chest] openChest failed'].every(t => source.includes(t)) ||
      !sources.some(([, s]) => s.includes('"cat-panel-action"'))) {
    throw new Error('兼容性检查失败：补数或宝箱入口契约已变化');
  }
  const quits = sources.filter(([p, s]) => /\/quitApp-[\w-]+\.js$/.test(p) &&
    s.includes('"exit_app"') && /export\{[^}]*\bas quitApp\b/.test(s));
  if (quits.length !== 1) throw new Error('兼容性检查失败：正常退出能力缺失或不唯一');
  return quits[0][0];
}

async function discoverProfile() {
  if (location.hostname !== 'tauri.localhost' || window.__TAURI_INTERNALS__?.metadata?.currentWindow?.label !== 'main' ||
      typeof window.__TAURI_INTERNALS__?.invoke !== 'function') throw new Error('兼容性检查失败：非桌宠主窗口');
  const paths = [...new Set([...document.scripts].map(s => s.src).concat(performance.getEntriesByType('resource').map(e => e.name)))]
    .map(p => new URL(p, location.href)).filter(p => p.origin === location.origin && /^\/assets\/[\w-]+\.js$/.test(p.pathname)).map(p => p.pathname);
  if (!paths.length || paths.length > 80) throw new Error('兼容性检查失败：应用资源列表异常');
  const sources = await Promise.all(paths.map(async p => {
    const r = await fetch(p, { signal: AbortSignal.timeout(5000) });
    if (!r.ok) throw new Error('兼容性检查失败：无法读取应用资源');
    const s = await r.text();
    if (s.length > 4000000) throw new Error('兼容性检查失败：应用资源过大');
    return [p, s];
  }));
  const quitPath = inspectSources(sources);
  const candidates = sources.filter(([, s]) => s.includes('function userInfo()') && s.includes('function isForceUpdateLocked()'));
  if (candidates.length !== 1) throw new Error('兼容性检查失败：共享状态模块缺失或不唯一');
  const commonPath = candidates[0][0];
  const exports = inspectModule(await import(commonPath));
  const quit = await import(quitPath);
  if (typeof quit.quitApp !== 'function' || !Function.prototype.toString.call(quit.quitApp).includes('"exit_app"')) {
    throw new Error('兼容性检查失败：正常退出接口已变化');
  }
  return { commonPath, quitPath, exports };
}


try{return {profile:await discoverProfile()}}catch(error){return {error:String(error.message).startsWith('兼容性检查失败：')?error.message:'兼容性检查失败：资源或接口无法确认'}}})()