import { ChangeDetectionStrategy, Component } from '@angular/core';
import { EngineeringCards } from '../pulse/engineering-cards';
import { repository } from '../pulse/readings';

@Component({
  selector: 'app-engineering',
  imports: [EngineeringCards],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="section-head">
      <div>
        <h1>Engineering</h1>
        <p>The architecture and operating practices behind HomeLab Pulse.</p>
      </div>
      <a class="button primary" [href]="repository"
        >View GitHub repository <img src="assets/c6ba4.svg" alt=""
      /></a>
    </header>
    <app-engineering-cards />
    <section class="panel page-section">
      <h2>From sensor to public dashboard</h2>
      <p>Agents send readings privately. The dashboard receives approved updates over SignalR.</p>
      <ol class="pipeline">
        <li><strong>Python agent</strong><span>HTTP POST over WireGuard</span></li>
        <li><strong>.NET + SQLite</strong><span>Validate and store readings</span></li>
        <li><strong>SignalR + JSON</strong><span>Live updates and snapshots</span></li>
        <li><strong>HomeLab Pulse</strong><span>Angular web and mobile</span></li>
      </ol>
    </section>
    <section class="panel page-section">
      <h2>Operational checks</h2>
      <p class="empty">No verified backup or restore results are published.</p>
    </section>
  `,
})
export class EngineeringPage {
  protected readonly repository = repository;
}
