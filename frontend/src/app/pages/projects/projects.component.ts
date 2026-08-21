import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ProjectListItem } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { ApiErrorFormatterService } from '../../services/api-error-formatter.service';
import { WalletService } from '../../services/wallet.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-projects',
  imports: [FormsModule, PageShellComponent, RouterLink],
  templateUrl: './projects.component.html',
  styleUrl: './projects.component.scss'
})
export class ProjectsComponent implements OnInit {
  projects: ProjectListItem[] = [];
  isLoading = true;
  loadError = '';
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
    private readonly errors: ApiErrorFormatterService,
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
      this.errorMessage = this.errors.format(error, 'Could not create project.');
    } finally {
      this.isSaving = false;
    }
  }

  deploymentNetworkSummary(item: ProjectListItem): string {
    return item.deploymentNetworks.length > 0 ? item.deploymentNetworks.join(', ') : 'Not deployed';
  }

  visibleProjects(projects: ProjectListItem[]): ProjectListItem[] {
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

  totalPages(projects: ProjectListItem[]): number {
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

  changePage(projects: ProjectListItem[], direction: number): void {
    this.page = Math.min(Math.max(1, this.page + direction), this.totalPages(projects));
  }

  creatorSummary(item: ProjectListItem): string {
    const address = item.project.createdByWalletAddress;

    return address ? `${address.slice(0, 6)}...${address.slice(-4)}` : 'Legacy';
  }

  loadProjects(): void {
    this.isLoading = true;
    this.loadError = '';
    this.api.getProjectCards().subscribe({
      next: (projects) => {
        this.projects = projects;
        this.isLoading = false;
      },
      error: (error) => {
        this.projects = [];
        this.loadError = this.errors.format(error, 'Could not load projects.');
        this.isLoading = false;
      }
    });
  }
}
