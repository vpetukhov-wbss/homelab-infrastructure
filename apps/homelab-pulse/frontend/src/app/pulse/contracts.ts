export type ReadingStatus = 'available' | 'stale' | 'unavailable';
export type ServiceStatus = ReadingStatus | 'unknown';
export type Window = '1h' | '24h' | '7d';
export const windows: readonly Window[] = ['1h', '24h', '7d'];

export interface HealthSignal {
  severity: string;
  messageKey: string;
  value: number | null;
  unit: string | null;
  threshold: number | null;
  window: string | null;
}

export interface Metrics {
  cpuUsagePercent: number | null;
  memoryUsedBytes: number | null;
  memoryTotalBytes: number | null;
  swapUsedBytes: number | null;
  swapTotalBytes: number | null;
  diskAvailableBytes: number | null;
  diskTotalBytes: number | null;
  networkRxBytesPerSecond: number | null;
  networkTxBytesPerSecond: number | null;
  temperatureCelsius: number | null;
}

interface Snapshot {
  schemaVersion: number;
  generatedAt: string;
}

export interface Reading {
  status: ReadingStatus;
  observedAt: string | null;
}

export interface NodeOverview extends Reading {
  alias: string;
  metrics: Metrics;
  signals: HealthSignal[];
}

export interface Overview extends Snapshot {
  revision: number;
  nodes: NodeOverview[];
}

export interface HistoryPoint {
  startedAt: string;
  endedAt: string;
  cpuUsagePercent: number | null;
  memoryUsedBytes: number | null;
  memoryTotalBytes: number | null;
}

export interface FleetPoint extends HistoryPoint {
  nodes: (Omit<Reading, 'status'> & { alias: string; status: 'available' | 'missing' })[];
}

export interface FleetHistory extends Snapshot {
  history: { window: Window; resolutionSeconds: number; points: FleetPoint[] };
}

export const serviceKinds = [
  'postgresql',
  'redis',
  'rabbitmq',
  'sqlite',
  'gitea',
  'giteaActions',
  'nginx',
  'nginxAccess',
  'tls',
  'dockerWorkloads',
  'dockerVolumes',
] as const;
export type ServiceKind = (typeof serviceKinds)[number];
export type ServiceValue = string | number | boolean | null;

export interface ServiceReading extends Omit<Reading, 'status'> {
  status: ServiceStatus;
  alias: string;
  metrics: Record<string, ServiceValue | unknown[]> | null;
  signals: HealthSignal[];
}

export interface Services extends Snapshot {
  nodes: ({ alias: string } & Record<ServiceKind, ServiceReading[]>)[];
}

export interface Machine extends Snapshot {
  machine: {
    alias: string;
    hardware: {
      manufacturer: string | null;
      model: string | null;
      osVisibleMemoryBytes: number;
      cpu: {
        model: string | null;
        architecture: string;
        physicalCoreCount: number | null;
        logicalCpuCount: number;
      };
      memoryModules:
        | {
            capacityBytes: number;
            type: string | null;
            ratedSpeedMtPerSecond: number | null;
            configuredSpeedMtPerSecond: number | null;
          }[]
        | null;
    } | null;
    host: Reading & {
      metrics: Metrics;
      uptimeSeconds: number | null;
      loadAverage: { oneMinute: number; fiveMinutes: number; fifteenMinutes: number } | null;
    };
    filesystems: (Reading & {
      alias: string;
      totalBytes: number | null;
      availableBytes: number | null;
    })[];
    temperatures: (Reading & { alias: string; celsius: number | null })[];
    fans: (Reading & { alias: string; rpm: number | null })[];
    disks: {
      alias: string;
      hardware: { status: string; capacityBytes: number | null; model: string | null } | null;
      smart: Reading & { temperatureCelsius: number | null; healthPassed: boolean | null };
    }[];
    history: { window: Window; points: HistoryPoint[] };
    signals: HealthSignal[];
  };
}
