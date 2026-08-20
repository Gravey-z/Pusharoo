import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { NetworkType } from 'neo-n3-walletkit';
import { firstValueFrom } from 'rxjs';
import { isPusharooNetwork } from '../../config/wallet.config';
import { Artifact, Deployment, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { NeoRpcService } from '../../services/neo-rpc.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { RuntimeConfigService } from '../../services/runtime-config.service';
import { DeploymentFeeEstimate, WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

@Component({
  selector: 'app-deployment-create',
  imports: [FormsModule, PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './deployment-create.component.html',
  styleUrl: './deployment-create.component.scss'
})
export class DeploymentCreateComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  artifacts: Artifact[] = [];
  artifactId = '';
  notes = '';
  errorMessage = '';
  deployStatus = '';
  isSaving = false;
  isPreparingReview = false;
  isReviewing = false;
  mainnetConfirmed = false;
  feeEstimate: DeploymentFeeEstimate | null = null;
  feeEstimateError = '';
  private preparedNefHex = '';
  private preparedArtifactId = '';
  readonly projectId: string;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');
  readonly walletNetwork = computed(() => this.wallet.session()?.network ?? '');

  get pageTitle(): string {
    return this.updateMode ? 'Update Contract' : 'Deploy Contract';
  }

  get submitLabel(): string {
    if (this.isSaving) {
      return this.updateMode ? 'Updating...' : 'Deploying...';
    }

    return this.updateMode ? 'Update Contract' : 'Deploy Contract';
  }

  get selectedArtifact(): Artifact | null {
    return this.artifacts.find((artifact) => artifact.id === this.artifactId) ?? null;
  }

  get operation(): 'deploy' | 'update' {
    return this.getExistingDeployment(this.walletNetwork())?.contractHash ? 'update' : 'deploy';
  }

  get targetContract(): string | null {
    return this.getExistingDeployment(this.walletNetwork())?.contractHash ?? null;
  }

  get canManageProject(): boolean {
    return this.ownership.canManage(this.overview?.project, this.walletAddress());
  }

  get networkDeploymentStatus(): string {
    const network = this.walletNetwork();
    const existingDeployment = this.getExistingDeployment(network);

    if (!network) {
      return 'Connect a wallet to detect the target network.';
    }

    if (existingDeployment?.contractHash) {
      return `Existing ${network} deployment found at ${existingDeployment.contractHash}. Pusharoo will call update on that contract.`;
    }

    return `No ${network} deployment found. Pusharoo will deploy a new contract on this network.`;
  }

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly deploymentHistory: DeploymentHistoryService,
    private readonly neoRpc: NeoRpcService,
    private readonly ownership: ProjectOwnershipService,
    private readonly runtimeConfig: RuntimeConfigService,
    readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.api.getProjectOverview(this.projectId).subscribe((overview) => {
      this.overview = overview;
      this.artifacts = overview?.artifacts ?? [];
      this.artifactId = this.artifacts[0]?.id ?? '';
    });
  }

  async save(): Promise<void> {
    this.errorMessage = '';
    this.deployStatus = '';

    if (!this.isReviewing || !this.preparedNefHex || this.preparedArtifactId !== this.artifactId) {
      this.errorMessage = 'Review this release before opening the wallet.';
      return;
    }

    const artifact = this.artifacts.find((item) => item.id === this.artifactId);
    if (!artifact) {
      this.errorMessage = 'The selected artifact could not be loaded.';
      return;
    }

    const session = this.wallet.session();
    if (!session || !this.walletAddress()) {
      this.errorMessage = 'Connect a wallet before adding a deployment.';
      return;
    }

    const ownershipError = this.ownership.managementError(
      this.overview?.project,
      this.walletAddress()
    );

    if (ownershipError) {
      this.errorMessage = ownershipError;
      return;
    }

    if (session.network === 'neo3:mainnet' && !this.mainnetConfirmed) {
      this.errorMessage = 'Confirm the N3:Mainnet warning before opening the wallet.';
      return;
    }

    this.isSaving = true;
    let attempt: Deployment | null = null;
    let transactionId = '';

    try {
      const deploymentNotes = this.notes.trim() || null;
      this.deployStatus = 'Creating deployment attempt...';
      attempt = await firstValueFrom(this.api.startDeploymentAttempt(this.projectId, {
        artifactId: this.artifactId,
        network: session.network,
        deployedBy: this.walletAddress(),
        notes: deploymentNotes
      }));

      const manifestJson = JSON.stringify(artifact.manifest);
      transactionId = await this.deployOrUpdateContract(
        session.network,
        artifact,
        this.preparedNefHex,
        manifestJson
      );

      this.deployStatus = 'Saving submitted transaction...';
      await firstValueFrom(this.api.markDeploymentSubmitted(
        this.projectId,
        attempt.id,
        transactionId,
        this.walletAddress()
      ));

      this.deployStatus = 'Confirming the transaction on Neo...';
      await this.waitForTransaction(session.network, transactionId, attempt.operation);
      await firstValueFrom(this.api.confirmDeploymentAttempt(this.projectId, attempt.id, this.walletAddress()));

      await this.router.navigate(['/projects', this.projectId]);
    } catch (error) {
      this.errorMessage = this.getErrorMessage(error);
      if (attempt && !transactionId && this.walletAddress()) {
        const stage = this.deployStatus.includes('wallet') ? 'wallet' : 'preparing';
        void firstValueFrom(this.api.markDeploymentFailed(
          this.projectId,
          attempt.id,
          this.walletAddress(),
          stage,
          this.errorMessage
        ));
      }
    } finally {
      this.isSaving = false;
      this.deployStatus = '';
    }
  }

  async reviewRelease(): Promise<void> {
    this.errorMessage = '';
    this.feeEstimateError = '';
    this.feeEstimate = null;
    this.mainnetConfirmed = false;

    const artifact = this.selectedArtifact;
    const session = this.wallet.session();

    if (!artifact) {
      this.errorMessage = 'Choose an artifact version.';
      return;
    }

    if (!session || !this.walletAddress()) {
      this.errorMessage = 'Connect a wallet before reviewing a release.';
      return;
    }

    const ownershipError = this.ownership.managementError(this.overview?.project, this.walletAddress());
    if (ownershipError) {
      this.errorMessage = ownershipError;
      return;
    }

    const target = this.getExistingDeployment(session.network)?.contractHash;
    this.isPreparingReview = true;

    try {
      const nefHex = await firstValueFrom(this.api.getArtifactNefHex(artifact.id));
      this.ensureValidNef(nefHex);
      this.preparedNefHex = nefHex;
      this.preparedArtifactId = artifact.id;
      this.isReviewing = true;

      try {
        this.feeEstimate = await this.wallet.estimateDeploymentFees(
          session.network,
          target ? 'update' : 'deploy',
          nefHex,
          JSON.stringify(artifact.manifest),
          target ?? undefined
        );
      } catch (error) {
        this.feeEstimateError = this.getErrorMessage(error);
      }
    } catch (error) {
      this.errorMessage = this.getErrorMessage(error);
    } finally {
      this.isPreparingReview = false;
    }
  }

  editRelease(): void {
    this.isReviewing = false;
    this.mainnetConfirmed = false;
    this.feeEstimate = null;
    this.feeEstimateError = '';
    this.preparedNefHex = '';
    this.preparedArtifactId = '';
  }

  private getErrorMessage(error: unknown): string {
    const rpcException = this.findRpcException(error);
    if (rpcException) {
      const contractAlreadyExists = /^Contract Already Exists:\s*(.+)$/i.exec(rpcException);

      return contractAlreadyExists
        ? `This contract is already deployed on the selected network (${contractAlreadyExists[1]}). Add or recover that deployment before trying to deploy this artifact again.`
        : `Neo rejected the deployment: ${rpcException}`;
    }

    return this.errors.format(error, 'Could not deploy or update contract.');
  }

  private findRpcException(error: unknown): string | null {
    if (!error || typeof error !== 'object') {
      return null;
    }

    const response = error as {
      data?: { exception?: unknown };
      exception?: unknown;
    };
    const exception = response.data?.exception ?? response.exception;

    return typeof exception === 'string' && exception.trim()
      ? exception.trim()
      : null;
  }

  get updateMode(): boolean {
    return Boolean(this.deploymentHistory.latestForNetwork(
      this.overview?.deployments ?? [],
      this.walletNetwork()
    ));
  }

  private ensureValidNef(nefHex: string): void {
    const nefMagic = '4e454633';

    if (!nefHex.toLowerCase().startsWith(nefMagic)) {
      throw new Error(`The stored NEF file is invalid. Expected ${nefMagic}, got ${nefHex.slice(0, 8) || 'empty'}. Upload the compiled .nef file again.`);
    }
  }

  private getExistingDeployment(network: string) {
    return this.deploymentHistory.latestForNetwork(this.overview?.deployments ?? [], network);
  }

  private async deployOrUpdateContract(
    network: NetworkType,
    artifact: Artifact,
    nefHex: string,
    manifestJson: string
  ): Promise<string> {
    if (!isPusharooNetwork(network)) {
      throw new Error(`Pusharoo does not support ${network}. Use Neo N3 testnet or mainnet.`);
    }

    const deployments = this.overview?.deployments ?? [];
    const existingDeployment = this.deploymentHistory.latestForNetwork(deployments, network);
    const networkDeployments = this.deploymentHistory.forNetwork(deployments, network);
    const submittedAttempt = networkDeployments.find((deployment) =>
      ['submitted', 'confirming'].includes(deployment.status) && deployment.transactionId
    );
    const incompleteLegacyDeployment = networkDeployments.find((deployment) =>
      (!deployment.status || deployment.status === 'confirmed') && !deployment.contractHash
    );

    this.deployStatus = 'Waiting for wallet approval...';

    if (existingDeployment?.contractHash) {
      const transactionId = await this.wallet.updateContract(
        network,
        existingDeployment.contractHash,
        nefHex,
        manifestJson,
        artifact.contractName
      );

      return transactionId;
    }

    if (submittedAttempt) {
      throw new Error(
        `A ${network} deployment transaction is already submitted but not confirmed. ` +
        'Open Deployments and select Resume Confirmation instead of creating another transaction.'
      );
    }

    if (incompleteLegacyDeployment) {
      throw new Error(`A deployment already exists on ${network}, but it has no contract hash. Pusharoo cannot update without the deployed contract hash.`);
    }

    const transactionId = await this.wallet.deployContract(
      network,
      nefHex,
      manifestJson,
      artifact.contractName
    );

    return transactionId;
  }

  private async waitForTransaction(network: NetworkType, transactionId: string, operation: Deployment['operation']): Promise<void> {
    if (operation === 'update') {
      await this.neoRpc.waitForHalt(network, transactionId);
      return;
    }

    await this.neoRpc.waitForDeployment(
      network,
      transactionId,
      this.runtimeConfig.value.wallet.contractManagement[network]
    );
  }
}
