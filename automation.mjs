import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { connect, chooseAction, confirmClaim, confirmRejectedClaim, sleep } from './lib/tappo.mjs';
import { replaceSnapshot } from './lib/storage.mjs';
import { loadSettings } from './lib/settings.mjs';
import { modes } from './lib/modes.mjs';

const root = path.dirname(fileURLToPath(import.meta.url));
const settings = loadSettings(root);
const runtime = path.join(root, 'runtime');
fs.mkdirSync(runtime, { recursive: true });
const file = name => path.join(runtime, name);
function write(name, data) {
  const temporary = file(name + '.' + process.pid + '.tmp');
  fs.writeFileSync(temporary, JSON.stringify(data, null, 2) + '\n');
  replaceSnapshot(temporary, file(name));
}
function read(name) { return JSON.parse(fs.readFileSync(file(name), 'utf8').replace(/^\uFEFF/, '')); }
function log(event, data = {}) {
  fs.appendFileSync(file('events.jsonl'), JSON.stringify({ at: new Date().toISOString(), event, ...data }) + '\n');
}
const verify = process.argv.includes('--verify');
if (!verify && !process.argv.includes('--run')) throw new Error('Use --verify or --run');
// An existing action journal is deliberately retained across crashes.
try { fs.writeFileSync(file('lock.json'), JSON.stringify({ pid: process.pid }), { flag: 'wx' }); }
catch (e) {
  if (e.code !== 'EEXIST') throw e;
  const previous = read('lock.json');
  let alive = true;
  try { process.kill(previous.pid, 0); } catch (error) { if (error.code === 'ESRCH') alive = false; }
  if (alive) throw new Error('自动化已经运行；请先使用状态或停止操作');
  fs.unlinkSync(file('lock.json'));
  fs.writeFileSync(file('lock.json'), JSON.stringify({ pid: process.pid }), { flag: 'wx' });
}
let client, stop = false, restoreRequested = false, paused = false, lastCommand, fault, claims = 0;
let pending = fs.existsSync(file('pending.json')) ? read('pending.json') : null;
let current, mode = 'brute', nextBoostAt = 0;
process.on('SIGINT', () => { stop = true; });
process.on('SIGTERM', () => { stop = true; });
const status = extra => write('status.json', { at: new Date().toISOString(), pid: process.pid, running: true, paused: paused || !!fault || !!pending, fault, mode, claimsThisRun: claims, state: current, ...extra });
function clearPending() { fs.unlinkSync(file('pending.json')); pending = null; }
function begin(action, before, server) {
  pending = { at: new Date().toISOString(), action, before, server };
  write('pending.json', pending);
}
async function restoreNormal() {
  if (!client) throw new Error('没有可用连接；未尝试强制结束桌宠，请手动退出后普通启动');
  try { await client.quit(); } catch { /* The connection may close during normal quit. */ }
  client.close(); client = null;
  for (let i = 0; i < 30; i++) {
    let portOpen = false;
    try { await fetch('http://127.0.0.1:19222/json/version', { signal: AbortSignal.timeout(500) }); portOpen = true; } catch {}
    if (!portOpen) {
      await sleep(1000);
      const env = { ...process.env }; delete env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS;
      const child = spawn(settings.appPath, [], { cwd: path.win32.dirname(settings.appPath), env, detached: true, windowsHide: true, stdio: 'ignore' });
      await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
      child.unref(); return true;
    }
    await sleep(400);
  }
  throw new Error('桌宠未完成正常退出；没有强制结束，请手动退出以关闭调试端口');
}

