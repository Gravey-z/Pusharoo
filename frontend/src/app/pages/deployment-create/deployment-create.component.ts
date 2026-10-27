import { DestroyRef, Component, inject, OnInit, computed, effect } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { workspaceRoute, contractLink } from '../../services/workspace-route';
import type { NetworkType } from 'neo-n3-walletkit';
import { firstValueFrom, forkJoin } from 'rxjs';
import { isPusharooNetwork } from '../../config/wallet.config';
import {
  Artifact,
  Deployment,
  DeploymentReview,
  DeploymentDataValue,
  ProjectAuthorizedDeployer,
  ProjectOverviewViewModel
} from '../../models/pusharoo.models';
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
import {
  DeploymentDataEditorComponent,
  EditableDeploymentData
} from '../../components/deployment-data-editor/deployment-data-editor.component';
import { DeploymentDataService } from '../../services/deployment-data.service';
import { WalletAuthService } from '../../services/wallet-auth.service';

@Component({
  selector: 'app-deployment-create',
  imports: [
    FormsModule,
    PageShellComponent,
    ProjectReleaseNavComponent,
    DeploymentDataEditorComponent,
    RouterLink
  ],
  templateUrl: './deployment-create.component.html',
  styleUrl: './deployment-create.component.scss'
})
export class DeploymentCreateComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  private loadGeneration = 0;
  contractId: string | null = null;
  contractPath(...segments: string[]): string[] {
    return contractLink(this.projectId, this.contractId, ...segments);
  }
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
  authorizationPreview: DeploymentReview | null = null;
  authorizedDeployers: ProjectAuthorizedDeployer[] = [];
  private deniedDeploymentAttempt: { walletAddress: string; network: string } | null = null;
  private preparedNefHex = '';
  private preparedArtifactId = '';
  private reviewedContext: {
    artifactId: string;
    wallet: string;
    network: string;
    operation: 'deploy' | 'update';
    target: string | null;
    revision: number;
  } | null = null;
  private reviewGeneration = 0;
  projectId = '';
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
    return this.artifacts.find(artifact => artifact.id === this.artifactId) ?? null;
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
    return JSON.stringify(
      this.authorizationPreview?.deploymentData ?? { type: 'Any', value: null },
      null,
      2
    );
  }

  get deploymentAccess() {
    return this.deploymentAccessService.resolve(
      this.overview?.project,
      this.authorizedDeployers,
      this.walletAddress()
    );
  }

  get canDeployOnSelectedNetwork(): boolean {
    return this.deploymentAccessService.canDeployToNetwork(
      this.deploymentAccess,
      this.walletNetwork()
    );
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
    private readonly auth: WalletAuthService,
    readonly wallet: WalletService,
    private readonly deploymentDataService: DeploymentDataService
  ) {
    let previousWalletContext = `${this.walletAddress()}|${this.walletNetwork()}`;
    effect(() => {
      const walletContext = `${this.walletAddress()}|${this.walletNetwork()}`;
      if (walletContext !== previousWalletContext) {
        previousWalletContext = walletContext;
        if (
          this.isReviewing ||
          this.isPreparingReview ||
          this.authorizationPreview ||
          this.feeEstimate
        )
          this.editRelease();
      }
    });
  }

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => this.editRelease());
    workspaceRoute(this.route)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(scope => {
        this.projectId = scope.projectId;
        this.contractId = scope.contractId;
        this.overview = null;
        this.editRelease();
        this.artifacts = [];
        this.artifactId = '';
        this.notes = '';
        this.deploymentDataMode = 'none';
        this.deploymentDataDraft = { type: 'Any', value: null };
        this.deploymentDataError = '';
        this.errorMessage = '';
        this.deployStatus = '';
        this.isSaving = false;
        this.authorizedDeployers = [];
        this.loadProject();
      });
  }

  retryLoad(): void {
    this.loadProject();
  }

  private loadProject(): void {
    const generation = ++this.loadGeneration;
    this.isLoading = true;
    this.loadError = '';
    forkJoin({
      overview: this.contractId
        ? this.api.getContractOverview(this.projectId, this.contractId)
        : this.api.getProjectOverview(this.projectId),
      authorizedDeployers: this.api.getAuthorizedDeployers(this.projectId)
    }).subscribe({
      next: ({ overview, authorizedDeployers }) => {
        if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
        this.overview = overview;
        this.authorizedDeployers = authorizedDeployers;
        this.artifacts = overview.artifacts ?? [];
        this.artifactId = this.artifacts[0]?.id ?? '';
        this.isLoading = false;
      },
      error: error => {
        if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
        this.overview = null;
        this.artifacts = [];
        this.authorizedDeployers = [];
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  async save(): Promise<void> {
    const generation = this.loadGeneration;
    const projectId = this.projectId;
    const contractId = this.contractId;
    const preparedNefHex = this.preparedNefHex;
    const originalReviewContext = this.reviewedContext;
    const reviewed = this.authorizationPreview;
    this.errorMessage = '';
    this.deployStatus = '';

    if (!this.isReviewing || !this.preparedNefHex || this.preparedArtifactId !== this.artifactId) {
      this.errorMessage = 'Review this release before opening the wallet.';
      return;
    }

    if (!this.reviewedContextIsCurrent) {
      this.errorMessage =
        'The wallet, network, artifact, or deployment action changed. Review this release again.';
      return;
    }

    if (this.operation === 'deploy' && this.deploymentSimulationState === 'fault') {
      this.errorMessage =
        'The contract initialization simulation failed. Correct the deployment data or signer before submitting.';
      return;
    }

    const artifact = this.artifacts.find(item => item.id === this.artifactId);
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
    let walletReturnedInvalidHash = false;
    let walletMayHaveBroadcast = false;

    try {
      const deploymentNotes = this.notes.trim() || null;
      this.deployStatus = 'Checking Pusharoo login...';
      await this.auth.ensureAuthenticated();
      if (
        generation !== this.loadGeneration ||
        this.destroyRef.destroyed ||
        !originalReviewContext ||
        !this.contextMatches(originalReviewContext)
      )
        throw new Error('The wallet or release changed. Review it again before deploying.');
      if (!reviewed) throw new Error('Review this release before deploying.');

      this.deployStatus = 'Creating deployment attempt...';
      attempt = await firstValueFrom(
        this.api.startDeploymentAttempt(projectId, {
          artifactId: artifact.id,
          broadcastTrackingVersion: 1,
          contractId: reviewed.contractId,
          network: session.network,
          notes: deploymentNotes,
          expectedOperation: reviewed.operation,
          expectedTargetContractHash: reviewed.expectedTargetContractHash,
          expectedDeploymentRevision: reviewed.expectedDeploymentRevision,
          artifactNefSha256: reviewed.artifactNefSha256,
          artifactManifestSha256: reviewed.artifactManifestSha256,
          notesSha256: reviewed.notesSha256,
          deploymentDataSha256: reviewed.deploymentDataSha256,
          ...(reviewed.operation === 'deploy' ? { deploymentData: reviewed.deploymentData } : {})
        })
      );
      attemptCapability = attempt.attemptCapability ?? '';
      if (!attemptCapability) {
        throw new Error(
          'Pusharoo did not return a deployment attempt capability. Start the release again.'
        );
      }
      if (
        attempt.projectId !== projectId ||
        (attempt.contractId ?? null) !== (artifact.contractId ?? null) ||
        attempt.artifactId !== artifact.id ||
        attempt.network !== session.network ||
        attempt.deployedBy !== this.walletAddress() ||
        attempt.operation !== reviewed.operation
      ) {
        throw new Error(
          'The deployment attempt does not match the reviewed release. The wallet was not opened.'
        );
      }
      if (
        attempt.deploymentDataSha256 !== reviewed.deploymentDataSha256 ||
        attempt.deploymentDataFormatVersion !== reviewed.deploymentDataFormatVersion
      ) {
        throw new Error(
          'The deployment attempt did not retain the data from the reviewed release. The attempt was stopped before opening the wallet.'
        );
      }
      if (
        generation !== this.loadGeneration ||
        this.destroyRef.destroyed ||
        !originalReviewContext ||
        !this.contextMatches(originalReviewContext) ||
        !this.auth.canUseSession()
      ) {
        throw new Error(
          'The wallet or Pusharoo login changed. The attempt was stopped before opening the wallet.'
        );
      }

      this.attemptCapabilities.set(attempt, attemptCapability);
      this.attemptCapabilities.setBroadcastPending(attempt);
      walletMayHaveBroadcast = true;
      const walletOpened = await firstValueFrom(
        this.api.markDeploymentWalletOpened(
          projectId,
          attempt.id,
          attemptCapability,
          attempt.contractId
        )
      );
      this.attemptCapabilities.assertParent(attempt, walletOpened);
      if (!walletOpened.broadcastOutcomeUnknown) {
        throw new Error(
          'Pusharoo did not retain the wallet transaction marker. The wallet was not opened.'
        );
      }

      if (
        generation !== this.loadGeneration ||
        this.destroyRef.destroyed ||
        !this.reviewedContextIsCurrent
      )
        throw new Error(
          'The selected contract or release changed. Recovery information was retained.'
        );

      const manifestJson = JSON.stringify(artifact.manifest);
      const walletTransactionId = await this.deployOrUpdateContract(
        session.network,
        artifact,
        preparedNefHex,
        manifestJson,
        attempt.deploymentData ?? { type: 'Any', value: null },
        attempt.operation,
        reviewed.expectedTargetContractHash ?? null
      );
      try {
        transactionId = this.requireTransactionId(walletTransactionId);
      } catch (error) {
        walletReturnedInvalidHash = true;
        throw error;
      }
      this.attemptCapabilities.setTransactionId(attempt, transactionId);

      if (generation === this.loadGeneration && !this.destroyRef.destroyed)
        this.deployStatus = 'Saving submitted transaction...';
      const submitted = await firstValueFrom(
        this.api.markDeploymentSubmitted(
          projectId,
          attempt.id,
          transactionId,
          attemptCapability,
          attempt.contractId
        )
      );
      this.attemptCapabilities.assertParent(attempt, submitted);
      if (!this.attemptCapabilities.matchesTransaction(submitted.transactionId, transactionId)) {
        throw new Error(
          'The recorded transaction does not match the wallet response. Recovery information was retained.'
        );
      }
      attemptCapability = submitted.attemptCapability ?? '';
      if (!attemptCapability) {
        throw new Error(
          'Pusharoo did not return the next deployment attempt capability. Start the release again.'
        );
      }
      this.attemptCapabilities.set(attempt, attemptCapability);

      if (generation === this.loadGeneration && !this.destroyRef.destroyed)
        this.deployStatus = 'Confirming the transaction on Neo...';
      await this.waitForTransaction(session.network, transactionId, attempt.operation);
      const confirmed = await firstValueFrom(
        this.api.confirmDeploymentAttempt(
          projectId,
          attempt.id,
          attemptCapability,
          attempt.contractId
        )
      );
      this.attemptCapabilities.assertParent(attempt, confirmed);
      this.attemptCapabilities.complete(confirmed);

      if (generation === this.loadGeneration && !this.destroyRef.destroyed)
        await this.router.navigate(contractLink(projectId, contractId, 'deployments'));
    } catch (error) {
      if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
      this.errorMessage =
        transactionId && attempt
          ? `Transaction ${transactionId} was submitted. Open Deployments and choose Resume Confirmation. ${this.getErrorMessage(error)}`
          : this.getErrorMessage(error);
      if (walletMayHaveBroadcast && !transactionId) {
        this.errorMessage +=
          ' The wallet outcome is uncertain. Recover the transaction by hash; this attempt was retained.';
      }
      if (
        attempt &&
        !transactionId &&
        !walletMayHaveBroadcast &&
        !walletReturnedInvalidHash &&
        attemptCapability
      ) {
        try {
          const failed = await firstValueFrom(
            this.api.markDeploymentFailed(
              projectId,
              attempt.id,
              attemptCapability,
              'preparing',
              this.errorMessage,
              attempt.contractId
            )
          );
          this.attemptCapabilities.assertParent(attempt, failed);
          this.attemptCapabilities.discardUnbroadcast(failed);
        } catch {
          this.errorMessage +=
            ' Open Deployments to reconcile the retained attempt with the same wallet.';
        }
      }
    } finally {
      if (generation === this.loadGeneration && !this.destroyRef.destroyed) {
        this.isSaving = false;
        this.deployStatus = '';
      }
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
      this.deploymentDataError =
        error instanceof Error ? error.message : 'Enter valid deployment data.';
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
      await this.auth.ensureAuthenticated();
      if (!this.contextMatches(reviewContext)) return;
      const authorizationPreview = await firstValueFrom(
        this.api.reviewDeployment(this.projectId, {
          artifactId: artifact.id,
          contractId: artifact.contractId,
          network: session.network,
          notes: this.notes.trim() || null,
          ...(this.operation === 'deploy' ? { deploymentData } : {})
        })
      );
      if (
        (authorizationPreview.contractId ?? null) !== (artifact.contractId ?? null) ||
        authorizationPreview.artifactId !== artifact.id ||
        authorizationPreview.network !== session.network
      ) {
        throw new Error(
          'Pusharoo returned a release review for a different artifact or network. Review again.'
        );
      }
      if (!this.contextMatches(reviewContext)) {
        this.errorMessage =
          'The wallet, network, artifact, or deployment action changed while preparing review. Review the release again.';
        return;
      }
      this.authorizationPreview = JSON.parse(
        JSON.stringify(authorizationPreview)
      ) as DeploymentReview;
      const nefHex = await firstValueFrom(this.api.getArtifactNefHex(artifact.id));
      if (!this.contextMatches(reviewContext)) {
        this.errorMessage =
          'The wallet, network, artifact, or deployment action changed while preparing review. Review the release again.';
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
            this.errorMessage =
              'The wallet, network, artifact, or deployment action changed during initialization simulation. Review the release again.';
            return;
          }
          if (simulation.state === 'HALT') {
            this.deploymentSimulationState = 'passed';
            this.deploymentSimulationMessage =
              'Contract initialization simulation passed. The chain state may change before the transaction is submitted.';
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
        this.feeEstimateError =
          'Fee estimation was skipped because contract initialization failed during simulation.';
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
          if (this.contextMatches(reviewContext))
            this.feeEstimateError = this.getErrorMessage(error);
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

  private contextMatches(context: {
    artifactId: string;
    wallet: string;
    network: string;
    operation: 'deploy' | 'update';
    target: string | null;
    revision: number;
  }): boolean {
    return (
      context.artifactId === this.artifactId &&
      context.wallet === this.walletAddress() &&
      context.network === this.walletNetwork() &&
      context.operation === this.operation &&
      context.target === this.targetContract &&
      context.revision === this.reviewGeneration
    );
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

    return typeof exception === 'string' && exception.trim() ? exception.trim() : null;
  }

  get updateMode(): boolean {
    return Boolean(
      this.deploymentHistory.latestForNetwork(
        this.overview?.deployments ?? [],
        this.walletNetwork(),
        this.contractId
      )
    );
  }

  private ensureValidNef(nefHex: string): void {
    const nefMagic = '4e454633';

    if (!nefHex.toLowerCase().startsWith(nefMagic)) {
      throw new Error(
        `The stored NEF file is invalid. Expected ${nefMagic}, got ${nefHex.slice(0, 8) || 'empty'}. Upload the compiled .nef file again.`
      );
    }
  }

  private requireTransactionId(value: unknown): string {
    if (typeof value !== 'string') {
      throw new Error(
        'The wallet did not return a transaction hash. Find it in the wallet or Neo explorer and recover it by transaction.'
      );
    }
    const transactionId = value.trim().toLowerCase();
    const normalized = /^[0-9a-f]{64}$/.test(transactionId) ? `0x${transactionId}` : transactionId;
    if (!/^0x[0-9a-f]{64}$/.test(normalized)) {
      throw new Error(
        'The wallet submitted a transaction but did not return a valid transaction hash. Find its hash in the wallet or Neo explorer and recover it by transaction.'
      );
    }
    return normalized;
  }

  private getExistingDeployment(network: string) {
    return this.deploymentHistory.latestForNetwork(
      this.overview?.deployments ?? [],
      network,
      this.contractId
    );
  }

  private async deployOrUpdateContract(
    network: NetworkType,
    artifact: Artifact,
    nefHex: string,
    manifestJson: string,
    deploymentData: DeploymentDataValue,
    operation: Deployment['operation'],
    targetContract: string | null
  ): Promise<string> {
    if (!isPusharooNetwork(network)) {
      throw new Error(`Pusharoo does not support ${network}. Use Neo N3 testnet or mainnet.`);
    }

    this.deployStatus = 'Waiting for wallet approval...';

    if (operation === 'update') {
      if (!targetContract)
        throw new Error('The reviewed update target is missing. Review the release again.');
      const transactionId = await this.wallet.updateContract(
        network,
        targetContract,
        nefHex,
        manifestJson,
        artifact.contractName
      );

      return transactionId;
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

  private async waitForTransaction(
    network: NetworkType,
    transactionId: string,
    operation: Deployment['operation']
  ): Promise<void> {
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
