import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Artifact, Deployment, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ClipboardService } from '../../services/clipboard.service';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

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
  isLoading = true;
  loadError = '';
  private projectId = '';
  copiedValue = '';
  confirmingDeploymentId = '';
  releaseTab: 'overview' | 'artifacts' | 'deployments' = 'overview';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly clipboard: ClipboardService,
    private readonly deploymentHistory: DeploymentHistoryService,
    private readonly ownership: ProjectOwnershipService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    this.releaseTab = this.route.snapshot.data['releaseTab'] ?? 'overview';
    this.route.paramMap.subscribe((params) => this.loadOverview(params.get('projectId') ?? ''));
  }

  canManageProject(overview: ProjectOverviewViewModel): boolean {
    return this.ownership.canManage(overview.project, this.wallet.account()?.address ?? '');
  }

  latestDeploymentForNetwork(
    overview: ProjectOverviewViewModel,
    network: string
  ): Deployment | null {
    return this.deploymentHistory.latestForNetwork(overview.deployments, `neo3:${network}`);
  }

  isNetworkUnavailable(network: string): boolean {
    const walletNetwork = this.wallet.session()?.network;

    return Boolean(walletNetwork && walletNetwork !== `neo3:${network}`);
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
        detail: `${deployment.version} • ${this.wallet.networkLabel(deployment.network)}`,
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
    return Boolean(
      deployment.transactionId
      && ['submitted', 'confirming'].includes(deployment.status)
      && this.wallet.account()?.address === deployment.deployedBy
    );
  }

  async resumeConfirmation(overview: ProjectOverviewViewModel, deployment: Deployment): Promise<void> {
    const walletAddress = this.wallet.account()?.address;
    if (!walletAddress || !this.canResumeConfirmation(deployment)) {
      return;
    }

    this.confirmingDeploymentId = deployment.id;
    try {
      await firstValueFrom(this.api.confirmDeploymentAttempt(overview.project.id, deployment.id, walletAddress));
      this.loadOverview(overview.project.id);
    } finally {
      this.confirmingDeploymentId = '';
    }
  }

  loadOverview(projectId: string): void {
    this.projectId = projectId;
    this.isLoading = true;
    this.loadError = '';
    this.overview = null;
    this.api.getProjectOverview(projectId).subscribe({
      next: (overview) => {
        this.overview = overview;
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
}
