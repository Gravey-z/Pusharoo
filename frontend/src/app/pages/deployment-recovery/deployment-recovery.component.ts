import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { Artifact, ProjectCollaborator, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ProjectDeploymentAccessService } from '../../services/project-deployment-access.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

@Component({
  selector: 'app-deployment-recovery',
  imports: [FormsModule, PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './deployment-recovery.component.html',
  styleUrl: './deployment-recovery.component.scss'
})
export class DeploymentRecoveryComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  artifacts: Artifact[] = [];
  artifactId = '';
  transactionId = '';
  notes = '';
  errorMessage = '';
  statusMessage = '';
  isRecovering = false;
  isLoading = true;
  loadError = '';
  collaborators: ProjectCollaborator[] = [];
  readonly projectId: string;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');
  readonly walletNetwork = computed(() => this.wallet.session()?.network ?? '');

  get pageTitle(): string {
    return 'Recover Deployment';
  }

  get canInspectRecovery(): boolean {
    const access = this.deploymentAccess.resolve(this.overview?.project, this.collaborators, this.walletAddress());
    return this.deploymentAccess.canDeployToNetwork(access, this.walletNetwork());
  }

  get recoveryAccessMessage(): string {
    if (!this.walletAddress() || !this.walletNetwork()) {
      return 'Connect the wallet assigned to the transaction network to inspect recovery requirements.';
    }
    if (!this.canInspectRecovery) {
      return `The connected wallet has no deployment access for ${this.deploymentAccess.networkLabel(this.walletNetwork())}.`;
    }
    return 'Recovery is currently limited to authorized submitted attempts. Unbound transaction recovery will return when prepared deployment intents are available.';
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly deploymentAccess: ProjectDeploymentAccessService,
    readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.loadProject();
  }

  retryLoad(): void {
    this.loadProject();
  }

  private loadProject(): void {
    this.isLoading = true;
    this.loadError = '';
    forkJoin({
      overview: this.api.getProjectOverview(this.projectId),
      collaborators: this.api.getCollaborators(this.projectId)
    }).subscribe({
      next: ({ overview, collaborators }) => {
        this.overview = overview;
        this.collaborators = collaborators;
        this.artifacts = overview.artifacts ?? [];
        this.artifactId = this.artifacts[0]?.id ?? '';
        this.isLoading = false;
      },
      error: (error) => {
        this.overview = null;
        this.artifacts = [];
        this.collaborators = [];
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  async recover(): Promise<void> {
    this.errorMessage = '';
    this.statusMessage = '';

    if (!this.canInspectRecovery) {
      this.errorMessage = this.recoveryAccessMessage;
      return;
    }

    this.errorMessage = 'Unbound transaction recovery is disabled until prepared deployment intents are available. Resume an authorized submitted attempt instead.';
  }

}
