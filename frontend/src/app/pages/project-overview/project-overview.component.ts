import { Component, OnInit } from '@angular/core';
import { AsyncPipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, map, switchMap } from 'rxjs';
import { ProjectOverviewViewModel } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

@Component({
  selector: 'app-project-overview',
  imports: [AsyncPipe, PageShellComponent, RouterLink],
  templateUrl: './project-overview.component.html',
  styleUrl: './project-overview.component.scss'
})
export class ProjectOverviewComponent implements OnInit {
  overview$!: Observable<ProjectOverviewViewModel | null>;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService
  ) {}

  ngOnInit(): void {
    this.overview$ = this.route.paramMap.pipe(
      map((params) => params.get('projectId') ?? ''),
      switchMap((projectId) => this.api.getProjectOverview(projectId))
    );
  }
}
