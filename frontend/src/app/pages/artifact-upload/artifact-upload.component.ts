import {
  DestroyRef,
  Component,
  inject,
  OnInit,
  ElementRef,
  QueryList,
  ViewChildren
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { workspaceRoute, contractLink } from '../../services/workspace-route';
import { firstValueFrom } from 'rxjs';
import { ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { ContractManifestAnalyzerService } from '../../services/contract-manifest-analyzer.service';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { WalletAuthService } from '../../services/wallet-auth.service';
import { PageShellComponent } from '../page-shell/page-shell.component';
import { ProjectReleaseNavComponent } from '../../components/project-release-nav/project-release-nav.component';

@Component({
  selector: 'app-artifact-upload',
  imports: [FormsModule, PageShellComponent, ProjectReleaseNavComponent, RouterLink],
  templateUrl: './artifact-upload.component.html',
  styleUrl: './artifact-upload.component.scss'
})
export class ArtifactUploadComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  private loadGeneration = 0;
  @ViewChildren('artifactFile') private artifactFiles?: QueryList<ElementRef<HTMLInputElement>>;
  contractId: string | null = null;
  contractPath(...segments: string[]): string[] {
    return contractLink(this.projectId, this.contractId, ...segments);
  }
  overview: ProjectOverviewViewModel | null = null;
  version = '0.1.0';
  notes = 'Initial testnet build';
  nefFile: File | null = null;
  manifestFile: File | null = null;
  isUploading = false;
  isLoading = true;
  loadError = '';
  errorMessage = '';
  manifestWarning = '';
  latestVersion = '';
  suggestedVersion = '';
  existingVersions: string[] = [];
  projectId = '';
  private uploadAttempt: {
    version: string;
    notes: string;
    nef: File;
    manifest: File;
    key: string;
  } | null = null;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly manifestAnalyzer: ContractManifestAnalyzerService,
    private readonly ownership: ProjectOwnershipService,
    private readonly auth: WalletAuthService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    workspaceRoute(this.route)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(scope => {
        this.projectId = scope.projectId;
        this.contractId = scope.contractId;
        this.overview = null;
        this.version = '0.1.0';
        this.notes = '';
        this.artifactFiles?.forEach(file => (file.nativeElement.value = ''));
        this.nefFile = null;
        this.manifestFile = null;
        this.manifestWarning = '';
        this.uploadAttempt = null;
        this.errorMessage = '';
        this.isUploading = false;
        this.existingVersions = [];
        this.latestVersion = '';
        this.suggestedVersion = '';
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
    (this.contractId
      ? this.api.getContractOverview(this.projectId, this.contractId)
      : this.api.getProjectOverview(this.projectId)
    ).subscribe({
      next: overview => {
        if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
        this.overview = overview;
        const artifacts = overview?.artifacts ?? [];
        this.existingVersions = artifacts.map(artifact => artifact.version);
        this.latestVersion = artifacts[0]?.version ?? '';
        this.suggestedVersion = this.latestVersion
          ? this.getNextVersion(this.latestVersion)
          : this.version;
        this.version = this.suggestedVersion;
        this.isLoading = false;
      },
      error: error => {
        if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
        this.overview = null;
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  get versionAlreadyExists(): boolean {
    return this.existingVersions.some(existingVersion =>
      this.isSameVersion(existingVersion, this.version)
    );
  }

  get canManageProject(): boolean {
    return this.ownership.canManage(this.overview?.project, this.wallet.account()?.address ?? '');
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

    const file = this.manifestFile;
    const generation = this.loadGeneration;
    const warning = await this.manifestAnalyzer.getUpdateWarning(file);
    if (
      generation === this.loadGeneration &&
      this.manifestFile === file &&
      !this.destroyRef.destroyed
    )
      this.manifestWarning = warning;
  }

  async upload(): Promise<void> {
    const generation = this.loadGeneration;
    const projectId = this.projectId;
    const contractId = this.contractId;
    this.errorMessage = '';

    if (!this.version.trim()) {
      this.errorMessage = 'Version is required.';
      return;
    }

    if (this.versionAlreadyExists) {
      this.errorMessage = `Version ${this.version.trim()} already exists for this contract.`;
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
      await this.auth.ensureAuthenticated();
      if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
      const version = this.version.trim();
      if (
        !this.uploadAttempt ||
        this.uploadAttempt.version !== version ||
        this.uploadAttempt.notes !== this.notes ||
        this.uploadAttempt.nef !== nefFile ||
        this.uploadAttempt.manifest !== manifestFile
      ) {
        this.uploadAttempt = {
          version,
          notes: this.notes,
          nef: nefFile,
          manifest: manifestFile,
          key: crypto.randomUUID()
        };
      }
      await firstValueFrom(
        this.api.uploadArtifact(
          projectId,
          version,
          this.notes,
          nefFile,
          manifestFile,
          this.uploadAttempt.key,
          contractId
        )
      );
      if (generation === this.loadGeneration && !this.destroyRef.destroyed)
        await this.router.navigate(contractLink(projectId, contractId, 'artifacts'));
    } catch (error) {
      if (generation !== this.loadGeneration || this.destroyRef.destroyed) return;
      this.errorMessage = this.errors.format(error, 'Could not upload artifact.');
    } finally {
      if (generation === this.loadGeneration && !this.destroyRef.destroyed)
        this.isUploading = false;
    }
  }

  private getFile(event: Event): File | null {
    const input = event.target as HTMLInputElement;
    return input.files?.[0] ?? null;
  }

  private getNextVersion(version: string): string {
    const trimmedVersion = version.trim();
    const prefix =
      trimmedVersion.startsWith('v') || trimmedVersion.startsWith('V') ? trimmedVersion[0] : '';
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
