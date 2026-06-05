import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-artifact-upload',
  imports: [FormsModule, PageShellComponent, RouterLink],
  templateUrl: './artifact-upload.component.html',
  styleUrl: './artifact-upload.component.scss'
})
export class ArtifactUploadComponent implements OnInit {
  version = '0.1.0';
  notes = 'Initial testnet build';
  nefFile: File | null = null;
  manifestFile: File | null = null;
  isUploading = false;
  errorMessage = '';
  latestVersion = '';
  suggestedVersion = '';
  existingVersions: string[] = [];
  readonly projectId: string;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.api.getProjectOverview(this.projectId).subscribe((overview) => {
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

  onManifestSelected(event: Event): void {
    this.manifestFile = this.getFile(event);
  }

  upload(): void {
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

    this.isUploading = true;
    this.api
      .uploadArtifact(
        this.projectId,
        this.version.trim(),
        this.notes,
        this.nefFile,
        this.manifestFile
      )
      .subscribe({
        next: () => {
          this.isUploading = false;
          void this.router.navigate(['/projects', this.projectId]);
        },
        error: () => {
          this.isUploading = false;
          this.errorMessage = 'Could not upload artifact.';
        }
      });
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
