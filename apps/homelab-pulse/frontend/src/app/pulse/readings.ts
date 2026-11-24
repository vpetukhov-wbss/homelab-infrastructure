import { httpResource } from '@angular/common/http';
import { DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { HealthSignal, ServiceStatus, ServiceKind, Services, serviceKinds } from './contracts';

export const repository = 'https://github.com/vpetukhov-wbss/homelab-infrastructure';

export function pulseResource<T>(url: () => string | undefined) {
  const ref = httpResource<T>(() => {
    const address = url();
    return address ? { url: address, cache: 'no-store' } : undefined;
  });
  interval(15000)
    .pipe(takeUntilDestroyed(inject(DestroyRef)))
    .subscribe(() => ref.reload());
  return ref;
}

export const labels: Record<ServiceKind, string> = {
  postgresql: 'PostgreSQL',
  redis: 'Redis',
  rabbitmq: 'RabbitMQ',
  sqlite: 'SQLite',
  gitea: 'Gitea',
  giteaActions: 'Gitea Actions',
  nginx: 'NGINX',
  nginxAccess: 'NGINX access',
  tls: 'TLS',
  dockerWorkloads: 'Docker workloads',
  dockerVolumes: 'Docker volumes',
};

export const serviceIcons: Record<ServiceKind, string> = {
  postgresql: '4b0a2',
  redis: '34134',
  rabbitmq: 'fb655',
  sqlite: '4b0a2',
  gitea: '193af',
  giteaActions: 'd19c4',
  nginx: '9dc52',
  nginxAccess: '9dc52',
  tls: 'cee2e',
  dockerWorkloads: 'fb655',
  dockerVolumes: '38b39',
};

export function serviceRows(snapshot: Services | undefined) {
  return (
    snapshot?.nodes.flatMap((node) =>
      serviceKinds.flatMap((kind) =>
        node[kind].map((reading) => ({
          ...reading,
          node: node.alias,
          kind,
          label: labels[kind],
          icon: serviceIcons[kind],
        })),
      ),
    ) ?? []
  );
}

export function ratio(
  used: number | null | undefined,
  total: number | null | undefined,
): number | null {
  return used == null || total == null || total <= 0 ? null : (100 * used) / total;
}

export function percent(value: number | null | undefined): string {
  return value == null ? '—' : `${Math.round(value)}%`;
}

export function temperature(value: number | null | undefined): string {
  return value == null ? '—' : `${Math.round(value)} °C`;
}

export function bytes(value: number | null | undefined): string {
  if (value == null) return '—';
  const precision = value >= 1024 ** 4 ? 2 : 1;
  const unit = ['B', 'KiB', 'MiB', 'GiB', 'TiB'][
    Math.min(4, Math.max(0, Math.floor(Math.log(value || 1) / Math.log(1024))))
  ];
  const power = ['B', 'KiB', 'MiB', 'GiB', 'TiB'].indexOf(unit);
  return `${Number((value / 1024 ** power).toFixed(precision))} ${unit}`;
}

export function readingStatus(status: ServiceStatus): string {
  return {
    available: 'Still ticking',
    stale: 'Ghosted us',
    unknown: 'Waiting on a reading',
    unavailable: 'Unavailable',
  }[status];
}

const messages: Record<string, string> = {
  'node.ghosted_us': 'This node ghosted us.',
  'service.ghosted_us': 'This service ghosted us.',
  'cpu.doing_overtime': 'CPU’s doing overtime.',
  'temperature.getting_hot': 'It’s getting hot in here.',
  'memory.getting_crowded': 'RAM’s getting crowded.',
  'disk.getting_crowded': 'Dude, the disk’s getting crowded.',
  'postgres.traffic_jam': 'Postgres has a traffic jam.',
  'redis.kicking_keys_out': 'Redis is kicking keys out.',
  'rabbit.queue_growing': 'The queue is growing.',
  'nginx.proxy_busy': 'The proxy’s getting busy.',
  'gitea.check_failed': 'The Git service check failed.',
  'gitea.feeling_slow': 'Git’s taking its sweet time.',
  'ci.queue_growing': 'The queue’s growing a beard.',
  'docker.workload_unhealthy': 'One container woke up grumpy.',
  'tls.expiring_soon': 'The TLS certificate is expiring soon.',
  'service.check_failed': 'The service check failed.',
};

export function signalTitle(signal: HealthSignal): string {
  return messages[signal.messageKey] ?? 'Health signal';
}

export function signalReading(signal: HealthSignal): string {
  const unit =
    signal.unit === 'celsius'
      ? ' °C'
      : signal.unit === '%'
        ? '%'
        : signal.unit
          ? ` ${signal.unit}`
          : '';
  const value = signal.value == null ? '' : `${signal.value}${unit}`;
  const threshold = signal.threshold == null ? '' : `Alert at ${signal.threshold}${unit}`;
  return [value, threshold].filter(Boolean).join(' · ');
}

export function diskUsed(available: number | null, total: number | null): string {
  return percent(available == null || total == null ? null : ratio(total - available, total));
}
