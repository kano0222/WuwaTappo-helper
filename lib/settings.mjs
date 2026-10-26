import fs from 'node:fs';
import path from 'node:path';

export function loadSettings(root) {
  const file = path.join(root, 'config.local.json');
  if (!fs.existsSync(file)) throw new Error('请先在控制面板选择桌宠程序，生成 config.local.json。');
  const settings = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  if (!settings || typeof settings.appPath !== 'string' || !path.win32.isAbsolute(settings.appPath) || path.win32.parse(settings.appPath).root.length <= 1 || path.win32.basename(settings.appPath).toLowerCase() !== 'wuwatappo.exe') {
    throw new Error('appPath 必须是 WuwaTappo.exe 的完整 Windows 路径。');
  }
  return settings;
}
