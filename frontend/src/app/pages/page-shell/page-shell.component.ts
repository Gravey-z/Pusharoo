import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-page-shell',
  imports: [RouterLink],
  templateUrl: './page-shell.component.html',
  styleUrl: './page-shell.component.scss'
})
export class PageShellComponent {
  @Input({ required: true }) title = '';
}
