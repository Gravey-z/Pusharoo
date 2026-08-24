import { Component, Input, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WalletConnectComponent } from '../../components/wallet-connect/wallet-connect.component';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';

export interface PageBreadcrumb {
  label: string;
  link?: string | string[];
}

@Component({
  selector: 'app-page-shell',
  imports: [RouterLink, WalletConnectComponent],
  templateUrl: './page-shell.component.html',
  styleUrl: './page-shell.component.scss'
})
export class PageShellComponent {
  @Input({ required: true }) title = '';
  @Input() breadcrumbs: PageBreadcrumb[] = [];
  @Input() workspaceShell = false;

  private readonly workspaceContext = inject(ProjectWorkspaceContextService, { optional: true });

  get isEmbeddedInWorkspace(): boolean {
    return Boolean(this.workspaceContext) && !this.workspaceShell;
  }
}
