import { DatePipe } from '@angular/common';
import { Component, DestroyRef, Input, OnChanges, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { ProjectRetention } from '../../models/pusharoo.models';

export function retentionTimeRemaining(retention: ProjectRetention, now: number): number | null {
  if (!retention.deadlineUtc) return null;
  const duration = Date.parse(retention.deadlineUtc) - Date.parse(retention.serverTimeUtc);
  if (!Number.isFinite(duration)) return null;
  return Math.max(0, duration - Math.max(0, now - (retention.observedAt ?? now)));
}

@Component({
  selector: 'app-project-retention-notice',
  imports: [DatePipe],
  templateUrl: './project-retention-notice.component.html',
  styleUrl: './project-retention-notice.component.scss'
})
export class ProjectRetentionNoticeComponent implements OnChanges {
  @Input() retention: ProjectRetention | null | undefined;
  private readonly now = signal(performance.now());

  constructor() {
    interval(60_000)
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => this.now.set(performance.now()));
  }

  ngOnChanges(): void {
    if (this.retention && this.retention.observedAt === undefined)
      this.retention.observedAt = performance.now();
    this.now.set(performance.now());
  }

  get remaining(): number | null {
    return this.retention ? retentionTimeRemaining(this.retention, this.now()) : null;
  }

  get due(): boolean {
    return this.retention?.status === 'awaiting_cleanup' || this.remaining === 0;
  }

  get heading(): string {
    switch (this.retention?.status) {
      case 'unavailable':
        return 'This project is no longer available.';
      case 'deleting':
        return 'This project is being deleted.';
      case 'on_hold':
        return 'Automatic cleanup is on hold for this project.';
      case 'protected':
        return '';
      default:
        return this.due
          ? this.retention?.mode === 'enabled'
            ? 'Awaiting cleanup'
            : '30-day deadline reached'
          : 'Unused project';
    }
  }

  get modeNotice(): string {
    if (this.retention?.mode === 'disabled') return 'Automatic deletion is currently disabled.';
    if (this.retention?.mode === 'dry-run')
      return 'Automatic deletion is in preview mode. No projects are deleted by these checks.';
    if (this.retention?.status !== 'unused' && this.retention?.status !== 'awaiting_cleanup')
      return '';
    return this.due
      ? 'This project may be removed during a daily check.'
      : 'Unused projects may be removed during a daily check after the deadline.';
  }

  get countdown(): string {
    const remaining = this.remaining;
    if (remaining === null || remaining === 0) return '';
    const days = Math.floor(remaining / 86_400_000);
    if (days > 0) return `${days} ${days === 1 ? 'day' : 'days'} remaining`;
    return 'Less than a day remaining';
  }
}
