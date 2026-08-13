import { AsyncPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
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
  searchTerm = '';
  statusFilter: 'all' | 'deployed' | 'not-deployed' = 'all';
  sortOrder: 'recent' | 'name' = 'recent';
  page = 1;
  readonly pageSize = 9;

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
      await firstValueFrom(this.api.createProject(name, this.newProjectDescription, signature));
      this.cancelCreateProject();
      this.loadProjects();
    } catch (error) {
      this.errorMessage = this.getCreateProjectErrorMessage(error);
    } finally {
      this.isSaving = false;
    }
  }

  deploymentNetworkSummary(item: ProjectCardViewModel): string {
    const networks = [...new Set(item.deployments.map((deployment) => deployment.network))];

    return networks.length > 0 ? networks.join(', ') : 'Not deployed';
  }

  visibleProjects(projects: ProjectCardViewModel[]): ProjectCardViewModel[] {
    const query = this.searchTerm.trim().toLowerCase();
    const filtered = projects.filter((item) => {
      const matchesQuery = !query || [item.project.name, item.project.description ?? '']
        .some((value) => value.toLowerCase().includes(query));
      const matchesStatus = this.statusFilter === 'all'
        || (this.statusFilter === 'deployed' ? item.deployed : !item.deployed);

      return matchesQuery && matchesStatus;
    });
    const sorted = [...filtered].sort((left, right) => this.sortOrder === 'name'
      ? left.project.name.localeCompare(right.project.name)
      : new Date(right.project.createdAt).getTime() - new Date(left.project.createdAt).getTime());
    const lastIndex = this.page * this.pageSize;

    return sorted.slice(lastIndex - this.pageSize, lastIndex);
  }

  totalPages(projects: ProjectCardViewModel[]): number {
    const query = this.searchTerm.trim().toLowerCase();
    const count = projects.filter((item) => {
      const matchesQuery = !query || [item.project.name, item.project.description ?? '']
        .some((value) => value.toLowerCase().includes(query));
      return matchesQuery && (this.statusFilter === 'all' || (this.statusFilter === 'deployed' ? item.deployed : !item.deployed));
    }).length;
    return Math.max(1, Math.ceil(count / this.pageSize));
  }

  updateFilters(): void {
    this.page = 1;
  }

  changePage(projects: ProjectCardViewModel[], direction: number): void {
    this.page = Math.min(Math.max(1, this.page + direction), this.totalPages(projects));
  }

  creatorSummary(item: ProjectCardViewModel): string {
    const address = item.project.createdByWalletAddress;

    return address ? `${address.slice(0, 6)}...${address.slice(-4)}` : 'Legacy';
  }

  private loadProjects(): void {
    this.projects$ = this.api.getProjectCards();
  }

  private getCreateProjectErrorMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const apiError = error.error;
      if (apiError && typeof apiError === 'object' && typeof apiError.error === 'string') {
        return apiError.error;
      }

      return `Could not create project${error.status ? ` (HTTP ${error.status})` : ''}.`;
    }

    return error instanceof Error
      ? error.message
      : 'Could not create project.';
  }
}
