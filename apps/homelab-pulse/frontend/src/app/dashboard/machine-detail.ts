import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { Machine } from '../pulse/contracts';
import { FleetChart } from '../pulse/fleet-chart';
import {
  bytes,
  diskUsed,
  percent,
  pulseResource,
  ratio,
  readingStatus,
  signalReading,
  signalTitle,
  temperature,
} from '../pulse/readings';

@Component({
  selector: 'app-machine-detail',
  imports: [DatePipe, RouterLink, FleetChart],
  templateUrl: './machine-detail.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MachineDetailPage {
  private readonly params = toSignal(inject(ActivatedRoute).paramMap, { requireSync: true });
  protected readonly alias = computed(() => this.params().get('alias') ?? '');
  protected readonly request = pulseResource<Machine>(() =>
    this.alias() ? `/api/v1/machines/${encodeURIComponent(this.alias())}.json` : undefined,
  );
  protected readonly missing = computed(() => {
    const error = this.request.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
  protected readonly cards = computed(() => {
    if (!this.request.hasValue()) return [];
    const machine = this.request.value().machine;
    const metrics = machine.host.metrics;
    const diskTemps = machine.disks
      .filter((d) => d.smart.status === 'available')
      .map((d) => d.smart.temperatureCelsius)
      .filter((t) => t != null);
    return [
      { label: 'CPU usage', value: percent(metrics.cpuUsagePercent) },
      {
        label: 'RAM usage',
        value: percent(ratio(metrics.memoryUsedBytes, metrics.memoryTotalBytes)),
      },
      { label: 'CPU temperature', value: temperature(metrics.temperatureCelsius) },
      {
        label: 'Warmest disk',
        value: temperature(diskTemps.length ? Math.max(...diskTemps) : null),
      },
    ];
  });

  protected readonly bytes = bytes;
  protected readonly diskUsed = diskUsed;
  protected readonly temperature = temperature;
  protected readonly status = readingStatus;
  protected readonly signalTitle = signalTitle;
  protected readonly signalReading = signalReading;
  protected megabits(value: number | null): string {
    return value == null ? '—' : `${((value * 8) / 1000000).toFixed(1)} Mb/s`;
  }
}
