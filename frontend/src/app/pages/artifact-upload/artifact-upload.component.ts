import { Component } from '@angular/core';
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
export class ArtifactUploadComponent {
  version = '0.1.0';
  notes = 'Initial testnet build';
  nefFile: File | null = null;
  manifestFile: File | null = null;
  isUploading = false;
  errorMessage = '';
  readonly projectId: string;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
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
}
