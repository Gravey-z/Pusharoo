import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Artifact, ProjectOverviewViewModel } from '../../models/pusharoo.models';
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
  contractHash = '';
  notes = '';
  errorMessage = '';
  deployStatus = '';
  isSaving = false;
  readonly projectId: string;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');
  readonly walletNetwork = computed(() => this.wallet.session()?.network ?? '');

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
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

      this.deployStatus = 'Waiting for wallet approval...';
      const transactionId = await this.wallet.deployContract(
        session.network,
        nefHex,
        JSON.stringify(artifact.manifest),
        artifact.contractName
      );

      this.deployStatus = 'Saving deployment record...';
      await firstValueFrom(this.api.createDeployment(this.projectId, {
        artifactId: this.artifactId,
        network: session.network,
        contractHash: this.contractHash.trim() || null,
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

    return 'Could not deploy contract.';
  }

  private ensureValidNef(nefHex: string): void {
    const nefMagic = '4e454633';

    if (!nefHex.toLowerCase().startsWith(nefMagic)) {
      throw new Error(`The stored NEF file is invalid. Expected ${nefMagic}, got ${nefHex.slice(0, 8) || 'empty'}. Upload the compiled .nef file again.`);
    }
  }
}
