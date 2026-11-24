import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it } from 'vitest';
import { Metrics, NodeOverview } from './contracts';
import { MachineRows } from './machine-rows';

const noMetrics: Metrics = {
  cpuUsagePercent: null,
  memoryUsedBytes: null,
  memoryTotalBytes: null,
  swapUsedBytes: null,
  swapTotalBytes: null,
  diskAvailableBytes: null,
  diskTotalBytes: null,
  networkRxBytesPerSecond: null,
  networkTxBytesPerSecond: null,
  temperatureCelsius: null,
};

describe('Machine rows', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('labels stale readings and preserves measured zero in the rendered dashboard', () => {
    TestBed.configureTestingModule({ imports: [MachineRows], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(MachineRows);
    const readings: NodeOverview[] = [
      {
        alias: 'demo-edge',
        status: 'available',
        observedAt: '2026-11-24T00:00:00Z',
        metrics: { ...noMetrics, cpuUsagePercent: 0 },
        signals: [],
      },
      {
        alias: 'quiet-node',
        status: 'stale',
        observedAt: '2026-11-23T23:00:00Z',
        metrics: noMetrics,
        signals: [],
      },
    ];
    fixture.componentRef.setInput('nodes', readings);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const row = root.querySelector('[href="/machines/demo-edge"]');
    expect(row?.textContent).toContain('0%');
    const stale = root.querySelector('[href="/machines/quiet-node"]');
    expect(stale?.textContent).toContain('Ghosted us');
    expect(stale?.textContent).not.toContain('0%');
  });
});