try {
  client = await connect();
  log('started', { mode: verify ? 'verify-one-claim' : 'background' });
  if (pending) fault = '存在结果未确认的历史操作；不会自动重复，请查看 runtime/pending.json';
  status({ message: '已连接' });
  do {
    let loopDelay = 1000;
    if (fs.existsSync(file('control.json'))) {
      const command = read('control.json');
      if (command.id !== lastCommand) {
        lastCommand = command.id;
        if (command.action === 'stop' || command.action === 'disconnect') {
          stop = true; restoreRequested = command.action === 'stop';
        }
        else if (command.action === 'pause') paused = true;
        else if (command.action === 'resume' && !pending) { paused = false; fault = null; }
      }
    }
    if (stop) break;
    try {
      if (fs.existsSync(file('preferences.json'))) {
        const preference = read('preferences.json');
        if (!modes.includes(preference.mode)) throw new Error('增加模式配置无效，请在面板重新选择');
        if (mode !== preference.mode) { mode = preference.mode; nextBoostAt = 0; log('mode-changed', { mode }); }
      }
      current = await client.snapshot();
      if (!paused && !fault && !pending) {
        const action = chooseAction(current, { mode, boostDue: performance.now() >= nextBoostAt });
        if (action.kind === 'wait') {
          if (action.delayMs) nextBoostAt = performance.now() + action.delayMs;
          status({ message: action.reason });
        }
        else {
          const server = await client.server();
          if (![server.total, server.consumed, server.left, server.right].every(Number.isSafeInteger)) throw new Error('服务端状态格式异常');
          if (server.consumed !== current.consumed) { status({ message: '等待桌宠同步服务器余额' }); }
          else if (action.kind === 'claim' && (server[action.side] > Date.now() || server[action.side] !== current[action.side])) {
            status({ message: '等待服务器确认槽位到期' });
          } else {
            if (action.kind === 'boost') nextBoostAt = performance.now() + action.delayMs;
            begin(action, current, server);
            const before = current;
            const after = action.kind === 'boost' ? await client.boost(action.delta, before) : await client.claim(action.side, before);
            if (after.skipped) { log('skipped', { reason: after.reason }); clearPending(); }
            else if (after.rejected && action.kind === 'claim') {
              const serverAfter = await client.server();
              const reason = confirmRejectedClaim(after.code, before, after, server, serverAfter);
              log('claim-rejected', { side: action.side, code: after.code, before, after, serverBefore: server, serverAfter });
              clearPending(); current = after;
              throw new Error(reason);
            }
            else if (action.kind === 'boost') {
              if (after.cumulative < before.cumulative + action.delta || after.consumed !== before.consumed) throw new Error('补充次数的结果未确认，不重发');
              log('boost-confirmed', { mode, delta: action.delta, before, after });
              clearPending(); current = after;
            } else {
              const serverAfter = await client.server();
              confirmClaim(action.side, before, after, server, serverAfter);
              log('claim-confirmed', { side: action.side, before, after, serverBefore: server, serverAfter });
              claims++; clearPending(); current = after;
            }
            status({ message: action.kind === 'claim' ? '领取已确认' : '次数补充已确认' });
            if (action.kind === 'boost') loopDelay = Math.max(100, Math.min(1000, nextBoostAt - performance.now()));
          }
        }
      } else status({ message: fault || '已暂停' });
    } catch (error) {
      if (fault !== error.message) log('paused-on-error', { message: error.message });
      fault = error.message; status({ message: fault });
      if (verify) throw error;
    }
    if (verify && claims >= 1) break;
    if (verify && (paused || pending || fault)) throw new Error('验证已暂停');
    await sleep(loopDelay);
  } while (true);
  if (verify) {
    write('verification.json', { completedAt: new Date().toISOString(), claims, state: current });
    console.log(JSON.stringify({ claims, state: current }));
  }
} catch (error) {
  fault = error.message; process.exitCode = 1; log('stopped-on-error', { message: fault });
  console.error(fault);
} finally {
  let normalStartupRestored = false;
  if (restoreRequested) {
    try { normalStartupRestored = await restoreNormal(); }
    catch (error) { fault = error.message; log('cleanup-needs-attention', { message: fault }); process.exitCode = 1; }
  }
  client?.close();
  status({ running: false, message: fault || (verify ? '单箱验证完成' : '已停止'), normalStartupRestored, automationDisconnected: true });
  fs.unlinkSync(file('lock.json'));
}
