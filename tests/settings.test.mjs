import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { loadSettings } from '../lib/settings.mjs';

function fixture(run) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tappo-settings-'));
  const config = path.join(root, 'config.local.json');
  try { run(root, config); }
  finally { if (fs.existsSync(config)) fs.unlinkSync(config); fs.rmdirSync(root); }
}
test('干净检出缺少本机配置时给出配置提示', () => fixture(root => {
  assert.throws(() => loadSettings(root), /控制面板.*config\.local\.json/);
}));
test('保留含空格的安装路径，并接受 UTF-8 BOM', () => fixture((root, config) => {
  const appPath = 'C:\\Games Library\\WuwaTappo\\WuwaTappo.exe';
  fs.writeFileSync(config, '\uFEFF' + JSON.stringify({ appPath }));
  assert.equal(loadSettings(root).appPath, appPath);
}));
test('不接受相对路径、其他程序或空配置', () => fixture((root, config) => {
  for (const value of [null, {}, { appPath: './WuwaTappo.exe' }, { appPath: '\\WuwaTappo.exe' }, { appPath: 'C:\\Other.exe' }]) {
    fs.writeFileSync(config, JSON.stringify(value));
    assert.throws(() => loadSettings(root), /appPath/);
  }
}));
