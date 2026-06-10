import { Component, OnInit } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { WalletService } from './services/wallet.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit {
  constructor(private readonly wallet: WalletService) {}

  ngOnInit(): void {
    void this.wallet.restoreSavedSession();
  }
}
