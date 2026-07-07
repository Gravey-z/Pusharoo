import { Component, OnInit } from '@angular/core';
import { AsyncPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ProjectCardViewModel } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-projects',
  imports: [AsyncPipe, FormsModule, PageShellComponent, RouterLink],
  templateUrl: './projects.component.html',
  styleUrl: './projects.component.scss'
})
export class ProjectsComponent implements OnInit {
  projects$!: Observable<ProjectCardViewModel[]>;
  isCreating = false;
  isSaving = false;
  newProjectName = '';
  newProjectDescription = '';
  errorMessage = '';

  constructor(
    private readonly api: PusharooApiService,
    readonly wallet: WalletService
  ) {}

  ngOnInit(): void {
    this.loadProjects();
  }

  openCreateProject(): void {
    this.isCreating = true;
    this.errorMessage = '';
  }

  cancelCreateProject(): void {
    this.isCreating = false;
    this.newProjectName = '';
    this.newProjectDescription = '';
    this.errorMessage = '';
  }

  async createProject(): Promise<void> {
    const name = this.newProjectName.trim();
    if (!name) {
      this.errorMessage = 'Project name is required.';
      return;
    }

    if (!this.wallet.account()) {
      this.errorMessage = 'Connect a wallet before creating a project.';
      return;
    }

    this.isSaving = true;
    this.errorMessage = '';

    try {
      const signature = await this.wallet.signProjectCreation(name, this.newProjectDescription);
      this.api.createProject(name, this.newProjectDescription, signature).subscribe({
        next: () => {
          this.isSaving = false;
          this.cancelCreateProject();
          this.loadProjects();
        },
        error: () => {
          this.isSaving = false;
          this.errorMessage = 'Could not create project.';
        }
      });
    } catch (error) {
      this.isSaving = false;
      this.errorMessage = error instanceof Error
        ? error.message
        : 'Wallet signature was not completed.';
    }
  }

  deploymentNetworkSummary(item: ProjectCardViewModel): string {
    const networks = [...new Set(item.deployments.map((deployment) => deployment.network))];

    return networks.length > 0 ? networks.join(', ') : 'Not deployed';
  }

  private loadProjects(): void {
    this.projects$ = this.api.getProjectCards();
  }
}
