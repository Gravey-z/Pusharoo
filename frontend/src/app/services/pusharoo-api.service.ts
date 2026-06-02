import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { catchError, forkJoin, map, Observable, of, switchMap } from 'rxjs';
import {
  Artifact,
  Project,
  ProjectCardViewModel,
  ProjectOverviewViewModel
} from '../models/pusharoo.models';
import { demoArtifacts, demoProjectCards } from './demo-data';

@Injectable({ providedIn: 'root' })
export class PusharooApiService {
  private readonly apiBaseUrl = 'http://localhost:5000/api';

  constructor(private readonly http: HttpClient) {}

  getProjectCards(): Observable<ProjectCardViewModel[]> {
    return this.http.get<Project[]>(`${this.apiBaseUrl}/projects`).pipe(
      switchMap((projects) => {
        if (projects.length === 0) {
          return of(demoProjectCards);
        }

        return forkJoin(
          projects.map((project) =>
            this.getArtifacts(project.id).pipe(
              map((artifacts) => this.toProjectCard(project, artifacts))
            )
          )
        );
      }),
      catchError(() => of(demoProjectCards))
    );
  }

  getProjectOverview(projectId: string): Observable<ProjectOverviewViewModel | null> {
    const demoProjectCard = demoProjectCards.find((card) => card.project.id === projectId);
    if (demoProjectCard) {
      return of(demoProjectCard);
    }

    return this.http.get<Project>(`${this.apiBaseUrl}/projects/${projectId}`).pipe(
      switchMap((project) =>
        this.getArtifacts(project.id).pipe(
          map((artifacts) => this.toProjectCard(project, artifacts))
        )
      ),
      catchError(() => of(null))
    );
  }

  getArtifact(artifactId: string): Observable<Artifact | null> {
    const demoArtifact = demoArtifacts.find((artifact) => artifact.id === artifactId);
    if (demoArtifact) {
      return of(demoArtifact);
    }

    return this.http
      .get<Artifact>(`${this.apiBaseUrl}/artifacts/${artifactId}`)
      .pipe(catchError(() => of(null)));
  }

  createProject(name: string, description: string): Observable<Project> {
    return this.http.post<Project>(`${this.apiBaseUrl}/projects`, {
      name,
      description: description.trim() || null
    });
  }

  uploadArtifact(
    projectId: string,
    version: string,
    notes: string,
    nefFile: File,
    manifestFile: File
  ): Observable<Artifact> {
    const formData = new FormData();
    formData.append('version', version);
    formData.append('notes', notes);
    formData.append('files', nefFile, nefFile.name);
    formData.append('files', manifestFile, manifestFile.name);

    return this.http.post<Artifact>(
      `${this.apiBaseUrl}/projects/${projectId}/artifacts`,
      formData
    );
  }

  private getArtifacts(projectId: string): Observable<Artifact[]> {
    return this.http
      .get<Artifact[]>(`${this.apiBaseUrl}/projects/${projectId}/artifacts`)
      .pipe(catchError(() => of([])));
  }

  private toProjectCard(project: Project, artifacts: Artifact[]): ProjectCardViewModel {
    const sortedArtifacts = [...artifacts].sort(
      (left, right) =>
        new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()
    );

    return {
      project,
      artifacts: sortedArtifacts,
      latestArtifact: sortedArtifacts[0] ?? null,
      deployed: false
    };
  }
}
