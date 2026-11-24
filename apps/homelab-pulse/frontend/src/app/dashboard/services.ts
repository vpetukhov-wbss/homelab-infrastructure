import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ServiceKind, Services, ServiceValue } from '../pulse/contracts';
import {
  bytes,
  pulseResource,
  readingStatus,
  serviceRows,
  signalReading,
  signalTitle,
} from '../pulse/readings';

const sectionGroups: readonly { id: string; title: string; kinds: readonly ServiceKind[] }[] = [
  {
    id: 'databases',
    title: 'Databases and queues',
    kinds: ['postgresql', 'redis', 'rabbitmq', 'sqlite'],
  },
  { id: 'ci', title: 'Git and CI/CD', kinds: ['gitea', 'giteaActions'] },
  { id: 'proxy', title: 'NGINX and TLS', kinds: ['nginx', 'nginxAccess', 'tls'] },
  { id: 'docker', title: 'Docker', kinds: ['dockerWorkloads', 'dockerVolumes'] },
];

@Component({
  selector: 'app-services',
  imports: [DatePipe, RouterLink],
  templateUrl: './services.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ServicesPage {
  protected readonly request = pulseResource<Services>(() => '/api/v1/services.json');
  protected readonly groups = computed(() => {
    const rows = serviceRows(this.request.hasValue() ? this.request.value() : undefined);
    return sectionGroups.map((group) => ({
      ...group,
      rows: rows.filter((row) => group.kinds.includes(row.kind)),
    }));
  });
  protected readonly status = readingStatus;
  protected readonly signalTitle = signalTitle;
  protected readonly signalReading = signalReading;

  protected label(field: string): string {
    return field
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/^./, (initial) => initial.toUpperCase());
  }

  protected values(metrics: Record<string, ServiceValue | unknown[]> | null) {
    return Object.entries(metrics ?? {}).filter(
      (item): item is [string, ServiceValue] => !Array.isArray(item[1]),
    );
  }

  protected format(field: string, value: ServiceValue): string {
    if (field === 'inRecovery' && typeof value === 'boolean') return value ? 'Replica' : 'Primary';
    if (field.endsWith('Bytes') && typeof value === 'number') return bytes(value);
    if (value == null) return '—';
    if (typeof value === 'number')
      return new Intl.NumberFormat('en', { maximumFractionDigits: 2 }).format(value);
    if (typeof value === 'boolean') return value ? 'Yes' : 'No';
    return value;
  }
}
