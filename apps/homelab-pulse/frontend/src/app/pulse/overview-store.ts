import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { finalize } from 'rxjs';
import { Overview } from './contracts';

export function newerOverview(current: Overview | undefined, next: Overview): Overview | undefined {
  if (next.schemaVersion !== 1 || !Number.isSafeInteger(next.revision) || next.revision < 1)
    return current;
  return !current || next.revision > current.revision ? next : current;
}

@Injectable({ providedIn: 'root' })
export class OverviewStore {
  private readonly http = inject(HttpClient);
  private readonly lifetime = inject(DestroyRef);
  private readonly current = signal<Overview | undefined>(undefined);
  private readonly failure = signal(false);
  private readonly connected = signal(false);
  private readonly hub = new HubConnectionBuilder()
    .withUrl('/hubs/pulse')
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build();
  private connecting = false;
  private fetching = false;
  private refreshQueued = false;
  private retry: ReturnType<typeof setTimeout> | undefined;

  readonly snapshot = this.current.asReadonly();
  readonly unavailable = this.failure.asReadonly();
  readonly status = computed(() => {
    if (this.failure()) return 'Offline';
    if (this.connected()) return 'Live';
    return this.current() ? 'Polling' : 'Connecting';
  });

  constructor() {
    this.hub.on('TelemetryUpdated', (snapshot: Overview) => this.apply(snapshot));
    this.hub.onreconnecting(() => this.connected.set(false));
    this.hub.onreconnected(() => {
      this.connected.set(true);
      this.refresh();
    });
    this.hub.onclose(() => {
      this.connected.set(false);
      this.scheduleReconnect();
    });
    const polling = setInterval(() => this.refresh(), 15000);
    this.lifetime.onDestroy(() => {
      clearInterval(polling);
      clearTimeout(this.retry);
      void this.hub.stop().catch(() => undefined);
    });
    this.refresh();
    void this.connect();
  }

  refresh(): void {
    if (this.lifetime.destroyed) return;
    if (this.fetching) {
      this.refreshQueued = true;
      return;
    }
    this.fetching = true;
    const startedAtRevision = this.current()?.revision;
    this.http
      .get<Overview>('/api/v1/overview.json', { cache: 'no-store' })
      .pipe(
        takeUntilDestroyed(this.lifetime),
        finalize(() => {
          this.fetching = false;
          if (this.refreshQueued) {
            this.refreshQueued = false;
            this.refresh();
          }
        }),
      )
      .subscribe({
        next: (snapshot) => this.apply(snapshot),
        error: () => {
          if (this.current()?.revision === startedAtRevision) this.failure.set(true);
        },
      });
  }

  private apply(snapshot: Overview): void {
    if (
      snapshot.schemaVersion !== 1 ||
      !Number.isSafeInteger(snapshot.revision) ||
      snapshot.revision < 1
    ) {
      this.failure.set(true);
      return;
    }
    this.current.update((previous) => newerOverview(previous, snapshot));
    this.failure.set(false);
  }

  private async connect(): Promise<void> {
    if (this.connecting || this.lifetime.destroyed) return;
    this.connecting = true;
    try {
      await this.hub.start();
      if (!this.lifetime.destroyed) {
        this.connected.set(true);
        this.refresh();
      }
    } catch {
      this.scheduleReconnect();
    } finally {
      this.connecting = false;
    }
  }

  private scheduleReconnect(): void {
    if (this.lifetime.destroyed) return;
    clearTimeout(this.retry);
    this.retry = setTimeout(() => void this.connect(), 5000);
  }
}
