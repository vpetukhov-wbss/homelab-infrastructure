import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FleetHistory } from '../pulse/contracts';
import { MachineRows } from '../pulse/machine-rows';
import { OverviewStore } from '../pulse/overview-store';
import { RackPulse } from '../pulse/rack-pulse';
import { pulseResource, signalReading, signalTitle } from '../pulse/readings';

@Component({
  selector: 'app-machines',
  imports: [DatePipe, RouterLink, MachineRows, RackPulse],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="section-head">
      <div>
        <h1>Machines</h1>
        <p>Compare utilization and temperatures across the lab.</p>
      </div>
      @if (overview.snapshot(); as snapshot) {
        <span>Updated {{ snapshot.generatedAt | date: 'shortTime' }}</span>
      }
    </header>
    @if (alerts().length) {
      <section class="alert-grid page-section" aria-label="Machine alerts">
        @for (item of alerts(); track item.alias + item.signal.messageKey) {
          <a class="alert" [routerLink]="['/machines', item.alias]">
            <img
              [src]="
                item.signal.messageKey.startsWith('disk.') ? 'assets/f8904.svg' : 'assets/f8341.svg'
              "
              alt=""
            />
            <span
              ><strong>{{ title(item.signal) }}</strong
              ><small>{{ item.alias }} {{ reading(item.signal) }}</small></span
            >
            <img class="alert-arrow" src="assets/4075b.svg" alt="" />
          </a>
        }
      </section>
    }
    <div class="content-grid inventory">
      <app-machine-rows [nodes]="nodes()" heading="Fleet inventory" />
      <app-rack-pulse [nodes]="nodes()" [points]="points()" />
    </div>
  `,
})
export class MachinesPage {
  protected readonly overview = inject(OverviewStore);
  private readonly history = pulseResource<FleetHistory>(() => '/api/v1/history/fleet/1h.json');
  protected readonly nodes = computed(() => this.overview.snapshot()?.nodes ?? []);
  protected readonly points = computed(() =>
    this.history.hasValue() ? this.history.value().history.points : [],
  );
  protected readonly alerts = computed(() =>
    this.nodes()
      .flatMap((node) =>
        node.signals
          .filter((signal) => signal.severity !== 'info')
          .map((signal) => ({ alias: node.alias, signal })),
      )
      .slice(0, 2),
  );
  protected readonly title = signalTitle;
  protected readonly reading = signalReading;
}
