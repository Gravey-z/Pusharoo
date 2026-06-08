import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WalletConnectComponent } from '../../components/wallet-connect/wallet-connect.component';

@Component({
  selector: 'app-landing',
  imports: [RouterLink, WalletConnectComponent],
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.scss'
})
export class LandingComponent {}
