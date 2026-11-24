import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FleetPoint, NodeOverview } from './contracts';

@Component({
  selector: 'app-rack-pulse',
  standalone: true,
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel rack">
      <div class="panel-heading">
        <div>
          <h2>Rack pulse</h2>
          <p>Machine status over the last hour</p>
        </div>
        <span class="chip good"
          ><img src="assets/5a575.svg" alt="" />{{ active() }} / {{ nodes().length }} ticking</span
        >
      </div>
      @if (rows().length) {
        <div class="rack-rows">
          @for (row of rows(); track row.alias) {
            <div class="rack-row">
              <span>{{ row.alias }}</span>
              <div class="rack-cells" [attr.aria-label]="row.alias + ' status by time'">
                @for (cell of row.cells; track $index) {
                  <span [class]="cell.status" [title]="cell.time + ' · ' + cell.status"></span>
                }
              </div>
            </div>
          }
        </div>
        <div class="rack-times">
          <span>{{ displayed()[0]?.startedAt | date: 'shortTime' }}</span><span>{{
            displayed().at(-1)?.endedAt | date: 'shortTime'
          }}</span>
        </div>
        <div class="rack-legend">
          <span><img src="assets/5a575.svg" alt="" />Still ticking</span>
          <span><img src="assets/1a074.svg" alt="" />No reading</span>
        </div>
      } @else {
        <p class="empty">No machine history published yet.</p>
      }
    </section>
  `,
})
export class RackPulse {
  readonly points = input.required<readonly FleetPoint[]>();
  readonly nodes = input.required<readonly NodeOverview[]>();
  readonly active = computed(
    () => this.nodes().filter((node) => node.status === 'available').length,
  );
  readonly displayed = computed(() => this.points().slice(-32));
  readonly rows = computed(() => {
    const displayed = this.displayed();
    if (!displayed.length) return [];
    return this.nodes().map((node) => ({
      alias: node.alias,
      cells: displayed.map((point) => ({
        status: point.nodes.find((item) => item.alias === node.alias)?.status ?? 'missing',
        time: point.startedAt,
      })),
    }));
  });
}
