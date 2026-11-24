import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, from, interval, mergeMap, of, toArray } from 'rxjs';
import { FleetHistory, Machine, Services, Window, windows } from '../pulse/contracts';
import { OverviewStore } from '../pulse/overview-store';
import { FleetChart } from '../pulse/fleet-chart';
import { RackPulse } from '../pulse/rack-pulse';
import { MachineRows } from '../pulse/machine-rows';
import { EngineeringCards } from '../pulse/engineering-cards';
import {
  bytes,
  percent,
  pulseResource,
  ratio,
  readingStatus,
  repository,
  serviceRows,
  signalReading,
  signalTitle,
  temperature,
} from '../pulse/readings';

@Component({
  selector: 'app-overview',
  imports: [DatePipe, RouterLink, FleetChart, RackPulse, MachineRows, EngineeringCards],
  templateUrl: './overview.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OverviewPage {
  protected readonly store = inject(OverviewStore);
  private readonly http = inject(HttpClient);
  protected readonly selected = signal<Window>('1h');
  protected readonly windows = windows;
  protected readonly repository = repository;
  protected readonly nodes = computed(() => this.store.snapshot()?.nodes ?? []);
  private readonly fresh = computed(() =>
    this.store.unavailable() ? [] : this.nodes().filter((n) => n.status === 'available'),
  );
  private readonly oneHour = pulseResource<FleetHistory>(() => '/api/v1/history/fleet/1h.json');
  private readonly extended = pulseResource<FleetHistory>(() =>
    this.selected() === '1h' ? undefined : `/api/v1/history/fleet/${this.selected()}.json`,
  );
  private readonly services = pulseResource<Services>(() => '/api/v1/services.json');
  private readonly approved = computed(() =>
    this.nodes()
      .map((n) => n.alias)
      .join('\n'),
  );
  private readonly machineDetails = rxResource({
    params: () => this.approved() || undefined,
    stream: ({ params }) =>
      from(params.split('\n')).pipe(
        mergeMap(
          (alias) =>
            this.http
              .get<Machine>(`/api/v1/machines/${encodeURIComponent(alias)}.json`, {
                cache: 'no-store',
              })
              .pipe(catchError(() => of(null))),
          4,
        ),
        toArray(),
      ),
  });

  protected readonly rack = computed(() =>
    this.oneHour.hasValue() ? this.oneHour.value().history.points : [],
  );
  protected readonly fleet = computed(() => {
    const history = this.selected() === '1h' ? this.oneHour : this.extended;
    return history.hasValue() ? history.value().history.points : [];
  });
  protected readonly chartState = computed(() => {
    const history = this.selected() === '1h' ? this.oneHour : this.extended;
    return history.isLoading()
      ? 'Loading history…'
      : history.error()
        ? 'History is temporarily unavailable.'
        : 'No readings in this window.';
  });
  protected readonly fleetSnapshot = computed(() => {
    const history = this.selected() === '1h' ? this.oneHour : this.extended;
    return history.hasValue() ? history.value().generatedAt : null;
  });
  private readonly liveTotals = computed(() => {
    const machines = this.machineDetails.hasValue() ? this.machineDetails.value() : [];
    const weights = new Map(
      machines.flatMap((snapshot) =>
        snapshot?.machine.hardware
          ? [[snapshot.machine.alias, snapshot.machine.hardware.cpu.logicalCpuCount] as const]
          : [],
      ),
    );
    const readings = this.fresh();
    const weighted = readings.flatMap((node) => {
      const weight = weights.get(node.alias);
      const cpu = node.metrics.cpuUsagePercent;
      return weight && cpu != null ? [{ weight, cpu }] : [];
    });
    const totalWeight = weighted.reduce((total, reading) => total + reading.weight, 0);
    const memory = readings
      .map((node) => node.metrics)
      .filter((metrics) => metrics.memoryUsedBytes != null && metrics.memoryTotalBytes != null);
    return {
      cpu: totalWeight
        ? weighted.reduce((total, reading) => total + reading.cpu * reading.weight, 0) / totalWeight
        : null,
      memoryUsedBytes: this.sum(memory.map((metrics) => metrics.memoryUsedBytes)),
      memoryTotalBytes: this.sum(memory.map((metrics) => metrics.memoryTotalBytes)),
    };
  });
  protected readonly serviceReadings = computed(() =>
    serviceRows(this.services.hasValue() ? this.services.value() : undefined),
  );
  protected readonly flags = computed(() => [
    ...this.nodes().flatMap((n) =>
      n.signals.map((signal) => ({ label: n.alias, target: ['/machines', n.alias], signal })),
    ),
    ...this.serviceReadings().flatMap((r) =>
      r.signals.map((signal) => ({ label: r.alias, target: ['/services'], signal })),
    ),
  ]);
  protected readonly alerts = computed(() =>
    this.flags()
      .filter((flag) => flag.signal.severity !== 'info')
      .slice(0, 2),
  );
  protected readonly freshCount = computed(() => this.fresh().length);
  protected readonly warmestDisk = computed(() => {
    if (this.store.unavailable() || !this.machineDetails.hasValue()) return undefined;
    const eligible = this.machineDetails.value().flatMap(
      (snapshot) =>
        snapshot?.machine.disks
          .filter(
            (disk) => disk.smart.status === 'available' && disk.smart.temperatureCelsius != null,
          )
          .map((disk) => ({
            node: snapshot.machine.alias,
            celsius: disk.smart.temperatureCelsius!,
          })) ?? [],
    );
    return eligible.reduce<(typeof eligible)[number] | undefined>(
      (max, item) => (!max || item.celsius > max.celsius ? item : max),
      undefined,
    );
  });
  protected readonly cards = computed(() => {
    const last = this.liveTotals();
    const warmest = this.warmestDisk();
    return [
      {
        label: 'Pulse check',
        value:
          this.store.snapshot() && !this.store.unavailable()
            ? `${this.freshCount()} / ${this.nodes().length}`
            : '—',
        detail: this.nodes().length
          ? `${this.nodes().filter((n) => n.status === 'stale').length} nodes ghosted us`
          : 'Waiting for published nodes',
      },
      {
        label: 'Fleet CPU',
        value: percent(last.cpu),
        detail: `${this.freshCount()} nodes still ticking`,
      },
      {
        label: 'Fleet RAM',
        value: percent(ratio(last.memoryUsedBytes, last.memoryTotalBytes)),
        detail:
          last.memoryUsedBytes == null
            ? 'Waiting for a reading'
            : `${bytes(last.memoryUsedBytes)} / ${bytes(last.memoryTotalBytes)} in use`,
      },
      {
        label: 'Warmest disk',
        value: temperature(warmest?.celsius),
        detail: warmest?.node ?? 'Waiting for SMART readings',
      },
    ];
  });
  protected readonly resources = computed(() => {
    const metrics = this.fresh().map((n) => n.metrics);
    const disk = this.sum(metrics.map((m) => m.diskAvailableBytes));
    const rx = this.sum(metrics.map((m) => m.networkRxBytesPerSecond));
    const tx = this.sum(metrics.map((m) => m.networkTxBytesPerSecond));
    const swapUsed = this.sum(metrics.map((m) => m.swapUsedBytes));
    const swapTotal = this.sum(metrics.map((m) => m.swapTotalBytes));
    const hottest = metrics.map((m) => m.temperatureCelsius).filter((v) => v != null);
    const speed = (value: number | null) =>
      value == null ? '—' : ((value * 8) / 1000000).toFixed(1);
    return [
      { label: 'Disk space available', value: bytes(disk) },
      {
        label: 'Network receive / transmit',
        value: rx == null && tx == null ? '—' : `${speed(rx)} / ${speed(tx)} Mb/s`,
      },
      {
        label: 'Swap in use',
        value: swapUsed == null ? '—' : `${bytes(swapUsed)} / ${bytes(swapTotal)}`,
      },
      { label: 'Hottest CPU', value: temperature(hottest.length ? Math.max(...hottest) : null) },
    ];
  });

  protected readonly signalTitle = signalTitle;
  protected readonly signalReading = signalReading;
  protected readonly status = readingStatus;

  constructor() {
    interval(15000)
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => this.machineDetails.reload());
  }

  private sum(values: (number | null)[]): number | null {
    const numbers = values.filter((value) => value != null);
    return numbers.length ? numbers.reduce((total, value) => total + value, 0) : null;
  }
}
