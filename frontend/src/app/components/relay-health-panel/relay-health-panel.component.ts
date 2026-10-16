import { Component, EventEmitter, Input, Output } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { EventRelayStatus } from '../../models/pusharoo.models';

@Component({
  selector: 'app-relay-health-panel',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './relay-health-panel.component.html',
  styleUrl: './relay-health-panel.component.scss'
})
export class RelayHealthPanelComponent {
  @Input() detail?: EventRelayStatus;
  @Input() status = 'checking';
  @Input() network = '';
  @Output() refresh = new EventEmitter<void>();
}
