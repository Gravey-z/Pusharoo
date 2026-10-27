import { Component, OnInit, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink, RouterOutlet } from '@angular/router';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

@Component({
  selector: 'app-project-workspace',
  imports: [PageShellComponent, ProjectReleaseNavComponent, RouterLink, RouterOutlet],
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
    this.loadProject();
  }

  private loadProject(): void {
    this.isLoading = true;
    this.loadError = '';
    this.workspace.summary = null;
    const generation = ++this.generation;
    this.api.getProjectSummary(this.projectId).subscribe({
      next: overview => {
        if (generation !== this.generation || this.destroyRef.destroyed) return;
        this.workspace.summary = overview;
        this.isLoading = false;
      },
      error: error => {
        if (generation !== this.generation || this.destroyRef.destroyed) return;
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }
}
