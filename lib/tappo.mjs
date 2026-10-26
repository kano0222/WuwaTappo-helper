import { boostPlan, modes, countTarget } from './modes.mjs';
import fs from 'node:fs';
import { discoverySource } from './compatibility.mjs';
const base = 'http://127.0.0.1:19222';
export const profiles = JSON.parse(fs.readFileSync(new URL('../app-profile.json', import.meta.url), 'utf8')).profiles;
const validatedProfiles = new WeakSet();
export function stateSourceFor(profile) {
  if (!profile || (!profiles.includes(profile) && !validatedProfiles.has(profile))) throw new Error('不支持的桌宠接口');
  const ex = profile.exports;
  return `const m = await import(${JSON.stringify(profile.commonPath)});
  if (m[${JSON.stringify(ex.environment)}] !== 'cn-cn-prod' || window.__TAURI_INTERNALS__?.metadata?.currentWindow?.label !== 'main') throw new Error('应用环境不匹配');
  const s = m[${JSON.stringify(ex.store)}];
  if (!s || typeof m[${JSON.stringify(ex.locked)}] !== 'function' || typeof m[${JSON.stringify(ex.userInfo)}] !== 'function' || typeof m[${JSON.stringify(ex.decode)}] !== 'function') throw new Error('桌宠版本接口不匹配');
  const locked = () => m[${JSON.stringify(ex.locked)}]();
  const readServer = async () => m[${JSON.stringify(ex.decode)}](await m[${JSON.stringify(ex.userInfo)}]());
  const snapshot = () => ({ time: Date.now(), loggedIn: s.isLoggedIn, locked: locked(),
    available: s.totalClickCount, cumulative: s.cumulativeClickCount, local: s.localClickCount,
    consumed: s.serverClickConsume, serverTotal: s.serverClickTotal,
    left: Number(s.userInfo?.left_treasure_chest), right: Number(s.userInfo?.right_treasure_chest),
    claimBusy: !!document.querySelector('.reward-btn:disabled, .reward-btn--press, .reward-btn--open'),
    buttons: Object.fromEntries(['left','right'].map(side => { const b = document.querySelector('.reward-anchor--'+side+' .reward-btn'); return [side, !!b && !b.disabled]; })) });`;
}

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
export function chooseAction(s, { mode = 'brute', boostDue = true, random = Math.random } = {}) {
  if (!modes.includes(mode)) throw new Error('未知增加模式');
  if (!s.loggedIn || s.locked) return { kind: 'wait', reason: '等待登录或更新完成' };
  if (![s.available, s.cumulative, s.consumed].every(Number.isSafeInteger) || s.available < 0) throw new Error('计数格式异常');
  if (![s.left, s.right, s.time].every(Number.isFinite) || s.left < 0 || s.right < 0) throw new Error('宝箱时间尚未就绪');
  if (s.claimBusy) return { kind: 'wait', reason: '等待当前宝箱领取完成' };
  const readySide = ['left', 'right'].find(side => s[side] <= s.time && s.buttons[side]);
  if (readySide && s.available >= 1000) return { kind: 'claim', side: readySide };
  if (s.available < countTarget && (mode !== 'brute' || readySide)) {
    if (!boostDue) return { kind: 'wait', reason: '按所选速率补充次数' };
    const plan = boostPlan(mode, s.available, random);
    if (!plan.delta) return { kind: 'wait', reason: '波动模式短暂间歇', delayMs: plan.delayMs };
    return { kind: 'boost', ...plan };
  }
  return { kind: 'wait', reason: '等待原有活跃计时及宝箱入口' };
}

export function confirmClaim(side, before, after, serverBefore, serverAfter) {
  if (!['left','right'].includes(side)) throw new Error('领取槽位无效');
  const other = side === 'left' ? 'right' : 'left';
  if (after.consumed !== before.consumed + 1000 || serverAfter.consumed !== serverBefore.consumed + 1000 ||
      serverAfter[side] <= serverBefore[side] || after[side] !== serverAfter[side] ||
      after[other] !== before[other] || serverAfter[other] !== serverBefore[other] || after.claimBusy) {
    throw new Error('领取结果未能按槽位确认，已暂停且不会自动重发');
  }
}

export function confirmRejectedClaim(code, before, after, serverBefore, serverAfter) {
  const messages = {70004:'服务器判定暂无可领取宝箱，已暂停',70005:'服务器判定次数不足，已暂停',70006:'服务器仍判定宝箱冷却中，已暂停；稍后可继续'};
  if (!messages[code] || after.consumed !== before.consumed || serverAfter.consumed !== serverBefore.consumed ||
      after.left !== before.left || after.right !== before.right || serverAfter.left !== serverBefore.left || serverAfter.right !== serverBefore.right) {
    throw new Error('服务器拒绝后的状态仍不确定，保留记录且不自动重试');
  }
  return messages[code];
}

