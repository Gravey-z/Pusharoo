import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ContractManifestAnalyzerService } from '../../services/contract-manifest-analyzer.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-artifact-upload',
  imports: [FormsModule, PageShellComponent, RouterLink],
  templateUrl: './artifact-upload.component.html',
  styleUrl: './artifact-upload.component.scss'
})
export class ArtifactUploadComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  version = '0.1.0';
  notes = 'Initial testnet build';
  nefFile: File | null = null;
  manifestFile: File | null = null;
  isUploading = false;
  errorMessage = '';
  manifestWarning = '';
  latestVersion = '';
  suggestedVersion = '';
  existingVersions: string[] = [];
  readonly projectId: string;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
    private readonly manifestAnalyzer: ContractManifestAnalyzerService,
    private readonly ownership: ProjectOwnershipService,
    readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.api.getProjectOverview(this.projectId).subscribe((overview) => {
      this.overview = overview;
      const artifacts = overview?.artifacts ?? [];
      this.existingVersions = artifacts.map((artifact) => artifact.version);
      this.latestVersion = artifacts[0]?.version ?? '';
      this.suggestedVersion = this.latestVersion
        ? this.getNextVersion(this.latestVersion)
        : this.version;
      this.version = this.suggestedVersion;
    });
  }

  get versionAlreadyExists(): boolean {
    return this.existingVersions.some((existingVersion) =>
      this.isSameVersion(existingVersion, this.version)
    );
  }

  onNefSelected(event: Event): void {
    this.nefFile = this.getFile(event);
  }

  async onManifestSelected(event: Event): Promise<void> {
    this.manifestFile = this.getFile(event);
    this.manifestWarning = '';

    if (!this.manifestFile) {
      return;
    }

    this.manifestWarning = await this.manifestAnalyzer.getUpdateWarning(this.manifestFile);
  }

  async upload(): Promise<void> {
    this.errorMessage = '';

    if (!this.version.trim()) {
      this.errorMessage = 'Version is required.';
      return;
    }

    if (this.versionAlreadyExists) {
      this.errorMessage = `Version ${this.version.trim()} already exists for this project.`;
      return;
    }

    if (!this.nefFile) {
      this.errorMessage = 'NEF file is required.';
      return;
    }

    if (!this.manifestFile) {
      this.errorMessage = 'Manifest file is required.';
      return;
    }

    const walletAddress = this.wallet.account()?.address ?? '';
    const ownershipError = this.ownership.managementError(this.overview?.project, walletAddress);

    if (ownershipError) {
      this.errorMessage = ownershipError;
      return;
    }

    const nefFile = this.nefFile;
    const manifestFile = this.manifestFile;
    this.isUploading = true;

    try {
      const signature = await this.wallet.signArtifactUpload(
        this.projectId,
        this.version.trim(),
        this.notes,
        nefFile,
        manifestFile
      );

      await firstValueFrom(this.api.uploadArtifact(
        this.projectId,
        this.version.trim(),
        this.notes,
        signature,
        nefFile,
        manifestFile
      ));
      await this.router.navigate(['/projects', this.projectId]);
    } catch (error) {
      this.errorMessage = this.getErrorMessage(error);
    } finally {
      this.isUploading = false;
    }
  }

  private getErrorMessage(error: unknown): string {
    if (error instanceof Error && error.message) {
      return error.message;
    }

    return 'Could not upload artifact.';
  }

  private getFile(event: Event): File | null {
    const input = event.target as HTMLInputElement;
    return input.files?.[0] ?? null;
  }

  private getNextVersion(version: string): string {
    const trimmedVersion = version.trim();
    const prefix = trimmedVersion.startsWith('v') || trimmedVersion.startsWith('V')
      ? trimmedVersion[0]
      : '';
    const versionWithoutPrefix = prefix ? trimmedVersion.slice(1) : trimmedVersion;
    const parts = versionWithoutPrefix.split('.');
    const lastPart = parts.at(-1);

    if (!lastPart || !/^\d+$/.test(lastPart)) {
      return prefix ? `${prefix}${versionWithoutPrefix}.1` : `${versionWithoutPrefix}.1`;
    }

    parts[parts.length - 1] = String(Number(lastPart) + 1);

    return `${prefix}${parts.join('.')}`;
  }

  private isSameVersion(left: string, right: string): boolean {
    return this.normalizeVersion(left) === this.normalizeVersion(right);
  }

  private normalizeVersion(version: string): string {
    return version.trim().replace(/^v/i, '');
  }
}
