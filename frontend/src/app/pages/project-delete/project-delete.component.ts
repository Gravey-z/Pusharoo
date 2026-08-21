import { Component, OnInit, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Project } from '../../models/pusharoo.models';
import { ProjectOwnershipService } from '../../services/project-ownership.service';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
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
  isLoading = true;
  loadError = '';
  readonly projectId: string;
  readonly walletAddress = computed(() => this.wallet.account()?.address ?? '');

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: PusharooApiService,
    private readonly errors: ApiErrorFormatterService,
    private readonly ownership: ProjectOwnershipService,
    readonly wallet: WalletService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
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
    this.api.getProjectOverview(this.projectId).subscribe({
      next: (overview) => {
        this.project = overview?.project ?? null;
        this.isLoading = false;
      },
      error: (error) => {
        this.project = null;
        this.loadError = this.errors.format(error, 'Could not load this project.');
        this.isLoading = false;
      }
    });
  }

  get canDelete(): boolean {
    return Boolean(this.project && this.confirmationName.trim() === this.project.name && !this.isDeleting
      && this.ownership.canManage(this.project, this.walletAddress()));
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
      this.errorMessage = this.errors.format(error, 'Could not delete the project.');
    } finally {
      this.isDeleting = false;
    }
  }

}
