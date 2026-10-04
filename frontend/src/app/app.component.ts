import { Component, OnInit } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { WalletService } from './services/wallet.service';
import { WalletAuthService } from './services/wallet-auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit {
  constructor(private readonly wallet: WalletService, private readonly auth: WalletAuthService) {}

  ngOnInit(): void {
    void this.initializeSession();
  }

  private async initializeSession(): Promise<void> {
    try {
      await this.wallet.restoreSavedSession();
    } catch {
      // A failed wallet restoration should not prevent checking the API session.
    }
    await this.auth.restore();
  }
}
