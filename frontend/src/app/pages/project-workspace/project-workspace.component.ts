import { Component, OnInit, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { debounceTime, filter, interval } from 'rxjs';
import { ActivatedRoute, RouterLink, RouterOutlet } from '@angular/router';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';
import { ProjectRetentionNoticeComponent } from '../../components/project-retention-notice/project-retention-notice.component';

@Component({
  selector: 'app-project-workspace',
  imports: [
    PageShellComponent,
    ProjectReleaseNavComponent,
    RouterLink,
    RouterOutlet,
    ProjectRetentionNoticeComponent
  ],
  providers: [ProjectWorkspaceContextService],
  templateUrl: './project-workspace.component.html',
  styleUrl: './project-workspace.component.scss'
})
export class ProjectWorkspaceComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  private generation = 0;
  projectId = '';
  isLoading = true;
  loadError = '';
  unavailable = false;

  get retention() {
    return this.workspace.summary?.project.retention;
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly ownership: ProjectOwnershipService,
    private readonly workspace: ProjectWorkspaceContextService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      this.projectId = params.get('projectId') ?? '';
      this.loadProject();
    });
    this.api.projectChanges
      .pipe(
        filter(id => id === this.projectId),
        debounceTime(100),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe(() => {
        this.workspace.overview = null;
        this.loadProject(true, true);
      });
    interval(60_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (!this.isLoading && !this.unavailable && this.workspace.summary)
          this.loadProject(true, true);
      });
  }

  get projectName(): string {
    return this.workspace.summary?.project.name ?? 'Project';
  }

  get canManageProject(): boolean {
    return this.ownership.canManage(
      this.workspace.summary?.project,
      this.wallet.account()?.address ?? ''
    );
  }

  retryLoad(): void {
    this.loadProject(true);
  }

  private loadProject(refresh = false, background = false): void {
    this.isLoading = !background;
    this.loadError = '';
    if (!background) {
      this.unavailable = false;
      this.workspace.summary = null;
      this.workspace.overview = null;
    }
    const generation = ++this.generation;
    this.api
      .getProjectSummary(this.projectId, refresh)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: overview => {
          if (generation !== this.generation || this.destroyRef.destroyed) return;
          this.unavailable = overview.project.retention?.status === 'unavailable';
          this.workspace.summary = this.unavailable ? null : overview;
          this.isLoading = false;
        },
        error: error => {
          if (generation !== this.generation || this.destroyRef.destroyed) return;
          this.unavailable = error instanceof HttpErrorResponse && error.status === 404;
          if (this.unavailable) {
            this.workspace.summary = null;
            this.workspace.overview = null;
          } else if (!background) {
            this.loadError = this.errors.format(error, 'Could not load this project.');
          }
          this.isLoading = false;
        }
      });
  }
}
