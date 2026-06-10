import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { walletConfig } from '../../config/wallet.config';
import { Artifact, Deployment, ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { NeoRpcService } from '../../services/neo-rpc.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-deployment-create',
  imports: [FormsModule, PageShellComponent, RouterLink],
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
    private readonly neoRpc: NeoRpcService,
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

    if (!this.artifactId) {
      this.errorMessage = 'Choose an artifact version.';
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

    this.isSaving = true;

    try {
      this.deployStatus = 'Preparing NEF file...';
      const nefHex = await firstValueFrom(this.api.getArtifactNefHex(this.artifactId));
      this.ensureValidNef(nefHex);

      const existingDeployment = this.getExistingDeployment(session.network);
      const networkDeployments = this.getNetworkDeployments(session.network);

      this.deployStatus = 'Waiting for wallet approval...';
      const manifestJson = JSON.stringify(artifact.manifest);
      let transactionId: string;
      let contractHash: string;

      if (existingDeployment?.contractHash) {
        contractHash = existingDeployment.contractHash;
        transactionId = await this.wallet.updateContract(
          session.network,
          contractHash,
          nefHex,
          manifestJson,
          artifact.contractName
        );

        this.deployStatus = 'Waiting for update confirmation...';
        await this.neoRpc.waitForHalt(session.network, transactionId);
      } else {
        if (networkDeployments.length > 0) {
          throw new Error(`A deployment already exists on ${session.network}, but it has no contract hash. Pusharoo cannot update without the deployed contract hash.`);
        }

        transactionId = await this.wallet.deployContract(
          session.network,
          nefHex,
          manifestJson,
          artifact.contractName
        );

        this.deployStatus = 'Waiting for deployment confirmation...';
        const confirmedDeployment = await this.neoRpc.waitForDeployment(
          session.network,
          transactionId,
          walletConfig.contractManagement[session.network]
        );
        contractHash = confirmedDeployment.contractHash;
      }

      this.deployStatus = 'Saving deployment record...';
      await firstValueFrom(this.api.createDeployment(this.projectId, {
        artifactId: this.artifactId,
        network: session.network,
        contractHash,
        transactionId,
        deployedBy: this.walletAddress(),
        notes: this.notes.trim() || null
      }));

      await this.router.navigate(['/projects', this.projectId]);
    } catch (error) {
      this.errorMessage = this.getErrorMessage(error);
    } finally {
      this.isSaving = false;
      this.deployStatus = '';
    }
  }

  private getErrorMessage(error: unknown): string {
    if (error instanceof Error && error.message) {
      return error.message;
    }

    return 'Could not deploy or update contract.';
  }

  get updateMode(): boolean {
    return Boolean(this.getExistingDeployment(this.walletNetwork()));
  }

  private ensureValidNef(nefHex: string): void {
    const nefMagic = '4e454633';

    if (!nefHex.toLowerCase().startsWith(nefMagic)) {
      throw new Error(`The stored NEF file is invalid. Expected ${nefMagic}, got ${nefHex.slice(0, 8) || 'empty'}. Upload the compiled .nef file again.`);
    }
  }

  private getExistingDeployment(network: string): Deployment | null {
    if (!network) {
      return null;
    }

    return this.getNetworkDeployments(network).find((deployment) => deployment.contractHash) ?? null;
  }

  private getNetworkDeployments(network: string): Deployment[] {
    return this.overview?.deployments.filter((deployment) => deployment.network === network) ?? [];
  }
}
