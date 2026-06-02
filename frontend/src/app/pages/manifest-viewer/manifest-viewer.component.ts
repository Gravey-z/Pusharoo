import { Component, OnInit } from '@angular/core';
import { AsyncPipe, JsonPipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, map, switchMap } from 'rxjs';
import { Artifact, NeoEvent, NeoMethod, NeoParameter } from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

type ManifestTab = 'overview' | 'methods' | 'events' | 'permissions' | 'raw';

@Component({
  selector: 'app-manifest-viewer',
  imports: [AsyncPipe, JsonPipe, PageShellComponent, RouterLink],
  templateUrl: './manifest-viewer.component.html',
  styleUrl: './manifest-viewer.component.scss'
})
export class ManifestViewerComponent implements OnInit {
  artifact$!: Observable<Artifact | null>;
  activeTab: ManifestTab = 'overview';
  readonly tabs: { id: ManifestTab; label: string }[] = [
    { id: 'overview', label: 'Overview' },
    { id: 'methods', label: 'Methods' },
    { id: 'events', label: 'Events' },
    { id: 'permissions', label: 'Permissions' },
    { id: 'raw', label: 'Raw JSON' }
  ];

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService
  ) {}

  ngOnInit(): void {
    this.artifact$ = this.route.paramMap.pipe(
      map((params) => params.get('artifactId') ?? ''),
      switchMap((artifactId) => this.api.getArtifact(artifactId))
    );
  }

  selectTab(tab: ManifestTab): void {
    this.activeTab = tab;
  }

  methodReturnType(method: NeoMethod): string {
    return method.returntype ?? method.returnType ?? '-';
  }

  formatParameters(parameters: NeoParameter[]): string {
    if (parameters.length === 0) {
      return '-';
    }

    return parameters.map((parameter) => `${parameter.name}: ${parameter.type}`).join(', ');
  }

  formatEventParameters(event: NeoEvent): string {
    if (event.parameters.length === 0) {
      return '-';
    }

    return event.parameters.map((parameter) => parameter.name).join(', ');
  }

  rawJson(artifact: Artifact): string {
    return JSON.stringify(artifact.manifest, null, 2);
  }
}
