import { Component, ElementRef, Input, QueryList, ViewChildren, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { WalletService } from '../../services/wallet.service';
import { ProjectWorkspaceContextService } from '../../services/project-workspace-context.service';

@Component({
  selector: 'app-project-release-nav',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './project-release-nav.component.html',
  styleUrl: './project-release-nav.component.scss'
})
export class ProjectReleaseNavComponent {
  @Input({ required: true }) projectId = '';
  @Input() workspaceNavigation = false;
  @ViewChildren('releaseTab') private releaseTabs!: QueryList<ElementRef<HTMLAnchorElement>>;
  private readonly workspaceContext = inject(ProjectWorkspaceContextService, { optional: true });

  constructor(readonly wallet: WalletService) {}

  get isEmbeddedInWorkspace(): boolean {
    return Boolean(this.workspaceContext) && !this.workspaceNavigation;
  }

  onReleaseTabKeydown(event: KeyboardEvent): void {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
      return;
    }

    const tabs = this.releaseTabs.toArray().map((tab) => tab.nativeElement);
    const currentIndex = tabs.indexOf(event.currentTarget as HTMLAnchorElement);
    if (currentIndex === -1) {
      return;
    }

    event.preventDefault();
    const nextIndex = event.key === 'ArrowLeft'
      ? (currentIndex - 1 + tabs.length) % tabs.length
      : event.key === 'ArrowRight'
        ? (currentIndex + 1) % tabs.length
        : event.key === 'Home'
          ? 0
          : tabs.length - 1;
    tabs[nextIndex]?.focus();
  }
}
