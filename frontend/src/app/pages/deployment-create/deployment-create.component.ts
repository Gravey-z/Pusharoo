import { Component, OnInit, computed, effect } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { NetworkType } from 'neo-n3-walletkit';
import { firstValueFrom, forkJoin } from 'rxjs';
import { isPusharooNetwork } from '../../config/wallet.config';
import { Artifact, Deployment, DeploymentAuthorizationChallenge, DeploymentDataValue, ProjectAuthorizedDeployer, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { DeploymentHistoryService } from '../../services/deployment-history.service';
import { DeploymentAttemptCapabilityService } from '../../services/deployment-attempt-capability.service';
import { NeoRpcService } from '../../services/neo-rpc.service';
import { ProjectDeploymentAccessService } from '../../services/project-deployment-access.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { RuntimeConfigService } from '../../services/runtime-config.service';
import { DeploymentFeeEstimate, WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';
import { DeploymentDataEditorComponent, EditableDeploymentData } from '../../components/deployment-data-editor/deployment-data-editor.component';
import { DeploymentDataService } from '../../services/deployment-data.service';

@Component({
  selector: 'app-deployment-create',
  imports: [FormsModule, PageShellComponent, ProjectReleaseNavComponent, DeploymentDataEditorComponent, RouterLink],
  templateUrl: './deployment-create.component.html',
  styleUrl: './deployment-create.component.scss'
})
export class DeploymentCreateComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  artifacts: Artifact[] = [];
  artifactId = '';
  notes = '';
  deploymentDataMode: 'none' | 'custom' = 'none';
  deploymentDataDraft: EditableDeploymentData = { type: 'Any', value: null };
  deploymentDataError = '';
  errorMessage = '';
  deployStatus = '';
  isSaving = false;
  isLoading = true;
  loadError = '';
  isPreparingReview = false;
  isReviewing = false;
  mainnetConfirmed = false;
  feeEstimate: DeploymentFeeEstimate | null = null;
  feeEstimateError = '';
  deploymentSimulationState: 'not-run' | 'passed' | 'fault' | 'unavailable' = 'not-run';
  deploymentSimulationMessage = '';
  authorizationPreview: DeploymentAuthorizationChallenge | null = null;
  authorizedDeployers: ProjectAuthorizedDeployer[] = [];
  private deniedDeploymentAttempt: { walletAddress: string; network: string } | null = null;
  private preparedNefHex = '';
  private preparedArtifactId = '';
  private reviewedContext: { artifactId: string; wallet: string; network: string; operation: 'deploy' | 'update'; target: string | null; revision: number } | null = null;
  private reviewGeneration = 0;
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

  get reviewedContextIsCurrent(): boolean {
    const context = this.reviewedContext;
    return Boolean(context && this.contextMatches(context));
  }

  get formattedReviewedDeploymentData(): string {
    return JSON.stringify(this.authorizationPreview?.deploymentData ?? { type: 'Any', value: null }, null, 2);
  }

  get deploymentAccess() {
    return this.deploymentAccessService.resolve(this.overview?.project, this.authorizedDeployers, this.walletAddress());
  }

  get canDeployOnSelectedNetwork(): boolean {
    return this.deploymentAccessService.canDeployToNetwork(this.deploymentAccess, this.walletNetwork());
  }

  get deploymentPermissionMessage(): string {
    const network = this.walletNetwork();
    if (!this.walletAddress() || !network) {
      return 'Connect the wallet assigned to the target N3 network before continuing.';
    }
    if (!this.canDeployOnSelectedNetwork) {
      return `Connected wallet is not authorized to deploy on ${this.deploymentAccessService.networkLabel(network)}.`;
    }
    if (this.deploymentAccess.isAuthorizedDeployer) {
      return this.deploymentAccessService.description(this.deploymentAccess);
    }
    return 'Connected wallet can deploy on the selected network.';
  }

  get showDeploymentAccessNotice(): boolean {
    return Boolean(
      this.deniedDeploymentAttempt &&
      this.deniedDeploymentAttempt.walletAddress === this.walletAddress() &&
      this.deniedDeploymentAttempt.network === this.walletNetwork() &&
      !this.canDeployOnSelectedNetwork
    );
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
    private readonly attemptCapabilities: DeploymentAttemptCapabilityService,
    private readonly neoRpc: NeoRpcService,
    private readonly deploymentAccessService: ProjectDeploymentAccessService,
    private readonly runtimeConfig: RuntimeConfigService,
    readonly wallet: WalletService,
    private readonly deploymentDataService: DeploymentDataService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
    let previousWalletContext = `${this.walletAddress()}|${this.walletNetwork()}`;
    effect(() => {
      const walletContext = `${this.walletAddress()}|${this.walletNetwork()}`;
      if (walletContext !== previousWalletContext) {
        previousWalletContext = walletContext;
        if (this.isReviewing || this.isPreparingReview || this.authorizationPreview || this.feeEstimate) this.editRelease();
      }
    });
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
        this.artifacts = overview.artifacts ?? [];
        this.artifactId = this.artifacts[0]?.id ?? '';
        this.isLoading = false;
      },
      error: (error) => {
        this.overview = null;
        this.artifacts = [];
        this.authorizedDeployers = [];
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  async save(): Promise<void> {
    this.errorMessage = '';
    this.deployStatus = '';

    if (!this.isReviewing || !this.preparedNefHex || this.preparedArtifactId !== this.artifactId) {
      this.errorMessage = 'Review this release before opening the wallet.';
      return;
    }

    if (!this.reviewedContextIsCurrent) {
      this.errorMessage = 'The wallet, network, artifact, or deployment action changed. Review this release again.';
      return;
    }

    if (this.operation === 'deploy' && this.deploymentSimulationState === 'fault') {
      this.errorMessage = 'The contract initialization simulation failed. Correct the deployment data or signer before submitting.';
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

    const deploymentPermissionError = this.getDeploymentPermissionError();
    if (deploymentPermissionError) {
      return;
    }

    if (session.network === 'neo3:mainnet' && !this.mainnetConfirmed) {
      this.errorMessage = 'Confirm the N3:Mainnet warning before opening the wallet.';
      return;
    }

    this.isSaving = true;
    let attempt: Deployment | null = null;
    let attemptCapability = '';
    let transactionId = '';

    try {
      const deploymentNotes = this.notes.trim() || null;
      const authorizationContext = this.wallet.createDeploymentAuthorizationContext();
      this.deployStatus = 'Authorizing deployment attempt...';
      const authorizationChallenge = await firstValueFrom(this.api.createDeploymentAuthorizationChallenge(this.projectId, {
        artifactId: this.artifactId,
        network: session.network,
        deployedBy: this.walletAddress(),
        notes: deploymentNotes,
        ...(this.reviewedContext?.operation === 'deploy' ? { deploymentData: this.authorizationPreview?.deploymentData } : {}),
        ...authorizationContext
      }));
      const reviewed = this.authorizationPreview;
      if (!reviewed || authorizationChallenge.deploymentDataSha256 !== reviewed.deploymentDataSha256 ||
          authorizationChallenge.expectedDeploymentRevision !== reviewed.expectedDeploymentRevision ||
          authorizationChallenge.operation !== reviewed.operation ||
          authorizationChallenge.expectedTargetContractHash !== reviewed.expectedTargetContractHash) {
        this.errorMessage = 'This release changed after review. Edit the release and review it again before opening the wallet.';
        return;
      }
      const authorization = await this.wallet.signDeploymentAuthorization(
        authorizationChallenge.message,
        authorizationContext
      );

      this.deployStatus = 'Creating deployment attempt...';
      attempt = await firstValueFrom(this.api.startDeploymentAttempt(this.projectId, {
        artifactId: this.artifactId,
        network: session.network,
        deployedBy: this.walletAddress(),
        notes: deploymentNotes,
        authorization,
        ...(this.reviewedContext?.operation === 'deploy' ? { deploymentData: this.authorizationPreview?.deploymentData } : {})
      }));
      attemptCapability = attempt.attemptCapability ?? '';
      if (!attemptCapability) {
        throw new Error('Pusharoo did not return a deployment attempt capability. Start the release again.');
      }
      this.attemptCapabilities.set(attempt.id, attemptCapability);
      if (this.reviewedContext?.operation === 'deploy' &&
          (attempt.deploymentDataSha256 !== this.authorizationPreview?.deploymentDataSha256 ||
           attempt.deploymentDataFormatVersion !== this.authorizationPreview?.deploymentDataFormatVersion)) {
        throw new Error('The deployment attempt did not retain the data from the reviewed release. The attempt was stopped before opening the wallet.');
      }

      const manifestJson = JSON.stringify(artifact.manifest);
      transactionId = await this.deployOrUpdateContract(
        session.network,
        artifact,
        this.preparedNefHex,
        manifestJson,
        attempt.deploymentData ?? { type: 'Any', value: null }
      );

      this.deployStatus = 'Saving submitted transaction...';
      const submitted = await firstValueFrom(this.api.markDeploymentSubmitted(
        this.projectId,
        attempt.id,
        transactionId,
        attemptCapability
      ));
      attemptCapability = submitted.attemptCapability ?? '';
      if (!attemptCapability) {
        throw new Error('Pusharoo did not return the next deployment attempt capability. Start the release again.');
      }
      this.attemptCapabilities.set(attempt.id, attemptCapability);

      this.deployStatus = 'Confirming the transaction on Neo...';
      await this.waitForTransaction(session.network, transactionId, attempt.operation);
      await firstValueFrom(this.api.confirmDeploymentAttempt(this.projectId, attempt.id, attemptCapability));
      this.attemptCapabilities.remove(attempt.id);

      await this.router.navigate(['/projects', this.projectId]);
    } catch (error) {
      this.errorMessage = this.getErrorMessage(error);
      if (attempt && !transactionId && attemptCapability) {
        const stage = this.deployStatus.includes('wallet') ? 'wallet' : 'preparing';
        void firstValueFrom(this.api.markDeploymentFailed(
          this.projectId,
          attempt.id,
          attemptCapability,
          stage,
          this.errorMessage
        )).then(() => this.attemptCapabilities.remove(attempt!.id));
      }
    } finally {
      this.isSaving = false;
      this.deployStatus = '';
    }
  }

  async reviewRelease(): Promise<void> {
    this.reviewGeneration++;
    this.errorMessage = '';
    this.feeEstimateError = '';
    this.feeEstimate = null;
    this.mainnetConfirmed = false;
    this.deploymentDataError = '';
    this.deploymentSimulationState = 'not-run';
    this.deploymentSimulationMessage = '';

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

    const deploymentPermissionError = this.getDeploymentPermissionError();
    if (deploymentPermissionError) {
      return;
    }

    let deploymentData: DeploymentDataValue;
    try {
      deploymentData = this.getDeploymentDataForReview();
    } catch (error) {
      this.deploymentDataError = error instanceof Error ? error.message : 'Enter valid deployment data.';
      return;
    }

    const reviewContext = {
      artifactId: artifact.id,
      wallet: this.walletAddress(),
      network: session.network,
      operation: this.operation,
      target: this.targetContract,
      revision: this.reviewGeneration
    };

    this.isPreparingReview = true;

    try {
      const authorizationContext = this.wallet.createDeploymentAuthorizationContext();
      const authorizationPreview = await firstValueFrom(this.api.createDeploymentAuthorizationChallenge(this.projectId, {
        artifactId: artifact.id,
        network: session.network,
        deployedBy: this.walletAddress(),
        notes: this.notes.trim() || null,
        ...(this.operation === 'deploy' ? { deploymentData } : {}),
        ...authorizationContext
      }));
      if (!this.contextMatches(reviewContext)) {
        this.errorMessage = 'The wallet, network, artifact, or deployment action changed while preparing review. Review the release again.';
        return;
      }
      this.authorizationPreview = JSON.parse(JSON.stringify(authorizationPreview)) as DeploymentAuthorizationChallenge;
      const nefHex = await firstValueFrom(this.api.getArtifactNefHex(artifact.id));
      if (!this.contextMatches(reviewContext)) {
        this.errorMessage = 'The wallet, network, artifact, or deployment action changed while preparing review. Review the release again.';
        return;
      }
      this.ensureValidNef(nefHex);
      this.preparedNefHex = nefHex;
      this.preparedArtifactId = artifact.id;
      this.reviewedContext = reviewContext;
      if (authorizationPreview.operation === 'deploy') {
        try {
          const simulation = await this.wallet.simulateContractDeployment(
            session.network,
            nefHex,
            JSON.stringify(artifact.manifest),
            authorizationPreview.deploymentData
          );
          if (!this.contextMatches(reviewContext)) {
            this.errorMessage = 'The wallet, network, artifact, or deployment action changed during initialization simulation. Review the release again.';
            return;
          }
          if (simulation.state === 'HALT') {
            this.deploymentSimulationState = 'passed';
            this.deploymentSimulationMessage = 'Contract initialization simulation passed. The chain state may change before the transaction is submitted.';
          } else if (simulation.state === 'FAULT') {
            this.deploymentSimulationState = 'fault';
            this.deploymentSimulationMessage = simulation.exception?.trim()
              ? `Contract initialization failed: ${simulation.exception.trim()}`
              : 'Contract initialization failed during Neo VM simulation.';
          } else {
            this.deploymentSimulationState = 'unavailable';
            this.deploymentSimulationMessage = `Neo returned an unrecognized simulation state (${simulation.state || 'empty'}). Review the contract initialization before submitting.`;
          }
        } catch (error) {
          if (this.contextMatches(reviewContext)) {
            this.deploymentSimulationState = 'unavailable';
            this.deploymentSimulationMessage = `Initialization simulation is unavailable: ${this.getErrorMessage(error)}`;
          }
        }
      }
      if (!this.contextMatches(reviewContext)) return;
      this.isReviewing = true;

      if (this.deploymentSimulationState === 'fault') {
        this.feeEstimateError = 'Fee estimation was skipped because contract initialization failed during simulation.';
      } else {
        try {
          const feeEstimate = await this.wallet.estimateDeploymentFees(
            session.network,
            authorizationPreview.operation,
            nefHex,
            JSON.stringify(artifact.manifest),
            authorizationPreview.expectedTargetContractHash ?? undefined,
            authorizationPreview.deploymentData
          );
          if (this.contextMatches(reviewContext)) this.feeEstimate = feeEstimate;
        } catch (error) {
          if (this.contextMatches(reviewContext)) this.feeEstimateError = this.getErrorMessage(error);
        }
      }
    } catch (error) {
      if (this.contextMatches(reviewContext)) this.errorMessage = this.getErrorMessage(error);
    } finally {
      if (this.contextMatches(reviewContext)) this.isPreparingReview = false;
    }
  }

  editRelease(): void {
    this.reviewGeneration++;
    this.isPreparingReview = false;
    this.isReviewing = false;
    this.mainnetConfirmed = false;
    this.feeEstimate = null;
    this.feeEstimateError = '';
    this.authorizationPreview = null;
    this.preparedNefHex = '';
    this.preparedArtifactId = '';
    this.reviewedContext = null;
    this.deploymentSimulationState = 'not-run';
    this.deploymentSimulationMessage = '';
  }

  private getDeploymentDataForReview(): DeploymentDataValue {
    if (this.operation === 'update' || this.deploymentDataMode === 'none') {
      return this.deploymentDataService.normalize({ type: 'Any', value: null });
    }
    return this.deploymentDataService.normalize(this.deploymentDataDraft);
  }

  private contextMatches(context: { artifactId: string; wallet: string; network: string; operation: 'deploy' | 'update'; target: string | null; revision: number }): boolean {
    return context.artifactId === this.artifactId && context.wallet === this.walletAddress() &&
      context.network === this.walletNetwork() && context.operation === this.operation && context.target === this.targetContract
      && context.revision === this.reviewGeneration;
  }

  onDeploymentDataChanged(): void {
    this.deploymentDataError = '';
  }

  private getDeploymentPermissionError(): string {
    if (!this.canDeployOnSelectedNetwork) {
      this.deniedDeploymentAttempt = {
        walletAddress: this.walletAddress(),
        network: this.walletNetwork()
      };
      return this.deploymentPermissionMessage;
    }

    this.deniedDeploymentAttempt = null;
    return '';
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
    manifestJson: string,
    deploymentData: DeploymentDataValue
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
      artifact.contractName,
      deploymentData
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
