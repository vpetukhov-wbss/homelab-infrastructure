import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { OverviewStore } from './pulse/overview-store';
import { repository } from './pulse/readings';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly overview = inject(OverviewStore);
  protected readonly repository = repository;
  protected readonly navigation = [
    { href: '/', label: 'Overview', desktop: '7196a', mobile: '02e23' },
    { href: '/machines', label: 'Machines', desktop: '1e930', mobile: '0fa55' },
    { href: '/services', label: 'Services', desktop: '47465', mobile: 'e92d4' },
    { href: '/engineering', label: 'Engineering', desktop: '07ea0', mobile: 'b4e37' },
  ];
}
