import fs from 'node:fs';

const waits = [4, 8, 16, 32, 64, 128];
const sleeper = new Int32Array(new SharedArrayBuffer(4));

// Windows may briefly deny replacing a snapshot while another process reads it.
// Retry only this file operation, never an application event or reward request.
export function replaceSnapshot(source, destination) {
  for (let attempt = 0; ; attempt++) {
    try { fs.renameSync(source, destination); return; }
    catch (error) {
      if (!['EPERM', 'EACCES', 'EBUSY'].includes(error.code) || attempt >= waits.length) throw error;
      Atomics.wait(sleeper, 0, 0, waits[attempt]);
    }
  }
}
