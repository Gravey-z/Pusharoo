import { Routes } from '@angular/router';
import { ArtifactCompareComponent } from './pages/artifact-compare/artifact-compare.component';
import { ArtifactUploadComponent } from './pages/artifact-upload/artifact-upload.component';
import { ManifestViewerComponent } from './pages/manifest-viewer/manifest-viewer.component';
import { LandingComponent } from './pages/landing/landing.component';
import { ProjectOverviewComponent } from './pages/project-overview/project-overview.component';
import { ProjectsComponent } from './pages/projects/projects.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'home' },
  { path: 'home', component: LandingComponent },
  { path: 'projects', component: ProjectsComponent },
  { path: 'projects/:projectId', component: ProjectOverviewComponent },
  { path: 'projects/:projectId/upload', component: ArtifactUploadComponent },
  { path: 'projects/:projectId/compare', component: ArtifactCompareComponent },
  { path: 'artifacts/:artifactId/manifest', component: ManifestViewerComponent },
  { path: '**', redirectTo: 'projects' }
];
