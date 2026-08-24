import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WalletConnectComponent } from '../../components/wallet-connect/wallet-connect.component';

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
}
