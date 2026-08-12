import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Project } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-project-delete',
  imports: [FormsModule, PageShellComponent, RouterLink],
  templateUrl: './project-delete.component.html',
  styleUrl: './project-delete.component.scss'
})
export class ProjectDeleteComponent implements OnInit {
  project: Project | null = null;
  confirmationName = '';
  errorMessage = '';
  isDeleting = false;
  readonly projectId: string;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');

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
      this.project = overview?.project ?? null;
    });
  }

  get canDelete(): boolean {
    return Boolean(this.project && this.confirmationName.trim() === this.project.name && !this.isDeleting);
  }

  async deleteProject(): Promise<void> {
    this.errorMessage = '';
    if (!this.project || this.confirmationName.trim() !== this.project.name) {
      this.errorMessage = 'Type the exact project name to confirm deletion.';
      return;
    }

    if (!this.walletAddress()) {
      this.errorMessage = 'Connect the project owner wallet before deleting this project.';
      return;
    }

    this.isDeleting = true;
    try {
      const signature = await this.wallet.signProjectDeletion(this.project.id, this.project.name);
      await firstValueFrom(this.api.deleteProject(this.project.id, {
        projectName: this.project.name,
        signature
      }));
      await this.router.navigate(['/projects']);
    } catch (error) {
      this.errorMessage = this.getErrorMessage(error);
    } finally {
      this.isDeleting = false;
    }
  }

  private getErrorMessage(error: unknown): string {
    if (error && typeof error === 'object') {
      const apiError = error as { error?: { error?: unknown } };
      if (typeof apiError.error?.error === 'string' && apiError.error.error.trim()) {
        return apiError.error.error;
      }
    }

    return error instanceof Error && error.message ? error.message : 'Could not delete the project.';
  }
}
