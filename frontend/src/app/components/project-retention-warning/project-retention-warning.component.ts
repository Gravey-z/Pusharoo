import { Component, ElementRef, HostListener, Input } from '@angular/core';
import { ProjectRetention } from '../../models/pusharoo.models';
import { ProjectRetentionNoticeComponent } from '../project-retention-notice/project-retention-notice.component';

@Component({
  selector: 'app-project-retention-warning',
  imports: [ProjectRetentionNoticeComponent],
  templateUrl: './project-retention-warning.component.html',
  styleUrl: './project-retention-warning.component.scss'
})
export class ProjectRetentionWarningComponent {
  private static nextId = 0;
  @Input() retention: ProjectRetention | null | undefined;
  @Input() projectName = '';
  readonly tooltipId = `project-retention-tooltip-${++ProjectRetentionWarningComponent.nextId}`;
  tooltipOpen = false;
  private hovered = false;

  constructor(private readonly element: ElementRef<HTMLElement>) {}

  showFromPointer(event: PointerEvent): void {
    if (event.pointerType === 'touch') return;
    this.hovered = true;
    this.tooltipOpen = true;
  }

  hideFromPointer(event: PointerEvent): void {
    if (event.pointerType === 'touch') return;
    this.hovered = false;
    if (!this.element.nativeElement.querySelector('button')?.matches(':focus-visible'))
      this.tooltipOpen = false;
  }

  showFromFocus(event: FocusEvent): void {
    if ((event.target as HTMLElement).matches(':focus-visible')) this.tooltipOpen = true;
  }

  hideFromFocus(): void {
    if (!this.hovered) this.tooltipOpen = false;
  }

  toggleTooltip(): void {
    this.tooltipOpen = !this.tooltipOpen;
  }

  @HostListener('document:keydown.escape')
  dismissTooltip(): void {
    this.tooltipOpen = false;
  }

  @HostListener('document:click', ['$event'])
  dismissFromOutside(event: MouseEvent): void {
    if (!this.element.nativeElement.contains(event.target as Node)) {
      this.tooltipOpen = false;
      this.hovered = false;
    }
  }
}
