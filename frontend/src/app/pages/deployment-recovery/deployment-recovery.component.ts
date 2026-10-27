import { workspaceRoute, contractLink } from '../../services/workspace-route';
import { Component, OnDestroy, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest, firstValueFrom, forkJoin, of, Subscription } from 'rxjs';
import {
  Deployment,
  ProjectAuthorizedDeployer,
  ProjectOverviewViewModel
} from '../../models/pusharoo.models';
import { ProjectDeploymentAccessService } from '../../services/project-deployment-access.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { DeploymentAttemptCapabilityService } from '../../services/deployment-attempt-capability.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

@Component({
  selector: 'app-deployment-recovery',
  imports: [FormsModule, PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './deployment-recovery.component.html',
  styleUrl: './deployment-recovery.component.scss'
})
export class DeploymentRecoveryComponent implements OnInit, OnDestroy {
  overview: ProjectOverviewViewModel | null = null;
  transactionId = '';
  errorMessage = '';
  statusMessage = '';
  isRecovering = false;
  isLoading = true;
  loadError = '';
  authorizedDeployers: ProjectAuthorizedDeployer[] = [];
  projectId = '';
  contractId: string | null = null;
  contractName = '';
  private generation = 0;
  private routeSubscription: Subscription | null = null;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');
  readonly walletNetwork = computed(() => this.wallet.session()?.network ?? '');

  contractPath(...segments: string[]): string[] {
    return contractLink(this.projectId, this.contractId, ...segments);
  }

  get pageTitle(): string {
    return 'Recover Deployment';
  }

  get canInspectRecovery(): boolean {
    const access = this.deploymentAccess.resolve(
      this.overview?.project,
      this.authorizedDeployers,
      this.walletAddress()
    );
    return (
      this.deploymentAccess.canDeployToNetwork(access, this.walletNetwork()) ||
      this.hasUnrecordedAttempt
    );
  }

  get hasUnrecordedAttempt(): boolean {
    return Boolean(
      this.walletAddress() &&
        this.walletNetwork() &&
        this.selectedDeployments.some(
          deployment =>
            deployment.network === this.walletNetwork() &&
            deployment.deployedBy === this.walletAddress() &&
            [
              'preparing',
              'awaiting_wallet',
              'submitted',
              'confirming',
              'record_failed',
              'failed',
              'confirmed'
            ].includes(deployment.status)
        )
    );
  }

  private get selectedDeployments(): Deployment[] {
    return (this.overview?.deployments ?? []).filter(
      deployment =>
        deployment.projectId === this.projectId &&
        (deployment.contractId ?? null) === this.contractId
    );
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
    private readonly deploymentAccess: ProjectDeploymentAccessService,
    private readonly auth: WalletAuthService,
    private readonly attemptCapabilities: DeploymentAttemptCapabilityService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    this.routeSubscription = combineLatest([
      workspaceRoute(this.route),
      this.route.queryParamMap
    ]).subscribe(([scope, query]) => {
      this.projectId = scope.projectId;
      this.contractId = scope.contractId ?? query.get('contractId');
      this.isRecovering = false;
      this.transactionId = '';
      this.errorMessage = '';
      this.statusMessage = '';
      this.loadProject();
    });
  }

  ngOnDestroy(): void {
    this.generation++;
    this.routeSubscription?.unsubscribe();
  }

  retryLoad(): void {
    this.loadProject();
  }

  private loadProject(): void {
    this.isLoading = true;
    this.loadError = '';
    this.overview = null;
    this.contractName = '';
    const generation = ++this.generation;
    const projectId = this.projectId;
    const contractId = this.contractId;
    forkJoin({
      contract: contractId ? this.api.getProjectContract(projectId, contractId) : of(null),
      overview: contractId
        ? this.api.getContractOverview(projectId, contractId)
        : this.api.getProjectOverview(projectId),
      authorizedDeployers: this.api.getAuthorizedDeployers(this.projectId)
    }).subscribe({
      next: ({ overview, authorizedDeployers, contract }) => {
        if (generation !== this.generation) return;
        if (
          overview.project.id !== projectId ||
          (contract && (contract.id !== contractId || contract.projectId !== projectId))
        ) {
          this.loadError = 'The selected contract does not belong to this project.';
          this.isLoading = false;
          return;
        }
        this.contractName = contract?.name ?? '';
        this.overview = overview;
        this.authorizedDeployers = authorizedDeployers;
        this.isLoading = false;
      },
      error: error => {
        if (generation !== this.generation) return;
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
    const generation = this.generation;
    const projectId = this.projectId;
    const contractId = this.contractId;
    try {
      const walletAddress = this.walletAddress();
      const network = this.walletNetwork();
      await this.auth.ensureAuthenticated();
      if (
        generation !== this.generation ||
        this.walletAddress() !== walletAddress ||
        this.walletNetwork() !== network
      ) {
        throw new Error('The connected wallet or network changed. Try recovery again.');
      }
      this.statusMessage = 'Checking the transaction on Neo...';
      const recovered = await firstValueFrom(
        this.api.recoverDeployment(projectId, { network, transactionId }, contractId)
      );
      if (
        recovered.projectId !== projectId ||
        (recovered.contractId ?? null) !== contractId ||
        recovered.network !== network ||
        recovered.deployedBy !== walletAddress ||
        !this.attemptCapabilities.matchesTransaction(recovered.transactionId, transactionId)
      ) {
        throw new Error(
          'The recovery response does not match the selected contract or transaction.'
        );
      }
      this.attemptCapabilities.complete(recovered);
      if (generation === this.generation)
        await this.router.navigate(contractLink(projectId, contractId, 'deployments'));
    } catch (error) {
      if (generation !== this.generation) return;
      const pending = (error as { error?: { deployment?: Deployment } } | null)?.error?.deployment;
      if (
        pending &&
        pending.projectId === projectId &&
        (pending.contractId ?? null) === contractId &&
        pending.network === this.walletNetwork() &&
        pending.deployedBy === this.walletAddress() &&
        this.attemptCapabilities.matchesTransaction(pending.transactionId, transactionId)
      ) {
        try {
          this.attemptCapabilities.setTransactionId(pending, transactionId);
          this.attemptCapabilities.setBroadcastPending(pending);
          if (pending.attemptCapability)
            this.attemptCapabilities.set(pending, pending.attemptCapability);
        } catch {}
      }
      this.statusMessage = '';
      this.errorMessage = this.errors.format(
        error,
        'Could not recover this deployment transaction.'
      );
    } finally {
      if (generation === this.generation) this.isRecovering = false;
    }
  }
}
