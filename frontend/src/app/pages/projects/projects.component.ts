import { Component, OnInit } from '@angular/core';
import { AsyncPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ProjectCardViewModel } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
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

  constructor(private readonly api: PusharooApiService) {}

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

  createProject(): void {
    const name = this.newProjectName.trim();
    if (!name) {
      this.errorMessage = 'Project name is required.';
      return;
    }

    this.isSaving = true;
    this.api.createProject(name, this.newProjectDescription).subscribe({
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
  }

  private loadProjects(): void {
    this.projects$ = this.api.getProjectCards();
  }
}
