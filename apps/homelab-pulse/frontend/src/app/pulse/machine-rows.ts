import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NodeOverview } from './contracts';
import { diskUsed, percent, ratio, readingStatus, temperature } from './readings';

@Component({
  selector: 'app-machine-rows',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel machine-table">
      <div class="panel-heading">
        <h2>{{ heading() }}</h2>
        @if (showRoute()) {
          <a class="button compact" routerLink="/machines"
            >All machines <img src="assets/f8701.svg" alt=""
          /></a>
        }
      </div>
      @if (nodes().length) {
        <div class="machine-labels" aria-hidden="true">
          <span>Machine</span><span>CPU</span><span>RAM</span><span>Disk</span><span>CPU temp</span
          ><span>Status</span><span></span>
        </div>
        @for (node of nodes(); track node.alias) {
          <a
            class="machine-row"
            [class.warning-row]="node.status === 'available' && node.signals.length"
            [routerLink]="['/machines', node.alias]"
          >
            <strong>{{ node.alias }}</strong>
            <span [attr.aria-label]="'CPU ' + percent(node.metrics.cpuUsagePercent)">{{
              percent(node.metrics.cpuUsagePercent)
            }}</span>
            <span
              [attr.aria-label]="
                'RAM ' + percent(ratio(node.metrics.memoryUsedBytes, node.metrics.memoryTotalBytes))
              "
              >{{
                percent(ratio(node.metrics.memoryUsedBytes, node.metrics.memoryTotalBytes))
              }}</span
            >
            <span
              [attr.aria-label]="
                'Disk ' + diskUsed(node.metrics.diskAvailableBytes, node.metrics.diskTotalBytes)
              "
              >{{ diskUsed(node.metrics.diskAvailableBytes, node.metrics.diskTotalBytes) }}</span
            >
            <span>{{ temperature(node.metrics.temperatureCelsius) }}</span>
            <span
              class="chip"
              [class.good]="node.status === 'available' && !node.signals.length"
              [class.warn]="node.status === 'available' && node.signals.length"
            >
              <img
                [src]="
                  node.status !== 'available'
                    ? 'assets/1a074.svg'
                    : node.signals.length
                      ? 'assets/52d6c.svg'
                      : 'assets/5a575.svg'
                "
                alt=""
              />
              {{
                node.status === 'available' && node.signals.length
                  ? 'A little spicy'
                  : status(node.status)
              }}
            </span>
            <img class="row-arrow" src="assets/29b5f.svg" alt="" />
          </a>
        }
      } @else {
        <p class="empty">No machines have been approved for publication.</p>
      }
    </section>
  `,
})
export class MachineRows {
  readonly nodes = input.required<readonly NodeOverview[]>();
  readonly heading = input('Machines');
  readonly showRoute = input(false);
  protected readonly percent = percent;
  protected readonly ratio = ratio;
  protected readonly temperature = temperature;
  protected readonly diskUsed = diskUsed;
  protected readonly status = readingStatus;
}
