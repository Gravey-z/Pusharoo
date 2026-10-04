import { Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Artifact, Deployment, DeploymentDataValue, ProjectAuthorizedDeployer, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ClipboardService } from '../../services/clipboard.service';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { DeploymentAttemptCapabilityService } from '../../services/deployment-attempt-capability.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';
import { ProjectDeploymentAccessService } from '../../services/project-deployment-access.service';

interface ReleaseTimelineEvent {
  id: string;
  occurredAt: string;
  type: 'artifact' | 'deployment' | 'update' | 'failure';
  title: string;
  detail: string;
  network?: string;
  deployment?: Deployment;
}

@Component({
  selector: 'app-project-overview',
  imports: [PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './project-overview.component.html',
  styleUrl: './project-overview.component.scss'
})
export class ProjectOverviewComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;

  formatDeploymentData(value: DeploymentDataValue): string {
    return JSON.stringify(value, null, 2);
  }
  isLoading = true;
  loadError = '';
  private projectId = '';
  copiedValue = '';
  confirmingDeploymentId = '';
  confirmationErrorDeploymentId = '';
  confirmationError = '';
  authorizedDeployers: ProjectAuthorizedDeployer[] = [];
  authorizedDeployerAccessError = '';
  releaseTab: 'overview' | 'artifacts' | 'deployments' = 'overview';
  private readonly workspace = inject(ProjectWorkspaceContextService, { optional: true });

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly attemptCapabilities: DeploymentAttemptCapabilityService,
    private readonly errors: ApiErrorFormatterService,
    private readonly clipboard: ClipboardService,
    private readonly deploymentHistory: DeploymentHistoryService,
    private readonly ownership: ProjectOwnershipService,
    private readonly deploymentAccess: ProjectDeploymentAccessService,
    private readonly auth: WalletAuthService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    this.releaseTab = this.route.snapshot.data['releaseTab'] ?? 'overview';
    (this.route.parent ?? this.route).paramMap.subscribe((params) => this.loadOverview(params.get('projectId') ?? ''));
  }

  canManageProject(overview: ProjectOverviewViewModel): boolean {
    return this.ownership.canManage(overview.project, this.wallet.account()?.address ?? '');
  }

  canDeployToNetwork(overview: ProjectOverviewViewModel, network: string): boolean {
    return this.deploymentAccess.canDeployToNetwork(
      this.deploymentAccess.resolve(overview.project, this.authorizedDeployers, this.wallet.account()?.address),
      `neo3:${network}`
    );
  }

  canStartDeploymentToNetwork(overview: ProjectOverviewViewModel, network: string): boolean {
    const normalizedNetwork = network.startsWith('neo3:') ? network : `neo3:${network}`;
    return this.deploymentAccess.canStartDeployment(
      this.deploymentAccess.resolve(overview.project, this.authorizedDeployers, this.wallet.account()?.address),
      normalizedNetwork
    );
  }

  hasAnyDeploymentAccess(overview: ProjectOverviewViewModel): boolean {
    return this.deploymentAccess.resolve(overview.project, this.authorizedDeployers, this.wallet.account()?.address)
      .allowedNetworks.length > 0;
  }

  deploymentAccessMessage(overview: ProjectOverviewViewModel): string {
    return this.deploymentAccess.description(
      this.deploymentAccess.resolve(overview.project, this.authorizedDeployers, this.wallet.account()?.address)
    );
  }

  latestDeploymentForNetwork(
    overview: ProjectOverviewViewModel,
    network: string
  ): Deployment | null {
    return this.deploymentHistory.latestForNetwork(overview.deployments, `neo3:${network}`);
  }

  liveDeployments(overview: ProjectOverviewViewModel): Deployment[] {
    return this.deploymentHistory.latestConfirmedByNetwork(overview.deployments)
      .filter((deployment) => ['neo3:testnet', 'neo3:mainnet'].includes(deployment.network));
  }

  hasConfirmedDeploymentOnConnectedNetwork(overview: ProjectOverviewViewModel): boolean {
    const network = this.wallet.session()?.network;
    return Boolean(network && this.deploymentHistory.latestForNetwork(overview.deployments, network));
  }

  latestAttemptsByVersion(overview: ProjectOverviewViewModel): Deployment[] {
    const latestByVersionAndNetwork = new Map<string, Deployment>();

    for (const deployment of [...overview.deployments]
      .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())) {
      const key = `${deployment.version}\u0000${deployment.network}`;
      if (!latestByVersionAndNetwork.has(key)) {
        latestByVersionAndNetwork.set(key, deployment);
      }
    }

    return [...latestByVersionAndNetwork.values()];
  }

  liveArtifacts(overview: ProjectOverviewViewModel): Artifact[] {
    const artifactsById = new Map(overview.artifacts.map((artifact) => [artifact.id, artifact]));
    const includedArtifactIds = new Set<string>();
    const liveArtifacts: Artifact[] = [];

    for (const deployment of this.liveDeployments(overview)) {
      const artifact = artifactsById.get(deployment.artifactId);
      if (artifact && !includedArtifactIds.has(artifact.id)) {
        includedArtifactIds.add(artifact.id);
        liveArtifacts.push(artifact);
      }
    }

    return liveArtifacts;
  }

  isNetworkUnavailable(network: string): boolean {
    const walletNetwork = this.wallet.session()?.network;

    return Boolean(walletNetwork && walletNetwork !== `neo3:${network}`);
  }

  hasActiveAttempt(overview: ProjectOverviewViewModel, network: string): boolean {
    return overview.deployments.some((deployment) => deployment.network === `neo3:${network}`
      && ['preparing', 'awaiting_wallet', 'submitted', 'confirming'].includes(deployment.status));
  }

  hasUnrecordedAttemptForConnectedWallet(overview: ProjectOverviewViewModel): boolean {
    const account = this.wallet.account();
    const network = this.wallet.session()?.network;
    return Boolean(account && network && overview.deployments.some((deployment) =>
      deployment.network === network && deployment.deployedBy === account.address
      && ['preparing', 'awaiting_wallet'].includes(deployment.status) && !deployment.transactionId));
  }

  deployerLabel(overview: ProjectOverviewViewModel, walletAddress: string): string {
    if (overview.project.createdByWalletAddress === walletAddress) {
      return 'Owner';
    }

    return this.authorizedDeployers.some((authorizedDeployer) => authorizedDeployer.walletAddress === walletAddress)
      ? 'Authorized deployer'
      : 'Deployment wallet';
  }

  artifactDeployments(overview: ProjectOverviewViewModel, artifact: Artifact): Deployment[] {
    return this.deploymentHistory.latestForArtifact(overview, artifact);
  }

  artifactNetworks(overview: ProjectOverviewViewModel, artifact: Artifact): string[] {
    return this.deploymentHistory.networksForLatestArtifact(overview, artifact);
  }

  isArtifactDeployed(overview: ProjectOverviewViewModel, artifact: Artifact): boolean {
    return this.artifactDeployments(overview, artifact).length > 0;
  }

  hasWebhookTarget(overview: ProjectOverviewViewModel): boolean {
    return overview.deployments.some((deployment) => Boolean(deployment.contractHash));
  }

  shortText(value: string | null | undefined, leading = 3, trailing = 4): string {
    if (!value) {
      return '-';
    }

    if (value.length <= leading + trailing + 3) {
      return value;
    }

    return `${value.slice(0, leading)}...${value.slice(-trailing)}`;
  }

  shortTransactionId(value: string | null | undefined): string {
    return value ? this.shortText(value, 10, 4) : 'No txid';
  }

  async copyValue(value: string | null | undefined, event: Event): Promise<void> {
    event.preventDefault();
    event.stopPropagation();

    if (!value) {
      return;
    }

    await this.clipboard.copy(value);
    this.copiedValue = value;
    window.setTimeout(() => {
      if (this.copiedValue === value) {
        this.copiedValue = '';
      }
    }, 1400);
  }

  deploymentStatusLabel(deployment: Deployment): string {
    return deployment.status.replaceAll('_', ' ');
  }

  deploymentStatusTone(status: string): 'success' | 'warning' | 'danger' {
    if (status === 'failed' || status === 'record_failed') {
      return 'danger';
    }

    return status === 'confirmed' || !status ? 'success' : 'warning';
  }

  explorerUrl(network: string, kind: 'transaction' | 'contract' | 'address', value: string): string {
    const normalizedNetwork = network.toLowerCase().includes('mainnet') ? 'mainnet' : 'testnet';
    const path = kind === 'transaction' ? 'transaction' : kind;

    return `https://dora.coz.io/${path}/neo3/${normalizedNetwork}/${encodeURIComponent(value)}`;
  }

  releaseTimeline(overview: ProjectOverviewViewModel): ReleaseTimelineEvent[] {
    const artifacts = overview.artifacts.map((artifact) => ({
      id: `artifact-${artifact.id}`,
      occurredAt: artifact.createdAt,
      type: 'artifact' as const,
      title: `Artifact ${artifact.version} uploaded`,
      detail: `${artifact.contractName} • ${artifact.nefFileName}`
    }));

    const deployments = overview.deployments.flatMap((deployment) => {
      const failed = deployment.status === 'failed' || deployment.status === 'record_failed';
      const confirmed = deployment.status === 'confirmed' || !deployment.status;
      const action = deployment.operation === 'update' ? 'Contract update' : 'Contract deployment';
      const events: ReleaseTimelineEvent[] = [{
        id: `deployment-started-${deployment.id}`,
        occurredAt: deployment.createdAt,
        type: deployment.operation === 'update' ? 'update' : 'deployment',
        title: `${action} started`,
        detail: `${deployment.version} • ${this.wallet.networkLabel(deployment.network)} • ${this.shortText(deployment.deployedBy)} (${this.deployerLabel(overview, deployment.deployedBy)})`,
        network: deployment.network,
        deployment
      }];

      if (deployment.transactionId) {
        events.push({
          id: `deployment-submitted-${deployment.id}`,
          occurredAt: deployment.updatedAt || deployment.createdAt,
          type: deployment.operation === 'update' ? 'update' : 'deployment',
          title: `${action} submitted`,
          detail: `${this.wallet.networkLabel(deployment.network)} • ${this.shortTransactionId(deployment.transactionId)}`,
          network: deployment.network,
          deployment
        });
      }

      if (deployment.updatedAt && deployment.updatedAt !== deployment.createdAt) {
        events.push({
          id: `deployment-result-${deployment.id}`,
          occurredAt: deployment.updatedAt,
          type: failed ? 'failure' : deployment.operation === 'update' ? 'update' : 'deployment',
          title: failed ? `${action} failed` : confirmed ? `${action} confirmed` : `${action} ${this.deploymentStatusLabel(deployment)}`,
          detail: failed
            ? (deployment.failureReason || 'The release did not complete.')
            : `${deployment.version} • ${this.wallet.networkLabel(deployment.network)}${deployment.contractHash ? ` • ${this.shortText(deployment.contractHash, 10, 4)}` : ''}`,
          network: deployment.network,
          deployment
        });
      }

      return events;
    });

    return [...artifacts, ...deployments]
      .sort((left, right) => new Date(right.occurredAt).getTime() - new Date(left.occurredAt).getTime());
  }

  formatTimelineDate(value: string): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short'
    }).format(new Date(value));
  }

  canResumeConfirmation(deployment: Deployment): boolean {
    const retryableFailure = deployment.status === 'failed'
      && deployment.failureStage === 'confirmation'
      && deployment.failureReason?.startsWith('Pusharoo could not verify the submitted invocation:');
    const locallySubmitted = deployment.status === 'awaiting_wallet'
      && Boolean(this.attemptCapabilities.getTransactionId(deployment.id));
    return Boolean(
      (deployment.transactionId || this.attemptCapabilities.getTransactionId(deployment.id))
      && (['submitted', 'confirming'].includes(deployment.status) || retryableFailure || locallySubmitted)
      && this.wallet.account()?.address === deployment.deployedBy
    );
  }

  canCancelAttempt(deployment: Deployment): boolean {
    return deployment.status === 'awaiting_wallet'
      && !deployment.transactionId
      && !this.attemptCapabilities.getTransactionId(deployment.id)
      && this.wallet.account()?.address === deployment.deployedBy;
  }

  async cancelAttempt(overview: ProjectOverviewViewModel, deployment: Deployment): Promise<void> {
    if (!this.canCancelAttempt(deployment)
      || !window.confirm('Cancel this attempt only if your wallet did not submit a transaction. Continue?')) return;
    this.confirmingDeploymentId = deployment.id;
    this.confirmationErrorDeploymentId = '';
    this.confirmationError = '';
    try {
      await this.auth.ensureAuthenticated();
      const renewed = await firstValueFrom(this.api.resumeDeploymentAttempt(overview.project.id, deployment.id));
      if (!renewed.attemptCapability) throw new Error('Pusharoo could not renew the attempt. Refresh and try again.');
      await firstValueFrom(this.api.markDeploymentFailed(overview.project.id, deployment.id,
        renewed.attemptCapability, 'wallet', 'Canceled before transaction submission.'));
      this.attemptCapabilities.remove(deployment.id);
      if (this.workspace) this.workspace.overview = null;
      this.loadOverview(overview.project.id);
    } catch (error) {
      this.confirmationErrorDeploymentId = deployment.id;
      this.confirmationError = this.errors.format(error, 'Could not cancel this deployment attempt.');
    } finally {
      this.confirmingDeploymentId = '';
    }
  }

  async resumeConfirmation(overview: ProjectOverviewViewModel, deployment: Deployment): Promise<void> {
    if (!this.canResumeConfirmation(deployment)) {
      return;
    }

    this.confirmingDeploymentId = deployment.id;
    this.confirmationErrorDeploymentId = '';
    this.confirmationError = '';
    try {
      await this.auth.ensureAuthenticated();
      const renewed = await firstValueFrom(this.api.resumeDeploymentAttempt(overview.project.id, deployment.id));
      let capability = renewed.attemptCapability;
      if (!capability) throw new Error('Pusharoo could not renew the attempt. Refresh and try again.');
      this.attemptCapabilities.set(deployment.id, capability);
      const transactionId = renewed.transactionId ?? this.attemptCapabilities.getTransactionId(deployment.id);
      if (!transactionId) throw new Error('The submitted transaction ID is missing. Recover it by transaction hash.');
      if (!renewed.transactionId) {
        const submitted = await firstValueFrom(this.api.markDeploymentSubmitted(
          overview.project.id, deployment.id, transactionId, capability));
        capability = submitted.attemptCapability;
        if (!capability) throw new Error('Pusharoo could not renew the submitted attempt. Try Resume Confirmation again.');
        this.attemptCapabilities.set(deployment.id, capability);
      }
      await firstValueFrom(this.api.confirmDeploymentAttempt(overview.project.id, deployment.id, capability));
      this.attemptCapabilities.remove(deployment.id);
      if (this.workspace) this.workspace.overview = null;
      this.loadOverview(overview.project.id);
    } catch (error) {
      this.confirmationErrorDeploymentId = deployment.id;
      this.confirmationError = this.errors.format(error, 'Could not confirm this deployment.');
    } finally {
      this.confirmingDeploymentId = '';
    }
  }

  loadOverview(projectId: string): void {
    this.projectId = projectId;
    const cachedOverview = this.workspace?.getFreshOverview(projectId);
    if (cachedOverview?.project.id === projectId) {
      this.overview = cachedOverview;
      this.isLoading = false;
      this.loadError = '';
      this.loadAuthorizedDeployers(projectId);
      return;
    }

    this.isLoading = true;
    this.loadError = '';
    this.overview = null;
    this.api.getProjectOverview(projectId).subscribe({
      next: (overview) => {
        this.overview = overview;
        if (this.workspace) {
          this.workspace.overview = overview;
        }
        this.loadAuthorizedDeployers(projectId);
        this.isLoading = false;
      },
      error: (error) => {
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  retryLoad(): void {
    this.loadOverview(this.projectId);
  }

  private loadAuthorizedDeployers(projectId: string): void {
    this.authorizedDeployerAccessError = '';
    this.api.getAuthorizedDeployers(projectId).subscribe({
      next: (authorizedDeployers) => this.authorizedDeployers = authorizedDeployers,
      error: () => {
        this.authorizedDeployers = [];
        this.authorizedDeployerAccessError = 'Could not verify authorized-deployer access. Refresh before starting a deployment.';
      }
    });
  }

}
