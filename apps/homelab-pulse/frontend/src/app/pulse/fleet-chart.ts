import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HistoryPoint } from './contracts';
import { ratio } from './readings';

export function historyPath(values: readonly (number | null)[]): string {
  const scale = 800 / Math.max(1, values.length - 1);
  let continues = false;
  const segments: string[] = [];
  for (const [index, value] of values.entries()) {
    if (value == null || !Number.isFinite(value)) {
      continues = false;
      continue;
    }
    const x = Math.round(index * scale);
    const y = Math.round(140 - Math.min(100, Math.max(0, value)) * 1.4);
    segments.push(`${continues ? 'L' : 'M'}${x},${y}l0.01,0`);
    continues = true;
  }
  return segments.join(' ');
}

@Component({
  selector: 'app-fleet-chart',
  imports: [DatePipe],
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (hasValues()) {
      <div class="chart" role="img" [attr.aria-label]="label()">
        <span class="chart-scale" aria-hidden="true">100%<br />75%<br />50%<br />25%<br />0%</span>
        <div class="chart-area">
          <svg viewBox="0 0 800 140" preserveAspectRatio="none" aria-hidden="true">
            <path class="grid-line" d="M0 0H800M0 35H800M0 70H800M0 105H800M0 140H800" />
            <path class="cpu-line" [attr.d]="cpu()" />
            <path class="ram-line" [attr.d]="ram()" />
          </svg>
          <div class="chart-axis" aria-hidden="true">
            <span>{{ points()[0]?.startedAt | date: 'shortTime' }}</span>
            <span>{{ points().at(-1)?.endedAt | date: 'shortTime' }}</span>
          </div>
        </div>
      </div>
      <div class="chart-legend">
        <span><img src="assets/44ed7.svg" alt="" />CPU</span>
        <span><img src="assets/5face.svg" alt="" />RAM</span>
      </div>
    } @else {
      <p class="empty-chart">{{ empty() }}</p>
    }
  `,
})
export class FleetChart {
  readonly points = input.required<readonly HistoryPoint[]>();
  readonly empty = input('No readings in this window.');
  readonly label = input('CPU and RAM usage over time');
  readonly cpu = computed(() => historyPath(this.points().map((point) => point.cpuUsagePercent)));
  readonly ram = computed(() =>
    historyPath(this.points().map((point) => ratio(point.memoryUsedBytes, point.memoryTotalBytes))),
  );
  readonly hasValues = computed(() => Boolean(this.cpu() || this.ram()));
}
