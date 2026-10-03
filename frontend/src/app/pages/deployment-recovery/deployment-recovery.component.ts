import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom, forkJoin } from 'rxjs';
import { ProjectAuthorizedDeployer, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ProjectDeploymentAccessService } from '../../services/project-deployment-access.service';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
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
  transactionId = '';
  errorMessage = '';
  statusMessage = '';
  isRecovering = false;
  isLoading = true;
  loadError = '';
  authorizedDeployers: ProjectAuthorizedDeployer[] = [];
  readonly projectId: string;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');
  readonly walletNetwork = computed(() => this.wallet.session()?.network ?? '');

  get pageTitle(): string {
    return 'Recover Deployment';
  }

  get canInspectRecovery(): boolean {
    const access = this.deploymentAccess.resolve(this.overview?.project, this.authorizedDeployers, this.walletAddress());
    return this.deploymentAccess.canDeployToNetwork(access, this.walletNetwork());
  }

  get hasConfirmedDeploymentOnConnectedNetwork(): boolean {
    return Boolean(this.deploymentHistory.latestForNetwork(this.overview?.deployments ?? [], this.walletNetwork()));
  }

  get recoveryAccessMessage(): string {
    if (!this.walletAddress() || !this.walletNetwork()) {
      return 'Connect the wallet that signed the deployment on the transaction network.';
    }
    if (!this.canInspectRecovery) {
      return `The connected wallet has no deployment access for ${this.deploymentAccess.networkLabel(this.walletNetwork())}.`;
    }
    return 'The connected wallet can recover a deployment transaction it signed on this network.';
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly deploymentHistory: DeploymentHistoryService,
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
      authorizedDeployers: this.api.getAuthorizedDeployers(this.projectId)
    }).subscribe({
      next: ({ overview, authorizedDeployers }) => {
        this.overview = overview;
        this.authorizedDeployers = authorizedDeployers;
        this.isLoading = false;
      },
      error: (error) => {
        this.overview = null;
        this.authorizedDeployers = [];
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  async recover(): Promise<void> {
    this.errorMessage = '';
    this.statusMessage = '';

    if (this.hasConfirmedDeploymentOnConnectedNetwork) {
      this.errorMessage = 'This project already has a confirmed deployment on the connected network.';
      return;
    }

    const transactionId = this.transactionId.trim().toLowerCase();
    if (!/^0x[0-9a-f]{64}$/.test(transactionId)) {
      this.errorMessage = 'Enter a 0x-prefixed Neo N3 transaction hash.';
      return;
    }
    if (!this.canInspectRecovery) {
      this.errorMessage = this.recoveryAccessMessage;
      return;
    }

    this.isRecovering = true;
    try {
      const authorization = await this.wallet.signDeploymentRecovery(this.projectId, transactionId);
      this.statusMessage = 'Checking the transaction on Neo...';
      await firstValueFrom(this.api.recoverDeployment(this.projectId, {
        network: this.walletNetwork(),
        transactionId,
        deployedBy: this.walletAddress(),
        authorization
      }));
      await this.router.navigate(['/projects', this.projectId, 'deployments']);
    } catch (error) {
      this.statusMessage = '';
      this.errorMessage = this.errors.format(error, 'Could not recover this deployment transaction.');
    } finally {
      this.isRecovering = false;
    }
  }

}
