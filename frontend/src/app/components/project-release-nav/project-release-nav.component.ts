import { Component, Input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-project-release-nav',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './project-release-nav.component.html',
  styleUrl: './project-release-nav.component.scss'
})
export class ProjectReleaseNavComponent {
  @Input({ required: true }) projectId = '';
}
