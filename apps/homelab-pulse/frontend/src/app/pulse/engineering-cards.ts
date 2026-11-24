import { ChangeDetectionStrategy, Component } from '@angular/core';
import { repository } from './readings';

@Component({
  selector: 'app-engineering-cards',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="engineering-cards">
      @for (item of cards; track item.title) {
        <article class="panel engineering-card">
          <span class="icon-tile" [class]="item.tone"
            ><img [src]="'assets/' + item.icon + '.svg'" alt=""
          /></span>
          <h3>{{ item.title }}</h3>
          <strong [class]="item.tone">{{ item.stack }}</strong>
          <p>{{ item.description }}</p>
          <a class="button compact" [href]="item.href"
            >{{ item.action }} <img src="assets/19fb4.svg" alt=""
          /></a>
        </article>
      }
    </div>
  `,
})
export class EngineeringCards {
  protected readonly cards = [
    {
      title: 'Repeatable provisioning',
      icon: '89237',
      tone: 'mint',
      stack: 'Ubuntu · Ansible',
      description: 'Host bootstrap and role definitions for a repeatable rebuild.',
      action: 'View Ansible directory',
      href: repository + '/tree/dev/ansible',
    },
    {
      title: 'Private network and edge',
      icon: 'cee2e',
      tone: 'blue',
      stack: 'WireGuard · Caddy',
      description:
        'The network design keeps service traffic on WireGuard and gives public routes a dedicated edge gateway.',
      action: 'Read the architecture',
      href: repository + '/blob/dev/docs/architecture/overview.md',
    },
    {
      title: 'Operations and recovery',
      icon: '38b39',
      tone: 'amber',
      stack: 'SQLite · SignalR',
      description: 'The roadmap tracks alerts, backups, and recovery drills.',
      action: 'Read the roadmap',
      href: repository + '/blob/dev/docs/roadmap.md',
    },
  ];
}
