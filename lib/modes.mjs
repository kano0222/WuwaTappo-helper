export const modes = ['brute', 'clicker', 'human'];
export const countTarget = 2000;

export function boostPlan(mode, available, random = Math.random) {
  if (!modes.includes(mode)) throw new Error('未知增加模式');
  const remaining = Math.max(0, countTarget - available);
  if (mode === 'brute') return { delta: remaining, delayMs: 0 };
  if (mode === 'clicker') return { delta: Math.min(100, remaining), delayMs: 1000 };
  // A variable, deliberately bounded rate; this does not establish human input or evade detection.
  if (random() < 0.15) return { delta: 0, delayMs: 2000 + Math.floor(random() * 2000) };
  return { delta: Math.min(remaining, 2 + Math.floor(random() * 6)), delayMs: 1000 + Math.floor(random() * 500) };
}
