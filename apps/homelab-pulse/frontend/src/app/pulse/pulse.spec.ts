import { describe, expect, it } from 'vitest';
import { Overview } from './contracts';
import { historyPath } from './fleet-chart';
import { newerOverview } from './overview-store';
import { bytes, diskUsed, percent, ratio } from './readings';

function overview(revision: number, schemaVersion = 1): Overview {
  return { revision, schemaVersion, generatedAt: '2026-11-24T07:00:00Z', nodes: [] };
}

describe('Overview synchronization', () => {
  it('keeps a live revision when an older REST response arrives afterward', () => {
    const live = overview(13);
    expect(newerOverview(live, overview(12))).toBe(live);
    expect(newerOverview(live, overview(13))).toBe(live);
    expect(newerOverview(live, overview(14))?.revision).toBe(14);
  });

  it('rejects incompatible snapshots and invalid revisions', () => {
    const current = overview(1);
    expect(newerOverview(current, overview(4, 2))).toBe(current);
    expect(newerOverview(current, overview(Number.MAX_SAFE_INTEGER + 1))).toBe(current);
    expect(newerOverview(undefined, overview(0))).toBeUndefined();
  });
});

describe('Published readings', () => {
  it('keeps missing data distinct from zero', () => {
    expect(percent(ratio(null, 100))).toBe('—');
    expect(percent(ratio(0, 100))).toBe('0%');
    expect(diskUsed(0, 100)).toBe('100%');
    expect(bytes(0)).toBe('0 B');
  });

  it('breaks the graph at missing buckets instead of connecting invented points', () => {
    const line = historyPath([40, null, 0]);
    expect(line).toContain('M0,84');
    expect(line).toContain('M800,140');
    expect(line).not.toContain('L');
    expect(historyPath([null, null])).toBe('');
  });
});