class Connection {
  constructor(socket) {
    this.socket = socket; this.id = 0; this.pending = new Map(); this.profile = null;
    socket.addEventListener('message', e => {
      const r = JSON.parse(String(e.data)), p = this.pending.get(r.id);
      if (!p) return;
      this.pending.delete(r.id); clearTimeout(p.timer);
      if (r.error || r.result?.exceptionDetails) {
        const description = String(r.result?.exceptionDetails?.exception?.description || '');
        const known = ['未确认领取结果，不自动重试','领取期间出现其他计数或槽位变化，已暂停且不会自动重发'];
        p.reject(new Error(known.find(message => description.includes(message)) || '应用调用失败，结果可能不确定'));
      }
      else p.resolve(r.result?.result?.value);
    });
    socket.addEventListener('close', () => {
      for (const p of this.pending.values()) { clearTimeout(p.timer); p.reject(new Error('后台连接断开，禁止自动重发操作')); }
      this.pending.clear();
    });
  }
  evaluate(expression, timeout = 8000) {
    return new Promise((resolve, reject) => {
      const id = ++this.id;
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('调用超时，禁止自动重发操作')); }, timeout + 2000);
      this.pending.set(id, { resolve, reject, timer });
      try { this.socket.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true, timeout } })); }
      catch (e) { this.pending.delete(id); clearTimeout(timer); reject(e); }
    });
  }
  get stateSource() { return stateSourceFor(this.profile); }
  snapshot() { return this.evaluate(`(async()=>{ ${this.stateSource} return snapshot(); })()`); }
  server() {
    return this.evaluate(`(async()=>{ ${this.stateSource} const d=await readServer();
      return { total:Number(d.total_click_count), consumed:Number(d.total_click_consume), left:Number(d.left_treasure_chest), right:Number(d.right_treasure_chest) }; })()`, 15000);
  }
  boost(delta, before) {
    if (!Number.isInteger(delta) || delta < 1 || delta > countTarget) throw new Error('补充次数超出范围');
    return this.evaluate(`(async()=>{ ${this.stateSource} const before=snapshot();
      if (!before.loggedIn || before.locked || before.available!==${before.available} || before.cumulative!==${before.cumulative}) return {skipped:true,reason:'计数已变化'};
      await window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-panel-action',payload:{type:'boost-local-clicks',delta:${delta}}});
      await new Promise(r=>setTimeout(r,250)); return snapshot(); })()`);
  }
  claim(side, before) {
    if (!['left', 'right'].includes(side)) throw new Error('槽位不匹配');
    return this.evaluate(`(async()=>{ ${this.stateSource} const before=snapshot(),side=${JSON.stringify(side)};
      if (!before.loggedIn || before.locked || before.claimBusy || before.available<1000 || before.left!==${before.left} || before.right!==${before.right} || before[side]>Date.now() || before.consumed!==${before.consumed}) return {skipped:true,reason:'领取条件已变化'};
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
      })()`, 25000);
  }
  quit() { return this.evaluate(`(async()=>{ await (await import(${JSON.stringify(this.profile.quitPath)})).quitApp(); return true; })()`, 15000); }
  close() { this.socket.close(); }
}

export async function connect() {
  const targets = await fetch(base + '/json/list', { signal: AbortSignal.timeout(5000) }).then(r => { if (!r.ok) throw new Error('调试入口未就绪'); return r.json(); });
  let selected;
  try {
    for (const target of targets.filter(t => t.type === 'page')) {
      const page = new URL(target.url), address = new URL(target.webSocketDebuggerUrl);
      if (page.hostname !== 'tauri.localhost') continue;
      if (!['127.0.0.1', 'localhost', '[::1]'].includes(address.hostname) || address.port !== '19222') throw new Error('调试地址超出本机范围');
      const socket = new WebSocket(address);
      await new Promise((resolve, reject) => {
        const timer = setTimeout(() => { socket.close(); reject(new Error('连接超时')); }, 5000);
        socket.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
        socket.addEventListener('error', () => { clearTimeout(timer); reject(new Error('连接失败')); }, { once: true });
      });
      const c = new Connection(socket);
      let label;
      try { label = await c.evaluate('window.__TAURI_INTERNALS__?.metadata?.currentWindow?.label'); }
      catch (error) { c.close(); throw error; }
      if (label === 'main') { if (selected) { c.close(); throw new Error('主窗口不唯一'); } selected = c; }
      else c.close();
    }
    if (!selected) throw new Error('未找到桌宠主窗口');
    const version = await selected.evaluate("window.__TAURI_INTERNALS__.invoke('plugin:app|version')");
    const result = await selected.evaluate(discoverySource, 15000);
    if (!result?.profile) throw new Error(result?.error || '兼容性检查失败：无法确认应用能力');
    selected.profile = { ...result.profile, version };
    validatedProfiles.add(selected.profile);
    return selected;
  } catch (e) { selected?.close(); throw e; }
}
