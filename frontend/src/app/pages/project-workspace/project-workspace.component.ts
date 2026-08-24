import { Component, OnInit } from '@angular/core';
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
    this.route.paramMap.subscribe((params) => {
      this.projectId = params.get('projectId') ?? '';
      this.loadProject();
    });
  }

  get projectName(): string {
    return this.workspace.overview?.project.name ?? 'Project';
  }

  get canManageProject(): boolean {
    return this.ownership.canManage(this.workspace.overview?.project, this.wallet.account()?.address ?? '');
  }

  retryLoad(): void {
    this.loadProject();
  }

  private loadProject(): void {
    this.isLoading = true;
    this.loadError = '';
    this.workspace.overview = null;
    this.api.getProjectOverview(this.projectId).subscribe({
      next: (overview) => {
        this.workspace.overview = overview;
        this.isLoading = false;
      },
      error: (error) => {
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }
}
